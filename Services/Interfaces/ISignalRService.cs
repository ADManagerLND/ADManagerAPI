using ADManagerAPI.Models;

namespace ADManagerAPI.Services.Interfaces
{
    /// <summary>
    /// Interface pour le service SignalR qui gère les communications en temps réel
    /// spécifiquement pour les fonctionnalités d'importation CSV
    /// </summary>
    public interface ISignalRService
    {
        /// <summary>
        /// Vérifie si le service SignalR est connecté et opérationnel
        /// </summary>
        /// <returns>True si le service est connecté, sinon False</returns>
        Task<bool> IsConnectedAsync();
        
        /// <summary>
        /// Envoie la progression de l'analyse CSV à un client spécifique
        /// </summary>
        /// <param name="connectionId">ID de connexion du client</param>
        /// <param name="progress">Pourcentage de progression (0-100)</param>
        /// <param name="status">Statut actuel (analyzing, error, completed, etc.)</param>
        /// <param name="message">Message descriptif</param>
        /// <param name="analysis">Résultat d'analyse (optionnel)</param>
        Task SendCsvAnalysisProgressAsync(string connectionId, int progress, string status, string message, ImportAnalysis? analysis = null);
        
        /// <summary>
        /// Envoie le résultat complet d'une analyse CSV à un client spécifique
        /// </summary>
        /// <param name="connectionId">ID de connexion du client</param>
        /// <param name="result">Résultat de l'analyse</param>
        Task SendCsvAnalysisCompleteAsync(string connectionId, AnalysisResult result);
        
        /// <summary>
        /// Envoie une notification d'erreur d'analyse CSV à un client spécifique
        /// </summary>
        /// <param name="connectionId">ID de connexion du client</param>
        /// <param name="errorMessage">Message d'erreur</param>
        Task SendCsvAnalysisErrorAsync(string connectionId, string errorMessage);
        
        /// <summary>
        /// Envoie le résultat complet d'un import CSV à un client spécifique
        /// </summary>
        /// <param name="connectionId">ID de connexion du client</param>
        /// <param name="result">Résultat de l'import</param>
        Task SendCsvImportCompleteAsync(string connectionId, ImportResult result);
        
        /// <summary>
        /// Traite un upload CSV pour un client spécifique
        /// </summary>
        /// <param name="connectionId">ID de connexion du client</param>
        /// <param name="fileStream">Flux du fichier</param>
        /// <param name="fileName">Nom du fichier</param>
        /// <param name="config">Configuration d'import</param>
        Task ProcessCsvUpload(string? connectionId, Stream fileStream, string fileName, ImportConfig config);
        
        /// <summary>
        /// Envoie une notification à un utilisateur spécifique
        /// </summary>
        /// <param name="userId">ID de l'utilisateur</param>
        /// <param name="message">Message de notification</param>
        Task SendNotificationAsync(string userId, string message);
        
        /// <summary>
        /// Diffuse un message à tous les clients connectés
        /// </summary>
        /// <param name="message">Message à diffuser</param>
        Task BroadcastAsync(string message);
    }
} 