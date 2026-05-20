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

            // ★ 修正 1：依視圖名稱末尾的剖面編號排序 (自然數字排序)
            viewIds = viewIds
                .OrderBy(id =>
                {
                    View v = doc.GetElement(id) as View;
                    if (v == null) return int.MaxValue;
                    var match = System.Text.RegularExpressions.Regex.Match(v.Name, @"(\d+)\s*$");
                    return match.Success ? int.Parse(match.Groups[1].Value) : int.MaxValue;
                })
                .ToList();

            // 可用空間大小 (mm)
            double minXMm = _cachedLayoutBoundary.Min.X * 304.8;
            double maxXMm = _cachedLayoutBoundary.Max.X * 304.8;
            double minYMm = _cachedLayoutBoundary.Min.Y * 304.8;
            double maxYMm = _cachedLayoutBoundary.Max.Y * 304.8;
            double boundaryWidthMm = maxXMm - minXMm;
            double boundaryHeightMm = maxYMm - minYMm;

            // ★ 修正 2：標頭氣泡的額外空間補償 (固定值，以 mm 計算)
            // 剖面符號氣泡 (Datum Annotation) 出現在視埠上方，佔用額外空間
            // 視圖標題 (Viewport Title) 位於視埠下方
            const double VIEWPORT_TITLE_HEIGHT_MM = 12.0; // 視埠標題高度
            const double DATUM_BUBBLE_HEIGHT_MM = 8.0;    // 上方標頭氣泡高度
            const double DATUM_BUBBLE_WIDTH_MM = 5.0;     // 左右氣泡寬度補償
            const double H_MARGIN_MM = 15.0;              // 視圖間水平間距
            const double V_MARGIN_MM = 10.0;              // 視圖間垂直間距

            // ================================================
            // 第一階段：計算所有視圖的物理尺寸並分行規劃
            // ================================================

            // 共用變數
            var errors = new List<string>();
            var sheetLayouts = new List<List<ViewportPlacement>>();
            sheetLayouts.Add(new List<ViewportPlacement>());
            int sheetIndex = 0;

            // 輔助結構：帶有計算好尺寸的視圖資訊
            var viewInfos = new List<(ElementId ViewId, View View, double WidthMm, double HeightMm)>();

            foreach (var viewId in viewIds)
            {
                View view = doc.GetElement(viewId) as View;
                if (view == null || view.IsTemplate)
                {
                    errors.Add($"ID {viewId} 不是有效的視圖或為視圖樣板。");
                    continue;
                }

                // 防呆：檢查是否已放置於其他圖紙
                if (!Viewport.CanAddViewToSheet(doc, _cachedSourceSheetId, view.Id))
                {
                    errors.Add($"視圖 '{view.Name}' (ID {view.Id}) 已放置於其他圖紙，無法重複放置。");
                    continue;
                }

                BoundingBoxXYZ cropBox = view.CropBox;
                if (cropBox == null)
                {
                    errors.Add($"無法取得視圖 '{view.Name}' 的 CropBox。");
                    continue;
                }

                // 計算視圖本體物理尺寸 (mm)
                double viewWidth = ((cropBox.Max.X - cropBox.Min.X) * 304.8) / view.Scale;
                double viewHeight = ((cropBox.Max.Y - cropBox.Min.Y) * 304.8) / view.Scale;

                // 防呆：單一視圖就超過邊界寬度
                double slotWidth = viewWidth + DATUM_BUBBLE_WIDTH_MM * 2;
                if (slotWidth > boundaryWidthMm)
                {
                    errors.Add($"視圖 '{view.Name}' 的寬度 ({Math.Round(viewWidth)} mm) 超過排版邊界限制 ({Math.Round(boundaryWidthMm)} mm)，已跳過。");
                    continue;
                }

                viewInfos.Add((viewId, view, viewWidth, viewHeight));
            }

            // ★ 新演算法：以行為單位預算（Row Pre-calculation）
            // 步驟：累加接下來視圖的 X 寬度，塞滿一行後換行
            // 行高取該行內所有視圖中的最大高度
            var rows = new List<List<(ElementId ViewId, View View, double WidthMm, double HeightMm)>>();
            var currentRow = new List<(ElementId ViewId, View View, double WidthMm, double HeightMm)>();
            double currentRowWidthSum = 0;

            foreach (var info in viewInfos)
            {
                // 視圖在行內佔用的格位寬度（含左右氣泡補償）
                double slotWidth = info.WidthMm + DATUM_BUBBLE_WIDTH_MM * 2 + H_MARGIN_MM;

                // 若加入後超過邊界，先把目前行存起來，開新行
                if (currentRow.Count > 0 && currentRowWidthSum + slotWidth > boundaryWidthMm)
                {
                    rows.Add(currentRow);
                    currentRow = new List<(ElementId, View, double, double)>();
                    currentRowWidthSum = 0;
                }

                currentRow.Add(info);
                currentRowWidthSum += slotWidth;
            }
            // 把最後一行存入
            if (currentRow.Count > 0)
                rows.Add(currentRow);

            // ================================================
            // 第二階段：依行順序分配圖紙並計算放置座標
            // ================================================
            double layoutCurrentY = maxYMm;

            foreach (var row in rows)
            {
                // 此行的最大視圖高度 = 行高基準
                double rowMaxViewHeight = row.Max(r => r.HeightMm);
                // 此行佔用的總垂直空間 (視圖本體 + 上方氣泡 + 下方標題 + 行距)
                double rowTotalHeight = rowMaxViewHeight + DATUM_BUBBLE_HEIGHT_MM + VIEWPORT_TITLE_HEIGHT_MM + V_MARGIN_MM;

                // 防呆：此行高度超過整個排版邊界（不可能放入任何一頁）
                if (rowTotalHeight > boundaryHeightMm)
                {
                    foreach (var item in row)
                        errors.Add($"視圖 '{item.View.Name}' 所在行的高度 ({Math.Round(rowMaxViewHeight)} mm) 超過排版邊界，已跳過。");
                    continue;
                }

                // 若當前 Y 不夠放此行，換頁（建立新圖紙）
                if (layoutCurrentY - rowTotalHeight < minYMm)
                {
                    sheetIndex++;
                    sheetLayouts.Add(new List<ViewportPlacement>());
                    layoutCurrentY = maxYMm;
                }

                // 由左至右依序放置此行的視圖
                double layoutCurrentX = minXMm;
                foreach (var item in row)
                {
                    // 視埠放置中心點：
                    //   X = 目前 X 起點 + 左側氣泡補償 + 視圖寬/2
                    //   Y = 目前行頂部 - 上方氣泡高度 - 視圖高/2
                    double centerX = layoutCurrentX + DATUM_BUBBLE_WIDTH_MM + item.WidthMm / 2.0;
                    double centerY = layoutCurrentY - DATUM_BUBBLE_HEIGHT_MM - item.HeightMm / 2.0;

                    sheetLayouts[sheetIndex].Add(new ViewportPlacement
                    {
                        ViewId = item.ViewId,
                        ViewName = item.View.Name,
                        Center = new XYZ(centerX / 304.8, centerY / 304.8, 0),
                        WidthMm = Math.Round(item.WidthMm, 1),
                        HeightMm = Math.Round(item.HeightMm, 1)
                    });

                    // 移動到下一個視圖的 X 起點
                    layoutCurrentX += item.WidthMm + DATUM_BUBBLE_WIDTH_MM * 2 + H_MARGIN_MM;
                }

                // 此行放置完成，Y 往下移動一行高度
                layoutCurrentY -= rowTotalHeight;
            }

            var createdSheets = new List<string>();
            var placedViewports = new List<string>();

            // 執行實體 Revit 修改 (里程碑 3 實作)
            using (Transaction trans = new Transaction(doc, "自動排版出圖"))
            {
                trans.Start();

                try
                {
                    // 取得起始圖紙資訊
                    ViewSheet sourceSheet = doc.GetElement(_cachedSourceSheetId) as ViewSheet;
                    string currentSheetNumber = sourceSheet?.SheetNumber ?? "A-101";
                    string currentSheetName = sourceSheet?.Name ?? "剖面圖紙";

                    for (int i = 0; i < sheetLayouts.Count; i++)
                    {
                        var page = sheetLayouts[i];
                        if (page.Count == 0) continue;

                        ElementId targetSheetId = null;

                        if (i == 0)
                        {
                            // 第一頁直接使用目前設定範圍的來源圖紙
                            targetSheetId = _cachedSourceSheetId;
                            createdSheets.Add($"{currentSheetName} ({currentSheetNumber}) [現有圖紙]");
                        }
                        else
                        {
                            // 後續頁數建立新圖紙
                            currentSheetNumber = IncrementString(currentSheetNumber);
                            
                            // 避免編號衝突
                            int safetyCounter = 0;
                            while (IsSheetNumberExists(doc, currentSheetNumber) && safetyCounter < 100)
                            {
                                currentSheetNumber = IncrementString(currentSheetNumber);
                                safetyCounter++;
                            }

                            ViewSheet newSheet = ViewSheet.Create(doc, _cachedTitleBlockId);
                            newSheet.SheetNumber = currentSheetNumber;
                            newSheet.Name = $"{currentSheetName} - {i + 1}";
                            
                            targetSheetId = newSheet.Id;
                            createdSheets.Add($"{newSheet.Name} ({newSheet.SheetNumber}) [新增圖紙]");
                        }

                        // 放置視圖
                        foreach (var vp in page)
                        {
                            if (Viewport.CanAddViewToSheet(doc, targetSheetId, vp.ViewId))
                            {
                                Viewport newVp = Viewport.Create(doc, targetSheetId, vp.ViewId, vp.Center);
                                placedViewports.Add($"視圖 '{vp.ViewName}' ➜ 圖紙 ({currentSheetNumber})");
                            }
                            else
                            {
                                errors.Add($"無法將視圖 '{vp.ViewName}' (ID {vp.ViewId}) 放置到圖紙 ({currentSheetNumber}) 上。可能是因為視圖已被放置到其他圖紙。");
                            }
                        }
                    }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    return new { Success = false, Message = $"自動排版過程中發生錯誤已復原，錯誤原因：{ex.Message}" };
                }
            }

            return new
            {
                Success = true,
                Message = "自動排版出圖成功！",
                TotalPagesCreated = createdSheets.Count,
                CreatedSheets = createdSheets,
                PlacedViewports = placedViewports,
                Errors = errors
            };
        }

        /// <summary>
        /// 輔助方法：檢查圖紙編號是否已存在於專案中
        /// </summary>
        private bool IsSheetNumberExists(Document doc, string sheetNumber)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Any(s => s.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase));
        }
    }
}
