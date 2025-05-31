using ADManagerAPI.Controllers;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ADManagerAPI.Tests.Controllers
{
    public class ConfigControllerTests
    {
        private readonly Mock<IConfigService> _configServiceMock;
        private readonly Mock<ILogger<ConfigController>> _loggerMock;
        private readonly ConfigController _controller;

        public ConfigControllerTests()
        {
            _configServiceMock = new Mock<IConfigService>();
            _loggerMock = new Mock<ILogger<ConfigController>>();
            _controller = new ConfigController(_configServiceMock.Object, _loggerMock.Object);
        }

        [Fact]
        public async Task GetSavedImportConfigs_ReturnsOkResult_WithListOfConfigs()
        {
            // Arrange
            var expectedConfigs = new List<SavedImportConfig>
            {
                new SavedImportConfig { Id = "1", Name = "Import Config 1" },
                new SavedImportConfig { Id = "2", Name = "Import Config 2" }
            };
            _configServiceMock.Setup(s => s.GetSavedImportConfigs()).ReturnsAsync(expectedConfigs);

            // Act
            var result = await _controller.GetSavedImportConfigs();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            var actualConfigs = Assert.IsAssignableFrom<List<SavedImportConfig>>(okResult.Value);
            Assert.Equal(expectedConfigs.Count, actualConfigs.Count);
        }

        [Fact]
        public async Task GetSavedImportConfigs_ServiceThrowsException_ReturnsStatusCode500()
        {
            // Arrange
            var exceptionMessage = "Erreur de service simulée";
            _configServiceMock.Setup(s => s.GetSavedImportConfigs()).ThrowsAsync(new Exception(exceptionMessage));

            // Act
            var result = await _controller.GetSavedImportConfigs();

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(500, statusCodeResult.StatusCode);
            Assert.Equal("Une erreur est survenue lors de la récupération des configurations d'import", statusCodeResult.Value);

            _loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Erreur lors de la récupération des configurations d'import")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
                Times.Once);
        }

        // Tests pour SaveImportConfig
        [Fact]
        public async Task SaveImportConfig_ValidConfig_ReturnsOkResult_WithSavedConfig()
        {
            // Arrange
            var newConfig = new SavedImportConfig { Name = "New Import Config" };
            var savedConfigFromService = new SavedImportConfig { Id = "newId", Name = newConfig.Name };

            _configServiceMock.Setup(s => s.SaveImportConfig(newConfig)).ReturnsAsync(savedConfigFromService);

            // Act
            var result = await _controller.SaveImportConfig(newConfig);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            var actualConfig = Assert.IsType<SavedImportConfig>(okResult.Value);
            Assert.Equal(savedConfigFromService.Id, actualConfig.Id);
            Assert.Equal(newConfig.Name, actualConfig.Name);
        }

        [Fact]
        public async Task SaveImportConfig_ServiceThrowsException_ReturnsStatusCode500()
        {
            // Arrange
            var newConfig = new SavedImportConfig { Name = "New Import Config" };
            var exceptionMessage = "Erreur de sauvegarde simulée";
            _configServiceMock.Setup(s => s.SaveImportConfig(newConfig)).ThrowsAsync(new Exception(exceptionMessage));

            // Act
            var result = await _controller.SaveImportConfig(newConfig);

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(500, statusCodeResult.StatusCode);
            Assert.Equal("Une erreur est survenue lors de la sauvegarde de la configuration d'import", statusCodeResult.Value);

            _loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Erreur lors de la sauvegarde de la configuration d'import")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
                Times.Once);
        }

        // Tests pour DeleteImportConfig
        [Fact]
        public async Task DeleteImportConfig_ConfigExists_ReturnsOkResult()
        {
            // Arrange
            var configId = "config123";
            _configServiceMock.Setup(s => s.DeleteImportConfig(configId)).ReturnsAsync(true);

            // Act
            var result = await _controller.DeleteImportConfig(configId);

            // Assert
            Assert.IsType<OkResult>(result);
        }

        [Fact]
        public async Task DeleteImportConfig_ConfigNotFound_ReturnsNotFoundResult()
        {
            // Arrange
            var configId = "nonExistentConfig";
            _configServiceMock.Setup(s => s.DeleteImportConfig(configId)).ReturnsAsync(false);

            // Act
            var result = await _controller.DeleteImportConfig(configId);

            // Assert
            var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal($"Configuration d'import avec l'ID {configId} non trouvée", notFoundResult.Value);
        }

        [Fact]
        public async Task DeleteImportConfig_ServiceThrowsException_ReturnsStatusCode500()
        {
            // Arrange
            var configId = "configToFail";
            var exceptionMessage = "Erreur de suppression simulée";
            _configServiceMock.Setup(s => s.DeleteImportConfig(configId)).ThrowsAsync(new Exception(exceptionMessage));

            // Act
            var result = await _controller.DeleteImportConfig(configId);

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, statusCodeResult.StatusCode);
            Assert.Equal("Une erreur est survenue lors de la suppression de la configuration d'import", statusCodeResult.Value);

            _loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Erreur lors de la suppression de la configuration d'import")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
                Times.Once);
        }

        #region Configuration générale

        [Fact]
        public async Task GetAppConfig_ReturnsOkResult_WithApiSettings()
        {
            // Arrange
            var expectedConfig = new ApiSettings { ApiUrl = "https://test.api" };
            _configServiceMock.Setup(s => s.GetApiSettingsAsync()).ReturnsAsync(expectedConfig);

            // Act
            var result = await _controller.GetAppConfig();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            var actualConfig = Assert.IsType<ApiSettings>(okResult.Value);
            Assert.Equal(expectedConfig.ApiUrl, actualConfig.ApiUrl);
        }

        [Fact]
        public async Task GetAppConfig_ServiceThrowsException_ReturnsStatusCode500()
        {
            // Arrange
            _configServiceMock.Setup(s => s.GetApiSettingsAsync()).ThrowsAsync(new Exception("Simulated error"));

            // Act
            var result = await _controller.GetAppConfig();

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(500, statusCodeResult.StatusCode);
            Assert.Equal("Une erreur est survenue lors de la récupération de la configuration", statusCodeResult.Value);
            _loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Erreur lors de la récupération de la configuration")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
                Times.Once);
        }

        [Fact]
        public async Task UpdateAppConfig_ValidConfig_ReturnsOkResult()
        {
            // Arrange
            var configToUpdate = new ApiSettings { ApiUrl = "https://new.api" };
            _configServiceMock.Setup(s => s.UpdateApiSettingsAsync(configToUpdate)).Returns(Task.CompletedTask);

            // Act
            var result = await _controller.UpdateAppConfig(configToUpdate);

            // Assert
            Assert.IsType<OkResult>(result);
        }

        [Fact]
        public async Task UpdateAppConfig_ServiceThrowsException_ReturnsStatusCode500()
        {
            // Arrange
            var configToUpdate = new ApiSettings { ApiUrl = "https://fail.api" };
            _configServiceMock.Setup(s => s.UpdateApiSettingsAsync(configToUpdate)).ThrowsAsync(new Exception("Simulated error"));

            // Act
            var result = await _controller.UpdateAppConfig(configToUpdate);

            // Assert
            var statusCodeResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, statusCodeResult.StatusCode);
            Assert.Equal("Une erreur est survenue lors de la mise à jour de la configuration", statusCodeResult.Value);
            _loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Erreur lors de la mise à jour de la configuration")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
                Times.Once);
        }

        #endregion
    }
} 