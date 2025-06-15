using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ADManagerAPI.Models;
using ADManagerAPI.Services;
using ADManagerAPI.Services.Interfaces;
using ADManagerAPI.Services.Parse;
using ADManagerAPI.Services.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ADManagerAPI.Tests.Controllers
{
    public class SpreadsheetImportServiceTests
    {
        private readonly Mock<ILdapService> _ldapServiceMock;
        private readonly Mock<ILogService> _logServiceMock;
        private readonly Mock<ILogger<SpreadsheetImportService>> _loggerMock;
        private readonly Mock<IServiceScopeFactory> _serviceScopeFactoryMock;
        private readonly Mock<IFolderManagementService> _folderManagementServiceMock;
        private readonly Mock<ISpreadsheetParserService> _parserMock;
        private readonly SpreadsheetImportService _service;

        public SpreadsheetImportServiceTests()
        {
            _ldapServiceMock = new Mock<ILdapService>();
            _logServiceMock = new Mock<ILogService>();
            _loggerMock = new Mock<ILogger<SpreadsheetImportService>>();
            _serviceScopeFactoryMock = new Mock<IServiceScopeFactory>();
            _folderManagementServiceMock = new Mock<IFolderManagementService>();
            _parserMock = new Mock<ISpreadsheetParserService>();

            var parserServices = new List<ISpreadsheetParserService>
            {
                _parserMock.Object
            };

          
        }

        [Fact]
        public async Task AnalyzeSpreadsheetContentAsync_ValidFile_ReturnsSuccessfulAnalysisResult()
        {
            var mockCsvData = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string> { { "Prénom", "Jean" }, { "Nom", "Dupont" }, { "Email", "jean.dupont@adtst01.local" } },
                new Dictionary<string, string> { { "Prénom", "Marie" }, { "Nom", "Martin" }, { "Email", "marie.martin@adtst01.local" } }
            };

            var fileStream = new MemoryStream();
            var fileName = "test.csv";
            var config = new ImportConfig
            {
                DefaultOU = "DC=adtst01,DC=local",
                CsvDelimiter = ';',
                HeaderMapping = new Dictionary<string, string>
                {
                    { "Prénom", "givenName" },
                    { "Nom", "sn" },
                    { "Email", "mail" }
                },
                ManualColumns = new List<string>()
            };

            // Créer un mock de l'analyse
            var analysis = new ImportAnalysis
            {
                Actions = new List<ImportAction>
                {
                    new ImportAction
                    {
                        ActionType = ActionType.CREATE_USER,
                        Path = "DC=adtst01,DC=local",
                        ObjectName = "Jean Dupont",
                        Attributes = new Dictionary<string, string>
                        {
                            { "givenName", "Jean" },
                            { "sn", "Dupont" },
                            { "mail", "jean.dupont@example.com" }
                        }
                    }
                },
                Summary = new ImportSummary { TotalObjects = 2, CreateCount = 2 }
            };

            // Créer un mock du service pour contrôler le comportement interne
            var serviceMock = new Mock<SpreadsheetImportService>(
                new List<ISpreadsheetParserService> { _parserMock.Object },
                _ldapServiceMock.Object,
                _logServiceMock.Object,
                _loggerMock.Object,
                _serviceScopeFactoryMock.Object,
                _folderManagementServiceMock.Object
            ) { CallBase = true };

            // Setup du parser mock pour retourner des données simulées
       

            // Mock des méthodes LDAP pour simuler qu'aucun objet n'existe déjà
            _ldapServiceMock.Setup(x => x.GetOrganizationalUnitPathsRecursiveAsync(
                    It.IsAny<string>()))
                .ReturnsAsync(new List<string>());

            _ldapServiceMock.Setup(x => x.GetUsersInOUAsync(
                    It.IsAny<string>()))
                .ReturnsAsync(new List<string>());

            // Mock pour vérifier l'existence d'OU
            _ldapServiceMock.Setup(x => x.OrganizationalUnitExistsAsync(It.IsAny<string>()))
                .ReturnsAsync(true);

           
            // Act
            var result = await serviceMock.Object.AnalyzeSpreadsheetContentAsync(fileStream, fileName, config);
            // Assert
            Assert.True(result.Success);
            Assert.NotNull(result.CsvHeaders);
            Assert.NotNull(result.PreviewData);
            Assert.NotNull(result.Analysis);
            Assert.Equal(2, result.CsvData.Count);
        }

        [Fact]
        public async Task AnalyzeSpreadsheetContentAsync_EmptyFile_ReturnsFailure()
        {
            // Arrange
            var fileStream = new MemoryStream();
            var fileName = "empty.csv";
            var config = new ImportConfig();

            // Act
            var result = await _service.AnalyzeSpreadsheetContentAsync(fileStream, fileName, config);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("vide ou invalide", result.ErrorMessage);
        }

        [Fact]
        public async Task AnalyzeSpreadsheetDataAsync_ValidData_ReturnsSuccessfulAnalysis()
        {
            // Arrange
            var mockCsvData = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string> { { "Prénom", "Jean" }, { "Nom", "Dupont" }, { "Email", "jean.dupont@example.com" } },
                new Dictionary<string, string> { { "Prénom", "Marie" }, { "Nom", "Martin" }, { "Email", "marie.martin@example.com" } }
            };

            var config = new ImportConfig
            {
                DefaultOU = "DC=adtst01,DC=local",
                CsvDelimiter = ';',
                HeaderMapping = new Dictionary<string, string>
                {
                    { "Prénom", "givenName" },
                    { "Nom", "sn" },
                    { "Email", "mail" }
                }
            };

            // Créer un mock de l'analyse
            var analysis = new ImportAnalysis
            {
                Actions = new List<ImportAction>
                {
                    new ImportAction
                    {
                        ActionType = ActionType.CREATE_USER,
                        Path = "DC=adtst01,DC=local",
                        ObjectName = "Jean Dupont",
                        Attributes = new Dictionary<string, string>
                        {
                            { "givenName", "Jean" },
                            { "sn", "Dupont" },
                            { "mail", "jean.dupont@example.com" }
                        }
                    }
                },
                Summary = new ImportSummary { TotalObjects = 2, CreateCount = 2 }
            };

            // Créer un mock du service pour contrôler le comportement interne
            var serviceMock = new Mock<SpreadsheetImportService>(
                new List<ISpreadsheetParserService> { _parserMock.Object },
                _ldapServiceMock.Object,
                _logServiceMock.Object,
                _loggerMock.Object,
                _serviceScopeFactoryMock.Object,
                _folderManagementServiceMock.Object
            ) { CallBase = true };

            // Mock des méthodes LDAP qui sont utilisées dans l'analyse
            _ldapServiceMock.Setup(x => x.GetOrganizationalUnitPathsRecursiveAsync(
                    It.IsAny<string>()))
                .ReturnsAsync(new List<string>());

            _ldapServiceMock.Setup(x => x.GetUsersInOUAsync(
                    It.IsAny<string>()))
                .ReturnsAsync(new List<string>());

            // Mock pour vérifier l'existence d'OU
            _ldapServiceMock.Setup(x => x.OrganizationalUnitExistsAsync(It.IsAny<string>()))
                .ReturnsAsync(true);

  
            // Act
            var result = await serviceMock.Object.AnalyzeSpreadsheetDataAsync(mockCsvData, config);

            // Assert
            Assert.True(result.Success);
            Assert.NotNull(result.Analysis);
            Assert.NotNull(result.CsvHeaders);
            Assert.Equal(mockCsvData.FirstOrDefault()?.Keys.Count, result.CsvHeaders.Count);
        }

        [Fact]
        public async Task AnalyzeSpreadsheetDataAsync_EmptyData_ReturnsFailure()
        {
            // Arrange
            var emptyData = new List<Dictionary<string, string>>();
            var config = new ImportConfig();

            // Act
            var result = await _service.AnalyzeSpreadsheetDataAsync(emptyData, config);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Aucune donnée", result.ErrorMessage);
        }

        [Fact]
        public async Task ExecuteImportFromAnalysisAsync_ValidAnalysis_ReturnsSuccessfulImportResult()
        {
            // Arrange
            var analysis = new ImportAnalysis
            {
                Actions = new List<ImportAction>
                {
                    new ImportAction
                    {
                        ActionType = ActionType.CREATE_USER,
                        Path = "OU=Users,DC=adtst01,DC=local",
                        ObjectName = "Jean Dupont",
                        Attributes = new Dictionary<string, string>
                        {
                            { "givenName", "Jean" },
                            { "sn", "Dupont" },
                            { "mail", "jean.dupont@example.com" },
                            { "userPrincipalName", "jean.dupont@domain.local" },
                            { "sAMAccountName", "jean.dupont" }
                        }
                    }
                },
                Summary = new ImportSummary { TotalObjects = 1, CreateCount = 1 }
            };

            var config = new ImportConfig
            {
                DefaultOU = "DC=adtst01,DC=local"
            };

            // Mock de la méthode LDAP pour simuler un succès lors de la création de l'utilisateur
            _ldapServiceMock.Setup(x => x.CreateUser(
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<string>()))
                .Returns(new UserModel { DisplayName = "Jean Dupont" });

            // Act
            var result = await _service.ExecuteImportFromAnalysisAsync(analysis, config);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(1, result.TotalSucceeded);
            Assert.Equal(0, result.ErrorCount);
       
        }

        [Fact]
        public async Task ProcessSpreadsheetDataAsync_ValidData_PerformsImportSuccessfully()
        {
            // Arrange
            var mockCsvData = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string> { { "Prénom", "Jean" }, { "Nom", "Dupont" }, { "Email", "jean.dupont@example.com" } }
            };

            var config = new ImportConfig
            {
                DefaultOU = "DC=adtst01,DC=local",
                HeaderMapping = new Dictionary<string, string>
                {
                    { "Prénom", "givenName" },
                    { "Nom", "sn" },
                    { "Email", "mail" },
                    { "sAMAccountName", "%Prénom%.%Nom%:lowercase" }
                },
                ManualColumns = new List<string>()
            };

            // Simuler le processus d'analyse qui génère des actions valides
            var analysis = new ImportAnalysis
            {
                Actions = new List<ImportAction>
                {
                    new ImportAction
                    {
                        ActionType = ActionType.CREATE_USER,
                        Path = "DC=adtst01,DC=local",
                        ObjectName = "Jean Dupont",
                        Attributes = new Dictionary<string, string>
                        {
                            { "givenName", "Jean" },
                            { "sn", "Dupont" },
                            { "mail", "jean.dupont@example.com" },
                            { "sAMAccountName", "jean.dupont" }
                        }
                    }
                },
                Summary = new ImportSummary { TotalObjects = 1, CreateCount = 1 }
            };

            // Mockez AnalyzeSpreadsheetDataAsync pour retourner un résultat valide avec l'analyse
            var analysisResult = new AnalysisResult
            {
                Success = true,
                IsValid = true,
                CsvData = mockCsvData,
                CsvHeaders = mockCsvData[0].Keys.ToList(),
                Analysis = analysis
            };

            // Mock pour vérifier l'existence d'OU
            _ldapServiceMock.Setup(x => x.OrganizationalUnitExistsAsync(It.IsAny<string>()))
                .ReturnsAsync(true);

            // Mock des méthodes LDAP utilisées dans l'analyse
            _ldapServiceMock.Setup(x => x.GetOrganizationalUnitPathsRecursiveAsync(It.IsAny<string>()))
                .ReturnsAsync(new List<string>());

            _ldapServiceMock.Setup(x => x.GetUsersInOUAsync(It.IsAny<string>()))
                .ReturnsAsync(new List<string>());

            // Mocker la création d'utilisateur
            _ldapServiceMock.Setup(x => x.CreateUser(
                    It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>>(),
                    It.IsAny<string>()))
                .Returns(new UserModel { DisplayName = "Jean Dupont" });

            // Mocker directement AnalyzeSpreadsheetDataAsync au lieu de compter sur son implémentation
            var serviceMock = new Mock<SpreadsheetImportService>(
                new List<ISpreadsheetParserService> { _parserMock.Object },
                _ldapServiceMock.Object,
                _logServiceMock.Object,
                _loggerMock.Object,
                _serviceScopeFactoryMock.Object,
                _folderManagementServiceMock.Object
            ) { CallBase = true };

    
            // Simuler le résultat d'exécution
          

        }
    }
} 