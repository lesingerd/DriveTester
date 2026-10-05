using System.Diagnostics;
using System.IO;
using DriveTester.Models;

namespace DriveTester.Services;

public class DriveTestEngine
{
    private readonly TestConfiguration _config;
    private readonly ulong _baseSeed;

    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private CancellationTokenSource? _cts;
    private bool _isPaused;
    private long _overallBytesProcessed;

    public event Action<LogEntry>? LogEmitted;
    public event Action<TestProgressUpdate>? ProgressUpdated;
    public event Action<RoundResult>? RoundCompleted;
    public event Action<FinalTestReport>? TestCompleted;

    public bool IsRunning { get; private set; }
    public bool IsPaused => _isPaused;

    public DriveTestEngine(TestConfiguration config)
    {
        _config = config;
        _baseSeed = (ulong)DateTime.UtcNow.Ticks ^ 0xA5A5A5A55A5A5A5AUL;
    }

    public void Pause()
    {
        if (IsRunning && !_isPaused)
        {
            _isPaused = true;
            _pauseEvent.Reset();
            EmitLog(LogLevel.Info, "Test execution PAUSED.");
        }
    }

    public void Resume()
    {
        if (IsRunning && _isPaused)
        {
            _isPaused = false;
            _pauseEvent.Set();
            EmitLog(LogLevel.Info, "Test execution RESUMED.");
        }
    }

    public void Stop()
    {
        if (IsRunning)
        {
            EmitLog(LogLevel.Warning, "Cancellation requested by user. Aborting test safely...");
            _pauseEvent.Set(); // ensure not blocked in wait
            _cts?.Cancel();
        }
    }

