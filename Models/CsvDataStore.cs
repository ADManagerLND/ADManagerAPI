using System.Collections.Concurrent;

namespace ADManagerAPI.Models
{
    public class CsvDataStore
    {
        private static readonly ConcurrentDictionary<string, List<Dictionary<string, string>>> _csvData = new();
        
        private static readonly string DEFAULT_KEY = "default";
        

        public static void SetCsvData(List<Dictionary<string, string>> data, string? key = null)
        {
            key = key ?? DEFAULT_KEY;
            _csvData[key] = data;
        }

        public static List<Dictionary<string, string>>? GetCsvData(string? key = null)
        {
            key = key ?? DEFAULT_KEY;
            _csvData.TryGetValue(key, out var data);
            return data;
        }
        
        public static void ClearCsvData(string? key = null)
        {
            if (key == null)
            {
                _csvData.Clear();
            }
            else
            {
                _csvData.TryRemove(key, out _);
            }
        }
    }
} 