namespace DriveTester.Models;

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string Message { get; set; } = string.Empty;

    public string TimeFormatted => Timestamp.ToString("HH:mm:ss");

    public override string ToString() => $"[{TimeFormatted}] [{Level}] {Message}";
}
