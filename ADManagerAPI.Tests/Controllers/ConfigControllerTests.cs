using ADManagerAPI.Controllers;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace ADManagerAPI.Tests.Controllers;

public class ConfigControllerTests
{
    private readonly Mock<IConfigService> _mockConfigService;
    private readonly Mock<ILogger<ConfigController>> _mockLogger;
    private readonly ConfigController _controller;

    public ConfigControllerTests()
    {
        _mockConfigService = new Mock<IConfigService>();
        _mockLogger = new Mock<ILogger<ConfigController>>();
        _controller = new ConfigController(_mockConfigService.Object, _mockLogger.Object);
    }

    #region Tests pour Configuration d'import

    [Fact]
    public async Task GetSavedImportConfigs_ShouldReturnOkWithConfigurations()
    {
        // Arrange
        var expectedConfigs = new List<SavedImportConfig>
        {
            new SavedImportConfig { Id = "1", Name = "Config 1" },
            new SavedImportConfig { Id = "2", Name = "Config 2" }
        };
        _mockConfigService.Setup(s => s.GetSavedImportConfigs())
            .ReturnsAsync(expectedConfigs);

        // Act
        var result = await _controller.GetSavedImportConfigs();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var actualConfigs = okResult.Value.Should().BeAssignableTo<List<SavedImportConfig>>().Subject;
        actualConfigs.Should().HaveCount(2);
        actualConfigs.Should().BeEquivalentTo(expectedConfigs);
    }

