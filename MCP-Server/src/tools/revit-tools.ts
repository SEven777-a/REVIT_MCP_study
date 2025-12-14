/**
 * Revit MCP 工具定義
 * 定義可供 AI 呼叫的 Revit 操作工具
 */

import { Tool } from "@modelcontextprotocol/sdk/types.js";
import { RevitSocketClient } from "../socket.js";

/**
 * 註冊所有 Revit 工具
 */
export function registerRevitTools(): Tool[] {
    return [
        // 1. 建立牆元素
        {
            name: "create_wall",
            description: "在 Revit 中建立一面牆。需要指定起點、終點座標和高度。",
            inputSchema: {
                type: "object",
                properties: {
                    startX: {
                        type: "number",
                        description: "起點 X 座標（公釐）",
                    },
                    startY: {
                        type: "number",
                        description: "起點 Y 座標（公釐）",
                    },
                    endX: {
                        type: "number",
                        description: "終點 X 座標（公釐）",
                    },
                    endY: {
                        type: "number",
                        description: "終點 Y 座標（公釐）",
                    },
                    height: {
                        type: "number",
                        description: "牆高度（公釐）",
                        default: 3000,
                    },
                    wallType: {
                        type: "string",
                        description: "牆類型名稱（選填）",
                    },
                },
                required: ["startX", "startY", "endX", "endY"],
            },
        },

        // 2. 查詢專案資訊
        {
            name: "get_project_info",
            description: "取得目前開啟的 Revit 專案基本資訊，包括專案名稱、建築物名稱、業主等。",
            inputSchema: {
                type: "object",
                properties: {},
            },
        },

        // 3. 查詢元素
        {
            name: "query_elements",
            description: "查詢 Revit 專案中的元素。可依類別、族群、類型等條件篩選。",
            inputSchema: {
                type: "object",
                properties: {
                    category: {
                        type: "string",
                        description: "元素類別（如：牆、門、窗等）",
                    },
                    family: {
                        type: "string",
                        description: "族群名稱（選填）",
                    },
                    type: {
                        type: "string",
                        description: "類型名稱（選填）",
                    },
                    level: {
                        type: "string",
                        description: "樓層名稱（選填）",
                    },
                },
            },
        },

        // 4. 建立樓板
        {
            name: "create_floor",
            description: "在 Revit 中建立樓板。需要指定矩形範圍的四個角點座標。",
            inputSchema: {
                type: "object",
                properties: {
                    points: {
                        type: "array",
                        description: "樓板邊界點陣列，每個點包含 x, y 座標（公釐）",
                        items: {
                            type: "object",
                            properties: {
                                x: { type: "number" },
                                y: { type: "number" },
                            },
                        },
                    },
                    levelName: {
                        type: "string",
                        description: "樓層名稱",
                        default: "Level 1",
                    },
                    floorType: {
                        type: "string",
                        description: "樓板類型名稱（選填）",
                    },
                },
                required: ["points"],
            },
        },

        // 5. 刪除元素
        {
            name: "delete_element",
            description: "依 Element ID 刪除 Revit 元素。",
            inputSchema: {
                type: "object",
                properties: {
                    elementId: {
                        type: "number",
                        description: "要刪除的元素 ID",
                    },
                },
                required: ["elementId"],
            },
        },

        // 6. 取得元素資訊
        {
            name: "get_element_info",
            description: "取得指定元素的詳細資訊，包括參數、幾何資訊等。",
            inputSchema: {
                type: "object",
                properties: {
                    elementId: {
                        type: "number",
                        description: "元素 ID",
                    },
                },
                required: ["elementId"],
            },
        },

        // 7. 修改元素參數
        {
            name: "modify_element_parameter",
            description: "修改 Revit 元素的參數值。",
            inputSchema: {
                type: "object",
                properties: {
                    elementId: {
                        type: "number",
                        description: "元素 ID",
                    },
                    parameterName: {
                        type: "string",
                        description: "參數名稱",
                    },
                    value: {
                        type: "string",
                        description: "新的參數值",
                    },
                },
                required: ["elementId", "parameterName", "value"],
            },
        },

        // 8. 取得所有樓層
        {
            name: "get_all_levels",
            description: "取得專案中所有樓層的清單，包括樓層名稱和標高。",
            inputSchema: {
                type: "object",
                properties: {},
            },
        },

        // 9. 建立門
        {
            name: "create_door",
            description: "在指定的牆上建立門。",
            inputSchema: {
                type: "object",
                properties: {
                    wallId: {
                        type: "number",
                        description: "要放置門的牆 ID",
                    },
                    locationX: {
                        type: "number",
                        description: "門在牆上的位置 X 座標（公釐）",
                    },
                    locationY: {
                        type: "number",
                        description: "門在牆上的位置 Y 座標（公釐）",
                    },
                    doorType: {
                        type: "string",
                        description: "門類型名稱（選填）",
                    },
                },
                required: ["wallId", "locationX", "locationY"],
            },
        },

        // 10. 建立窗
        {
            name: "create_window",
            description: "在指定的牆上建立窗。",
            inputSchema: {
                type: "object",
                properties: {
                    wallId: {
                        type: "number",
                        description: "要放置窗的牆 ID",
                    },
                    locationX: {
                        type: "number",
                        description: "窗在牆上的位置 X 座標（公釐）",
                    },
                    locationY: {
                        type: "number",
                        description: "窗在牆上的位置 Y 座標（公釐）",
                    },
                    windowType: {
                        type: "string",
                        description: "窗類型名稱（選填）",
                    },
                },
                required: ["wallId", "locationX", "locationY"],
            },
        },

        // 11. 取得所有網格線
        {
            name: "get_all_grids",
            description: "取得專案中所有網格線（Grid）的資訊，包含名稱、方向、起點和終點座標。可用於計算網格交會點。",
            inputSchema: {
                type: "object",
                properties: {},
            },
        },

        // 12. 取得柱類型
        {
            name: "get_column_types",
            description: "取得專案中所有可用的柱類型，包含名稱、尺寸和族群資訊。",
            inputSchema: {
                type: "object",
                properties: {
                    material: {
                        type: "string",
                        description: "篩選材質（如：混凝土、鋼），選填",
                    },
                },
            },
        },

        // 13. 建立柱子
        {
            name: "create_column",
            description: "在指定位置建立柱子。需要指定座標和底部樓層。",
            inputSchema: {
                type: "object",
                properties: {
                    x: {
                        type: "number",
                        description: "柱子位置 X 座標（公釐）",
                    },
                    y: {
                        type: "number",
                        description: "柱子位置 Y 座標（公釐）",
                    },
                    bottomLevel: {
                        type: "string",
                        description: "底部樓層名稱",
                        default: "Level 1",
                    },
                    topLevel: {
                        type: "string",
                        description: "頂部樓層名稱（選填，如不指定則使用非約束高度）",
                    },
                    columnType: {
                        type: "string",
                        description: "柱類型名稱（選填，如不指定則使用預設類型）",
                    },
                },
                required: ["x", "y"],
            },
        },

        // 14. 取得家具類型
        {
            name: "get_furniture_types",
            description: "取得專案中已載入的家具類型清單，包含名稱和族群資訊。",
            inputSchema: {
                type: "object",
                properties: {
                    category: {
                        type: "string",
                        description: "家具類別篩選（如：椅子、桌子、床），選填",
                    },
                },
            },
        },

        // 15. 放置家具
        {
            name: "place_furniture",
            description: "在指定位置放置家具實例。",
            inputSchema: {
                type: "object",
                properties: {
                    x: {
                        type: "number",
                        description: "X 座標（公釐）",
                    },
                    y: {
                        type: "number",
                        description: "Y 座標（公釐）",
                    },
                    furnitureType: {
                        type: "string",
                        description: "家具類型名稱（需與 get_furniture_types 回傳的名稱一致）",
                    },
                    level: {
                        type: "string",
                        description: "樓層名稱",
                        default: "Level 1",
                    },
                    rotation: {
                        type: "number",
                        description: "旋轉角度（度），預設 0",
                        default: 0,
                    },
                },
                required: ["x", "y", "furnitureType"],
            },
        },

        // 16. 取得房間資訊
        {
            name: "get_room_info",
            description: "取得房間詳細資訊，包含中心點座標和邊界範圍。可用於智慧放置家具。",
            inputSchema: {
                type: "object",
                properties: {
                    roomId: {
                        type: "number",
                        description: "房間 Element ID（選填，如果知道的話）",
                    },
                    roomName: {
                        type: "string",
                        description: "房間名稱（選填，用於搜尋）",
                    },
                },
            },
        },

        // 17. 取得樓層房間清單
        {
            name: "get_rooms_by_level",
            description: "取得指定樓層的所有房間清單，包含名稱、編號、面積、用途等資訊。可用於容積檢討。",
            inputSchema: {
                type: "object",
                properties: {
                    level: {
                        type: "string",
                        description: "樓層名稱（如：1F、Level 1）",
                    },
                    includeUnnamed: {
                        type: "boolean",
                        description: "是否包含未命名的房間，預設 true",
                        default: true,
                    },
                },
                required: ["level"],
            },
        },
    ];
}

/**
 * 執行 Revit 工具
 */
export async function executeRevitTool(
    toolName: string,
    args: Record<string, any>,
    client: RevitSocketClient
): Promise<any> {
    // 將工具名稱轉換為 Revit 命令名稱
    const commandName = toolName;

    // 發送命令到 Revit
    const response = await client.sendCommand(commandName, args);

    if (!response.success) {
        throw new Error(response.error || "命令執行失敗");
    }

    return response.data;
}
