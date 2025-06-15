using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ADManagerAPI.Models;

namespace ADManagerAPI.Services
{
    public partial class SpreadsheetImportService
    {
        #region Utilitaires et transformations

        private Dictionary<string, string> MapRow(Dictionary<string, string> row, ImportConfig config)
        {
            if (row == null) 
                return new Dictionary<string, string>();
                
            var result = new Dictionary<string, string>();

            // Appliquer les templates de configuration pour chaque attribut demandé
            foreach (var mapping in config.HeaderMapping ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(mapping.Value))
                    continue;
                    
                string val = ApplyTemplateOptimized(mapping.Value, row);
                if (!string.IsNullOrWhiteSpace(val))
                {
                    result[mapping.Key] = NormalizeAdAttribute(mapping.Key, val);
                }
            }

            // Valider et auto-compléter les attributs obligatoires manquants
            ValidateRequiredAttributes(result);
            
            return result;
        }

        private string ApplyTemplateOptimized(string template, Dictionary<string, string> row)
        {
            if (string.IsNullOrWhiteSpace(template))
                return string.Empty;

            if (!template.Contains("%"))
                return template;

            // Utiliser le cache pour les regex et tokens du template
            if (!_templateTokenCache.TryGetValue(template, out var tokens))
            {
                tokens = ParseTemplateTokens(template);
                _templateTokenCache[template] = tokens;
            }

            if (tokens.Count == 0)
                return template;

            var result = new StringBuilder(template);
            // Remplacer en sens inverse pour éviter les décalages d'index
            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                var token = tokens[i];
                string value = GetTokenValue(token, row);
                result.Replace(token.FullMatch, value);
            }

