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
        /// 自動排版並生成圖紙放置視埠 (里程碑 2 實作)
        /// </summary>
        private object AutoLayoutSheets(JObject parameters)
        {
            return new
            {
                Success = false,
                Message = "自動排版放置邏輯將在里程碑 2 實作。"
            };
        }
    }
}
