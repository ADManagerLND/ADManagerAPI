using ADManagerAPI.Controllers;
using ADManagerAPI.Models;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace ADManagerAPI.Tests.Controllers
{
    public class FileImportControllerTests
    {
        private readonly Mock<ILdapService> _ldapServiceMock;
        private readonly Mock<ILogService> _logServiceMock;
        private readonly Mock<IConfigService> _configServiceMock;
        private readonly Mock<ISignalRService> _signalRServiceMock;
        private readonly Mock<ILogger<FileImportController>> _loggerMock;
        private readonly FileImportController _controller;

        public FileImportControllerTests()
        {
            _ldapServiceMock = new Mock<ILdapService>();
            _logServiceMock = new Mock<ILogService>();
            _configServiceMock = new Mock<IConfigService>();
            _signalRServiceMock = new Mock<ISignalRService>();
            _loggerMock = new Mock<ILogger<FileImportController>>();

            var spreadsheetImportServiceMock = new Mock<ISpreadsheetImportService>();
            var hubContextMock = new Mock<IHubContext<CsvImportHub>>();
            var serviceScopeFactoryMock = new Mock<IServiceScopeFactory>(); 

            _controller = new FileImportController(
                _ldapServiceMock.Object,
                _logServiceMock.Object,
                _configServiceMock.Object,
                _signalRServiceMock.Object,
                spreadsheetImportServiceMock.Object, 
                _loggerMock.Object,
                hubContextMock.Object, 
                serviceScopeFactoryMock.Object 
            );

            var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[]
            {
                new Claim(ClaimTypes.Name, "testuser")
            }, "mock"));
            _controller.ControllerContext = new ControllerContext()
            {
                HttpContext = new DefaultHttpContext() { User = user }
            };
        }

        // Tests pour GetSavedConfigs
        [Fact]
        public async Task GetSavedConfigs_ReturnsOkResult_WithListOfConfigs()
        {
            // Arrange
            var expectedConfigs = new List<SavedImportConfig>
            {
                new SavedImportConfig { Id = "1", Name = "Config 1" },
                new SavedImportConfig { Id = "2", Name = "Config 2" }
            };
            _configServiceMock.Setup(s => s.GetSavedImportConfigs()).ReturnsAsync(expectedConfigs);

            // Act
            var result = await _controller.GetSavedConfigs();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var actualConfigs = Assert.IsAssignableFrom<IEnumerable<SavedImportConfig>>(okResult.Value);
            Assert.Equal(expectedConfigs.Count, new List<SavedImportConfig>(actualConfigs).Count);
            _logServiceMock.Verify(l => l.Log("IMPORT", "Récupération des configurations sauvegardées"), Times.Once);
        }

        [Fact]
        public async Task GetSavedConfigs_ReturnsBadRequest_WhenServiceThrowsException()
        {
            // Arrange
            var exceptionMessage = "Service error";
            _configServiceMock.Setup(s => s.GetSavedImportConfigs()).ThrowsAsync(new System.Exception(exceptionMessage));

            // Act
            var result = await _controller.GetSavedConfigs();

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequestResult.Value);
            var errorObject = badRequestResult.Value.GetType().GetProperty("error")?.GetValue(badRequestResult.Value, null);
            Assert.Equal(exceptionMessage, errorObject as string);
            _logServiceMock.Verify(l => l.Log("IMPORT", It.Is<string>(s => s.Contains(exceptionMessage))), Times.Once);
        }

        // Tests pour SaveConfig
        [Fact]
        public async Task SaveConfig_ValidDto_ReturnsOkResult_WithSavedConfig()
        {
            // Arrange
            var configDto = new SavedImportConfigDto 
            { 
                Name = "Test Config", 
                ConfigData = new ImportConfigDto() // Assurez-vous que ConfigData n'est pas null
            };
            var savedConfig = new SavedImportConfig 
            { 
                Id = "new_id", 
                Name = configDto.Name, 
                CreatedBy = "testuser", 
                ConfigData = configDto.ConfigData.ToImportConfig()
            };

            _configServiceMock.Setup(s => s.SaveImportConfig(It.IsAny<SavedImportConfig>()))
                              .ReturnsAsync(savedConfig);

            // Act
            var result = await _controller.SaveConfig(configDto);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var actualConfig = Assert.IsType<SavedImportConfig>(okResult.Value);
            Assert.Equal(savedConfig.Id, actualConfig.Id);
            Assert.Equal(configDto.Name, actualConfig.Name);
            Assert.Equal("testuser", actualConfig.CreatedBy); // Vérifie que CreatedBy est défini
            _logServiceMock.Verify(l => l.Log("IMPORT", "Début du traitement de la requête SaveConfig avec DTO"), Times.Once);
            _logServiceMock.Verify(l => l.Log("IMPORT", It.Is<string>(s => s.Contains("Configuration convertie, tentative de sauvegarde"))), Times.Once);
        }

        [Fact]
        public async Task SaveConfig_NullDto_ReturnsBadRequest()
        {
            // Arrange
            SavedImportConfigDto? configDto = null;

            // Act
            var result = await _controller.SaveConfig(configDto!); 

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequestResult.Value);
            // Accéder à la propriété 'error' de manière robuste
            var errorProperty = badRequestResult.Value.GetType().GetProperty("error");
            Assert.NotNull(errorProperty);
            var errorMessage = errorProperty.GetValue(badRequestResult.Value) as string;
            Assert.Equal("Aucune donnée reçue", errorMessage);
            _logServiceMock.Verify(l => l.Log("IMPORT", "Le DTO reçu est null"), Times.Once);
        }

        [Fact]
        public async Task SaveConfig_MissingName_ReturnsBadRequest()
        {
            // Arrange
            var configDto = new SavedImportConfigDto { ConfigData = new ImportConfigDto() }; 

            // Act
            var result = await _controller.SaveConfig(configDto);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequestResult.Value);
            var errorProperty = badRequestResult.Value.GetType().GetProperty("error");
            Assert.NotNull(errorProperty);
            var errorMessage = errorProperty.GetValue(badRequestResult.Value) as string;
            Assert.Equal("Le nom de la configuration est requis.", errorMessage);
        }

        [Fact]
        public async Task SaveConfig_MissingConfigData_ReturnsBadRequest()
        {
            // Arrange
            var configDto = new SavedImportConfigDto { Name = "Test Config" }; 

            // Act
            var result = await _controller.SaveConfig(configDto);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequestResult.Value);
            var errorProperty = badRequestResult.Value.GetType().GetProperty("error");
            Assert.NotNull(errorProperty);
            var errorMessage = errorProperty.GetValue(badRequestResult.Value) as string;
            Assert.Equal("Les données de configuration sont requises.", errorMessage);
        }

        [Fact]
        public async Task SaveConfig_ServiceThrowsException_ReturnsBadRequest()
        {
            // Arrange
            var configDto = new SavedImportConfigDto 
            { 
                Name = "Test Config", 
                ConfigData = new ImportConfigDto() 
            };
            var exceptionMessage = "Service save error";
            _configServiceMock.Setup(s => s.SaveImportConfig(It.IsAny<SavedImportConfig>()))
                              .ThrowsAsync(new System.Exception(exceptionMessage));

            // Act
            var result = await _controller.SaveConfig(configDto);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequestResult.Value);
            // Accéder dynamiquement à la propriété 'error'
            var errorProperty = badRequestResult.Value.GetType().GetProperty("error");
            Assert.NotNull(errorProperty);
            var errorMessage = errorProperty.GetValue(badRequestResult.Value) as string;
            Assert.Equal(exceptionMessage, errorMessage);
            _logServiceMock.Verify(l => l.Log("IMPORT", It.Is<string>(s => s.Contains(exceptionMessage))), Times.Once);
        }
    }
} 