using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ADManagerAPI.Models;

public partial class ImportConfig
{
    [JsonPropertyName("adMappingSettings")]
    public ADMappingSettings? ADMappingSettings { get; set; }
}

public class ADMappingSettings
{
    [JsonPropertyName("isADMappingEnabled")]
    public bool IsADMappingEnabled { get; set; } = true;

    [JsonPropertyName("targetOU")] public string TargetOU { get; set; } = "";

    [JsonPropertyName("conflictBehavior")]
    public ConflictBehavior ConflictBehavior { get; set; } = ConflictBehavior.Update;

    [JsonPropertyName("synchronizationSettings")]
    public SynchronizationSettings SynchronizationSettings { get; set; } = new();

    [JsonPropertyName("validationRules")] public Dictionary<string, string> ValidationRules { get; set; } = new();

    [JsonPropertyName("customTransformations")]
    public Dictionary<string, string> CustomTransformations { get; set; } = new();
}

public class PredefinedColumnDefinition
{
    [JsonPropertyName("key")] [Required] public string Key { get; set; } = "";

    [JsonPropertyName("label")] [Required] public string Label { get; set; } = "";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonPropertyName("category")] public ColumnCategory Category { get; set; } = ColumnCategory.Custom;

    [JsonPropertyName("dataType")] public ColumnDataType DataType { get; set; } = ColumnDataType.Text;

    [JsonPropertyName("examples")] public List<string> Examples { get; set; } = new();

    [JsonPropertyName("isPopular")] public bool IsPopular { get; set; } = false;

    [JsonPropertyName("validation")] public ColumnValidation? Validation { get; set; }

    [JsonPropertyName("defaultTransformation")]
    public string? DefaultTransformation { get; set; }
}

/// <summary>
///     Catégories de colonnes
/// </summary>
public enum ColumnCategory
{
    Identity,
    Contact,
    Personal,
    Organization,
    Academic,
    System,
    Custom
}

/// <summary>
///     Types de données pour les colonnes
/// </summary>
public enum ColumnDataType
{
    Text,
    Email,
    Date,
    Number,
    Phone,
    Boolean
}

/// <summary>
///     Validation des colonnes
/// </summary>
public class ColumnValidation
{
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }

    [JsonPropertyName("minLength")] public int? MinLength { get; set; }

    [JsonPropertyName("maxLength")] public int? MaxLength { get; set; }

    [JsonPropertyName("required")] public bool Required { get; set; } = false;

    [JsonPropertyName("customValidator")] public string? CustomValidator { get; set; }

    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
}

