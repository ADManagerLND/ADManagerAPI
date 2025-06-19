using System.Collections.Concurrent;

namespace ADManagerAPI.Utils;

public class FileDataStore
{
    private static readonly ConcurrentDictionary<string, List<Dictionary<string, string>>> _csvData = new();
    private static readonly ConcurrentDictionary<string, Dictionary<string, object>> _rawFileData = new();

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
            _csvData.Clear();
        else
            _csvData.TryRemove(key, out _);
    }

    // 🔧 NOUVELLES MÉTHODES pour stocker les fichiers bruts
    public static void SetRawFileData(Dictionary<string, object> fileData, string? key = null)
    {
        key = key ?? DEFAULT_KEY;
        _rawFileData[key] = fileData;
    }

    public static Dictionary<string, object>? GetRawFileData(string? key = null)
    {
        key = key ?? DEFAULT_KEY;
        _rawFileData.TryGetValue(key, out var data);
        return data;
    }

    public static void ClearRawFileData(string? key = null)
    {
        if (key == null)
            _rawFileData.Clear();
        else
            _rawFileData.TryRemove(key, out _);
    }

    public static void ClearAllData(string? key = null)
    {
        ClearCsvData(key);
        ClearRawFileData(key);
    }
}