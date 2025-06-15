// Fichier potentiel: Models/AnalysisDataStore.cs
using System.Collections.Concurrent;
using ADManagerAPI.Models;

namespace ADManagerAPI.Utils
{
    public static class AnalysisDataStore
    {
        // Utilisation d'un ConcurrentDictionary pour gérer plusieurs utilisateurs par connectionId
        private static readonly ConcurrentDictionary<string, ImportAnalysis> _analysisData = new();
        private static readonly object _lock = new object();

        // Méthodes avec connectionId pour multi-utilisateurs
        public static void SetAnalysis(string connectionId, ImportAnalysis analysis)
        {
            if (string.IsNullOrEmpty(connectionId))
            {
                Console.WriteLine("[AnalysisDataStore] ERREUR: connectionId null ou vide dans SetAnalysis");
                return;
            }

            _analysisData.AddOrUpdate(connectionId, analysis, (key, old) => analysis);
            Console.WriteLine($"[AnalysisDataStore] Analyse stockée pour connectionId '{connectionId}'. Actions: {analysis?.Actions?.Count ?? 0}");
        }

        public static ImportAnalysis? GetAnalysis(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
            {
                Console.WriteLine("[AnalysisDataStore] ERREUR: connectionId null ou vide dans GetAnalysis");
                return null;
            }

            _analysisData.TryGetValue(connectionId, out var analysis);
            Console.WriteLine($"[AnalysisDataStore] Récupération analyse pour connectionId '{connectionId}'. Trouvée: {analysis != null}. Actions: {analysis?.Actions?.Count ?? 0}");
            return analysis;
        }

        public static void ClearAnalysis(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
                return;

            bool removed = _analysisData.TryRemove(connectionId, out _);
            Console.WriteLine($"[AnalysisDataStore] Suppression analyse pour connectionId '{connectionId}'. Supprimée: {removed}");
        }

        // Méthodes legacy pour compatibilité (utilise une clé par défaut)
        private static readonly string DEFAULT_CONNECTION_ID = "default";

        public static void SetLatestAnalysis(ImportAnalysis analysis)
        {
            Console.WriteLine("[AnalysisDataStore] ATTENTION: Utilisation de SetLatestAnalysis (legacy). Recommandé d'utiliser SetAnalysis avec connectionId.");
            SetAnalysis(DEFAULT_CONNECTION_ID, analysis);
        }

        public static ImportAnalysis? GetLatestAnalysis()
        {
            Console.WriteLine("[AnalysisDataStore] ATTENTION: Utilisation de GetLatestAnalysis (legacy). Recommandé d'utiliser GetAnalysis avec connectionId.");
            return GetAnalysis(DEFAULT_CONNECTION_ID);
        }

        public static void ClearLatestAnalysis()
        {
            Console.WriteLine("[AnalysisDataStore] Utilisation de ClearLatestAnalysis (legacy).");
            ClearAnalysis(DEFAULT_CONNECTION_ID);
        }

        // Méthode utilitaire pour debug
        public static void LogCurrentState()
        {
            Console.WriteLine($"[AnalysisDataStore] État actuel: {_analysisData.Count} analyses stockées");
            foreach (var kvp in _analysisData)
            {
                Console.WriteLine($"  - ConnectionId: '{kvp.Key}', Actions: {kvp.Value?.Actions?.Count ?? 0}");
            }
        }
    }
}