/// <summary>
///     Helper pour travailler avec les mappages existants
/// </summary>
public static class HeaderMappingExtensions
{
    /// <summary>
    ///     Valide un headerMapping contre les attributs AD disponibles
    /// </summary>
    public static ValidationResult ValidateHeaderMapping(Dictionary<string, string> headerMapping,
        List<AdAttributeDefinition> availableAttributes)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        foreach (var mapping in headerMapping)
        {
            var adAttribute = mapping.Key;
            var template = mapping.Value;

            // Vérifier si l'attribut AD existe
            var attributeDefinition =
                availableAttributes.FirstOrDefault(a => a.Name.Equals(adAttribute, StringComparison.OrdinalIgnoreCase));
            if (attributeDefinition == null)
                warnings.Add($"Attribut AD '{adAttribute}' non trouvé dans les définitions disponibles");

            // Vérifier si l'attribut obligatoire a une valeur
            if (attributeDefinition?.IsRequired == true && string.IsNullOrEmpty(template))
                errors.Add($"L'attribut obligatoire '{adAttribute}' ne peut pas être vide");

            // Valider la syntaxe du template
            if (!string.IsNullOrEmpty(template) && template.Contains("%"))
            {
                var templateValidation = ValidateTemplate(template);
                if (!templateValidation.IsValid)
                    errors.Add($"Template invalide pour '{adAttribute}': {templateValidation.Error}");
            }
        }

        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings
        };
    }

    /// <summary>
    ///     Valide la syntaxe d'un template
    /// </summary>
    private static (bool IsValid, string Error) ValidateTemplate(string template)
    {
        try
        {
            // Vérifier que les % sont bien appairés
            var percentCount = template.Count(c => c == '%');
            if (percentCount % 2 != 0) return (false, "Nombre impair de caractères % dans le template");

            // Vérifier la syntaxe des transformations
            var tokens = ExtractTemplateTokens(template);
            foreach (var token in tokens)
            {
                var parts = token.Split(':');
                if (parts.Length > 2) return (false, $"Syntaxe de transformation invalide: {token}");

                if (parts.Length == 2)
                {
                    var transformation = parts[1].ToLower();
                    var validTransformations = new[] { "uppercase", "lowercase", "capitalize", "trim", "first" };
                    if (!validTransformations.Contains(transformation))
                        return (false, $"Transformation inconnue: {transformation}");
                }
            }

            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    ///     Extrait les tokens d'un template (ex: "prenom", "nom:uppercase")
    /// </summary>
    private static List<string> ExtractTemplateTokens(string template)
    {
        var tokens = new List<string>();
        var currentToken = "";
        var insideToken = false;

        for (var i = 0; i < template.Length; i++)
            if (template[i] == '%')
            {
                if (insideToken)
                {
                    tokens.Add(currentToken);
                    currentToken = "";
                    insideToken = false;
                }
                else
                {
                    insideToken = true;
                }
            }
            else if (insideToken)
            {
                currentToken += template[i];
            }

        return tokens;
    }

    /// <summary>
    ///     Convertit un headerMapping en format standardisé pour l'affichage
    /// </summary>
    public static List<MappingDisplayItem> ConvertToDisplayFormat(Dictionary<string, string> headerMapping)
    {
        return headerMapping.Select(kvp => new MappingDisplayItem
        {
            ADAttribute = kvp.Key,
            Template = kvp.Value,
            IsTemplate = kvp.Value.Contains("%"),
            EstimatedColumns = ExtractTemplateTokens(kvp.Value).Where(t => !t.Contains(":")).ToList()
        }).ToList();
    }
}

/// <summary>
///     Résultat de validation
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
///     Item d'affichage pour les mappages
/// </summary>
public class MappingDisplayItem
{
    public string ADAttribute { get; set; } = "";
    public string Template { get; set; } = "";
    public bool IsTemplate { get; set; }
    public List<string> EstimatedColumns { get; set; } = new();
}

/// <summary>
///     Enum pour le comportement en cas de conflit (repris de l'existant)
/// </summary>
public enum ConflictBehavior
{
    Update,
    Skip,
    Error,
    CreateNew
}

/// <summary>
///     Paramètres de synchronisation (simplifiés pour s'intégrer à l'existant)
/// </summary>
public class SynchronizationSettings
{
    [JsonPropertyName("enableAutoSync")] public bool EnableAutoSync { get; set; } = false;

    [JsonPropertyName("syncInterval")] public SyncInterval SyncInterval { get; set; } = SyncInterval.Manual;

    [JsonPropertyName("enableNotifications")]
    public bool EnableNotifications { get; set; } = true;

    [JsonPropertyName("maxBatchSize")] public int MaxBatchSize { get; set; } = 100;
}

public enum SyncInterval
{
    Manual,
    Hourly,
    Daily,
    Weekly,
    Monthly
}

/// <summary>
///     Classes de support pour les requêtes (simplifiées sans IA)
/// </summary>
public class BasicMappingValidationRequest
{
    public string AttributeName { get; set; } = "";
    public string Template { get; set; } = "";
    public List<string> AvailableColumns { get; set; } = new();
    public bool IsRequired { get; set; } = false;
}

public class BasicPreviewRequest
{
    public Dictionary<string, string> HeaderMapping { get; set; } = new();
    public Dictionary<string, object> SampleData { get; set; } = new();
}

public class BasicValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class BasicMappingPreview
{
    public string ADAttribute { get; set; } = "";
    public string Template { get; set; } = "";
    public string SampleValue { get; set; } = "";
    public string TransformedValue { get; set; } = "";
    public bool IsValid { get; set; }
    public string? Error { get; set; }
}