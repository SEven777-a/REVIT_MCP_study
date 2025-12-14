using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Models;

namespace RevitMCP.Core
{
    /// <summary>
    /// 命令執行器 - 執行各種 Revit 操作
    /// </summary>
    public class CommandExecutor
    {
        private readonly UIApplication _uiApp;

        public CommandExecutor(UIApplication uiApp)
        {
            _uiApp = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
        }

        /// <summary>
        /// 執行命令
        /// </summary>
        public RevitCommandResponse ExecuteCommand(RevitCommandRequest request)
        {
            try
            {
                var parameters = request.Parameters as JObject ?? new JObject();
                object result = null;

                switch (request.CommandName.ToLower())
                {
                    case "create_wall":
                        result = CreateWall(parameters);
                        break;
                    
                    case "get_project_info":
                        result = GetProjectInfo();
                        break;
                    
                    case "query_elements":
                        result = QueryElements(parameters);
                        break;
                    
                    case "get_all_levels":
                        result = GetAllLevels();
                        break;
                    
                    case "get_element_info":
                        result = GetElementInfo(parameters);
                        break;
                    
                    case "delete_element":
                        result = DeleteElement(parameters);
                        break;
                    
                    case "get_all_grids":
                        result = GetAllGrids();
                        break;
                    
                    case "get_column_types":
                        result = GetColumnTypes(parameters);
                        break;
                    
                    case "create_column":
                        result = CreateColumn(parameters);
                        break;
                    
                    case "get_furniture_types":
                        result = GetFurnitureTypes(parameters);
                        break;
                    
                    case "place_furniture":
                        result = PlaceFurniture(parameters);
                        break;
                    
                    case "get_room_info":
                        result = GetRoomInfo(parameters);
                        break;
                    
                    case "get_rooms_by_level":
                        result = GetRoomsByLevel(parameters);
                        break;
                    
                    default:
                        throw new NotImplementedException($"未實作的命令: {request.CommandName}");
                }

                return new RevitCommandResponse
                {
                    Success = true,
                    Data = result,
                    RequestId = request.RequestId
                };
            }
            catch (Exception ex)
            {
                return new RevitCommandResponse
                {
                    Success = false,
                    Error = ex.Message,
                    RequestId = request.RequestId
                };
            }
        }

        #region 命令實作

        /// <summary>
        /// 建立牆
        /// </summary>
        private object CreateWall(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            double startX = parameters["startX"]?.Value<double>() ?? 0;
            double startY = parameters["startY"]?.Value<double>() ?? 0;
            double endX = parameters["endX"]?.Value<double>() ?? 0;
            double endY = parameters["endY"]?.Value<double>() ?? 0;
            double height = parameters["height"]?.Value<double>() ?? 3000;

            // 轉換為英尺 (Revit 內部單位)
            XYZ start = new XYZ(startX / 304.8, startY / 304.8, 0);
            XYZ end = new XYZ(endX / 304.8, endY / 304.8, 0);

            using (Transaction trans = new Transaction(doc, "建立牆"))
            {
                trans.Start();

                // 建立線
                Line line = Line.CreateBound(start, end);

                // 取得預設樓層
                Level level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault();

                if (level == null)
                {
                    throw new Exception("找不到樓層");
                }

                // 建立牆
                Wall wall = Wall.Create(doc, line, level.Id, false);
                
                // 設定高度
                Parameter heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (heightParam != null && !heightParam.IsReadOnly)
                {
                    heightParam.Set(height / 304.8);
                }

                trans.Commit();

                return new
                {
                    ElementId = wall.Id.IntegerValue,
                    Message = $"成功建立牆，ID: {wall.Id.IntegerValue}"
                };
            }
        }

        /// <summary>
        /// 取得專案資訊
        /// </summary>
        private object GetProjectInfo()
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            ProjectInfo projInfo = doc.ProjectInformation;

            return new
            {
                ProjectName = doc.Title,
                BuildingName = projInfo.BuildingName,
                OrganizationName = projInfo.OrganizationName,
                Author = projInfo.Author,
                Address = projInfo.Address,
                ClientName = projInfo.ClientName,
                ProjectNumber = projInfo.Number,
                ProjectStatus = projInfo.Status
            };
        }

