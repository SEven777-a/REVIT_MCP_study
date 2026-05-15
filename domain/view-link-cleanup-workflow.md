---
name: view-link-cleanup-workflow
description: "視圖連結雜訊清理工作流程：關閉當前視圖中所有連結模型（如結構、機電）的基準元件（樓層線、網格線、參考平面），以維持圖面整潔。當使用者提到清理視圖、隱藏連結、關閉連結基準、連結模型可見性時觸發。"
metadata:
  version: "1.0"
  updated: "2026-05-15"
  created: "2026-05-15"
  contributors:
    - "AI Assistant"
  references: [] 
  related: [] 
  referenced_by: []
  tags: [清理視圖, 連結模型, 隱藏網格, 可見性, link visibility, clean view]
---

# 視圖連結雜訊清理工作流程 (View Link Cleanup)

本文件定義了在 Revit 視圖中，如何安全且符合 BIM 邏輯地批次關閉連結模型（Revit Links）的特定註解品類（如網格線、樓層線、參考平面等），以消除圖面雜訊。

---

## 🛑 執行前置防呆守衛 (Context & Guardrails)

在嘗試修改任何可見性之前，AI 必須嚴格執行以下檢查：

### 1. 視圖樣版檢查 (View Template Guard)
- **規則**：如果當前視圖受「視圖樣版（View Template）」控制，且該樣板啟用了「V/G 覆寫 RVT 連結」的控制權，則**絕對不可**直接修改視圖設定。
- **AI 應對策略**：必須主動攔截並詢問使用者：「*目前視圖受樣板 `{TemplateName}` 控制。請問您希望 (A) 直接修改樣板（將影響所有套用此樣板的視圖）？ 還是 (B) 暫時解除此視圖的樣板關聯再做修改？*」

### 2. 3D 視圖特例 (3D View Exception)
- **規則**：在 3D 視圖中，網格線（Grids）與樓層線（Levels）的預設可見性行為與 2D 視圖不同。
- **AI 應對策略**：若偵測到當前為 `ThreeD` 視圖，需提醒使用者：「*此操作通常針對 2D 平/立/剖面圖紙，是否確定要在 3D 視圖中執行？*」

### 3. 禁止單一元素隱藏 (No Element-Level Hiding)
- **規則**：**嚴禁**讓 AI 使用選取幾何元素並「Hide in View」的方式來隱藏連結模型內容。
- **原因**：當連結模型更新並產生新元素時，新元素仍會顯示。必須使用「品類覆寫（Category Override）」來處理。

---

## 🔄 標準執行步驟 (Standard Workflow)

**執行時，請依照以下順序呼叫對應工具：**

### 步驟 1：取得當前視圖與連結資訊
1. 呼叫 `get_active_view`：確認視圖類型與 View Template 狀態。
2. 呼叫 `get_linked_models`：取得所有已載入的 `RevitLinkInstance` 清單與其 ID。

### 步驟 2：確認關閉目標
與使用者確認要關閉的品類，預設目標為：
- **註解品類**：`OST_Grids` (網格線)、`OST_Levels` (樓層線)、`OST_ReferencePlanes` (參考平面)

### 步驟 3：執行可見性覆寫
使用專用工具 `set_link_category_visibility`（待開發），將目標連結模型的圖形顯示切換為「自訂 (Custom)」，並關閉上述品類。

### 步驟 4：結果回報
向使用者總結：「*已在視圖 '{ViewName}' 中，將 {N} 個連結模型（包含：XXX, YYY）的網格線、樓層線與參考平面設定為隱藏。*」

---

## 🛠️ 所需的 MCP 工具介面規範 (API Contract)

為了支撐此工作流程，C# 端 (`CommandExecutor.cs`) 必須提供以下工具：

**工具名稱**：`set_link_category_visibility`
**預期輸入 (Input Schema)**：
```json
{
  "viewId": 0, // 0 表示 Active View
  "linkInstanceIds": [12345, 67890], // 若傳入 ["ALL"] 則套用至所有連結
  "categories": ["OST_Grids", "OST_Levels", "OST_ReferencePlanes"],
  "visible": false // false 為隱藏
}
```
**預期 C# 行為**：
針對指定的 `viewId`，取得其 `RevitLinkGraphicsSettings`。設定 `LinkVisibilityType = Custom` 後，呼叫 `SetCategoryHidden(categoryId, !visible)`，然後以 `View.SetLinkOverrides()` 套用。