            return result.ToString();
        }

        private List<TemplateToken> ParseTemplateTokens(string template)
        {
            if (!_templateRegexCache.TryGetValue(template, out var regex))
            {
                regex = new Regex(@"%([^:%]+)(?::([^%]+))?%", RegexOptions.Compiled);
                _templateRegexCache[template] = regex;
            }

            var matches = regex.Matches(template);
            var result = new List<TemplateToken>();
            
            foreach (Match match in matches)
            {
                if (match.Success)
                {
                    result.Add(new TemplateToken(
                        match.Value,
                        match.Groups[1].Value,
                        match.Groups.Count > 2 && match.Groups[2].Success ? match.Groups[2].Value : null
                    ));
                }
            }
            
            return result;
        }

        private string GetTokenValue(TemplateToken token, Dictionary<string, string> row)
        {
            string result = string.Empty;
            
            // Chercher la colonne correspondante (insensible à la casse)
            var columnKey = row.Keys.FirstOrDefault(k => 
                string.Equals(k, token.ColumnName, StringComparison.OrdinalIgnoreCase));
                
            if (columnKey != null && row.TryGetValue(columnKey, out var value))
            {
                result = value;
            }
            
            // Appliquer le modificateur si présent
            if (!string.IsNullOrEmpty(token.Modifier))
            {
                result = ApplyModifier(result, token.Modifier);
            }
            
            return result;
        }

        private string ApplyModifier(string value, string? modifier)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(modifier))
                return value;

            return modifier.ToLowerInvariant() switch
            {
                "lowercase" => value.ToLowerInvariant(),
                "uppercase" => value.ToUpperInvariant(),
                "capitalize" => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLowerInvariant()),
                "trim" => value.Trim(),
                "username" => NormalizeSamAccountName(value),
                "camelcase" => string.Join("", value.Split(' ')
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select((s, i) => i == 0 
                        ? s.ToLowerInvariant() 
                        : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant())),
                "pascalcase" => string.Join("", value.Split(' ')
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant())),
                "first" => !string.IsNullOrEmpty(value) ? value[0].ToString() : "",
                "firstchar" => !string.IsNullOrEmpty(value) ? value[0].ToString() : "",
                "firstcharlower" => !string.IsNullOrEmpty(value) ? char.ToLowerInvariant(value[0]).ToString() : "",
                "firstcharupper" => !string.IsNullOrEmpty(value) ? char.ToUpperInvariant(value[0]).ToString() : "",
                _ => value
            };
        }

        private void ValidateRequiredAttributes(Dictionary<string, string> result)
        {
            var requiredAttributes = new List<string> { "givenName", "sn", "sAMAccountName" };
            var missingAttributes = requiredAttributes.Where(attr => !result.ContainsKey(attr) || string.IsNullOrWhiteSpace(result[attr])).ToList();
            
            if (missingAttributes.Count > 0)
            {
                AutoCompleteAttributes(result, missingAttributes);
            }
            
            // Vérifier d'autres attributs potentiellement requis
            if (!result.ContainsKey("displayName") || string.IsNullOrWhiteSpace(result["displayName"]))
            {
                string firstName = result.ContainsKey("givenName") ? result["givenName"] : "";
                string lastName = result.ContainsKey("sn") ? result["sn"] : "";
                result["displayName"] = $"{firstName} {lastName}".Trim();
            }
            
            if (!result.ContainsKey("userPrincipalName") || string.IsNullOrWhiteSpace(result["userPrincipalName"]))
            {
                if (result.ContainsKey("sAMAccountName") && !string.IsNullOrWhiteSpace(result["sAMAccountName"]) &&
                    result.ContainsKey("mail") && !string.IsNullOrWhiteSpace(result["mail"]))
                {
                    result["userPrincipalName"] = result["mail"];
                }
                else if (result.ContainsKey("sAMAccountName"))
                {
                    result["userPrincipalName"] = $"{result["sAMAccountName"]}@domain.local";
                }
            }
        }

        private void AutoCompleteAttributes(Dictionary<string, string> result, List<string> missingAttributes)
        {
            // Si le prénom est manquant
            if (missingAttributes.Contains("givenName"))
            {
                if (result.ContainsKey("displayName") && !string.IsNullOrWhiteSpace(result["displayName"]))
                {
                    var parts = result["displayName"].Split(' ');
                    if (parts.Length > 0)
                    {
                        result["givenName"] = parts[0];
                    }
                }
            }
            
            // Si le nom est manquant
            if (missingAttributes.Contains("sn"))
            {
                if (result.ContainsKey("displayName") && !string.IsNullOrWhiteSpace(result["displayName"]))
                {
                    var parts = result["displayName"].Split(' ');
                    if (parts.Length > 1)
                    {
                        result["sn"] = parts[^1]; // Dernier élément du tableau
                    }
                    else
                    {
                        result["sn"] = "Utilisateur"; // Valeur par défaut
                    }
                }
                else
                {
                    result["sn"] = "Utilisateur"; // Valeur par défaut
                }
            }
            
            // Si le samAccountName est manquant
            if (missingAttributes.Contains("sAMAccountName"))
            {
                string firstName = result.ContainsKey("givenName") ? result["givenName"] : "";
                string lastName = result.ContainsKey("sn") ? result["sn"] : "";
                
                if (!string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(lastName))
                {
                    result["sAMAccountName"] = NormalizeSamAccountName($"{firstName}.{lastName}");
                }
                else if (!string.IsNullOrWhiteSpace(firstName))
                {
                    result["sAMAccountName"] = NormalizeSamAccountName(firstName);
                }
                else if (!string.IsNullOrWhiteSpace(lastName))
                {
                    result["sAMAccountName"] = NormalizeSamAccountName(lastName);
                }
            }
        }

        private string NormalizeAdAttribute(string attributeName, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;
                
            return attributeName.ToLowerInvariant() switch
            {
                "samaccountname" or "sAMAccountName" => NormalizeSamAccountName(value),
                "displayname" or "displayName" => NormalizeDisplayName(value),
                "givenname" or "givenName" => NormalizeGivenName(value),
                "mail" or "email" => NormalizeEmail(value),
                _ => value.Trim()
            };
        }

        private string NormalizeSamAccountName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;
                
            string normalized = RemoveDiacritics(value.ToLowerInvariant())
                .Trim()
                .Replace(" ", ".")
                .Replace("'", ".")
                .Replace("-", ".")
                .Replace("_", ".")
                .Replace("@", ".")
                .Replace("#", "")
                .Replace("&", "")
                .Replace("(", "")
                .Replace(")", "")
                .Replace("[", "")
                .Replace("]", "")
                .Replace("{", "")
                .Replace("}", "")
                .Replace("$", "")
                .Replace("*", "")
                .Replace("+", "")
                .Replace("/", "")
                .Replace("\\", "");

            // Remplacer les séquences multiples de points par un seul point
            normalized = Regex.Replace(normalized, "\\.+", ".");
            
            // Supprimer les points au début et à la fin
            normalized = normalized.Trim('.');
            
            // Limiter la longueur à 20 caractères (limitations de SAM dans Active Directory)
            if (normalized.Length > 20)
            {
                normalized = normalized.Substring(0, 20);
            }
            
            return normalized;
        }

        private string NormalizeDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;
                
            return CultureInfo.CurrentCulture.TextInfo
                .ToTitleCase(value.ToLowerInvariant().Trim());
        }

        private string NormalizeGivenName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;
                
            string normalized = value.Trim();
            
            if (normalized.Length > 2)
            {
                normalized = char.ToUpperInvariant(normalized[0]) + normalized.Substring(1).ToLowerInvariant();
            }
            
            return normalized;
        }

        private string NormalizeEmail(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;
                
            string normalized = value
                .ToLowerInvariant()
                .Replace(" ", "")
                .Trim();
                
            if (!normalized.Contains("@"))
            {
                normalized += "@domain.local";
            }
            
            return normalized;
        }

        private string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;
                
            string normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();

            foreach (char c in normalizedString)
            {
                UnicodeCategory unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
        }

        #endregion
    }
}