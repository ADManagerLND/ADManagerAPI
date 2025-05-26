namespace ADManagerAPI.Services
{
    public static class CsvDataStore
    {
        private static List<Dictionary<string, string>>? _currentCsvData;

        public static void SetCsvData(List<Dictionary<string, string>> data)
        {
            _currentCsvData = data;
        }

        public static List<Dictionary<string, string>>? GetCsvData()
        {
            return _currentCsvData;
        }

        public static void ClearCsvData()
        {
            _currentCsvData = null;
        }
    }
}