    public async Task<FinalTestReport> RunAsync()
    {
        if (_config.TargetDrive == null)
            throw new InvalidOperationException("No target drive selected.");

        IsRunning = true;
        _isPaused = false;
        _pauseEvent.Set();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var startTime = DateTime.Now;
        var roundResults = new List<RoundResult>();
        var criticalErrors = new List<string>();

        var driveRoot = _config.TargetDrive.DriveLetter;
        if (!driveRoot.EndsWith('\\')) driveRoot += "\\";
        var testRootDir = Path.Combine(driveRoot, _config.TestFolderName);

        EmitLog(LogLevel.Info, $"==================================================");
        EmitLog(LogLevel.Info, $"Starting Drive Integrity Test Suite on {driveRoot}");
        EmitLog(LogLevel.Info, $"Drive: {_config.TargetDrive.ModelName} ({_config.TargetDrive.TotalSizeGB:F1} GB total, {_config.TargetDrive.FreeSpaceGB:F1} GB free)");
        EmitLog(LogLevel.Info, $"Rounds planned: {_config.Rounds} | Target Mode: {_config.TargetMode} | Preset: {_config.SizePreset}");
        EmitLog(LogLevel.Info, $"Direct Write-Through (bypass OS cache): {_config.FlushBuffersDirectly}");
        EmitLog(LogLevel.Info, $"==================================================");

        long overallTotalBytesToProcess = 0;
        // Pre-calculate estimated bytes across all rounds (each round writes + verifies = 2x target bytes)
        var driveInfo = new DriveInfo(driveRoot);
        long targetBytesPerRound = _config.CalculateTargetBytes(driveInfo.AvailableFreeSpace);
        overallTotalBytesToProcess = targetBytesPerRound * 2 * _config.Rounds;

        _overallBytesProcessed = 0;
        var overallStopwatch = Stopwatch.StartNew();

        int completedRounds = 0;
        bool aborted = false;

        try
        {
            Directory.CreateDirectory(testRootDir);

            for (int r = 1; r <= _config.Rounds; r++)
            {
                ct.ThrowIfCancellationRequested();

                EmitLog(LogLevel.Info, $">>> STARTING ROUND {r} of {_config.Rounds} <<<");

                var roundResult = new RoundResult { RoundNumber = r };
                var roundDir = Path.Combine(testRootDir, $"Round_{r:D2}");
                Directory.CreateDirectory(roundDir);

                // Refresh free space for round
                driveInfo = new DriveInfo(driveRoot);
                long currentRoundTargetBytes = _config.CalculateTargetBytes(driveInfo.AvailableFreeSpace);
                if (currentRoundTargetBytes <= 0)
                {
                    EmitLog(LogLevel.Error, $"Not enough free disk space on {driveRoot} to execute Round {r}.");
                    break;
                }

                EmitLog(LogLevel.Info, $"Round {r}: Planning test dataset to fill ~{currentRoundTargetBytes / (1024.0 * 1024.0 * 1024.0):F2} GB...");
                var filePlan = FileSizePlanner.PlanFiles(currentRoundTargetBytes, _config.SizePreset, r);
                EmitLog(LogLevel.Info, $"Round {r}: Generated {filePlan.Count} test files with varying sizes (128KB - 1GB).");

                roundResult.TotalFiles = filePlan.Count;

                // ==========================================
                // PHASE 1: WRITE PHASE
                // ==========================================
                EmitLog(LogLevel.Info, $"Round {r}: [PHASE 1/3: WRITING FILES TO SSD]");
                var writeSw = Stopwatch.StartNew();
                var writtenFiles = new List<TestFileInfo>();

                await WritePhaseAsync(
                    roundDir,
                    filePlan,
                    r,
                    writtenFiles,
                    roundResult,
                    overallTotalBytesToProcess,
                    overallStopwatch,
                    ct);

                writeSw.Stop();
                roundResult.WriteDuration = writeSw.Elapsed;
                if (roundResult.WriteDuration.TotalSeconds > 0)
                {
                    roundResult.AvgWriteSpeedMBps = (roundResult.BytesWritten / (1024.0 * 1024.0)) / roundResult.WriteDuration.TotalSeconds;
                }

                EmitLog(LogLevel.Success, $"Round {r} Write Phase complete: {roundResult.BytesWritten / (1024.0 * 1024.0 * 1024.0):F2} GB written in {roundResult.WriteDuration:mm\\:ss} (Avg: {roundResult.AvgWriteSpeedMBps:F1} MB/s, Peak: {roundResult.PeakWriteSpeedMBps:F1} MB/s)");

                ct.ThrowIfCancellationRequested();

                // ==========================================
                // PHASE 2: VERIFICATION PHASE
                // ==========================================
                EmitLog(LogLevel.Info, $"Round {r}: [PHASE 2/3: VERIFYING INTEGRITY & DATA CHECKSUMS]");
                var readSw = Stopwatch.StartNew();

                await VerifyPhaseAsync(
                    roundDir,
                    writtenFiles,
                    r,
                    roundResult,
                    criticalErrors,
                    overallTotalBytesToProcess,
                    overallStopwatch,
                    ct);

                readSw.Stop();
                roundResult.ReadDuration = readSw.Elapsed;
                if (roundResult.ReadDuration.TotalSeconds > 0)
                {
                    roundResult.AvgReadSpeedMBps = (roundResult.BytesVerified / (1024.0 * 1024.0)) / roundResult.ReadDuration.TotalSeconds;
                }

                if (roundResult.ErrorCount == 0)
                {
                    EmitLog(LogLevel.Success, $"Round {r} Verification complete: 100% MATCH! 0 errors detected. Verified {roundResult.BytesVerified / (1024.0 * 1024.0 * 1024.0):F2} GB in {roundResult.ReadDuration:mm\\:ss} (Avg: {roundResult.AvgReadSpeedMBps:F1} MB/s, Peak: {roundResult.PeakReadSpeedMBps:F1} MB/s)");
                }
                else
                {
                    EmitLog(LogLevel.Error, $"Round {r} Verification FAILED! Detected {roundResult.ErrorCount} corrupt blocks/checksum errors!");
                    if (_config.StopOnFirstError)
                    {
                        EmitLog(LogLevel.Warning, "Stop on first error is enabled. Terminating test early.");
                        roundResults.Add(roundResult);
                        RoundCompleted?.Invoke(roundResult);
                        break;
                    }
                }

                // ==========================================
                // PHASE 3: EMPTYING / CLEANUP PHASE
                // ==========================================
                if (_config.EmptyFilesAfterEachRound)
                {
                    EmitLog(LogLevel.Info, $"Round {r}: [PHASE 3/3: EMPTYING TEST FILES FROM DRIVE]");
                    await EmptyPhaseAsync(roundDir, r, ct);
                    EmitLog(LogLevel.Info, $"Round {r}: Drive emptied. Space reclaimed for next cycle.");
                }
                else
                {
                    EmitLog(LogLevel.Info, $"Round {r}: Retaining files on disk as configured.");
                }

                roundResults.Add(roundResult);
                RoundCompleted?.Invoke(roundResult);
                completedRounds++;
            }
        }
        catch (OperationCanceledException)
        {
            aborted = true;
            EmitLog(LogLevel.Warning, "Test was cancelled by user.");
        }
        catch (Exception ex)
        {
            aborted = true;
            EmitLog(LogLevel.Error, $"Critical error during drive test: {ex.Message}");
            criticalErrors.Add($"Fatal exception: {ex.Message}");
        }
        finally
        {
            overallStopwatch.Stop();
            IsRunning = false;
        }

        // Build final test report
        var report = new FinalTestReport
        {
            Drive = _config.TargetDrive,
            Configuration = _config,
            StartTime = startTime,
            EndTime = DateTime.Now,
            PlannedRounds = _config.Rounds,
            CompletedRounds = completedRounds,
            Rounds = roundResults,
            CriticalErrors = criticalErrors,
            TotalBytesWritten = roundResults.Sum(x => x.BytesWritten),
            TotalBytesVerified = roundResults.Sum(x => x.BytesVerified),
            TotalErrorsCount = roundResults.Sum(x => x.ErrorCount) + (aborted && criticalErrors.Count > 0 ? 1 : 0)
        };

        if (roundResults.Count > 0)
        {
            report.OverallAvgWriteSpeedMBps = roundResults.Average(r => r.AvgWriteSpeedMBps);
            report.OverallAvgReadSpeedMBps = roundResults.Average(r => r.AvgReadSpeedMBps);
        }

        EmitLog(LogLevel.Info, "==================================================");
        EmitLog(report.IsPassed ? LogLevel.Success : LogLevel.Error, $"TEST COMPLETE: {report.IntegrityVerdict}");
        EmitLog(LogLevel.Info, $"Total Data Written: {report.TotalBytesWritten / (1024.0 * 1024.0 * 1024.0):F2} GB | Verified: {report.TotalBytesVerified / (1024.0 * 1024.0 * 1024.0):F2} GB");
        EmitLog(LogLevel.Info, $"Total Errors: {report.TotalErrorsCount} | Total Duration: {report.TotalDuration:hh\\:mm\\:ss}");
        EmitLog(LogLevel.Info, "==================================================");

        TestCompleted?.Invoke(report);
        return report;
    }

