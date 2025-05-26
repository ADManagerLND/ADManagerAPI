namespace ADManagerAPI.Models;

public class LogModel
{
    public DateTime Timestamp { get; set; }
    public string Action { get; set; }
    public string Message { get; set; }
}