namespace DriveTester.Models;

public class RoundResult
{
    public int RoundNumber { get; set; }
    public long BytesWritten { get; set; }
    public long BytesVerified { get; set; }
    public int TotalFiles { get; set; }

    public TimeSpan WriteDuration { get; set; }
    public TimeSpan ReadDuration { get; set; }
    public TimeSpan TotalRoundDuration => WriteDuration + ReadDuration;

    public double AvgWriteSpeedMBps { get; set; }
    public double PeakWriteSpeedMBps { get; set; }
    public double MinWriteSpeedMBps { get; set; }

    public double AvgReadSpeedMBps { get; set; }
    public double PeakReadSpeedMBps { get; set; }
    public double MinReadSpeedMBps { get; set; }

    public int ErrorCount { get; set; }
    public List<string> ErrorMessages { get; set; } = new();

    public bool IsPassed => ErrorCount == 0 && BytesVerified == BytesWritten && BytesWritten > 0;

    public string StatusBadge => IsPassed ? "PASSED" : "FAILED";

    public double WrittenGB => BytesWritten / (1024.0 * 1024.0 * 1024.0);
    public double VerifiedGB => BytesVerified / (1024.0 * 1024.0 * 1024.0);
}
