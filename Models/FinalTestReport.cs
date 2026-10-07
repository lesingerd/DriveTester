using System.Text;

namespace DriveTester.Models;

public class FinalTestReport
{
    public DriveTargetInfo Drive { get; set; } = new();
    public TestConfiguration Configuration { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan TotalDuration => EndTime - StartTime;

    public int PlannedRounds { get; set; }
    public int CompletedRounds { get; set; }
    public long TotalBytesWritten { get; set; }
    public long TotalBytesVerified { get; set; }
    public int TotalErrorsCount { get; set; }

    public double OverallAvgWriteSpeedMBps { get; set; }
    public double OverallAvgReadSpeedMBps { get; set; }

    public List<RoundResult> Rounds { get; set; } = new();
    public List<string> CriticalErrors { get; set; } = new();

    public bool IsPassed => TotalErrorsCount == 0 && CompletedRounds >= PlannedRounds && CompletedRounds > 0;

    public string IntegrityVerdict
    {
        get
        {
            if (IsPassed)
            {
                return "GENUINE / HEALTHY: Full capacity verified without bit errors across all cycles.";
            }

            if (TotalErrorsCount > 0)
            {
                // Check if errors indicate fake drive
                return "FAIL: Data corruption detected! Possible fake capacity (looping firmware) or defective flash memory.";
            }

            if (CompletedRounds > 0 && CompletedRounds < PlannedRounds)
            {
                return $"PARTIAL PASS: {CompletedRounds} of {PlannedRounds} rounds completed with 0 errors, but test was interrupted before all rounds finished.";
            }

            return "INCOMPLETE: Test was interrupted or aborted before all rounds completed.";
        }
    }

    public string GenerateMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Drive Integrity & Stress Benchmark Report");
        sb.AppendLine($"**Generated:** {EndTime:yyyy-MM-dd HH:mm:ss}  ");
        sb.AppendLine($"**Overall Status:** {(IsPassed ? "PASS (Genuine & Healthy)" : "FAIL / INCOMPLETE")}  ");
        sb.AppendLine();

        sb.AppendLine("## Target Drive Information");
        sb.AppendLine($"- **Drive Letter:** {Drive.DriveLetter}");
        sb.AppendLine($"- **Model Name:** {Drive.ModelName}");
        sb.AppendLine($"- **Bus / Interface:** {Drive.BusType} {(Drive.IsUsb ? "(USB)" : "")}");
        sb.AppendLine($"- **File System:** {Drive.DriveFormat}");
        sb.AppendLine($"- **Reported Capacity:** {Drive.TotalSizeGB:F2} GB");
        sb.AppendLine($"- **Free Space at Start:** {Drive.FreeSpaceGB:F2} GB");
        sb.AppendLine();

        sb.AppendLine("## Test Execution Summary");
        sb.AppendLine($"- **Verdict:** {IntegrityVerdict}");
        sb.AppendLine($"- **Rounds Planned / Completed:** {PlannedRounds} / {CompletedRounds}");
        sb.AppendLine($"- **Total Data Written:** {TotalBytesWritten / (1024.0 * 1024.0 * 1024.0):F2} GB");
        sb.AppendLine($"- **Total Data Verified:** {TotalBytesVerified / (1024.0 * 1024.0 * 1024.0):F2} GB");
        sb.AppendLine($"- **Total Bit / Block Errors:** {TotalErrorsCount}");
        sb.AppendLine($"- **Total Test Duration:** {TotalDuration:hh\\:mm\\:ss}");
        sb.AppendLine($"- **Overall Average Write Speed:** {OverallAvgWriteSpeedMBps:F1} MB/s");
        sb.AppendLine($"- **Overall Average Read Speed:** {OverallAvgReadSpeedMBps:F1} MB/s");
        sb.AppendLine();

        sb.AppendLine("## Per-Round Performance Breakdown");
        sb.AppendLine("| Round | Status | Written (GB) | Avg Write (MB/s) | Peak Write (MB/s) | Verified (GB) | Avg Read (MB/s) | Peak Read (MB/s) | Errors |");
        sb.AppendLine("|-------|--------|--------------|------------------|-------------------|---------------|-----------------|------------------|--------|");

        foreach (var r in Rounds)
        {
            sb.AppendLine($"| {r.RoundNumber} | {r.StatusBadge} | {r.WrittenGB:F2} | {r.AvgWriteSpeedMBps:F1} | {r.PeakWriteSpeedMBps:F1} | {r.VerifiedGB:F2} | {r.AvgReadSpeedMBps:F1} | {r.PeakReadSpeedMBps:F1} | {r.ErrorCount} |");
        }

        if (CriticalErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Detected Corruption / Errors Details");
            foreach (var err in CriticalErrors.Take(50))
            {
                sb.AppendLine($"- {err}");
            }
            if (CriticalErrors.Count > 50)
            {
                sb.AppendLine($"- ...and {CriticalErrors.Count - 50} more errors truncated.");
            }
        }

        return sb.ToString();
    }

