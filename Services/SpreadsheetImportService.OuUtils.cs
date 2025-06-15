namespace ADManagerAPI.Services
{
    public partial class SpreadsheetImportService
    {
        /// <summary>
        /// Extrait le nom de l'OU à partir d'un chemin d'OU
        /// </summary>
        private string ExtractOuName(string ouPath)
        {
            if (string.IsNullOrEmpty(ouPath)) return string.Empty;
            string ouName = ouPath.Split(',')[0];
            if (ouName.StartsWith("OU="))
                ouName = ouName.Substring(3);
            return ouName.Trim();
        }

        /// <summary>
        /// Extrait le chemin d'OU à partir d'un DN complet
        /// </summary>
        private string ExtractOuFromDistinguishedName(string distinguishedName)
        {
            if (string.IsNullOrEmpty(distinguishedName)) return string.Empty;
            
            var parts = distinguishedName.Split(',');
            if (parts.Length <= 1) return string.Empty;
            
            return string.Join(",", parts.Skip(1));
        }

        /// <summary>
        /// Construit un chemin d'OU complet à partir d'une valeur CSV et d'une OU par défaut
        /// </summary>
        private string BuildOuPath(string ouValueFromCsv, string defaultOu)
        {
            _logger.LogTrace($"[OU_DEBUG_BUILD] BuildOuPath appelé avec ouValueFromCsv: '{ouValueFromCsv}', defaultOu: '{defaultOu}'");
            string cleanDefaultOu = defaultOu?.Trim();

            if (string.IsNullOrEmpty(ouValueFromCsv))
            {
                _logger.LogWarning("[OU_DEBUG_BUILD] ouValueFromCsv est vide. Retour de defaultOu uniquement.");
                return cleanDefaultOu;
            }

            bool isLikelyDn = ouValueFromCsv.Contains("DC=", StringComparison.OrdinalIgnoreCase) ||
                              (ouValueFromCsv.Contains("OU=", StringComparison.OrdinalIgnoreCase) && ouValueFromCsv.Contains(","));

            if (isLikelyDn)
            {
                _logger.LogDebug($"[OU_DEBUG_BUILD] ouValueFromCsv '{ouValueFromCsv}' semble être un DN. Extraction des composants OU et DC.");
                var components = ouValueFromCsv.Split(',')
                    .Select(s => s.Trim())
                    .Where(s => s.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || 
                                s.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (components.Any())
                {
                    string extractedOuPath = string.Join(",", components);
                    _logger.LogDebug($"[OU_DEBUG_BUILD] Chemin d'OU extrait du DN: '{extractedOuPath}'");
                    return extractedOuPath;
                }
                else
                {
                    _logger.LogWarning($"[OU_DEBUG_BUILD] Aucun composant OU ou DC trouvé dans le DN présumé '{ouValueFromCsv}'. Retour de defaultOu.");
                    return cleanDefaultOu;
                }
            }
            else
            {
                var ouParts = ouValueFromCsv.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => $"OU={part.Trim()}")
                    .Reverse(); 

                string formattedOuPathRelative = string.Join(",", ouParts);

                if (string.IsNullOrEmpty(formattedOuPathRelative))
                {
                    _logger.LogWarning($"[OU_DEBUG_BUILD] ouValueFromCsv '{ouValueFromCsv}' n'a pas produit de parties d'OU valides après parsing. Retour de defaultOu.");
                    return cleanDefaultOu;
                }

                if (string.IsNullOrEmpty(cleanDefaultOu))
                {
                    _logger.LogDebug($"[OU_DEBUG_BUILD] defaultOu est vide. Retour de l'OU formatée relative: '{formattedOuPathRelative}'");
                    return formattedOuPathRelative;
                }
                else
                {
                    string finalPath = $"{formattedOuPathRelative},{cleanDefaultOu}";
                    _logger.LogDebug($"[OU_DEBUG_BUILD] defaultOu n'est pas vide. Concaténation pour chemin final: '{finalPath}'");
                    return finalPath;
                }
            }
        }

        /// <summary>
        /// Extrait le DN parent à partir d'un chemin d'OU
        /// </summary>
        private string ExtractParentDnFromPath(string ouPath)
        {
            if (string.IsNullOrEmpty(ouPath)) return string.Empty;
            int firstComma = ouPath.IndexOf(',');
            if (firstComma == -1 || firstComma == ouPath.Length - 1) return "racine du domaine";
            return ouPath.Substring(firstComma + 1).Trim();
        }
    }
} 