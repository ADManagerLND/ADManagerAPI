using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Utilities
{
    public static class ImportConfigHelpers
    {
        public static ImportConfig EnsureValidConfig(ImportConfig config, ILogger? logger = null)
        {
            if (config == null)
            {
                logger?.LogWarning("La configuration d'import est null, création d'une configuration par défaut");
                config = new ImportConfig
                {
                    DefaultOU = "DC=domain,DC=local",
                    ManualColumns = new List<string>(),
                    HeaderMapping = new Dictionary<string, string>(),
                    CsvDelimiter = ';'
                };
            }

            if (config.ManualColumns == null)
            {
                logger?.LogWarning("config.ManualColumns est null, initialisation d'une liste vide");
                config.ManualColumns = new List<string>();
            }

            if (config.HeaderMapping == null)
            {
                logger?.LogWarning("config.HeaderMapping est null, initialisation d'un dictionnaire vide");
                config.HeaderMapping = new Dictionary<string, string>();
            }
            
            if (config.ClassGroupFolderCreationConfig == null)
            {
                logger?.LogWarning("config.ClassGroupFolderCreationConfig est null, initialisation avec des valeurs par défaut implicites.");
                config.ClassGroupFolderCreationConfig = new ClassGroupFolderCreationConfig();
            }

            if (config.TeamGroupCreationConfig == null)
            {
                logger?.LogWarning("config.TeamGroupCreationConfig est null, initialisation avec des valeurs par défaut implicites.");
                config.TeamGroupCreationConfig = new TeamGroupCreationConfig();
            }

            return config;
        }
    }
} 