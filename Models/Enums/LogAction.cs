namespace ADManagerAPI.Models
{
    /// <summary>
    /// Énumération des types d'actions à journaliser
    /// </summary>
    public enum LogAction
    {
        /// <summary>
        /// Action d'information standard
        /// </summary>
        Information,
        
        /// <summary>
        /// Action réussie
        /// </summary>
        Success,
        
        /// <summary>
        /// Avertissement
        /// </summary>
        Warning,
        
        /// <summary>
        /// Erreur
        /// </summary>
        Error,
        
        /// <summary>
        /// Création d'un utilisateur
        /// </summary>
        CreateUser,
        
        /// <summary>
        /// Modification d'un utilisateur
        /// </summary>
        UpdateUser,
        
        /// <summary>
        /// Suppression d'un utilisateur
        /// </summary>
        DeleteUser,
        
        /// <summary>
        /// Déplacement d'un utilisateur
        /// </summary>
        MoveUser,
        
        UserCreated = CreateUser,
        UserUpdated = UpdateUser,
        UserDeleted = DeleteUser,
        UserMoved = MoveUser,
        
        /// <summary>
        /// Création d'une unité organisationnelle
        /// </summary>
        CreateOU,
        
        /// <summary>
        /// Suppression d'une unité organisationnelle
        /// </summary>
        DeleteOU,
        
        /// <summary>
        /// Import de données
        /// </summary>
        Import,
        
        /// <summary>
        /// Analyse de fichier
        /// </summary>
        Analysis
    }
} 