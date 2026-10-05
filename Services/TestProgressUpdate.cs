using DriveTester.Models;

namespace DriveTester.Services;

public class TestProgressUpdate
{
    public int CurrentRound { get; set; }
    public int TotalRounds { get; set; }
    public TestPhase CurrentPhase { get; set; }

    public string CurrentFileName { get; set; } = string.Empty;
    public int CurrentFileIndex { get; set; }
    public int TotalFilesInRound { get; set; }
    public long CurrentFileBytesProcessed { get; set; }
    public long CurrentFileTotalBytes { get; set; }

    public long PhaseBytesProcessed { get; set; }
    public long PhaseTotalBytes { get; set; }

    public long OverallBytesProcessed { get; set; }
    public long OverallTotalBytes { get; set; }

    public double CurrentSpeedMBps { get; set; }
    public double AverageSpeedMBps { get; set; }
    public double PeakSpeedMBps { get; set; }

    public TimeSpan ElapsedTime { get; set; }
    public TimeSpan EstimatedTimeRemaining { get; set; }

    public int ErrorCount { get; set; }
    public string StatusMessage { get; set; } = string.Empty;

    public double PhaseProgressPercent => PhaseTotalBytes > 0
        ? Math.Clamp((double)PhaseBytesProcessed / PhaseTotalBytes * 100.0, 0.0, 100.0)
        : 0.0;

    public double FileProgressPercent => CurrentFileTotalBytes > 0
        ? Math.Clamp((double)CurrentFileBytesProcessed / CurrentFileTotalBytes * 100.0, 0.0, 100.0)
        : 0.0;

    public double OverallProgressPercent => OverallTotalBytes > 0
        ? Math.Clamp((double)OverallBytesProcessed / OverallTotalBytes * 100.0, 0.0, 100.0)
        : 0.0;
}
