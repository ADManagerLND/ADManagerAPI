// Fichier potentiel: Models/AnalysisDataStore.cs
using System.Collections.Concurrent;
using ADManagerAPI.Models;

namespace ADManagerAPI.Utils
{
    public static class AnalysisDataStore
    {
        // Pour un seul utilisateur, un simple champ statique suffit.
        // Si vous revenez un jour à une gestion par ConnectionId, un ConcurrentDictionary serait approprié.
        private static ImportAnalysis _latestAnalysis;
        private static readonly object _lock = new object();

        public static void SetLatestAnalysis(ImportAnalysis analysis)
        {
            lock (_lock)
            {
                _latestAnalysis = analysis;
            }
        }

        public static ImportAnalysis GetLatestAnalysis()
        {
            lock (_lock)
            {
                // Retourner une copie ou l'objet directement dépend si l'objet ImportAnalysis est mutable
                // et si des modifications concurrentes sont un souci après récupération.
                // Pour l'instant, on retourne directement.
                return _latestAnalysis;
            }
        }

        public static void ClearLatestAnalysis()
        {
            lock (_lock)
            {
                _latestAnalysis = null;
            }
        }
    }
}