        /// <summary>
        /// 查詢元素
        /// </summary>
        private object QueryElements(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string category = parameters["category"]?.Value<string>();

            var collector = new FilteredElementCollector(doc);
            
            if (!string.IsNullOrEmpty(category))
            {
                // 依類別篩選
                BuiltInCategory builtInCategory;
                if (Enum.TryParse($"OST_{category}", true, out builtInCategory))
                {
                    collector.OfCategory(builtInCategory);
                }
            }

            var elements = collector
                .WhereElementIsNotElementType()
                .ToElements()
                .Take(100) // 限制回傳數量
                .Select(e => new
                {
                    ElementId = e.Id.IntegerValue,
                    Name = e.Name,
                    Category = e.Category?.Name,
                    LevelName = doc.GetElement(e.LevelId)?.Name
                })
                .ToList();

            return new
            {
                Count = elements.Count,
                Elements = elements
            };
        }

        /// <summary>
        /// 取得所有樓層
        /// </summary>
        private object GetAllLevels()
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new
                {
                    ElementId = l.Id.IntegerValue,
                    Name = l.Name,
                    Elevation = Math.Round(l.Elevation * 304.8, 2) // 轉換為公釐
                })
                .ToList();

            return new
            {
                Count = levels.Count,
                Levels = levels
            };
        }

        /// <summary>
        /// 取得元素資訊
        /// </summary>
        private object GetElementInfo(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int elementId = parameters["elementId"]?.Value<int>() ?? 0;

            Element element = doc.GetElement(new ElementId(elementId));
            if (element == null)
            {
                throw new Exception($"找不到元素 ID: {elementId}");
            }

            var parameterList = new List<object>();
            foreach (Parameter param in element.Parameters)
            {
                if (param.HasValue)
                {
                    parameterList.Add(new
                    {
                        Name = param.Definition.Name,
                        Value = param.AsValueString() ?? param.AsString(),
                        Type = param.StorageType.ToString()
                    });
                }
            }

            return new
            {
                ElementId = element.Id.IntegerValue,
                Name = element.Name,
                Category = element.Category?.Name,
                Type = doc.GetElement(element.GetTypeId())?.Name,
                Level = doc.GetElement(element.LevelId)?.Name,
                Parameters = parameterList
            };
        }

        /// <summary>
        /// 刪除元素
        /// </summary>
        private object DeleteElement(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int elementId = parameters["elementId"]?.Value<int>() ?? 0;

            using (Transaction trans = new Transaction(doc, "刪除元素"))
            {
                trans.Start();

                Element element = doc.GetElement(new ElementId(elementId));
                if (element == null)
                {
                    throw new Exception($"找不到元素 ID: {elementId}");
                }

                doc.Delete(new ElementId(elementId));
                trans.Commit();

                return new
                {
                    Message = $"成功刪除元素 ID: {elementId}"
                };
            }
        }

        /// <summary>
        /// 取得所有網格線
        /// </summary>
        private object GetAllGrids()
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            var grids = new FilteredElementCollector(doc)
                .OfClass(typeof(Grid))
                .Cast<Grid>()
                .Select(g =>
                {
                    // 取得 Grid 的曲線（通常是直線）
                    Curve curve = g.Curve;
                    XYZ startPoint = curve.GetEndPoint(0);
                    XYZ endPoint = curve.GetEndPoint(1);

                    // 判斷方向（水平或垂直）
                    double dx = Math.Abs(endPoint.X - startPoint.X);
                    double dy = Math.Abs(endPoint.Y - startPoint.Y);
                    string direction = dx > dy ? "水平" : "垂直";

                    return new
                    {
                        ElementId = g.Id.IntegerValue,
                        Name = g.Name,
                        Direction = direction,
                        StartX = Math.Round(startPoint.X * 304.8, 2),  // 英尺 → 公釐
                        StartY = Math.Round(startPoint.Y * 304.8, 2),
                        EndX = Math.Round(endPoint.X * 304.8, 2),
                        EndY = Math.Round(endPoint.Y * 304.8, 2)
                    };
                })
                .OrderBy(g => g.Name)
                .ToList();

            return new
            {
                Count = grids.Count,
                Grids = grids
            };
        }

        /// <summary>
        /// 取得柱類型
        /// </summary>
        private object GetColumnTypes(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string materialFilter = parameters["material"]?.Value<string>();

            // 查詢結構柱和建築柱的 FamilySymbol
            var columnTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(fs => fs.Category != null && 
                    (fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Columns ||
                     fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_StructuralColumns))
                .Select(fs =>
                {
                    // 嘗試取得尺寸參數
                    double width = 0, depth = 0;
                    
                    // 常見的柱尺寸參數名稱
                    Parameter widthParam = fs.LookupParameter("寬度") ?? 
                                          fs.LookupParameter("Width") ?? 
                                          fs.LookupParameter("b");
                    Parameter depthParam = fs.LookupParameter("深度") ?? 
                                          fs.LookupParameter("Depth") ?? 
                                          fs.LookupParameter("h");
                    
                    if (widthParam != null && widthParam.HasValue)
                        width = Math.Round(widthParam.AsDouble() * 304.8, 0);  // 轉公釐
                    if (depthParam != null && depthParam.HasValue)
                        depth = Math.Round(depthParam.AsDouble() * 304.8, 0);

                    return new
                    {
                        ElementId = fs.Id.IntegerValue,
                        TypeName = fs.Name,
                        FamilyName = fs.FamilyName,
                        Category = fs.Category?.Name,
                        Width = width,
                        Depth = depth,
                        SizeDescription = width > 0 && depth > 0 ? $"{width}x{depth}" : "未知尺寸"
                    };
                })
                .Where(ct => string.IsNullOrEmpty(materialFilter) || 
                             ct.FamilyName.Contains(materialFilter) || 
                             ct.TypeName.Contains(materialFilter))
                .OrderBy(ct => ct.FamilyName)
                .ThenBy(ct => ct.TypeName)
                .ToList();

            return new
            {
                Count = columnTypes.Count,
                ColumnTypes = columnTypes
            };
        }

        /// <summary>
        /// 建立柱子
        /// </summary>
        private object CreateColumn(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            // 解析參數
            double x = parameters["x"]?.Value<double>() ?? 0;
            double y = parameters["y"]?.Value<double>() ?? 0;
            string bottomLevelName = parameters["bottomLevel"]?.Value<string>() ?? "Level 1";
            string topLevelName = parameters["topLevel"]?.Value<string>();
            string columnTypeName = parameters["columnType"]?.Value<string>();

            // 轉換座標（公釐 → 英尺）
            XYZ location = new XYZ(x / 304.8, y / 304.8, 0);

            using (Transaction trans = new Transaction(doc, "建立柱子"))
            {
                trans.Start();

                // 取得底部樓層
                Level bottomLevel = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name == bottomLevelName);

                if (bottomLevel == null)
                {
                    // 如果找不到指定名稱，嘗試取得第一個樓層
                    bottomLevel = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .FirstOrDefault();
                }

                if (bottomLevel == null)
                {
                    throw new Exception($"找不到樓層: {bottomLevelName}");
                }

                // 取得柱類型（FamilySymbol）
                FamilySymbol columnSymbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Category != null &&
                        (fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Columns ||
                         fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_StructuralColumns))
                    .FirstOrDefault(fs => string.IsNullOrEmpty(columnTypeName) || 
                                          fs.Name == columnTypeName ||
                                          fs.FamilyName.Contains(columnTypeName));

                if (columnSymbol == null)
                {
                    throw new Exception(string.IsNullOrEmpty(columnTypeName) 
                        ? "專案中沒有可用的柱類型" 
                        : $"找不到柱類型: {columnTypeName}");
                }

                // 確保 FamilySymbol 已啟用
                if (!columnSymbol.IsActive)
                {
                    columnSymbol.Activate();
                    doc.Regenerate();
                }

                // 建立柱子
                FamilyInstance column = doc.Create.NewFamilyInstance(
                    location,
                    columnSymbol,
                    bottomLevel,
                    Autodesk.Revit.DB.Structure.StructuralType.Column
                );

                // 設定頂部樓層（如果有指定）
                if (!string.IsNullOrEmpty(topLevelName))
                {
                    Level topLevel = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l => l.Name == topLevelName);

                    if (topLevel != null)
                    {
                        Parameter topLevelParam = column.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                        if (topLevelParam != null && !topLevelParam.IsReadOnly)
                        {
                            topLevelParam.Set(topLevel.Id);
                        }
                    }
                }

                trans.Commit();

                return new
                {
                    ElementId = column.Id.IntegerValue,
                    ColumnType = columnSymbol.Name,
                    FamilyName = columnSymbol.FamilyName,
                    Level = bottomLevel.Name,
                    LocationX = x,
                    LocationY = y,
                    Message = $"成功建立柱子，ID: {column.Id.IntegerValue}"
                };
            }
        }

        /// <summary>
        /// 取得家具類型
        /// </summary>
        private object GetFurnitureTypes(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string categoryFilter = parameters["category"]?.Value<string>();

            var furnitureTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_Furniture)
                .Cast<FamilySymbol>()
                .Select(fs => new
                {
                    ElementId = fs.Id.IntegerValue,
                    TypeName = fs.Name,
                    FamilyName = fs.FamilyName,
                    IsActive = fs.IsActive
                })
                .Where(ft => string.IsNullOrEmpty(categoryFilter) ||
                             ft.FamilyName.Contains(categoryFilter) ||
                             ft.TypeName.Contains(categoryFilter))
                .OrderBy(ft => ft.FamilyName)
                .ThenBy(ft => ft.TypeName)
                .ToList();

            return new
            {
                Count = furnitureTypes.Count,
                FurnitureTypes = furnitureTypes
            };
        }

        /// <summary>
        /// 放置家具
        /// </summary>
        private object PlaceFurniture(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            double x = parameters["x"]?.Value<double>() ?? 0;
            double y = parameters["y"]?.Value<double>() ?? 0;
            string furnitureTypeName = parameters["furnitureType"]?.Value<string>();
            string levelName = parameters["level"]?.Value<string>() ?? "Level 1";
            double rotation = parameters["rotation"]?.Value<double>() ?? 0;

            // 轉換座標（公釐 → 英尺）
            XYZ location = new XYZ(x / 304.8, y / 304.8, 0);

            using (Transaction trans = new Transaction(doc, "放置家具"))
            {
                trans.Start();

                // 取得樓層
                Level level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name == levelName);

                if (level == null)
                {
                    level = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .FirstOrDefault();
                }

                if (level == null)
                {
                    throw new Exception($"找不到樓層: {levelName}");
                }

                // 取得家具類型
                FamilySymbol furnitureSymbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_Furniture)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Name == furnitureTypeName ||
                                          fs.FamilyName.Contains(furnitureTypeName));

                if (furnitureSymbol == null)
                {
                    throw new Exception($"找不到家具類型: {furnitureTypeName}");
                }

                // 確保 FamilySymbol 已啟用
                if (!furnitureSymbol.IsActive)
                {
                    furnitureSymbol.Activate();
                    doc.Regenerate();
                }

                // 放置家具
                FamilyInstance furniture = doc.Create.NewFamilyInstance(
                    location,
                    furnitureSymbol,
                    level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                );

                // 旋轉
                if (Math.Abs(rotation) > 0.001)
                {
                    Line axis = Line.CreateBound(location, location + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, furniture.Id, axis, rotation * Math.PI / 180);
                }

                trans.Commit();

                return new
                {
                    ElementId = furniture.Id.IntegerValue,
                    FurnitureType = furnitureSymbol.Name,
                    FamilyName = furnitureSymbol.FamilyName,
                    Level = level.Name,
                    LocationX = x,
                    LocationY = y,
                    Rotation = rotation,
                    Message = $"成功放置家具，ID: {furniture.Id.IntegerValue}"
                };
            }
        }

        /// <summary>
        /// 取得房間資訊
        /// </summary>
        private object GetRoomInfo(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            int? roomId = parameters["roomId"]?.Value<int>();
            string roomName = parameters["roomName"]?.Value<string>();

            Room room = null;

            if (roomId.HasValue)
            {
                room = doc.GetElement(new ElementId(roomId.Value)) as Room;
            }
            else if (!string.IsNullOrEmpty(roomName))
            {
                room = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .FirstOrDefault(r => r.Name.Contains(roomName) || 
                                         r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()?.Contains(roomName) == true);
            }

            if (room == null)
            {
                throw new Exception(roomId.HasValue 
                    ? $"找不到房間 ID: {roomId}" 
                    : $"找不到房間名稱包含: {roomName}");
            }

            // 取得房間位置點
            LocationPoint locPoint = room.Location as LocationPoint;
            XYZ center = locPoint?.Point ?? XYZ.Zero;

            // 取得 BoundingBox
            BoundingBoxXYZ bbox = room.get_BoundingBox(null);
            
            // 取得面積
            double area = room.Area * 0.092903; // 平方英尺 → 平方公尺

            return new
            {
                ElementId = room.Id.IntegerValue,
                Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                Number = room.Number,
                Level = doc.GetElement(room.LevelId)?.Name,
                Area = Math.Round(area, 2),
                CenterX = Math.Round(center.X * 304.8, 2),
                CenterY = Math.Round(center.Y * 304.8, 2),
                BoundingBox = bbox != null ? new
                {
                    MinX = Math.Round(bbox.Min.X * 304.8, 2),
                    MinY = Math.Round(bbox.Min.Y * 304.8, 2),
                    MaxX = Math.Round(bbox.Max.X * 304.8, 2),
                    MaxY = Math.Round(bbox.Max.Y * 304.8, 2)
                } : null
            };
        }

        /// <summary>
        /// 取得樓層房間清單
        /// </summary>
        private object GetRoomsByLevel(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string levelName = parameters["level"]?.Value<string>();
            bool includeUnnamed = parameters["includeUnnamed"]?.Value<bool>() ?? true;

            if (string.IsNullOrEmpty(levelName))
            {
                throw new Exception("請指定樓層名稱");
            }

            // 取得指定樓層
            Level targetLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name.Contains(levelName) || levelName.Contains(l.Name));

            if (targetLevel == null)
            {
                throw new Exception($"找不到樓層: {levelName}");
            }

            // 取得該樓層的所有房間
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.LevelId == targetLevel.Id)
                .Where(r => r.Area > 0) // 排除面積為 0 的房間（未封閉）
                .Select(r => 
                {
                    string roomName = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                    bool hasName = !string.IsNullOrEmpty(roomName) && roomName != "房間";
                    
                    // 取得房間中心點
                    LocationPoint locPoint = r.Location as LocationPoint;
                    XYZ center = locPoint?.Point ?? XYZ.Zero;
                    
                    // 取得面積（平方英尺 → 平方公尺）
                    double areaM2 = r.Area * 0.092903;
                    
                    return new
                    {
                        ElementId = r.Id.IntegerValue,
                        Name = roomName ?? "未命名",
                        Number = r.Number,
                        Area = Math.Round(areaM2, 2),
                        HasName = hasName,
                        CenterX = Math.Round(center.X * 304.8, 2),
                        CenterY = Math.Round(center.Y * 304.8, 2)
                    };
                })
                .Where(r => includeUnnamed || r.HasName)
                .OrderBy(r => r.Number)
                .ToList();

            // 計算統計
            double totalArea = rooms.Sum(r => r.Area);
            int roomsWithName = rooms.Count(r => r.HasName);
            int roomsWithoutName = rooms.Count(r => !r.HasName);

            return new
            {
                Level = targetLevel.Name,
                LevelId = targetLevel.Id.IntegerValue,
                TotalRooms = rooms.Count,
                TotalArea = Math.Round(totalArea, 2),
                RoomsWithName = roomsWithName,
                RoomsWithoutName = roomsWithoutName,
                DataCompleteness = rooms.Count > 0 
                    ? $"{Math.Round((double)roomsWithName / rooms.Count * 100, 1)}%" 
                    : "N/A",
                Rooms = rooms
            };
        }

        #endregion
    }
}
