namespace ADManagerAPI.Models
{
    /// <summary>
    /// Cache pour l'analyse optimisée des utilisateurs - évite les appels LDAP répétés
    /// </summary>
    public class UserAnalysisCache
    {
        /// <summary>
        /// Cache des utilisateurs LDAP existants (sAMAccountName -> LdapUser)
        /// </summary>
        public Dictionary<string, UserModel> ExistingUsers { get; set; } = new Dictionary<string, UserModel>(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// OUs existantes (chemins DN complets)
        /// </summary>
        public HashSet<string> ExistingOUs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Attributs des utilisateurs pour comparaison optimisée
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> UserAttributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Groupes par OU pour optimiser l'ajout automatique aux groupes
        /// </summary>
        public Dictionary<string, List<string>> GroupsByOu { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        
        /// <summary>
        /// Statistiques du cache pour le monitoring
        /// </summary>
        public CacheStatistics Statistics { get; set; } = new();
    }

    /// <summary>
    /// Statistiques du cache pour le monitoring des performances
    /// </summary>
    public class CacheStatistics
    {
        public int TotalUsersLoaded { get; set; }
        public int TotalOUsLoaded { get; set; }
        public int CacheHits { get; set; }
        public int CacheMisses { get; set; }
        public TimeSpan LoadTime { get; set; }
        
        public double HitRatio => TotalRequests > 0 ? (double)CacheHits / TotalRequests : 0;
        public int TotalRequests => CacheHits + CacheMisses;
    }
}
