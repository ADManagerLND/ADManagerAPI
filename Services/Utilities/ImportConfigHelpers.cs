using ADManagerAPI.Models;
using Microsoft.Extensions.Logging;

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
                    CsvDelimiter = ";"
                };
            }

            // S'assurer que ManualColumns n'est jamais null
            if (config.ManualColumns == null)
            {
                logger?.LogWarning("config.ManualColumns est null, initialisation d'une liste vide");
                config.ManualColumns = new List<string>();
            }

            // S'assurer que HeaderMapping n'est jamais null
            if (config.HeaderMapping == null)
            {
                logger?.LogWarning("config.HeaderMapping est null, initialisation d'un dictionnaire vide");
                config.HeaderMapping = new Dictionary<string, string>();
            }

            return config;
        }
    }
} 