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
- **氣泡與標題補償**：視圖本體外還需納入標頭氣泡（樓層/剖面頭）與視埠標題列的佔用空間。定義每個視圖的**佔用尺寸 (Occupied Size)**：
  - `OccupiedWidth  = ViewWidth  + DatumBubbleWidth × 2`
  - `OccupiedHeight = ViewHeight + DatumBubbleHeight + ViewportTitleHeight`
- **間距以「膨脹矩形」統一控制 (Spacing via Inflation)**：不在演算法內到處加減 margin，而是在裝箱前把每個視圖的佔用尺寸**各再膨脹半個間距**，用膨脹後的矩形去裝箱；放置時再放回實際中心點。
  - `PackWidth  = OccupiedWidth  + HMargin`
  - `PackHeight = OccupiedHeight + VMargin`
  - 如此可保證任兩張之間、以及與邊界之間，都自動維持設定的最小間距，且間距統一可控。

### 3.3. 排版演算法 (Packing Algorithm — 號段連續 + 內層自由裝箱)

> 設計取捨：**跨圖紙嚴格照剖面編號連續，同一張圖紙內部放寬排列以最大化空間利用率**。
> 亦即第一張放 1–N、第二張放 N+1–M……號段不跳；但同紙內這些剖面的**位置**不必照號，改用二維裝箱塞得緊密。目的是讓「一張紙能容納的連續號段」變長（例如從 5 張拉到 10 張），同時不出現同紙混號（如 1、2、5、20）。

演算法分**外層（決定號段成員）**與**內層（決定紙上位置）**兩層：

#### 3.3.1 外層：貪婪消費、號段連續 (Contiguous Greedy Consumption)
1. 先將所有待排視圖**依剖面編號自然排序**（名稱末尾數字）。
2. 從編號最小者開始，依序嘗試加入「目前這張紙」的候選集合。
3. 每加入一個，就對「目前候選集合」整體重跑一次**內層裝箱可行性檢查 (§3.3.2)**：
   - 可行 → 保留，繼續拿下一個編號。
   - 不可行 → 移除剛加入的那個，**收掉這張紙**（成員 = 加入前的集合），開新紙，該視圖成為新紙的第一個成員。
4. 重複直到所有視圖分配完畢。
> **可行性單調性**：裝箱可行性對集合大小單調——子集恆可行、超集恆不可行。故「加到第一次失敗為止」即為該紙在此啟發式下的**最大連續張數**。這是嚴格號段連續 (方案 A) 的直接結果，允許某張紙提早結束。

#### 3.3.2 內層：二維裝箱 (2D Bin Packing — MaxRects)
在單一圖紙的可用矩形範圍內，對候選集合的**膨脹矩形 (§3.2)** 求解無重疊放置：
1. **排序**：候選集合依高度（或面積）**由大到小**排列（First-Fit-Decreasing），大塊先卡位、小塊填縫。
2. **演算法**：採 **MaxRects (Best Short Side Fit, BSSF)**——維護一組「剩餘空矩形 (free rectangles)」，每放一個矩形就分割並更新空矩形集，能回填行式排版留下的空洞，填充率明顯優於行式。
3. **座標系**：以圖紙左上角為起點 (`X = Min.X`, `Y = Max.Y`)，X 向右、Y 向下。
4. **可行性**：若所有候選膨脹矩形皆成功放入且不越界，回傳「可行 + 各矩形左上角座標」；任一放不下則「不可行」。
5. **放置點換算**：Revit `Viewport` 放置點為視埠中心。由膨脹矩形左上角 `(X, Y)` 回推實際視圖中心：
   - `CenterX = X + HMargin/2 + DatumBubbleWidth + ViewWidth/2`
   - `CenterY = Y - VMargin/2 - DatumBubbleHeight - ViewHeight/2`

### 3.4. 圖紙生成與視圖放置 (Sheet Generation & Viewport Creation)
- **建立新圖紙**：使用 `ViewSheet.Create(doc, TitleblockId)`。TitleblockId 可自動取自於使用者最初設定範圍所在的那張圖紙，以保持專案圖框的一致性。
- **放置視埠**：使用 `Viewport.Create(doc, newSheetId, viewId, placementPoint)`。
- **放置點 (Placement Point)**：Revit 中 `Viewport` 的放置點預設是視埠的**中心點**。
  - 因此，若演算法算出左上角座標為 `(X, Y)`，其放置點應為 `(X + Width/2, Y - Height/2)`。

### 3.5. 末尾平衡 (Last-Sheet Rebalancing)
貪婪塞完後，最後一張常只剩少數幾張、填充率過低而顯得尷尬。此步驟**全自動**修正，使用者無需手動介入。

1. **觸發條件**：最後一張圖紙的**填充率 < 50%**（填充率 = 已放置膨脹矩形面積總和 ÷ 排版邊界面積）。
2. **平衡範圍**：只重分**末尾最後兩張**，不動前面已排好的圖紙。
   - 取「倒數第二張 + 最後一張」的合併連續號段（例：21–28 + 29–30 = 21–30）。
3. **重新切分**：在此連續號段中尋找一個切點，使切成的兩張**都通過內層裝箱可行性檢查 (§3.3.2)**，且讓兩張填充率盡量平均（目標：最大化 `min(兩張填充率)`）。
   - 例：21–30 → `[21–25][26–30]`，兩張各 5 張，號段仍連續。
4. **級聯 (Cascade)**：若重分後最後一張填充率仍 < 50%，再往前併入一張（末尾三張一起重分），至多級聯數張。
5. **不變式**：重分全程維持**號段連續**（切點只在連續序列中移動），絕不產生跳號。
> 代價：倒數第二張會從「塞好塞滿」變成稍鬆，換取最後一張不致空蕩。此為刻意取捨，對應人工排版最後的收尾調整。

## 4. 防呆機制與例外處理 (Exception Handling)
- 若某張視圖的大小大於整個排版可用區域（無法放入單張圖紙），則跳過該視圖並記錄 Error 回報。
- 若使用者選取到的視圖已經被放置在其他圖紙上（Revit 不允許同一視圖放置兩次），則必須先檢查 `Viewport.CanAddViewToSheet(doc, sheetId, viewId)`，若為 `false` 則視為已放置或無效。
