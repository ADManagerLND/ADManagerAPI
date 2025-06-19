using System.Text.RegularExpressions;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ADManagerAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ConfigController : ControllerBase
{
    private readonly IConfigService _configService;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(IConfigService configService, ILogger<ConfigController> logger)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Configuration d'import

    [HttpGet("import")]
    public async Task<ActionResult<List<SavedImportConfig>>> GetSavedImportConfigs()
    {
        try
        {
            var configs = await _configService.GetSavedImportConfigs();
            return Ok(configs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des configurations d'import");
            return StatusCode(500, "Une erreur est survenue lors de la récupération des configurations d'import");
        }
    }

    [HttpPost("import")]
    public async Task<ActionResult<SavedImportConfig>> SaveImportConfig([FromBody] SavedImportConfig config)
    {
        try
        {
            var savedConfig = await _configService.SaveImportConfig(config);
            return Ok(savedConfig);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la sauvegarde de la configuration d'import");
            return StatusCode(500, "Une erreur est survenue lors de la sauvegarde de la configuration d'import");
        }
    }

    [HttpDelete("import/{configId}")]
    public async Task<ActionResult> DeleteImportConfig(string configId)
    {
        try
        {
            var result = await _configService.DeleteImportConfig(configId);
            if (!result) return NotFound($"Configuration d'import avec l'ID {configId} non trouvée");

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la suppression de la configuration d'import");
            return StatusCode(500, "Une erreur est survenue lors de la suppression de la configuration d'import");
        }
    }

    #endregion

    #region Configuration générale

    [HttpGet]
    public async Task<ActionResult<ApiSettings>> GetAppConfig()
    {
        try
        {
            var config = await _configService.GetApiSettingsAsync();
            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration");
            return StatusCode(500, "Une erreur est survenue lors de la récupération de la configuration");
        }
    }

    [HttpPut]
    public async Task<ActionResult> UpdateAppConfig([FromBody] ApiSettings config)
    {
        try
        {
            await _configService.UpdateApiSettingsAsync(config);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration");
            return StatusCode(500, "Une erreur est survenue lors de la mise à jour de la configuration");
        }
    }

    #endregion

    #region Configuration LDAP

    [HttpGet("ldap")]
    public async Task<ActionResult<LdapSettings>> GetLdapConfig()
    {
        try
        {
            var config = await _configService.GetLdapSettingsAsync();
            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration LDAP");
            return StatusCode(500, "Une erreur est survenue lors de la récupération de la configuration LDAP");
        }
    }

    [HttpPut("ldap")]
    public async Task<ActionResult> UpdateLdapConfig([FromBody] LdapSettings config)
    {
        try
        {
            await _configService.UpdateLdapSettingsAsync(config);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration LDAP");
            return StatusCode(500, "Une erreur est survenue lors de la mise à jour de la configuration LDAP");
        }
    }

    #endregion

    #region Attributs utilisateur

    [HttpGet("attributes")]
    public async Task<ActionResult<List<AdAttributeDefinition>>> GetUserAttributes()
    {
        try
        {
            var attributes = await _configService.GetUserAttributesAsync();
            return Ok(attributes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des attributs utilisateur");
            return StatusCode(500, "Une erreur est survenue lors de la récupération des attributs utilisateur");
        }
    }

    [HttpPut("attributes")]
    public async Task<ActionResult> UpdateUserAttributes([FromBody] List<AdAttributeDefinition> attributes)
    {
        try
        {
            await _configService.UpdateUserAttributesAsync(attributes);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour des attributs utilisateur");
            return StatusCode(500, "Une erreur est survenue lors de la mise à jour des attributs utilisateur");
        }
    }

    #endregion

    #region Configuration des mappages AD (intégrée avec imports existants)

    /// <summary>
    ///     Récupère toutes les configurations d'import qui peuvent servir de mappages AD
    /// </summary>
    [HttpGet("ad-mappings")]
    public async Task<ActionResult<List<SavedImportConfig>>> GetADMappingConfigurations()
    {
        try
        {
            var configs = await _configService.GetSavedImportConfigs();
            return Ok(configs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des configurations de mapping AD");
            return StatusCode(500, "Une erreur est survenue lors de la récupération des configurations de mapping AD");
        }
    }

    /// <summary>
    ///     Récupère une configuration d'import spécifique pour les mappages AD
    /// </summary>
    [HttpGet("ad-mappings/{configId}")]
    public async Task<ActionResult<SavedImportConfig>> GetADMappingConfiguration(string configId)
    {
        try
        {
            var configs = await _configService.GetSavedImportConfigs();
            var configuration = configs.FirstOrDefault(c => c.Id == configId);

            if (configuration == null) return NotFound($"Configuration de mapping AD avec l'ID {configId} non trouvée");
            return Ok(configuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de la configuration de mapping AD {ConfigId}",
                configId);
            return StatusCode(500, "Une erreur est survenue lors de la récupération de la configuration de mapping AD");
        }
    }

    /// <summary>
    ///     Sauvegarde ou met à jour une configuration de mapping AD
    /// </summary>
    [HttpPost("ad-mappings")]
    public async Task<ActionResult<SavedImportConfig>> SaveADMappingConfiguration(
        [FromBody] SavedImportConfig configuration)
    {
        try
        {
            var savedConfiguration = await _configService.SaveImportConfig(configuration);
            return Ok(savedConfiguration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la sauvegarde de la configuration de mapping AD");
            return StatusCode(500, "Une erreur est survenue lors de la sauvegarde de la configuration de mapping AD");
        }
    }

    /// <summary>
    ///     Met à jour une configuration de mapping AD existante
    /// </summary>
    [HttpPut("ad-mappings/{configId}")]
    public async Task<ActionResult<SavedImportConfig>> UpdateADMappingConfiguration(string configId,
        [FromBody] SavedImportConfig configuration)
    {
        try
        {
            configuration.Id = configId;

            var updatedConfiguration = await _configService.SaveImportConfig(configuration);
            return Ok(updatedConfiguration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de la configuration de mapping AD {ConfigId}",
                configId);
            return StatusCode(500, "Une erreur est survenue lors de la mise à jour de la configuration de mapping AD");
        }
    }

    /// <summary>
    ///     Supprime une configuration de mapping AD
    /// </summary>
    [HttpDelete("ad-mappings/{configId}")]
    public async Task<ActionResult> DeleteADMappingConfiguration(string configId)
    {
        try
        {
            var result = await _configService.DeleteImportConfig(configId);
            if (!result) return NotFound($"Configuration de mapping AD avec l'ID {configId} non trouvée");

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la suppression de la configuration de mapping AD {ConfigId}",
                configId);
            return StatusCode(500, "Une erreur est survenue lors de la suppression de la configuration de mapping AD");
        }
    }

    /// <summary>
    ///     Valide un headerMapping contre les attributs AD disponibles
    /// </summary>
    [HttpPost("ad-mappings/validate")]
    public async Task<ActionResult<ValidationResult>> ValidateHeaderMapping(
        [FromBody] Dictionary<string, string> headerMapping)
    {
        try
        {
            var attributes = await _configService.GetUserAttributesAsync();
            var validationResult = HeaderMappingExtensions.ValidateHeaderMapping(headerMapping, attributes);
            return Ok(validationResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la validation du mapping AD");
            return StatusCode(500, "Une erreur est survenue lors de la validation du mapping AD");
        }
    }

    /// <summary>
    ///     Convertit un headerMapping en format d'affichage
    /// </summary>
    [HttpPost("ad-mappings/display-format")]
    public ActionResult<List<MappingDisplayItem>> ConvertToDisplayFormat(
        [FromBody] Dictionary<string, string> headerMapping)
    {
        try
        {
            var displayItems = HeaderMappingExtensions.ConvertToDisplayFormat(headerMapping);
            return Ok(displayItems);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la conversion du mapping AD");
            return StatusCode(500, "Une erreur est survenue lors de la conversion du mapping AD");
        }
    }

    /// <summary>
    ///     Récupère les attributs AD par défaut
    /// </summary>
    [HttpGet("ad-attributes/default")]
    public async Task<ActionResult<List<AdAttributeDefinition>>> GetDefaultADAttributes()
    {
        try
        {
            var attributes = await _configService.GetUserAttributesAsync();
            return Ok(attributes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des attributs AD par défaut");
            return StatusCode(500, "Une erreur est survenue lors de la récupération des attributs AD par défaut");
        }
    }

    #endregion

    #region FONCTIONNALITÉS AVANCÉES (SANS IA)

    /// <summary>
    ///     Obtient toutes les colonnes prédéfinies disponibles
    /// </summary>
    [HttpGet("predefined-columns")]
    public ActionResult<List<PredefinedColumnDefinition>> GetPredefinedColumns()
    {
        try
        {
            _logger.LogInformation("Récupération des colonnes prédéfinies");

            var predefinedColumns = new List<PredefinedColumnDefinition>
            {
                // Identité
                new()
                {
                    Key = "prenom",
                    Label = "Prénom",
                    Description = "Prénom de la personne",
                    Category = ColumnCategory.Identity,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "Jean", "Marie", "Pierre", "Sophie" },
                    IsPopular = true,
                    DefaultTransformation = "capitalize"
                },
                new()
                {
                    Key = "nom",
                    Label = "Nom de famille",
                    Description = "Nom de famille",
                    Category = ColumnCategory.Identity,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "Dupont", "Martin", "Bernard", "Durand" },
                    IsPopular = true,
                    DefaultTransformation = "uppercase"
                },
                new()
                {
                    Key = "sex",
                    Label = "Sexe/Genre",
                    Description = "Genre de la personne",
                    Category = ColumnCategory.Personal,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "M", "F", "H", "Homme", "Femme" },
                    IsPopular = true,
                    Validation = new ColumnValidation
                    {
                        Pattern = "^[MFH]$|^(Homme|Femme)$",
                        ErrorMessage = "Valeur invalide pour le sexe"
                    }
                },

                // Contact
                new()
                {
                    Key = "email",
                    Label = "Adresse email",
                    Description = "Adresse email principale",
                    Category = ColumnCategory.Contact,
                    DataType = ColumnDataType.Email,
                    Examples = new List<string> { "jean.dupont@ecole.fr", "marie.martin@entreprise.com" },
                    IsPopular = true,
                    Validation = new ColumnValidation
                    {
                        Pattern = @"^[^@]+@[^@]+\.[^@]+$",
                        ErrorMessage = "Format d'email invalide"
                    }
                },
                new()
                {
                    Key = "telephone",
                    Label = "Téléphone",
                    Description = "Numéro de téléphone principal",
                    Category = ColumnCategory.Contact,
                    DataType = ColumnDataType.Phone,
                    Examples = new List<string> { "01.23.45.67.89", "0123456789", "+33123456789" },
                    IsPopular = true,
                    Validation = new ColumnValidation
                    {
                        Pattern = @"^(\+33|0)[1-9](\s*[0-9]){8}$",
                        ErrorMessage = "Format de téléphone invalide"
                    }
                },

                // Personnel
                new()
                {
                    Key = "dateNaissance",
                    Label = "Date de naissance",
                    Description = "Date de naissance (DD/MM/YYYY)",
                    Category = ColumnCategory.Personal,
                    DataType = ColumnDataType.Date,
                    Examples = new List<string> { "15/03/2010", "28/07/1995", "05/12/1988" },
                    IsPopular = true,
                    Validation = new ColumnValidation
                    {
                        Pattern = @"^\d{2}/\d{2}/\d{4}$",
                        ErrorMessage = "Format de date invalide (DD/MM/YYYY attendu)"
                    }
                },
                new()
                {
                    Key = "adresse",
                    Label = "Adresse complète",
                    Description = "Adresse postale complète",
                    Category = ColumnCategory.Personal,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "123 Rue de la Paix", "45 Avenue des Champs" },
                    IsPopular = true
                },
                new()
                {
                    Key = "ville",
                    Label = "Ville",
                    Description = "Ville de résidence",
                    Category = ColumnCategory.Personal,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "Paris", "Lyon", "Marseille", "Bordeaux" },
                    IsPopular = true
                },
                new()
                {
                    Key = "codePostal",
                    Label = "Code postal",
                    Description = "Code postal",
                    Category = ColumnCategory.Personal,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "75001", "69000", "13000", "33000" },
                    IsPopular = true,
                    Validation = new ColumnValidation
                    {
                        Pattern = @"^\d{5}$",
                        ErrorMessage = "Le code postal doit contenir 5 chiffres"
                    }
                },

                // Organisation
                new()
                {
                    Key = "departement",
                    Label = "Département/Service",
                    Description = "Département ou service",
                    Category = ColumnCategory.Organization,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "Informatique", "Ressources Humaines", "Marketing" },
                    IsPopular = true
                },
                new()
                {
                    Key = "poste",
                    Label = "Poste/Fonction",
                    Description = "Fonction ou poste occupé",
                    Category = ColumnCategory.Organization,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "Développeur", "Manager", "Analyste", "Assistant" },
                    IsPopular = true
                },

                // Académique
                new()
                {
                    Key = "classe",
                    Label = "Classe/Niveau",
                    Description = "Classe ou niveau scolaire",
                    Category = ColumnCategory.Academic,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "6A", "CP", "Terminale S", "L1", "Master 2" },
                    IsPopular = true
                },
                new()
                {
                    Key = "niveau",
                    Label = "Niveau d'études",
                    Description = "Niveau scolaire ou professionnel",
                    Category = ColumnCategory.Academic,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "Primaire", "Collège", "Lycée", "Supérieur" },
                    IsPopular = true
                },

                // Système
                new()
                {
                    Key = "code",
                    Label = "Code/Identifiant",
                    Description = "Code unique ou identifiant système",
                    Category = ColumnCategory.System,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "EMP001", "USR123", "STU2024001" },
                    IsPopular = true
                },
                new()
                {
                    Key = "statut",
                    Label = "Statut",
                    Description = "Statut de la personne (actif/inactif/etc.)",
                    Category = ColumnCategory.System,
                    DataType = ColumnDataType.Text,
                    Examples = new List<string> { "Actif", "Inactif", "En congé", "Suspendu" },
                    IsPopular = true
                }
            };

            _logger.LogInformation($"Retour de {predefinedColumns.Count} colonnes prédéfinies");
            return Ok(predefinedColumns);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération des colonnes prédéfinies");
            return StatusCode(500, new { Message = "Erreur interne du serveur", Error = ex.Message });
        }
    }

    /// <summary>
    ///     Valide un mapping de base (sans IA)
    /// </summary>
    [HttpPost("validate-mapping")]
    public async Task<ActionResult<BasicValidationResult>> ValidateMapping(
        [FromBody] BasicMappingValidationRequest request)
    {
        try
        {
            _logger.LogInformation($"Validation du mapping pour l'attribut {request.AttributeName}");

            var errors = new List<string>();
            var warnings = new List<string>();

            // Validation de base
            if (request.IsRequired && string.IsNullOrWhiteSpace(request.Template))
                errors.Add($"L'attribut requis '{request.AttributeName}' ne peut pas être vide");

            // Validation des colonnes référencées
            var referencedColumns = ExtractColumnsFromTemplate(request.Template);
            foreach (var col in referencedColumns)
                if (!request.AvailableColumns.Contains(col))
                    errors.Add($"La colonne '{col}' référencée dans le template n'existe pas");

            // Validation de la syntaxe des transformations
            if (request.Template.Contains("%"))
            {
                var syntaxValidation = ValidateTemplateSyntax(request.Template);
                if (!syntaxValidation.IsValid) errors.Add(syntaxValidation.Error ?? "Syntaxe de template invalide");
            }

            var validationResult = new BasicValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                Warnings = warnings
            };

            _logger.LogInformation($"Validation terminée: {(validationResult.IsValid ? "Valide" : "Invalide")}");
            return Ok(validationResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la validation du mapping");
            return StatusCode(500, new { Message = "Erreur interne du serveur", Error = ex.Message });
        }
    }

    /// <summary>
    ///     Génère une prévisualisation de base (sans IA)
    /// </summary>
    [HttpPost("live-preview")]
    public ActionResult<List<BasicMappingPreview>> GenerateLivePreview(
        [FromBody] BasicPreviewRequest request)
    {
        try
        {
            _logger.LogInformation($"Génération de prévisualisation pour {request.HeaderMapping.Count} mappings");

            var preview = new List<BasicMappingPreview>();

            foreach (var mapping in request.HeaderMapping)
                try
                {
                    var transformedValue = ApplyTemplate(mapping.Value, request.SampleData);

                    preview.Add(new BasicMappingPreview
                    {
                        ADAttribute = mapping.Key,
                        Template = mapping.Value,
                        SampleValue = mapping.Value.Contains("%") ? "Calculé dynamiquement" : mapping.Value,
                        TransformedValue = transformedValue,
                        IsValid = !string.IsNullOrEmpty(transformedValue)
                    });
                }
                catch (Exception ex)
                {
                    preview.Add(new BasicMappingPreview
                    {
                        ADAttribute = mapping.Key,
                        Template = mapping.Value,
                        SampleValue = "Erreur",
                        TransformedValue = $"Erreur: {ex.Message}",
                        IsValid = false,
                        Error = ex.Message
                    });
                }

            _logger.LogInformation($"Prévisualisation générée avec {preview.Count} éléments");
            return Ok(preview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la génération de la prévisualisation");
            return StatusCode(500, new { Message = "Erreur interne du serveur", Error = ex.Message });
        }
    }

    #endregion

    #region Configuration complète

    [HttpGet("all")]
    public async Task<ActionResult<ApplicationSettings>> GetAllSettings()
    {
        try
        {
            var settings = await _configService.GetAllSettingsAsync();
            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la récupération de toute la configuration");
            return StatusCode(500, "Une erreur est survenue lors de la récupération de toute la configuration");
        }
    }

    [HttpPut("all")]
    public async Task<ActionResult> UpdateAllSettings([FromBody] ApplicationSettings settings)
    {
        try
        {
            await _configService.UpdateAllSettingsAsync(settings);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erreur lors de la mise à jour de toute la configuration");
            return StatusCode(500, "Une erreur est survenue lors de la mise à jour de toute la configuration");
        }
    }

    #endregion

    #region Méthodes utilitaires (sans IA)

    /// <summary>
    ///     Extrait les colonnes d'un template
    /// </summary>
    private List<string> ExtractColumnsFromTemplate(string template)
    {
        if (string.IsNullOrEmpty(template)) return new List<string>();

        var pattern = @"%([^%:]+)(?::[^%]*)?%";
        var matches = Regex.Matches(template, pattern);

        return matches
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
    }

    /// <summary>
    ///     Valide la syntaxe d'un template
    /// </summary>
    private (bool IsValid, string? Error) ValidateTemplateSyntax(string template)
    {
        try
        {
            // Vérifier que les % sont bien appairés
            var percentCount = template.Count(c => c == '%');
            if (percentCount % 2 != 0) return (false, "Nombre impair de caractères % dans le template");

            // Vérifier la syntaxe des transformations
            var pattern = @"%([^%:]+)(?::([^%]+))?%";
            var matches = Regex.Matches(template, pattern);

            foreach (Match match in matches)
                if (match.Groups.Count > 2 && !string.IsNullOrEmpty(match.Groups[2].Value))
                {
                    var transformation = match.Groups[2].Value.ToLower();
                    var validTransformations = new[] { "uppercase", "lowercase", "capitalize", "trim", "first" };

                    if (!validTransformations.Contains(transformation))
                        return (false, $"Transformation inconnue: {transformation}");
                }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    ///     Applique un template avec les données fournies
    /// </summary>
    private string ApplyTemplate(string template, Dictionary<string, object> data)
    {
        if (string.IsNullOrEmpty(template)) return "";

        var pattern = @"%([^%:]+)(?::([^%]+))?%";

        return Regex.Replace(template, pattern, match =>
        {
            var columnName = match.Groups[1].Value;
            var transformation = match.Groups.Count > 2 ? match.Groups[2].Value : null;

            if (!data.TryGetValue(columnName, out var valueObj)) return "";

            var value = valueObj?.ToString() ?? "";

            // Application des transformations
            if (!string.IsNullOrEmpty(transformation))
                value = transformation.ToLower() switch
                {
                    "uppercase" => value.ToUpper(),
                    "lowercase" => value.ToLower(),
                    "trim" => value.Trim(),
                    "capitalize" => char.ToUpper(value[0]) + value[1..].ToLower(),
                    "first" => value.Length > 0 ? value[0].ToString() : "",
                    _ => value
                };

            return value;
        });
    }

    #endregion
}

/// <summary>
/// Note: Les classes de support sont maintenant définies dans ADMappingIntegration.cs
/// </summary>