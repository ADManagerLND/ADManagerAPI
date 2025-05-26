using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Utilities
{
    public static class ActionItemConverter
    {
        public static List<LegacyImportActionItem> ToActionItems(this List<ImportAction> importActions)
        {
            if (importActions == null) return new List<LegacyImportActionItem>();
            
            return importActions.Select(action => new LegacyImportActionItem
            {
                RowIndex = 0, // Valeur par défaut
                ActionType = action.ActionType.ToString(),
                Data = new Dictionary<string, string>
                {
                    ["objectName"] = action.ObjectName,
                    ["path"] = action.Path,
                    ["message"] = action.Message
                },
                IsValid = true
            }).ToList();
        }
        
        public static List<ImportAction> ToImportActions(this List<LegacyImportActionItem> actionItems)
        {
            var result = new List<ImportAction>();
            
            foreach (var item in actionItems)
            {
                if (!item.IsValid) continue;
                
                ActionType actionType;
                if (!System.Enum.TryParse(item.ActionType, out actionType))
                {
                    actionType = ActionType.ERROR;
                }
                
                result.Add(new ImportAction
                {
                    ActionType = actionType,
                    ObjectName = item.Data.ContainsKey("objectName") ? item.Data["objectName"] : "",
                    Path = item.Data.ContainsKey("path") ? item.Data["path"] : "",
                    Message = item.Data.ContainsKey("message") ? item.Data["message"] : "",
                    Attributes = item.Data
                });
            }
            
            return result;
        }
    }
} 