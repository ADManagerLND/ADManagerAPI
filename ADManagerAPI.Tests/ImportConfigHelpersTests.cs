using ADManagerAPI.Models;
using ADManagerAPI.Services.Utilities;
using Xunit;
using System.Collections.Generic;
using Moq;
using Microsoft.Extensions.Logging;

namespace ADManagerAPI.Tests
{
    public class ImportConfigHelpersTests
    {
        [Fact]
        public void EnsureValidConfig_NullConfig_ReturnsDefaultConfig_AndLogsWarning()
        {
            // Arrange
            ImportConfig? config = null;
            var loggerMock = new Mock<ILogger>();

            // Act
            var result = ImportConfigHelpers.EnsureValidConfig(config, loggerMock.Object);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("DC=domain,DC=local", result.DefaultOU);
            Assert.NotNull(result.ManualColumns);
            Assert.Empty(result.ManualColumns);
            Assert.NotNull(result.HeaderMapping);
            Assert.Empty(result.HeaderMapping);
            Assert.Equal(';', result.CsvDelimiter);
            Assert.NotNull(result.ClassGroupFolderCreationConfig);
            Assert.NotNull(result.TeamGroupCreationConfig);

            loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("La configuration d'import est null, création d'une configuration par défaut")),
                    null,
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.Once);
        }

        [Fact]
        public void EnsureValidConfig_NullManualColumns_InitializesList_AndLogsWarning()
        {
            // Arrange
            var loggerMock = new Mock<ILogger>();
            var config = new ImportConfig
            {
                ManualColumns = null,
                HeaderMapping = new Dictionary<string, string>(),
                ClassGroupFolderCreationConfig = new ClassGroupFolderCreationConfig(),
                TeamGroupCreationConfig = new TeamGroupCreationConfig()
            };

            // Act
            var result = ImportConfigHelpers.EnsureValidConfig(config, loggerMock.Object);

            // Assert
            Assert.NotNull(result.ManualColumns);
            Assert.Empty(result.ManualColumns);

            loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("config.ManualColumns est null, initialisation d'une liste vide")),
                    null,
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.Once);
        }

        [Fact]
        public void EnsureValidConfig_NullHeaderMapping_InitializesDictionary_AndLogsWarning()
        {
            // Arrange
            var loggerMock = new Mock<ILogger>();
            var config = new ImportConfig
            {
                ManualColumns = new List<string>(),
                HeaderMapping = null,
                ClassGroupFolderCreationConfig = new ClassGroupFolderCreationConfig(),
                TeamGroupCreationConfig = new TeamGroupCreationConfig()
            };

            // Act
            var result = ImportConfigHelpers.EnsureValidConfig(config, loggerMock.Object);

            // Assert
            Assert.NotNull(result.HeaderMapping);
            Assert.Empty(result.HeaderMapping);
            
            loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("config.HeaderMapping est null, initialisation d'un dictionnaire vide")),
                    null,
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.Once);
        }
        
        [Fact]
        public void EnsureValidConfig_NullClassGroupFolderCreationConfig_InitializesConfig_AndLogsWarning()
        {
            // Arrange
            var loggerMock = new Mock<ILogger>();
            var config = new ImportConfig
            {
                ManualColumns = new List<string>(),
                HeaderMapping = new Dictionary<string, string>(),
                ClassGroupFolderCreationConfig = null,
                TeamGroupCreationConfig = new TeamGroupCreationConfig() 
            };

            // Act
            var result = ImportConfigHelpers.EnsureValidConfig(config, loggerMock.Object);

            // Assert
            Assert.NotNull(result.ClassGroupFolderCreationConfig);
            
            loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("config.ClassGroupFolderCreationConfig est null, initialisation avec des valeurs par défaut implicites.")),
                    null,
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.Once);
        }

        [Fact]
        public void EnsureValidConfig_NullTeamGroupCreationConfig_InitializesConfig_AndLogsWarning()
        {
            // Arrange
            var loggerMock = new Mock<ILogger>();
            var config = new ImportConfig
            {
                ManualColumns = new List<string>(),
                HeaderMapping = new Dictionary<string, string>(),
                ClassGroupFolderCreationConfig = new ClassGroupFolderCreationConfig(),
                TeamGroupCreationConfig = null
            };

            // Act
            var result = ImportConfigHelpers.EnsureValidConfig(config, loggerMock.Object);

            // Assert
            Assert.NotNull(result.TeamGroupCreationConfig);
            
            loggerMock.Verify(
                x => x.Log(
                    Microsoft.Extensions.Logging.LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("config.TeamGroupCreationConfig est null, initialisation avec des valeurs par défaut implicites.")),
                    null,
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.Once);
        }

        [Fact]
        public void EnsureValidConfig_ValidConfig_ReturnsSameConfig()
        {
            // Arrange
            var config = new ImportConfig
            {
                DefaultOU = "OU=Test,DC=example,DC=com",
                ManualColumns = new List<string> { "col1", "col2" },
                HeaderMapping = new Dictionary<string, string> { { "csvHeader", "adAttribute" } },
                CsvDelimiter = ',',
                ClassGroupFolderCreationConfig = new ClassGroupFolderCreationConfig { CreateClassGroupFolderColumnName = "SomeColumn1" },
                TeamGroupCreationConfig = new TeamGroupCreationConfig { CreateTeamGroupColumnName = "SomeColumn2" }
            };
            var originalConfigCopy = new ImportConfig // To ensure no mutation if not needed
            {
                DefaultOU = config.DefaultOU,
                ManualColumns = new List<string>(config.ManualColumns),
                HeaderMapping = new Dictionary<string, string>(config.HeaderMapping),
                CsvDelimiter = config.CsvDelimiter,
                ClassGroupFolderCreationConfig = new ClassGroupFolderCreationConfig { CreateClassGroupFolderColumnName = config.ClassGroupFolderCreationConfig?.CreateClassGroupFolderColumnName },
                TeamGroupCreationConfig = new TeamGroupCreationConfig { CreateTeamGroupColumnName = config.TeamGroupCreationConfig?.CreateTeamGroupColumnName }
            };


            // Act
            var result = ImportConfigHelpers.EnsureValidConfig(config, null);

            // Assert
            Assert.Same(config, result); // Should return the same instance if no changes needed
            Assert.Equal(originalConfigCopy.DefaultOU, result.DefaultOU);
            Assert.Equal(originalConfigCopy.ManualColumns, result.ManualColumns);
            Assert.Equal(originalConfigCopy.HeaderMapping, result.HeaderMapping);
            Assert.Equal(originalConfigCopy.CsvDelimiter, result.CsvDelimiter);
            Assert.NotNull(result.ClassGroupFolderCreationConfig);
            Assert.Equal(originalConfigCopy.ClassGroupFolderCreationConfig?.CreateClassGroupFolderColumnName, result.ClassGroupFolderCreationConfig?.CreateClassGroupFolderColumnName);
            Assert.NotNull(result.TeamGroupCreationConfig);
            Assert.Equal(originalConfigCopy.TeamGroupCreationConfig?.CreateTeamGroupColumnName, result.TeamGroupCreationConfig?.CreateTeamGroupColumnName);
        }
    }
} 