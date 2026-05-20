# 自動出圖排版 (Automated Sheet Layout) 領域規則

## 1. 核心概念 (Core Concept)
本功能旨在解決 Revit 中大量剖面視圖（Sections / Views）需要手動拖曳至圖紙排版的痛點。
透過 **所見即所得 (WYSIWYG)** 的操作模式，使用者不需輸入繁瑣的留白參數或圖框偏移值，而是直接在圖紙上用「細部線 (Detail Lines)」畫出預期的排版可用範圍。程式將讀取此範圍作為「二維裝箱演算法 (2D Bin Packing)」的絕對邊界，並自動進行多張圖紙的生成與視圖放置。

## 2. 使用者操作流程 (SOP)
1. **設定可用範圍**：使用者在目前 Active 的圖紙 (Sheet) 上，使用細部線畫出代表排版可用空間的幾何圖形（通常為矩形）。
2. **啟動範圍設定**：使用者選取這些線條，並呼叫對應命令（如 `set_layout_boundary`），將此邊界範圍快取在後端。
3. **執行排版放置**：使用者選取欲排版的視圖/剖面，並呼叫 `auto_layout_sheets`，程式將自動運算並放置。

## 3. 系統設計與資料流 (System Design)

### 3.1. 範圍讀取與計算 (Layout Boundary Evaluation)
- 抓取使用者選取的 `DetailCurve` 集合。
- 透過其幾何端點，合併計算出該範圍的 `BoundingBoxXYZ`。
- 在圖紙座標系中（Sheet View 是一個 2D 空間），提取其 `Min` (左下角) 與 `Max` (右上角)。
- 這個範圍的寬度為 `Max.X - Min.X`，高度為 `Max.Y - Min.Y`。

### 3.2. 視圖物理大小計算 (View Size on Paper Evaluation)
- 每個要放置的視圖，在還沒被放置在圖紙上時，可以透過 `View.Outline` 或 `View.CropBox` 與 `Scale` 換算出其在圖紙上的物理大小。
- 視圖在圖紙上的實際寬度：`(CropBox.Max.X - CropBox.Min.X) / View.Scale`。
- 視圖在圖紙上的實際高度：`(CropBox.Max.Y - CropBox.Min.Y) / View.Scale`。
- *(註：實務上 Revit 有 `Viewport.GetBoxOutline`，但這需要在視圖放置後才能取得。排版前需自算模型投影大小，並預留約 10~15 mm 的垂直空間給「視圖標題 (Viewport Title)」)*。

### 3.3. 排版演算法 (Packing Algorithm - Row-based Layout)
為了保持工程圖說的易讀性，採用**由左至右、由上至下 (Left-to-Right, Top-to-Bottom)** 的行式排版演算法。
1. **初始化起點**：從可用邊界框的左上角開始 (`X = Min.X`, `Y = Max.Y`)。
2. **放置邏輯**：
   - 嘗試放置第一個視圖，其佔據空間為 `Width` 與 `Height`。
   - `Next X = Current X + Width + Margin`。
   - 若 `Next X` 超過 `Max.X`（該行放不下了），則換行。
   - 換行邏輯：`Current Y = Current Y - 行高 (Max Height of Current Row) - Margin`，`Current X = Min.X`。
3. **換頁邏輯 (Page Break)**：
   - 若 `Current Y - 視圖 Height` 小於 `Min.Y`（該頁面底部放不下了），則觸發**建立新圖紙**。
   - 記錄下新圖紙的 ID，並將起點重置為左上角。

### 3.4. 圖紙生成與視圖放置 (Sheet Generation & Viewport Creation)
- **建立新圖紙**：使用 `ViewSheet.Create(doc, TitleblockId)`。TitleblockId 可自動取自於使用者最初設定範圍所在的那張圖紙，以保持專案圖框的一致性。
- **放置視埠**：使用 `Viewport.Create(doc, newSheetId, viewId, placementPoint)`。
- **放置點 (Placement Point)**：Revit 中 `Viewport` 的放置點預設是視埠的**中心點**。
  - 因此，若演算法算出左上角座標為 `(X, Y)`，其放置點應為 `(X + Width/2, Y - Height/2)`。

## 4. 防呆機制與例外處理 (Exception Handling)
- 若某張視圖的大小大於整個排版可用區域（無法放入單張圖紙），則跳過該視圖並記錄 Error 回報。
- 若使用者選取到的視圖已經被放置在其他圖紙上（Revit 不允許同一視圖放置兩次），則必須先檢查 `Viewport.CanAddViewToSheet(doc, sheetId, viewId)`，若為 `false` 則視為已放置或無效。
