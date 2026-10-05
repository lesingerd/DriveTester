using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriveTester.Models;

public enum SessionStatus
{
    InProgress,
    Completed,
    Aborted
}

public class TestSessionState
{
    public string Version { get; set; } = "1.0";
    public SessionStatus Status { get; set; } = SessionStatus.InProgress;
    public ulong BaseSeed { get; set; }

    // Configuration snapshot
    public int PlannedRounds { get; set; }
    public CapacityTargetMode TargetMode { get; set; }
    public double CustomCapacityGB { get; set; }
    public FileSizePreset SizePreset { get; set; }
    public bool FlushBuffersDirectly { get; set; }
    public bool StopOnFirstError { get; set; }
    public bool EmptyFilesAfterEachRound { get; set; }
    public long TargetBytesPerRound { get; set; }

    // Execution progress
    public int CurrentRound { get; set; } = 1;
    public TestPhase CurrentPhase { get; set; } = TestPhase.Writing;
    public bool IsGracefullyPaused { get; set; } = false;
    public string? InFlightFileName { get; set; }
    public int InFlightFileIndex { get; set; }

    public List<RoundResult> CompletedRoundsResults { get; set; } = new();

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    public static string GetSessionFilePath(string driveRoot, string testFolderName)
    {
        return Path.Combine(driveRoot, testFolderName, "session_state.json");
    }

    public void Save(string driveRoot, string testFolderName)
    {
        try
        {
            var dir = Path.Combine(driveRoot, testFolderName);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            LastUpdatedAt = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var path = GetSessionFilePath(driveRoot, testFolderName);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    public static TestSessionState? TryLoad(string driveRoot, string testFolderName)
    {
        try
        {
            var path = GetSessionFilePath(driveRoot, testFolderName);
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<TestSessionState>(json);
            return state;
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string driveRoot, string testFolderName)
    {
        try
        {
            var path = GetSessionFilePath(driveRoot, testFolderName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