    public string GenerateHtml()
    {
        var sb = new StringBuilder();
        var statusColor = IsPassed ? "#22c55e" : "#ef4444";
        var statusText = IsPassed ? "PASSED (GENUINE & HEALTHY)" : (TotalErrorsCount > 0 ? "FAILED (CORRUPTION DETECTED)" : "INCOMPLETE / CANCELLED");

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <title>DriveTester Diagnostic Report</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #0f172a; color: #f8fafc; margin: 0; padding: 24px; }");
        sb.AppendLine("    .container { max-width: 960px; margin: 0 auto; background: #1e293b; border-radius: 12px; padding: 32px; box-shadow: 0 10px 25px rgba(0,0,0,0.5); }");
        sb.AppendLine("    h1 { margin-top: 0; font-size: 26px; color: #38bdf8; display: flex; align-items: center; justify-content: space-between; }");
        sb.AppendLine($"   .badge {{ padding: 6px 14px; border-radius: 20px; font-weight: bold; font-size: 14px; background: {statusColor}; color: #ffffff; }}");
        sb.AppendLine("    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 16px; margin: 24px 0; }");
        sb.AppendLine("    .card { background: #0f172a; padding: 16px; border-radius: 8px; border: 1px solid #334155; }");
        sb.AppendLine("    .card-label { font-size: 12px; color: #94a3b8; text-transform: uppercase; letter-spacing: 0.5px; }");
        sb.AppendLine("    .card-val { font-size: 20px; font-weight: bold; color: #f1f5f9; margin-top: 4px; }");
        sb.AppendLine("    table { width: 100%; border-collapse: collapse; margin-top: 16px; background: #0f172a; border-radius: 8px; overflow: hidden; }");
        sb.AppendLine("    th, td { padding: 12px 14px; text-align: left; border-bottom: 1px solid #1e293b; font-size: 14px; }");
        sb.AppendLine("    th { background: #1e293b; color: #94a3b8; font-weight: 600; }");
        sb.AppendLine("    .error-box { background: rgba(239, 68, 68, 0.1); border: 1px solid #ef4444; border-radius: 8px; padding: 16px; margin-top: 24px; color: #fca5a5; font-family: monospace; font-size: 13px; max-height: 250px; overflow-y: auto; }");
        sb.AppendLine("    .footer { margin-top: 24px; font-size: 12px; color: #64748b; text-align: center; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"container\">");
        sb.AppendLine($"  <h1><span>DriveTester Verification Report</span><span class=\"badge\">{statusText}</span></h1>");
        sb.AppendLine($"  <p style=\"color: #94a3b8; margin-top: -8px;\">Target: <strong>{Drive.DriveLetter} ({Drive.ModelName})</strong> &bull; Interface: {Drive.BusType} &bull; Filesystem: {Drive.DriveFormat}</p>");
        sb.AppendLine($"  <p><strong>Verdict:</strong> {IntegrityVerdict}</p>");

        sb.AppendLine("  <div class=\"grid\">");
        sb.AppendLine($"    <div class=\"card\"><div class=\"card-label\">Reported Capacity</div><div class=\"card-val\">{Drive.TotalSizeGB:F1} GB</div></div>");
        sb.AppendLine($"    <div class=\"card\"><div class=\"card-label\">Data Written / Verified</div><div class=\"card-val\">{TotalBytesWritten / (1024.0 * 1024.0 * 1024.0):F1} GB</div></div>");
        sb.AppendLine($"    <div class=\"card\"><div class=\"card-label\">Avg Write Speed</div><div class=\"card-val\">{OverallAvgWriteSpeedMBps:F1} MB/s</div></div>");
        sb.AppendLine($"    <div class=\"card\"><div class=\"card-label\">Avg Read Speed</div><div class=\"card-val\">{OverallAvgReadSpeedMBps:F1} MB/s</div></div>");
        sb.AppendLine($"    <div class=\"card\"><div class=\"card-label\">Rounds Completed</div><div class=\"card-val\">{CompletedRounds} / {PlannedRounds}</div></div>");
        sb.AppendLine($"    <div class=\"card\"><div class=\"card-label\">Errors Encountered</div><div class=\"card-val\" style=\"color: {(TotalErrorsCount > 0 ? "#ef4444" : "#22c55e")}\">{TotalErrorsCount}</div></div>");
        sb.AppendLine("  </div>");

        sb.AppendLine("  <h3 style=\"color: #38bdf8; margin-bottom: 8px;\">Round Details</h3>");
        sb.AppendLine("  <table>");
        sb.AppendLine("    <thead>");
        sb.AppendLine("      <tr><th>Round</th><th>Status</th><th>Written</th><th>Avg Write</th><th>Verified</th><th>Avg Read</th><th>Duration</th><th>Errors</th></tr>");
        sb.AppendLine("    </thead>");
        sb.AppendLine("    <tbody>");

        foreach (var r in Rounds)
        {
            var rStatusColor = r.IsPassed ? "#22c55e" : "#ef4444";
            sb.AppendLine($"      <tr><td>#{r.RoundNumber}</td><td style=\"color:{rStatusColor}; font-weight:bold;\">{r.StatusBadge}</td><td>{r.WrittenGB:F2} GB</td><td>{r.AvgWriteSpeedMBps:F1} MB/s</td><td>{r.VerifiedGB:F2} GB</td><td>{r.AvgReadSpeedMBps:F1} MB/s</td><td>{r.TotalRoundDuration:mm\\:ss}</td><td>{r.ErrorCount}</td></tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");

        if (CriticalErrors.Count > 0)
        {
            sb.AppendLine("  <h3 style=\"color: #ef4444; margin-top: 24px;\">Corruption Log</h3>");
            sb.AppendLine("  <div class=\"error-box\">");
            foreach (var err in CriticalErrors.Take(100))
            {
                sb.AppendLine($"    <div>&bull; {err}</div>");
            }
            if (CriticalErrors.Count > 100)
            {
                sb.AppendLine($"    <div>...and {CriticalErrors.Count - 100} more error entries.</div>");
            }
            sb.AppendLine("  </div>");
        }

        sb.AppendLine($"  <div class=\"footer\">Generated on {EndTime:yyyy-MM-dd HH:mm:ss} | Test duration: {TotalDuration:hh\\:mm\\:ss} | DriveTester Suite</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}