    [Fact]
    public async Task GetSavedImportConfigs_ShouldReturnServerError_WhenExceptionThrown()
    {
        // Arrange
        _mockConfigService.Setup(s => s.GetSavedImportConfigs())
            .ThrowsAsync(new Exception("Test exception"));

        // Act
        var result = await _controller.GetSavedImportConfigs();

        // Assert
        var statusCodeResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        statusCodeResult.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task SaveImportConfig_ShouldReturnOkWithSavedConfig()
    {
        // Arrange
        var inputConfig = new SavedImportConfig { Name = "Test Config", Description = "Description de test" };
        var savedConfig = new SavedImportConfig { Id = "123", Name = "Test Config", Description = "Description de test" };
        
        _mockConfigService.Setup(s => s.SaveImportConfig(It.IsAny<SavedImportConfig>()))
            .ReturnsAsync(savedConfig);

        // Act
        var result = await _controller.SaveImportConfig(inputConfig);

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var actualConfig = okResult.Value.Should().BeAssignableTo<SavedImportConfig>().Subject;
        actualConfig.Id.Should().Be("123");
        actualConfig.Name.Should().Be("Test Config");
    }

    [Fact]
    public async Task DeleteImportConfig_ShouldReturnOk_WhenConfigExists()
    {
        // Arrange
        var configId = "test-config-id";
        _mockConfigService.Setup(s => s.DeleteImportConfig(configId))
            .ReturnsAsync(true);

        // Act
        var result = await _controller.DeleteImportConfig(configId);

        // Assert
        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task DeleteImportConfig_ShouldReturnNotFound_WhenConfigDoesNotExist()
    {
        // Arrange
        var configId = "non-existent-config";
        _mockConfigService.Setup(s => s.DeleteImportConfig(configId))
            .ReturnsAsync(false);

        // Act
        var result = await _controller.DeleteImportConfig(configId);

        // Assert
        var notFoundResult = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFoundResult.Value.Should().Be($"Configuration d'import avec l'ID {configId} non trouvée");
    }

    #endregion

    #region Tests pour Configuration générale

    [Fact]
    public async Task GetAppConfig_ShouldReturnOkWithApiSettings()
    {
        // Arrange
        var expectedSettings = new ApiSettings
        {
            ApiUrl = "https://test.example.com"
        };
        _mockConfigService.Setup(s => s.GetApiSettingsAsync())
            .ReturnsAsync(expectedSettings);

        // Act
        var result = await _controller.GetAppConfig();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var actualSettings = okResult.Value.Should().BeAssignableTo<ApiSettings>().Subject;
        actualSettings.Should().BeEquivalentTo(expectedSettings);
    }

    [Fact]
    public async Task UpdateAppConfig_ShouldReturnOk_WhenUpdateSuccessful()
    {
        // Arrange
        var configToUpdate = new ApiSettings
        {
            ApiUrl = "https://updated.example.com"
        };
        _mockConfigService.Setup(s => s.UpdateApiSettingsAsync(It.IsAny<ApiSettings>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.UpdateAppConfig(configToUpdate);

        // Assert
        result.Should().BeOfType<OkResult>();
        _mockConfigService.Verify(s => s.UpdateApiSettingsAsync(configToUpdate), Times.Once);
    }

    #endregion

    #region Tests pour Configuration LDAP

    [Fact]
    public async Task GetLdapConfig_ShouldReturnOkWithLdapSettings()
    {
        // Arrange
        var expectedSettings = new LdapSettings
        {
            LdapServer = "ldap.test.com",
            LdapPort = 389,
            LdapDomain = "TEST",
            LdapSsl = false
        };
        _mockConfigService.Setup(s => s.GetLdapSettingsAsync())
            .ReturnsAsync(expectedSettings);

        // Act
        var result = await _controller.GetLdapConfig();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var actualSettings = okResult.Value.Should().BeAssignableTo<LdapSettings>().Subject;
        actualSettings.Should().BeEquivalentTo(expectedSettings);
    }

    [Fact]
    public async Task UpdateLdapConfig_ShouldReturnOk_WhenUpdateSuccessful()
    {
        // Arrange
        var configToUpdate = new LdapSettings
        {
            LdapServer = "ldap.updated.com",
            LdapPort = 636,
            LdapDomain = "UPDATED",
            LdapSsl = true
        };
        _mockConfigService.Setup(s => s.UpdateLdapSettingsAsync(It.IsAny<LdapSettings>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.UpdateLdapConfig(configToUpdate);

        // Assert
        result.Should().BeOfType<OkResult>();
        _mockConfigService.Verify(s => s.UpdateLdapSettingsAsync(configToUpdate), Times.Once);
    }

    #endregion

    #region Tests pour Attributs utilisateur

    [Fact]
    public async Task GetUserAttributes_ShouldReturnOkWithAttributes()
    {
        // Arrange
        var expectedAttributes = new List<AdAttributeDefinition>
        {
            new AdAttributeDefinition { Name = "sAMAccountName", Description = "Nom d'utilisateur", Syntax = "String", IsRequired = true },
            new AdAttributeDefinition { Name = "mail", Description = "Email", Syntax = "String", IsRequired = false }
        };
        _mockConfigService.Setup(s => s.GetUserAttributesAsync())
            .ReturnsAsync(expectedAttributes);

        // Act
        var result = await _controller.GetUserAttributes();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var actualAttributes = okResult.Value.Should().BeAssignableTo<List<AdAttributeDefinition>>().Subject;
        actualAttributes.Should().HaveCount(2);
        actualAttributes.Should().BeEquivalentTo(expectedAttributes);
    }

    [Fact]
    public async Task UpdateUserAttributes_ShouldReturnOk_WhenUpdateSuccessful()
    {
        // Arrange
        var attributesToUpdate = new List<AdAttributeDefinition>
        {
            new AdAttributeDefinition { Name = "sAMAccountName", Description = "Nom d'utilisateur", Syntax = "String", IsRequired = true }
        };
        _mockConfigService.Setup(s => s.UpdateUserAttributesAsync(It.IsAny<List<AdAttributeDefinition>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.UpdateUserAttributes(attributesToUpdate);

        // Assert
        result.Should().BeOfType<OkResult>();
        _mockConfigService.Verify(s => s.UpdateUserAttributesAsync(attributesToUpdate), Times.Once);
    }

    #endregion

    #region Tests pour Configuration complète

    [Fact]
    public async Task GetAllSettings_ShouldReturnOkWithApplicationSettings()
    {
        // Arrange
        var expectedSettings = new ApplicationSettings
        {
            Ldap = new LdapSettings { LdapServer = "test.com", LdapPort = 389 },
            Api = new ApiSettings { ApiUrl = "https://api.test.com" }
        };
        _mockConfigService.Setup(s => s.GetAllSettingsAsync())
            .ReturnsAsync(expectedSettings);

        // Act
        var result = await _controller.GetAllSettings();

        // Assert
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var actualSettings = okResult.Value.Should().BeAssignableTo<ApplicationSettings>().Subject;
        actualSettings.Should().BeEquivalentTo(expectedSettings);
    }

    [Fact]
    public async Task UpdateAllSettings_ShouldReturnOk_WhenUpdateSuccessful()
    {
        // Arrange
        var settingsToUpdate = new ApplicationSettings
        {
            Ldap = new LdapSettings { LdapServer = "updated.com", LdapPort = 636 },
            Api = new ApiSettings { ApiUrl = "https://api.updated.com" }
        };
        _mockConfigService.Setup(s => s.UpdateAllSettingsAsync(It.IsAny<ApplicationSettings>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.UpdateAllSettings(settingsToUpdate);

        // Assert
        result.Should().BeOfType<OkResult>();
        _mockConfigService.Verify(s => s.UpdateAllSettingsAsync(settingsToUpdate), Times.Once);
    }

    #endregion
} 