using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ADManagerAPI.Models;

namespace ADManagerAPI.Services;

public partial class SpreadsheetImportService
{
    #region Utilitaires et transformations

    private Dictionary<string, string> MapRow(Dictionary<string, string> row, ImportConfig config)
    {
        if (row == null)
        {
            _logger?.LogWarning("⚠️ MapRow appelée avec une ligne null");
            return new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>();
        
        // Appliquer les templates de configuration pour chaque attribut demandé
        foreach (var mapping in config.HeaderMapping ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(mapping.Value))
            {
                continue;
            }

            var val = ApplyTemplateOptimized(mapping.Value, row);
            if (!string.IsNullOrWhiteSpace(val))
            {
                result[mapping.Key] = NormalizeAdAttribute(mapping.Key, val);
            }
        }

        // 🔧 CORRECTION CRITIQUE : Conserver la colonne OU si elle existe dans les données originales
        if (!string.IsNullOrEmpty(config.ouColumn) && row.ContainsKey(config.ouColumn))
        {
            var ouValue = row[config.ouColumn];
            if (!string.IsNullOrWhiteSpace(ouValue))
            {
                result[config.ouColumn] = ouValue;
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
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            var value = GetTokenValue(token, row);
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
            if (match.Success)
                result.Add(new TemplateToken(
                    match.Value,
                    match.Groups[1].Value,
                    match.Groups.Count > 2 && match.Groups[2].Success ? match.Groups[2].Value : null
                ));

        return result;
    }

    private string GetTokenValue(TemplateToken token, Dictionary<string, string> row)
    {
        var result = string.Empty;

        // Chercher la colonne correspondante (insensible à la casse)
        var columnKey = row.Keys.FirstOrDefault(k =>
            string.Equals(k, token.ColumnName, StringComparison.OrdinalIgnoreCase));

        if (columnKey != null && row.TryGetValue(columnKey, out var value))
        {
            result = value;
            _logger?.LogDebug($"🔍 Token trouvé: %{token.ColumnName}% -> colonne '{columnKey}' -> valeur '{value}'");
        }
        else
        {
            _logger?.LogWarning($"❌ Token non trouvé: %{token.ColumnName}% - Colonnes disponibles: {string.Join(", ", row.Keys)}");
        }

        // Appliquer le modificateur si présent
        if (!string.IsNullOrEmpty(token.Modifier))
        {
            var oldResult = result;
            result = ApplyModifier(result, token.Modifier);
            _logger?.LogDebug($"🔧 Modificateur '{token.Modifier}' appliqué: '{oldResult}' -> '{result}'");
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
        var missingAttributes = requiredAttributes
            .Where(attr => !result.ContainsKey(attr) || string.IsNullOrWhiteSpace(result[attr])).ToList();

        if (missingAttributes.Count > 0) AutoCompleteAttributes(result, missingAttributes);

        // Vérifier d'autres attributs potentiellement requis
        if (!result.ContainsKey("displayName") || string.IsNullOrWhiteSpace(result["displayName"]))
        {
            var firstName = result.ContainsKey("givenName") && !string.IsNullOrWhiteSpace(result["givenName"]) ? result["givenName"] : "";
            var lastName = result.ContainsKey("sn") && !string.IsNullOrWhiteSpace(result["sn"]) ? result["sn"] : "";
            
            // Construire le displayName seulement si au moins un des noms est disponible
            if (!string.IsNullOrWhiteSpace(firstName) || !string.IsNullOrWhiteSpace(lastName))
            {
                result["displayName"] = $"{firstName} {lastName}".Trim();
                _logger?.LogInformation($"✅ displayName auto-généré: '{result["displayName"]}'");
            }
            else
            {
                _logger?.LogWarning($"⚠️ Impossible de générer displayName - prénom et nom manquants");
                // Ne pas créer un displayName vide
            }
        }

        if (!result.ContainsKey("userPrincipalName") || string.IsNullOrWhiteSpace(result["userPrincipalName"]))
        {
            if (result.ContainsKey("sAMAccountName") && !string.IsNullOrWhiteSpace(result["sAMAccountName"]) &&
                result.ContainsKey("mail") && !string.IsNullOrWhiteSpace(result["mail"]))
                result["userPrincipalName"] = result["mail"];
            else if (result.ContainsKey("sAMAccountName"))
                result["userPrincipalName"] = $"{result["sAMAccountName"]}@domain.local";
        }
    }

    private void AutoCompleteAttributes(Dictionary<string, string> result, List<string> missingAttributes)
    {
        _logger?.LogWarning($"🔧 AutoCompleteAttributes appelée pour attributs manquants: {string.Join(", ", missingAttributes)}");
        _logger?.LogDebug($"📋 Attributs disponibles dans le résultat: {string.Join(", ", result.Keys)}");
        
        // Si le prénom est manquant
        if (missingAttributes.Contains("givenName"))
        {
            if (result.ContainsKey("displayName") && !string.IsNullOrWhiteSpace(result["displayName"]))
            {
                var parts = result["displayName"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) 
                {
                    result["givenName"] = parts[0];
                    _logger?.LogInformation($"✅ givenName auto-complété: {result["givenName"]}");
                }
                else
                {
                    _logger?.LogWarning($"⚠️ DisplayName vide ou invalide pour auto-complétion de givenName: '{result["displayName"]}'");
                }
            }
            else
            {
                _logger?.LogError($"❌ Aucune donnée disponible pour auto-compléter 'givenName' - vérifiez le mapping des colonnes");
            }
        }

        // Si le nom est manquant
        if (missingAttributes.Contains("sn"))
        {
            if (result.ContainsKey("displayName") && !string.IsNullOrWhiteSpace(result["displayName"]))
            {
                var parts = result["displayName"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    result["sn"] = parts[^1]; // Dernier élément du tableau
                    _logger?.LogInformation($"✅ sn auto-complété depuis displayName: {result["sn"]}");
                }
                else if (parts.Length == 1)
                {
                    // Si un seul mot, utiliser le nom complet comme nom de famille
                    result["sn"] = parts[0];
                    _logger?.LogInformation($"✅ sn auto-complété (mot unique): {result["sn"]}");
                }
                else
                {
                    _logger?.LogWarning($"⚠️ DisplayName vide ou invalide: '{result["displayName"]}'");
                    // Ne pas assigner de valeur par défaut, laisser vide pour forcer la correction manuelle
                }
            }
            else
            {
                _logger?.LogError($"❌ Aucune donnée disponible pour auto-compléter 'sn' - vérifiez le mapping des colonnes");
            }
        }

        // Si le samAccountName est manquant
        if (missingAttributes.Contains("sAMAccountName"))
        {
            var firstName = result.ContainsKey("givenName") ? result["givenName"] : "";
            var lastName = result.ContainsKey("sn") ? result["sn"] : "";

            if (!string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(lastName))
            {
                result["sAMAccountName"] = NormalizeSamAccountName($"{firstName}.{lastName}");
                _logger?.LogInformation($"✅ sAMAccountName auto-complété: {result["sAMAccountName"]}");
            }
            else if (!string.IsNullOrWhiteSpace(firstName))
            {
                result["sAMAccountName"] = NormalizeSamAccountName(firstName);
                _logger?.LogInformation($"✅ sAMAccountName auto-complété (prénom seul): {result["sAMAccountName"]}");
            }
            else if (!string.IsNullOrWhiteSpace(lastName))
            {
                result["sAMAccountName"] = NormalizeSamAccountName(lastName);
                _logger?.LogInformation($"✅ sAMAccountName auto-complété (nom seul): {result["sAMAccountName"]}");
            }
            else
            {
                _logger?.LogError($"❌ Impossible d'auto-compléter sAMAccountName - données insuffisantes pour prénom et nom");
            }
        }
        
        _logger?.LogInformation($"🔧 Auto-completion terminée. Résultat final: sAMAccountName='{result.GetValueOrDefault("sAMAccountName")}', sn='{result.GetValueOrDefault("sn")}', givenName='{result.GetValueOrDefault("givenName")}'");
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

        var normalized = RemoveDiacritics(value.ToLowerInvariant()).Trim();

        // ✅ CONTRAINTES ACTIVE DIRECTORY : Caractères interdits dans sAMAccountName
        // Supprimer tous les caractères spéciaux interdits par AD
        var forbiddenChars = new char[] 
        { 
            '"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', 
            '@', '#', '$', '%', '^', '&', '(', ')', '{', '}', '!', '~', '`'
        };
        
        foreach (var forbiddenChar in forbiddenChars)
        {
            normalized = normalized.Replace(forbiddenChar.ToString(), "");
        }

        // ✅ Remplacer les espaces, apostrophes, tirets et underscores par des points
        normalized = normalized
            .Replace(" ", ".")
            .Replace("'", ".")
            .Replace("-", ".")
            .Replace("_", ".");

        // ✅ Remplacer les séquences multiples de points par un seul point
        normalized = Regex.Replace(normalized, "\\.+", ".");

        // ✅ Supprimer les points au début et à la fin
        normalized = normalized.Trim('.');

        // ✅ CONTRAINTE AD : Le sAMAccountName ne peut pas commencer par un chiffre
        if (!string.IsNullOrEmpty(normalized) && char.IsDigit(normalized[0]))
        {
            normalized = "u" + normalized; // Préfixer avec 'u' (user)
        }

        // ✅ CONTRAINTE AD : Limiter la longueur à 20 caractères maximum
        if (normalized.Length > 20)
        {
            // Essayer de garder le format prénom.nom si possible
            var parts = normalized.Split('.');
            if (parts.Length == 2)
            {
                var firstName = parts[0];
                var lastName = parts[1];
                
                // Réduire progressivement pour tenir dans 20 caractères
                while ($"{firstName}.{lastName}".Length > 20 && firstName.Length > 1)
                {
                    firstName = firstName.Substring(0, firstName.Length - 1);
                }
                
                while ($"{firstName}.{lastName}".Length > 20 && lastName.Length > 1)
                {
                    lastName = lastName.Substring(0, lastName.Length - 1);
                }
                
                normalized = $"{firstName}.{lastName}";
            }
            
            // Si toujours trop long, tronquer brutalement
            if (normalized.Length > 20)
            {
                normalized = normalized.Substring(0, 20);
            }
        }

        // ✅ CONTRAINTE AD : Ne pas finir par un point après troncature
        normalized = normalized.TrimEnd('.');

        // ✅ CONTRAINTE AD : Le sAMAccountName ne peut pas être vide
        if (string.IsNullOrEmpty(normalized))
        {
            normalized = "user" + DateTime.Now.ToString("mmss"); // Fallback
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

        var normalized = value.Trim();

        if (normalized.Length > 2)
            normalized = char.ToUpperInvariant(normalized[0]) + normalized.Substring(1).ToLowerInvariant();

        return normalized;
    }

    private string NormalizeEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var normalized = value
            .ToLowerInvariant()
            .Replace(" ", "")
            .Trim();

        if (!normalized.Contains("@")) normalized += "@domain.local";

        return normalized;
    }

    private string RemoveDiacritics(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder();

        foreach (var c in normalizedString)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark) stringBuilder.Append(c);
        }

        return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
    }

    #endregion
}