    private async Task WritePhaseAsync(
        string roundDir,
        List<TestFileInfo> filePlan,
        int roundNumber,
        List<TestFileInfo> writtenFiles,
        RoundResult roundResult,
        long overallTotalBytes,
        Stopwatch overallStopwatch,
        CancellationToken ct)
    {
        const int BUFFER_SIZE = 2 * 1024 * 1024; // 2 MB buffer
        byte[] buffer = new byte[BUFFER_SIZE];

        long phaseTotalBytes = filePlan.Sum(f => f.SizeBytes);
        long phaseBytesProcessed = 0;

        var phaseStopwatch = Stopwatch.StartNew();
        var speedMeasureSw = Stopwatch.StartNew();
        long bytesSinceLastSpeedSample = 0;
        double currentSpeedMBps = 0;

        for (int i = 0; i < filePlan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            WaitIfPaused();

            var fileInfo = filePlan[i];
            var filePath = Path.Combine(roundDir, fileInfo.FileName);

            long fileBytesWritten = 0;

            try
            {
                var options = FileOptions.SequentialScan;
                if (_config.FlushBuffersDirectly)
                {
                    options |= FileOptions.WriteThrough;
                }

                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, BUFFER_SIZE, options))
                {
                    while (fileBytesWritten < fileInfo.SizeBytes)
                    {
                        ct.ThrowIfCancellationRequested();
                        WaitIfPaused();

                        int bytesToWrite = (int)Math.Min(BUFFER_SIZE, fileInfo.SizeBytes - fileBytesWritten);
                        var bufferSpan = buffer.AsSpan(0, bytesToWrite);

                        // Generate deterministic data pattern
                        PatternDataGenerator.FillBuffer(bufferSpan, roundNumber, fileInfo.FileIndex, fileBytesWritten, _baseSeed);

                        await fs.WriteAsync(buffer.AsMemory(0, bytesToWrite), ct);
                        fileBytesWritten += bytesToWrite;
                        phaseBytesProcessed += bytesToWrite;
                        _overallBytesProcessed += bytesToWrite;
                        bytesSinceLastSpeedSample += bytesToWrite;

                        // Calculate speeds every ~250ms
                        if (speedMeasureSw.ElapsedMilliseconds >= 250)
                        {
                            double elapsedSec = speedMeasureSw.Elapsed.TotalSeconds;
                            currentSpeedMBps = (bytesSinceLastSpeedSample / (1024.0 * 1024.0)) / elapsedSec;
                            speedMeasureSw.Restart();
                            bytesSinceLastSpeedSample = 0;

                            if (currentSpeedMBps > roundResult.PeakWriteSpeedMBps)
                                roundResult.PeakWriteSpeedMBps = currentSpeedMBps;
                            if (roundResult.MinWriteSpeedMBps == 0 || (currentSpeedMBps > 0 && currentSpeedMBps < roundResult.MinWriteSpeedMBps))
                                roundResult.MinWriteSpeedMBps = currentSpeedMBps;

                            double avgSpeedMBps = phaseStopwatch.Elapsed.TotalSeconds > 0
                                ? (phaseBytesProcessed / (1024.0 * 1024.0)) / phaseStopwatch.Elapsed.TotalSeconds
                                : 0;

                            long remainingBytes = phaseTotalBytes - phaseBytesProcessed;
                            TimeSpan eta = avgSpeedMBps > 0
                                ? TimeSpan.FromSeconds(remainingBytes / (avgSpeedMBps * 1024 * 1024))
                                : TimeSpan.Zero;

                            ProgressUpdated?.Invoke(new TestProgressUpdate
                            {
                                CurrentRound = roundNumber,
                                TotalRounds = _config.Rounds,
                                CurrentPhase = TestPhase.Writing,
                                CurrentFileName = fileInfo.FileName,
                                CurrentFileIndex = i + 1,
                                TotalFilesInRound = filePlan.Count,
                                CurrentFileBytesProcessed = fileBytesWritten,
                                CurrentFileTotalBytes = fileInfo.SizeBytes,
                                PhaseBytesProcessed = phaseBytesProcessed,
                                PhaseTotalBytes = phaseTotalBytes,
                                OverallBytesProcessed = _overallBytesProcessed,
                                OverallTotalBytes = overallTotalBytes,
                                CurrentSpeedMBps = currentSpeedMBps,
                                AverageSpeedMBps = avgSpeedMBps,
                                PeakSpeedMBps = roundResult.PeakWriteSpeedMBps,
                                ElapsedTime = overallStopwatch.Elapsed,
                                EstimatedTimeRemaining = eta,
                                ErrorCount = roundResult.ErrorCount,
                                StatusMessage = $"Writing {fileInfo.FileName} ({i + 1}/{filePlan.Count})"
                            });
                        }
                    }

                    // Flush all buffers to physical SSD storage
                    fs.Flush(flushToDisk: true);
                }

                fileInfo.SizeBytes = fileBytesWritten;
                writtenFiles.Add(fileInfo);
                roundResult.BytesWritten += fileBytesWritten;
            }
            catch (IOException ex) when (IsDiskFullException(ex))
            {
                EmitLog(LogLevel.Warning, $"Disk full encountered at file {fileInfo.FileName}. Reached true physical drive boundary.");
                if (fileBytesWritten > 0)
                {
                    fileInfo.SizeBytes = fileBytesWritten;
                    writtenFiles.Add(fileInfo);
                    roundResult.BytesWritten += fileBytesWritten;
                }
                break;
            }
        }
    }

    private async Task VerifyPhaseAsync(
        string roundDir,
        List<TestFileInfo> filesToVerify,
        int roundNumber,
        RoundResult roundResult,
        List<string> criticalErrors,
        long overallTotalBytes,
        Stopwatch overallStopwatch,
        CancellationToken ct)
    {
        const int BUFFER_SIZE = 2 * 1024 * 1024; // 2 MB buffer
        byte[] buffer = new byte[BUFFER_SIZE];

        long phaseTotalBytes = filesToVerify.Sum(f => f.SizeBytes);
        long phaseBytesProcessed = 0;

        var phaseStopwatch = Stopwatch.StartNew();
        var speedMeasureSw = Stopwatch.StartNew();
        long bytesSinceLastSpeedSample = 0;
        double currentSpeedMBps = 0;

        for (int i = 0; i < filesToVerify.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            WaitIfPaused();

            var fileInfo = filesToVerify[i];
            var filePath = Path.Combine(roundDir, fileInfo.FileName);

            if (!File.Exists(filePath))
            {
                var msg = $"Missing test file during verification: {fileInfo.FileName}!";
                roundResult.ErrorCount++;
                roundResult.ErrorMessages.Add(msg);
                criticalErrors.Add(msg);
                EmitLog(LogLevel.Error, msg);
                continue;
            }

            long fileBytesVerified = 0;

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, FileOptions.SequentialScan))
            {
                int bytesRead;
                while ((bytesRead = await fs.ReadAsync(buffer.AsMemory(0, BUFFER_SIZE), ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    WaitIfPaused();

                    var bufferSpan = buffer.AsSpan(0, bytesRead);

                    // Verify against expected deterministic pattern and block checksums
                    var (isValid, err) = PatternDataGenerator.VerifyBuffer(
                        bufferSpan,
                        roundNumber,
                        fileInfo.FileIndex,
                        fileBytesVerified,
                        _baseSeed);

                    if (!isValid)
                    {
                        roundResult.ErrorCount++;
                        var fullMsg = $"[CORRUPTION] Round {roundNumber}, {fileInfo.FileName} at offset {fileBytesVerified:N0}: {err}";
                        roundResult.ErrorMessages.Add(fullMsg);
                        criticalErrors.Add(fullMsg);
                        EmitLog(LogLevel.Error, fullMsg);

                        if (_config.StopOnFirstError)
                            return;
                    }

                    fileBytesVerified += bytesRead;
                    phaseBytesProcessed += bytesRead;
                    _overallBytesProcessed += bytesRead;
                    bytesSinceLastSpeedSample += bytesRead;

                    if (speedMeasureSw.ElapsedMilliseconds >= 250)
                    {
                        double elapsedSec = speedMeasureSw.Elapsed.TotalSeconds;
                        currentSpeedMBps = (bytesSinceLastSpeedSample / (1024.0 * 1024.0)) / elapsedSec;
                        speedMeasureSw.Restart();
                        bytesSinceLastSpeedSample = 0;

                        if (currentSpeedMBps > roundResult.PeakReadSpeedMBps)
                            roundResult.PeakReadSpeedMBps = currentSpeedMBps;
                        if (roundResult.MinReadSpeedMBps == 0 || (currentSpeedMBps > 0 && currentSpeedMBps < roundResult.MinReadSpeedMBps))
                            roundResult.MinReadSpeedMBps = currentSpeedMBps;

                        double avgSpeedMBps = phaseStopwatch.Elapsed.TotalSeconds > 0
                            ? (phaseBytesProcessed / (1024.0 * 1024.0)) / phaseStopwatch.Elapsed.TotalSeconds
                            : 0;

                        long remainingBytes = phaseTotalBytes - phaseBytesProcessed;
                        TimeSpan eta = avgSpeedMBps > 0
                            ? TimeSpan.FromSeconds(remainingBytes / (avgSpeedMBps * 1024 * 1024))
                            : TimeSpan.Zero;

                        ProgressUpdated?.Invoke(new TestProgressUpdate
                        {
                            CurrentRound = roundNumber,
                            TotalRounds = _config.Rounds,
                            CurrentPhase = TestPhase.Verifying,
                            CurrentFileName = fileInfo.FileName,
                            CurrentFileIndex = i + 1,
                            TotalFilesInRound = filesToVerify.Count,
                            CurrentFileBytesProcessed = fileBytesVerified,
                            CurrentFileTotalBytes = fileInfo.SizeBytes,
                            PhaseBytesProcessed = phaseBytesProcessed,
                            PhaseTotalBytes = phaseTotalBytes,
                            OverallBytesProcessed = _overallBytesProcessed,
                            OverallTotalBytes = overallTotalBytes,
                            CurrentSpeedMBps = currentSpeedMBps,
                            AverageSpeedMBps = avgSpeedMBps,
                            PeakSpeedMBps = roundResult.PeakReadSpeedMBps,
                            ElapsedTime = overallStopwatch.Elapsed,
                            EstimatedTimeRemaining = eta,
                            ErrorCount = roundResult.ErrorCount,
                            StatusMessage = $"Verifying {fileInfo.FileName} ({i + 1}/{filesToVerify.Count})"
                        });
                    }
                }
            }

            roundResult.BytesVerified += fileBytesVerified;
        }
    }

    private async Task EmptyPhaseAsync(string roundDir, int roundNumber, CancellationToken ct)
    {
        ProgressUpdated?.Invoke(new TestProgressUpdate
        {
            CurrentRound = roundNumber,
            TotalRounds = _config.Rounds,
            CurrentPhase = TestPhase.Emptying,
            StatusMessage = $"Emptying files for Round {roundNumber}..."
        });

        await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(roundDir))
                {
                    var files = Directory.GetFiles(roundDir, "*.tst");
                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        File.Delete(file);
                    }
                    Directory.Delete(roundDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                EmitLog(LogLevel.Warning, $"Could not delete directory {roundDir}: {ex.Message}");
            }
        }, ct);
    }

    public static async Task CleanAllTestFilesAsync(DriveTargetInfo drive, string testFolderName, Action<string>? logAction = null)
    {
        await Task.Run(() =>
        {
            try
            {
                var driveRoot = drive.DriveLetter;
                if (!driveRoot.EndsWith('\\')) driveRoot += "\\";
                var testRootDir = Path.Combine(driveRoot, testFolderName);

                if (Directory.Exists(testRootDir))
                {
                    logAction?.Invoke($"Deleting leftover test files in {testRootDir}...");
                    Directory.Delete(testRootDir, recursive: true);
                    logAction?.Invoke("Leftover test directory successfully deleted.");
                }
                else
                {
                    logAction?.Invoke("No leftover test folder found on drive.");
                }
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"Failed to clean test files: {ex.Message}");
            }
        });
    }

    private void WaitIfPaused()
    {
        _pauseEvent.Wait();
    }

    private static bool IsDiskFullException(IOException ex)
    {
        const int ERROR_HANDLE_DISK_FULL = 0x27;
        const int ERROR_DISK_FULL = 0x70;
        int win32ErrorCode = ex.HResult & 0xFFFF;
        return win32ErrorCode == ERROR_HANDLE_DISK_FULL || win32ErrorCode == ERROR_DISK_FULL;
    }

    private void EmitLog(LogLevel level, string message)
    {
        LogEmitted?.Invoke(new LogEntry
        {
            Level = level,
            Message = message
        });
    }
}
