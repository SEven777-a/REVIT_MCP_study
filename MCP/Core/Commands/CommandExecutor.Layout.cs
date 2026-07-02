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

            // ★ 視埠尺寸補償常數 (可依實際 Revit 排版效果調整)
            // Revit 中，視埠放置點為「模型內容框」的中心
            // 下列補償值用於計算整個視埠（含標頭氣泡與標題列）的實際佔用空間

            // 標頭氣泡 (Datum Annotations) 補償：
            //   剖面視圖的樓層標記 (Level Tags) 在裁減框左右兩側各延伸，含氣泡圓圈 + 文字
            //   剖面視圖的剖面頭標記 (Section Head) 在裁減框上方延伸
            const double DATUM_BUBBLE_WIDTH_MM = 25.0;    // 樓層氣泡在左右各延伸的距離 (mm)
            const double DATUM_BUBBLE_HEIGHT_MM = 15.0;   // 剖面頭氣泡在上方延伸的距離 (mm)

            // 視埠標題列 (Viewport Title) 補償：
            //   標題列包含視圖名稱文字，位於視埠內容框正下方
            const double VIEWPORT_TITLE_HEIGHT_MM = 20.0; // 視埠標題列高度 (mm)

            // 視圖間距 (Margin)
            const double H_MARGIN_MM = 10.0;              // 水平視圖間距 (mm)
            const double V_MARGIN_MM = 15.0;              // 垂直行間距 (mm)

            // ================================================
            // 第一階段：計算所有視圖的「膨脹矩形」尺寸 (domain §3.2)
            //   佔用尺寸 = 視圖本體 + 氣泡 + 標題列
            //   膨脹尺寸 = 佔用尺寸 + 間距 (間距以膨脹矩形統一控制)
            // ================================================
            var errors = new List<string>();
            var packItems = new List<PackItem>();

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

                // 佔用尺寸 (含氣泡與標題)
                double occW = viewWidth + DATUM_BUBBLE_WIDTH_MM * 2;
                double occH = viewHeight + DATUM_BUBBLE_HEIGHT_MM + VIEWPORT_TITLE_HEIGHT_MM;
                // 膨脹尺寸 (含間距) — 裝箱用此尺寸，即可保證彼此與邊界間距
                double packW = occW + H_MARGIN_MM;
                double packH = occH + V_MARGIN_MM;

                // 防呆：單一視圖(含補償)就超過邊界，無論如何放不下
                if (packW > boundaryWidthMm || packH > boundaryHeightMm)
                {
                    errors.Add($"視圖 '{view.Name}' 尺寸 ({Math.Round(viewWidth)}×{Math.Round(viewHeight)} mm，含補償) 超過排版邊界，已跳過。");
                    continue;
                }

                packItems.Add(new PackItem
                {
                    ViewId = viewId,
                    View = view,
                    ViewW = viewWidth,
                    ViewH = viewHeight,
                    PackW = packW,
                    PackH = packH
                });
            }

            // ================================================
            // 第二階段：外層貪婪(號段連續) + 內層 MaxRects 裝箱 (domain §3.3)
            //   packItems 已依剖面編號排序 → 依序消費即保證同紙號段連續
            // ================================================
            var sheetSets = new List<List<PackItem>>();
            var sheetPlacements = new List<List<(PackItem item, double x, double y)>>();

            int idx = 0;
            while (idx < packItems.Count)
            {
                var current = new List<PackItem>();
                List<(PackItem, double, double)> currentPlacement = null;

                int k = idx;
                while (k < packItems.Count)
                {
                    var trial = new List<PackItem>(current) { packItems[k] };
                    var res = TryPackMaxRects(trial, boundaryWidthMm, boundaryHeightMm);
                    if (res != null)
                    {
                        // 可行 → 保留，繼續拿下一個編號
                        current = trial;
                        currentPlacement = res;
                        k++;
                    }
                    else
                    {
                        // 不可行 → 收掉這張紙 (方案 A：嚴格號段連續，允許提早結束)
                        break;
                    }
                }

                if (current.Count == 0)
                {
                    // 單一視圖連空白紙都放不下 (理論上已被上面尺寸檢查攔下，此為保險)
                    errors.Add($"視圖 '{packItems[idx].View.Name}' 無法放入排版邊界，已跳過。");
                    idx++;
                    continue;
                }

                sheetSets.Add(current);
                sheetPlacements.Add(currentPlacement);
                idx += current.Count;
            }

            // ================================================
            // 第三階段：末尾平衡 (domain §3.5)
            //   最後一張填充率 < 50% → 重分末尾兩張，使兩張填充率盡量平均
            //   (級聯重分為後續增強，目前實作兩張版)
            // ================================================
            double boundaryArea = boundaryWidthMm * boundaryHeightMm;
            System.Func<List<PackItem>, double> fill =
                s => (boundaryArea <= 0) ? 0 : s.Sum(p => p.PackW * p.PackH) / boundaryArea;
            const double FILL_THRESHOLD = 0.5;

            if (sheetSets.Count >= 2 && fill(sheetSets[sheetSets.Count - 1]) < FILL_THRESHOLD)
            {
                int a = sheetSets.Count - 2;
                int b = sheetSets.Count - 1;

                // 合併末尾兩張的連續號段 (外層依序消費 → combined 仍為連續)
                var combined = new List<PackItem>(sheetSets[a]);
                combined.AddRange(sheetSets[b]);

                double currentMin = Math.Min(fill(sheetSets[a]), fill(sheetSets[b]));
                double bestMinFill = currentMin;
                List<PackItem> bestFirst = null, bestSecond = null;
                List<(PackItem, double, double)> bestFirstPl = null, bestSecondPl = null;

                // 在連續號段中尋找切點，兩張皆可行且 min(填充率) 最大
                for (int p = 1; p < combined.Count; p++)
                {
                    var first = combined.GetRange(0, p);
                    var second = combined.GetRange(p, combined.Count - p);

                    var pf = TryPackMaxRects(first, boundaryWidthMm, boundaryHeightMm);
                    if (pf == null) continue;
                    var ps = TryPackMaxRects(second, boundaryWidthMm, boundaryHeightMm);
                    if (ps == null) continue;

                    double mf = Math.Min(fill(first), fill(second));
                    if (mf > bestMinFill + 1e-6)
                    {
                        bestMinFill = mf;
                        bestFirst = first; bestSecond = second;
                        bestFirstPl = pf; bestSecondPl = ps;
                    }
                }

                if (bestFirst != null)
                {
                    sheetSets[a] = bestFirst; sheetSets[b] = bestSecond;
                    sheetPlacements[a] = bestFirstPl; sheetPlacements[b] = bestSecondPl;
                }
            }

            // ================================================
            // 第四階段：把裝箱結果 (膨脹矩形左上角) 換算為 Revit 視埠中心點
            // ================================================
            var sheetLayouts = new List<List<ViewportPlacement>>();
            for (int s = 0; s < sheetSets.Count; s++)
            {
                var page = new List<ViewportPlacement>();
                foreach (var (item, xTL, yTL) in sheetPlacements[s])
                {
                    // 裝箱局部座標 (0,0)=邊界左上，x 向右、y 向下
                    double absLeftMm = minXMm + xTL;
                    double absTopMm = maxYMm - yTL;

                    // 由膨脹矩形左上角回推實際視圖中心 (domain §3.3.2)
                    double centerXMm = absLeftMm + H_MARGIN_MM / 2.0 + DATUM_BUBBLE_WIDTH_MM + item.ViewW / 2.0;
                    double centerYMm = absTopMm - (V_MARGIN_MM / 2.0 + DATUM_BUBBLE_HEIGHT_MM + item.ViewH / 2.0);

                    page.Add(new ViewportPlacement
                    {
                        ViewId = item.ViewId,
                        ViewName = item.View.Name,
                        Center = new XYZ(centerXMm / 304.8, centerYMm / 304.8, 0),
                        WidthMm = Math.Round(item.ViewW, 1),
                        HeightMm = Math.Round(item.ViewH, 1)
                    });
                }
                sheetLayouts.Add(page);
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
        /// 輔助結構：待裝箱視圖 (含本體尺寸與膨脹後尺寸)
        /// </summary>
        private class PackItem
        {
            public ElementId ViewId { get; set; }
            public View View { get; set; }
            public double ViewW { get; set; }   // 視圖本體寬 (mm)
            public double ViewH { get; set; }   // 視圖本體高 (mm)
            public double PackW { get; set; }   // 膨脹矩形寬 (含氣泡+標題+間距，mm)
            public double PackH { get; set; }   // 膨脹矩形高 (mm)
        }

        /// <summary>
        /// 內層二維裝箱：MaxRects (Best Short Side Fit)。
        /// 在 W×H 邊界內對 items 的膨脹矩形求無重疊放置。
        /// 尺寸由大到小 (FFD) 排序放置；全部放入回傳各矩形左上角座標，任一放不下回傳 null。
        /// 座標系：(0,0)=左上，x 向右、y 向下。
        /// </summary>
        private List<(PackItem item, double x, double y)> TryPackMaxRects(List<PackItem> items, double W, double H)
        {
            const double EPS = 1e-6;

            // 剩餘空矩形集：每個 = {x, y, w, h}
            var free = new List<double[]> { new double[] { 0, 0, W, H } };
            var result = new List<(PackItem, double, double)>();

            // FFD：高度由大到小，其次寬度
            var ordered = items.OrderByDescending(i => i.PackH).ThenByDescending(i => i.PackW).ToList();

            foreach (var it in ordered)
            {
                // 以 BSSF 挑選最佳空矩形
                int bestIdx = -1;
                double bestShort = double.MaxValue, bestLong = double.MaxValue;
                for (int r = 0; r < free.Count; r++)
                {
                    double fw = free[r][2], fh = free[r][3];
                    if (it.PackW <= fw + EPS && it.PackH <= fh + EPS)
                    {
                        double leftH = fw - it.PackW;
                        double leftV = fh - it.PackH;
                        double shortS = Math.Min(leftH, leftV);
                        double longS = Math.Max(leftH, leftV);
                        if (shortS < bestShort - EPS ||
                            (Math.Abs(shortS - bestShort) <= EPS && longS < bestLong - EPS))
                        {
                            bestShort = shortS; bestLong = longS; bestIdx = r;
                        }
                    }
                }
                if (bestIdx < 0) return null; // 放不下 → 不可行

                double px = free[bestIdx][0], py = free[bestIdx][1];
                var placed = new double[] { px, py, it.PackW, it.PackH };
                result.Add((it, px, py));

                // 分割所有與 placed 重疊的空矩形 (MaxRects splitting)
                var newFree = new List<double[]>();
                foreach (var fr in free)
                {
                    if (Overlaps(fr, placed, EPS))
                    {
                        double fx = fr[0], fy = fr[1], fW = fr[2], fH = fr[3];
                        // 上方殘塊
                        if (placed[1] > fy + EPS)
                            newFree.Add(new double[] { fx, fy, fW, placed[1] - fy });
                        // 下方殘塊
                        if (placed[1] + placed[3] < fy + fH - EPS)
                            newFree.Add(new double[] { fx, placed[1] + placed[3], fW, (fy + fH) - (placed[1] + placed[3]) });
                        // 左方殘塊
                        if (placed[0] > fx + EPS)
                            newFree.Add(new double[] { fx, fy, placed[0] - fx, fH });
                        // 右方殘塊
                        if (placed[0] + placed[2] < fx + fW - EPS)
                            newFree.Add(new double[] { placed[0] + placed[2], fy, (fx + fW) - (placed[0] + placed[2]), fH });
                    }
                    else
                    {
                        newFree.Add(fr);
                    }
                }
                free = PruneContained(newFree, EPS);
            }

            return result;
        }

        /// <summary>兩矩形是否重疊 (each = {x,y,w,h})</summary>
        private bool Overlaps(double[] a, double[] b, double eps)
        {
            return a[0] < b[0] + b[2] - eps && a[0] + a[2] > b[0] + eps &&
                   a[1] < b[1] + b[3] - eps && a[1] + a[3] > b[1] + eps;
        }

        /// <summary>outer 是否完整包住 inner</summary>
        private bool Contains(double[] outer, double[] inner, double eps)
        {
            return outer[0] <= inner[0] + eps && outer[1] <= inner[1] + eps &&
                   outer[0] + outer[2] >= inner[0] + inner[2] - eps &&
                   outer[1] + outer[3] >= inner[1] + inner[3] - eps;
        }

        /// <summary>移除被其他空矩形完整包含的冗餘矩形</summary>
        private List<double[]> PruneContained(List<double[]> rects, double eps)
        {
            var keep = new List<double[]>();
            for (int i = 0; i < rects.Count; i++)
            {
                // 略過退化 (近零面積) 矩形
                if (rects[i][2] <= eps || rects[i][3] <= eps) continue;

                bool contained = false;
                for (int j = 0; j < rects.Count; j++)
                {
                    if (i == j) continue;
                    if (Contains(rects[j], rects[i], eps))
                    {
                        // 兩者互相包含 (相同矩形) 時只留索引較小者
                        bool reverse = Contains(rects[i], rects[j], eps);
                        if (!reverse || j < i) { contained = true; break; }
                    }
                }
                if (!contained) keep.Add(rects[i]);
            }
            return keep;
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
