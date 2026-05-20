using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        /// <summary>
        /// 讀取當前選取的細部線 (Detail Lines) 作為排版可用空間 Bounding Box 並暫存
        /// </summary>
        private object SetLayoutBoundary(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            ViewSheet activeSheet = doc.ActiveView as ViewSheet;

            if (activeSheet == null)
            {
                return new { Success = false, Message = "請先切換至圖紙 (Sheet) 視圖，並選取您所繪製的細部線範圍。" };
            }

            var uidoc = _uiApp.ActiveUIDocument;
            var selection = uidoc.Selection.GetElementIds();

            if (selection == null || selection.Count == 0)
            {
                return new { Success = false, Message = "尚未選取任何元件。請先選取圖紙上繪製的細部線範圍。" };
            }

            List<DetailCurve> detailCurves = new List<DetailCurve>();
            foreach (var id in selection)
            {
                Element elem = doc.GetElement(id);
                if (elem is DetailCurve dc)
                {
                    detailCurves.Add(dc);
                }
            }

            if (detailCurves.Count == 0)
            {
                return new { Success = false, Message = "選取的元件中不包含任何細部線 (Detail Lines)。" };
            }

            double minX = double.MaxValue, minY = double.MaxValue, minZ = 0;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = 0;
            bool hasPoints = false;

            foreach (var dc in detailCurves)
            {
                Curve curve = dc.GeometryCurve;
                if (curve == null) continue;

                XYZ p0 = curve.GetEndPoint(0);
                XYZ p1 = curve.GetEndPoint(1);

                minX = Math.Min(minX, Math.Min(p0.X, p1.X));
                minY = Math.Min(minY, Math.Min(p0.Y, p1.Y));
                maxX = Math.Max(maxX, Math.Max(p0.X, p1.X));
                maxY = Math.Max(maxY, Math.Max(p0.Y, p1.Y));
                hasPoints = true;
            }

            if (!hasPoints)
            {
                return new { Success = false, Message = "無法從選取的細部線中讀取幾何端點。" };
            }

            // 更新快取
            _cachedLayoutBoundary = new BoundingBoxXYZ();
            _cachedLayoutBoundary.Min = new XYZ(minX, minY, minZ);
            _cachedLayoutBoundary.Max = new XYZ(maxX, maxY, maxZ);
            _cachedSourceSheetId = activeSheet.Id;

            // 尋找圖紙上的 Titleblock (圖框)
            Element titleBlock = new FilteredElementCollector(doc, activeSheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .FirstOrDefault();

            if (titleBlock != null)
            {
                _cachedTitleBlockId = titleBlock.GetTypeId();
            }
            else
            {
                _cachedTitleBlockId = ElementId.InvalidElementId;
            }

            double widthMm = (maxX - minX) * 304.8;
            double heightMm = (maxY - minY) * 304.8;

            return new
            {
                Success = true,
                Message = "成功讀取並快取排版邊界！",
                SheetName = activeSheet.Name,
                WidthMm = Math.Round(widthMm, 1),
                HeightMm = Math.Round(heightMm, 1),
                HasTitleBlock = _cachedTitleBlockId != ElementId.InvalidElementId
            };
        }

        /// <summary>
        /// 輔助結構：儲存視圖的排版結果
        /// </summary>
        private class ViewportPlacement
        {
            public ElementId ViewId { get; set; }
            public string ViewName { get; set; }
            public XYZ Center { get; set; }
            public double WidthMm { get; set; }
            public double HeightMm { get; set; }
        }

        /// <summary>
        /// 自動排版並生成圖紙放置視埠 (里程碑 2 實作)
        /// </summary>
        private object AutoLayoutSheets(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            if (_cachedLayoutBoundary == null || _cachedSourceSheetId == null)
            {
                return new { Success = false, Message = "尚未設定排版邊界。請先使用 set_layout_boundary 設定可用範圍。" };
            }

            // 讀取待排版視圖 ID 陣列
            JArray viewIdsArray = parameters["viewIds"] as JArray;
            if (viewIdsArray == null || viewIdsArray.Count == 0)
            {
                return new { Success = false, Message = "未提供有效的 viewIds 參數。" };
            }

            List<ElementId> viewIds = new List<ElementId>();
            foreach (var token in viewIdsArray)
            {
                try
                {
                    IdType idVal = token.Value<IdType>();
#if REVIT2025_OR_GREATER
                    viewIds.Add(new ElementId(idVal));
#else
                    viewIds.Add(new ElementId((int)idVal));
#endif
                }
                catch
                {
                    // 忽略無效 ID
                }
            }

            if (viewIds.Count == 0)
            {
                return new { Success = false, Message = "無有效的視圖 ID 可供排版。" };
            }

            // 可用空間大小 (mm)
            double minXMm = _cachedLayoutBoundary.Min.X * 304.8;
            double maxXMm = _cachedLayoutBoundary.Max.X * 304.8;
            double minYMm = _cachedLayoutBoundary.Min.Y * 304.8;
            double maxYMm = _cachedLayoutBoundary.Max.Y * 304.8;
            double boundaryWidthMm = maxXMm - minXMm;
            double boundaryHeightMm = maxYMm - minYMm;

            // 排版演算法變數
            double currentX = minXMm;
            double currentY = maxYMm;
            double currentRowMaxHeight = 0;
            
            var sheetLayouts = new List<List<ViewportPlacement>>();
            sheetLayouts.Add(new List<ViewportPlacement>());
            
            var errors = new List<string>();
            int sheetIndex = 0;

            foreach (var viewId in viewIds)
            {
                View view = doc.GetElement(viewId) as View;
                if (view == null || view.IsTemplate)
                {
                    errors.Add($"ID {viewId} 不是有效的視圖或為視圖樣板。");
                    continue;
                }

                // 檢查是否已放置於其他圖紙
                if (!Viewport.CanAddViewToSheet(doc, _cachedSourceSheetId, view.Id))
                {
                    errors.Add($"視圖 '{view.Name}' (ID {view.Id}) 已放置於其他圖紙，無法重複放置。");
                    continue;
                }

                // 讀取 CropBox 計算物理大小
                if (!view.CropBoxActive)
                {
                    // 若未開啟，暫時以預設值或啟用它
                    // 在此先透過 API 取得 CropBox，即使未啟用 Revit API 也會回傳預設 CropBox
                }

                BoundingBoxXYZ cropBox = view.CropBox;
                if (cropBox == null)
                {
                    errors.Add($"無法取得視圖 '{view.Name}' 的 CropBox。");
                    continue;
                }

                // 視圖在圖紙上的實際大小 (mm)
                double viewWidth = ((cropBox.Max.X - cropBox.Min.X) * 304.8) / view.Scale;
                double viewHeight = ((cropBox.Max.Y - cropBox.Min.Y) * 304.8) / view.Scale;

                // 加上間距 (Margin)
                double totalWidth = viewWidth + 20.0; // 20mm 左右間距
                double totalHeight = viewHeight + 35.0; // 35mm 上下間距 (含標題列)

                // 1. 檢查單一視圖是否大於整個可用空間
                if (totalWidth > boundaryWidthMm || totalHeight > boundaryHeightMm)
                {
                    errors.Add($"視圖 '{view.Name}' 的尺寸 ({Math.Round(viewWidth)}x{Math.Round(viewHeight)} mm) 大於排版邊界限制 ({Math.Round(boundaryWidthMm)}x{Math.Round(boundaryHeightMm)} mm)，已跳過。");
                    continue;
                }

                // 2. 檢查水平空間是否足夠，不夠則換行
                if (currentX + totalWidth > maxXMm)
                {
                    currentX = minXMm;
                    currentY = currentY - currentRowMaxHeight - 15.0; // 15mm 行距
                    currentRowMaxHeight = 0;
                }

                // 3. 檢查垂直空間是否足夠，不夠則換頁 (開新圖紙)
                if (currentY - totalHeight < minYMm)
                {
                    sheetIndex++;
                    sheetLayouts.Add(new List<ViewportPlacement>());
                    currentX = minXMm;
                    currentY = maxYMm;
                    currentRowMaxHeight = 0;
                }

                // 4. 計算視埠中心點位置 (Revit 放置點為視埠中心)
                double centerX = currentX + viewWidth / 2.0;
                double centerY = currentY - viewHeight / 2.0;

                sheetLayouts[sheetIndex].Add(new ViewportPlacement
                {
                    ViewId = view.Id,
                    ViewName = view.Name,
                    Center = new XYZ(centerX / 304.8, centerY / 304.8, 0),
                    WidthMm = Math.Round(viewWidth, 1),
                    HeightMm = Math.Round(viewHeight, 1)
                });

                // 更新當前行的最大高度與 X 起點
                currentRowMaxHeight = Math.Max(currentRowMaxHeight, totalHeight);
                currentX = currentX + totalWidth + 15.0; // 15mm 視圖間距
            }

            // 里程碑 2：先回傳排版計畫供驗證
            var pagesJson = new JArray();
            for (int i = 0; i < sheetLayouts.Count; i++)
            {
                var page = sheetLayouts[i];
                if (page.Count == 0) continue;

                var pageJson = new JObject();
                pageJson["PageIndex"] = i + 1;
                
                var viewportsJson = new JArray();
                foreach (var vp in page)
                {
                    var vpJson = new JObject();
                    vpJson["ViewId"] = vp.ViewId.GetIdValue();
                    vpJson["ViewName"] = vp.ViewName;
                    vpJson["WidthMm"] = vp.WidthMm;
                    vpJson["HeightMm"] = vp.HeightMm;
                    vpJson["CenterXFeet"] = vp.Center.X;
                    vpJson["CenterYFeet"] = vp.Center.Y;
                    viewportsJson.Add(vpJson);
                }
                pageJson["Viewports"] = viewportsJson;
                pagesJson.Add(pageJson);
            }

            return new
            {
                Success = true,
                Message = "排版演算法運算成功！(此為模擬結果，未實際修改 Revit 模型)",
                TotalPagesNeeded = pagesJson.Count,
                Errors = errors,
                LayoutPlan = pagesJson
            };
        }
    }
}
