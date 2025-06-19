using ADManagerAPI.Models;
using ADManagerAPI.Services;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace ADManagerAPI.Tests.Services;

public class ConfigServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Mock<ILogger<ConfigService>> _mockLogger;
    private readonly Mock<IDataProtectionProvider> _mockDataProtectionProvider;
    private readonly ConfigService _configService;

    public ConfigServiceTests()
    {
        // Créer un répertoire temporaire pour les tests
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ADManagerTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);

        // Configuration des mocks
        _mockLogger = new Mock<ILogger<ConfigService>>();
        _mockDataProtectionProvider = new Mock<IDataProtectionProvider>();

        // Changer le répertoire de travail temporairement pour les tests
        Environment.CurrentDirectory = _tempDirectory;

        _configService = new ConfigService(_mockLogger.Object, _mockDataProtectionProvider.Object);
    }

    [Fact]
    public async Task GetAllSettingsAsync_ShouldReturnApplicationSettings()
    {
        // Act
        var settings = await _configService.GetAllSettingsAsync();

        // Assert
        settings.Should().NotBeNull();
        settings.Should().BeOfType<ApplicationSettings>();
    }

    [Fact]
    public async Task GetSavedImportConfigs_ShouldReturnEmptyListWhenNoConfigurations()
    {
        // Act
        var configs = await _configService.GetSavedImportConfigs();

        // Assert
        configs.Should().NotBeNull();
        configs.Should().BeOfType<List<SavedImportConfig>>();
    }

    [Fact]
    public async Task SaveImportConfig_ShouldAssignIdWhenNotProvided()
    {
        // Arrange
        var config = new SavedImportConfig
        {
            Name = "Test Config",
            Description = "Configuration de test"
        };

        // Act
        var savedConfig = await _configService.SaveImportConfig(config);

        // Assert
        savedConfig.Should().NotBeNull();
        savedConfig.Id.Should().NotBeNullOrEmpty();
        savedConfig.Name.Should().Be("Test Config");
        savedConfig.Description.Should().Be("Configuration de test");
    }

    [Fact]
    public async Task SaveImportConfig_ShouldPreserveIdWhenProvided()
    {
        // Arrange
        var existingId = Guid.NewGuid().ToString();
        var config = new SavedImportConfig
        {
            Id = existingId,
            Name = "Test Config avec ID",
            Description = "Configuration de test avec ID existant"
        };

        // Act
        var savedConfig = await _configService.SaveImportConfig(config);

        // Assert
        savedConfig.Should().NotBeNull();
        savedConfig.Id.Should().Be(existingId);
        savedConfig.Name.Should().Be("Test Config avec ID");
    }

    [Fact]
    public async Task GetLdapSettingsAsync_ShouldReturnLdapSettings()
    {
        // Act
        var ldapSettings = await _configService.GetLdapSettingsAsync();

        // Assert
        ldapSettings.Should().NotBeNull();
        ldapSettings.Should().BeOfType<LdapSettings>();
    }

    [Fact]
    public async Task UpdateLdapSettingsAsync_ShouldUpdateSettings()
    {
        // Arrange
        var newLdapSettings = new LdapSettings
        {
            LdapServer = "test.example.com",
            LdapPort = 389,
            LdapSsl = false,
            LdapDomain = "TEST"
        };

        // Act
        await _configService.UpdateLdapSettingsAsync(newLdapSettings);
        var updatedSettings = await _configService.GetLdapSettingsAsync();

        // Assert
        updatedSettings.Should().NotBeNull();
        updatedSettings.LdapServer.Should().Be("test.example.com");
        updatedSettings.LdapPort.Should().Be(389);
        updatedSettings.LdapSsl.Should().BeFalse();
        updatedSettings.LdapDomain.Should().Be("TEST");
    }

    [Fact]
    public async Task GetApiSettingsAsync_ShouldReturnApiSettings()
    {
        // Act
        var apiSettings = await _configService.GetApiSettingsAsync();

        // Assert
        apiSettings.Should().NotBeNull();
        apiSettings.Should().BeOfType<ApiSettings>();
    }

    [Fact]
    public async Task GetUserAttributesAsync_ShouldReturnUserAttributes()
    {
        // Act
        var attributes = await _configService.GetUserAttributesAsync();

        // Assert
        attributes.Should().NotBeNull();
        attributes.Should().BeOfType<List<AdAttributeDefinition>>();
    }

    public void Dispose()
    {
        // Nettoyer le répertoire temporaire après les tests
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch
            {
                // Ignorer les erreurs lors du nettoyage
            }
        }
    }
} 