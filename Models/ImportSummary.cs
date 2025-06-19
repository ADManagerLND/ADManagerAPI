namespace ADManagerAPI.Models;

public class DetailedImportSummary
{
    public int TotalItems { get; set; } = 0;
    public int SuccessCount { get; set; } = 0;
    public int ErrorCount { get; set; } = 0;
    public int CreateCount { get; set; } = 0;
    public int UpdateCount { get; set; } = 0;
    public int DeleteCount { get; set; } = 0;
    public int MoveCount { get; set; } = 0;
    public int SkippedCount { get; set; } = 0;
}