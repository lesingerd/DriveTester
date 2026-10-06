using System.Diagnostics;
using System.IO;
using DriveTester.Models;

namespace DriveTester.Services;

public class DriveTestEngine
{
    private readonly TestConfiguration _config;
    private ulong _baseSeed;
    private TestSessionState? _sessionState;

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
    public TestSessionState? SessionState => _sessionState;

    public DriveTestEngine(TestConfiguration config, ulong? customSeed = null)
    {
        _config = config;
        _baseSeed = customSeed ?? ((ulong)DateTime.UtcNow.Ticks ^ 0xA5A5A5A55A5A5A5AUL);
    }

    public void Pause()
    {
        if (IsRunning && !_isPaused)
        {
            _isPaused = true;
            _pauseEvent.Reset();
            EmitLog(LogLevel.Info, "Test execution PAUSED.");

            if (_config.TargetDrive != null && _sessionState != null)
            {
                _sessionState.IsGracefullyPaused = true;
                _sessionState.Save(_config.TargetDrive.DriveLetter, _config.TestFolderName);
            }
        }
    }

    public void Resume()
    {
        if (IsRunning && _isPaused)
        {
            _isPaused = false;
            _pauseEvent.Set();
            EmitLog(LogLevel.Info, "Test execution RESUMED.");

            if (_config.TargetDrive != null && _sessionState != null)
            {
                _sessionState.IsGracefullyPaused = false;
                _sessionState.Save(_config.TargetDrive.DriveLetter, _config.TestFolderName);
            }
        }
    }

    public void Stop()
    {
        if (IsRunning)
        {
            EmitLog(LogLevel.Warning, "Cancellation requested by user. Aborting test safely...");
            _pauseEvent.Set(); // ensure not blocked in wait

            if (_config.TargetDrive != null && _sessionState != null)
            {
                _sessionState.Status = SessionStatus.Aborted;
                _sessionState.Save(_config.TargetDrive.DriveLetter, _config.TestFolderName);
            }

            _cts?.Cancel();
        }
    }

    public async Task<FinalTestReport> RunAsync(TestSessionState? resumeState = null)
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

        var driveInfo = new DriveInfo(driveRoot);
        long targetBytesPerRound = _config.CalculateTargetBytes(driveInfo.AvailableFreeSpace);

        int startRound = 1;
        if (resumeState != null)
        {
            _sessionState = resumeState;
            _baseSeed = resumeState.BaseSeed;
            startRound = Math.Max(1, resumeState.CurrentRound);
            if (resumeState.CompletedRoundsResults.Count > 0)
            {
                roundResults.AddRange(resumeState.CompletedRoundsResults);
            }
            if (resumeState.TargetBytesPerRound > 0)
            {
                targetBytesPerRound = resumeState.TargetBytesPerRound;
            }

            EmitLog(LogLevel.Warning, $"==================================================");
            EmitLog(LogLevel.Warning, $"RESUMING INTERRUPTED TEST SESSION on {driveRoot}");
            EmitLog(LogLevel.Warning, $"Resuming from Round {startRound} of {_config.Rounds} (Last Phase: {resumeState.CurrentPhase})");
            EmitLog(LogLevel.Warning, $"==================================================");
        }
        else
        {
            _sessionState = new TestSessionState
            {
                BaseSeed = _baseSeed,
                PlannedRounds = _config.Rounds,
                TargetMode = _config.TargetMode,
                CustomCapacityGB = _config.CustomCapacityGB,
                SizePreset = _config.SizePreset,
                FlushBuffersDirectly = _config.FlushBuffersDirectly,
                StopOnFirstError = _config.StopOnFirstError,
                EmptyFilesAfterEachRound = _config.EmptyFilesAfterEachRound,
                TargetBytesPerRound = targetBytesPerRound,
                CurrentRound = 1,
                CurrentPhase = TestPhase.Writing,
                IsGracefullyPaused = false
            };
            _sessionState.Save(driveRoot, _config.TestFolderName);

            EmitLog(LogLevel.Info, $"==================================================");
            EmitLog(LogLevel.Info, $"Starting Drive Integrity Test Suite on {driveRoot}");
            EmitLog(LogLevel.Info, $"Drive: {_config.TargetDrive.ModelName} ({_config.TargetDrive.TotalSizeGB:F1} GB total, {_config.TargetDrive.FreeSpaceGB:F1} GB free)");
            EmitLog(LogLevel.Info, $"Rounds planned: {_config.Rounds} | Target Mode: {_config.TargetMode} | Preset: {_config.SizePreset}");
            EmitLog(LogLevel.Info, $"Direct Write-Through (bypass OS cache): {_config.FlushBuffersDirectly}");
            EmitLog(LogLevel.Info, $"==================================================");
        }

        long overallTotalBytesToProcess = targetBytesPerRound * 2 * _config.Rounds;
        _overallBytesProcessed = roundResults.Sum(r => r.BytesWritten + r.BytesVerified);
        var overallStopwatch = Stopwatch.StartNew();

        int completedRounds = roundResults.Count;
        bool aborted = false;

        try
        {
            Directory.CreateDirectory(testRootDir);

            for (int r = startRound; r <= _config.Rounds; r++)
            {
                ct.ThrowIfCancellationRequested();

                EmitLog(LogLevel.Info, $">>> STARTING ROUND {r} of {_config.Rounds} <<<");

                // Always ensure any completed previous rounds (e.g. Round_01 before Round 2) are cleaned up
                // to free drive space for the upcoming round
                await CleanPreviousRoundsAsync(testRootDir, r, ct);

                var roundResult = new RoundResult { RoundNumber = r };
                var roundDir = Path.Combine(testRootDir, $"Round_{r:D2}");
                Directory.CreateDirectory(roundDir);

                // Refresh free space from OS after cleaning previous rounds
                driveInfo = new DriveInfo(driveRoot);
                long currentRoundTargetBytes = _config.CalculateTargetBytes(driveInfo.AvailableFreeSpace);

                if (currentRoundTargetBytes <= 0)
                {
                    EmitLog(LogLevel.Error, $"Not enough free disk space on {driveRoot} to execute Round {r} (Available: {driveInfo.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0):F2} GB).");
                    break;
                }

                EmitLog(LogLevel.Info, $"Round {r}: Planning test dataset to fill ~{currentRoundTargetBytes / (1024.0 * 1024.0 * 1024.0):F2} GB...");
                var filePlan = FileSizePlanner.PlanFiles(currentRoundTargetBytes, _config.SizePreset, r);
                EmitLog(LogLevel.Info, $"Round {r}: Generated {filePlan.Count} test files with varying sizes (128KB - 1GB).");

                roundResult.TotalFiles = filePlan.Count;

                var activeResumeState = (resumeState != null && r == resumeState.CurrentRound) ? resumeState : null;
                var writtenFiles = new List<TestFileInfo>();

                // ==========================================
                // PHASE 1: WRITE PHASE
                // ==========================================
                bool skipWrite = activeResumeState != null && activeResumeState.CurrentPhase != TestPhase.Writing;

                if (!skipWrite)
                {
                    EmitLog(LogLevel.Info, $"Round {r}: [PHASE 1/3: WRITING FILES TO SSD]");
                    _sessionState.CurrentRound = r;
                    _sessionState.CurrentPhase = TestPhase.Writing;
                    _sessionState.IsGracefullyPaused = false;
                    _sessionState.Save(driveRoot, _config.TestFolderName);

                    var writeSw = Stopwatch.StartNew();

                    await WritePhaseAsync(
                        roundDir,
                        driveRoot,
                        filePlan,
                        r,
                        writtenFiles,
                        roundResult,
                        overallTotalBytesToProcess,
                        overallStopwatch,
                        activeResumeState,
                        ct);

                    writeSw.Stop();
                    roundResult.WriteDuration = writeSw.Elapsed;
                    if (roundResult.WriteDuration.TotalSeconds > 0)
                    {
                        roundResult.AvgWriteSpeedMBps = (roundResult.BytesWritten / (1024.0 * 1024.0)) / roundResult.WriteDuration.TotalSeconds;
                    }

                    EmitLog(LogLevel.Success, $"Round {r} Write Phase complete: {roundResult.BytesWritten / (1024.0 * 1024.0 * 1024.0):F2} GB written in {roundResult.WriteDuration:mm\\:ss} (Avg: {roundResult.AvgWriteSpeedMBps:F1} MB/s, Peak: {roundResult.PeakWriteSpeedMBps:F1} MB/s)");
                }
                else
                {
                    EmitLog(LogLevel.Info, $"Round {r}: Resuming directly to {activeResumeState!.CurrentPhase} (Write phase previously finished).");
                    // Populate writtenFiles from filePlan where file exists on disk
                    foreach (var file in filePlan)
                    {
                        var filePath = Path.Combine(roundDir, file.FileName);
                        if (File.Exists(filePath))
                        {
                            var fi = new FileInfo(filePath);
                            file.SizeBytes = fi.Length;
                            writtenFiles.Add(file);
                            roundResult.BytesWritten += fi.Length;
                        }
                    }
                }

                ct.ThrowIfCancellationRequested();

                // ==========================================
                // PHASE 2: VERIFICATION PHASE
                // ==========================================
                bool skipVerify = activeResumeState != null && activeResumeState.CurrentPhase == TestPhase.Emptying;

                if (!skipVerify)
                {
                    EmitLog(LogLevel.Info, $"Round {r}: [PHASE 2/3: VERIFYING INTEGRITY & DATA CHECKSUMS]");
                    _sessionState.CurrentRound = r;
                    _sessionState.CurrentPhase = TestPhase.Verifying;
                    _sessionState.InFlightFileName = null;
                    _sessionState.Save(driveRoot, _config.TestFolderName);

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
                }

                // ==========================================
                // PHASE 3: EMPTYING / CLEANUP PHASE
                // ==========================================
                bool shouldEmpty = _config.EmptyFilesAfterEachRound;
                if (!shouldEmpty && r < _config.Rounds)
                {
                    var driveCheck = new DriveInfo(driveRoot);
                    if (driveCheck.AvailableFreeSpace < currentRoundTargetBytes)
                    {
                        EmitLog(LogLevel.Warning, $"Round {r}: Drive free space ({driveCheck.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0):F2} GB) is insufficient for Round {r + 1}. Emptying Round {r} files so test can continue.");
                        shouldEmpty = true;
                    }
                }

                if (shouldEmpty)
                {
                    EmitLog(LogLevel.Info, $"Round {r}: [PHASE 3/3: EMPTYING TEST FILES FROM DRIVE]");
                    _sessionState.CurrentRound = r;
                    _sessionState.CurrentPhase = TestPhase.Emptying;
                    _sessionState.Save(driveRoot, _config.TestFolderName);

                    await EmptyPhaseAsync(roundDir, r, ct);

                    driveInfo = new DriveInfo(driveRoot);
                    EmitLog(LogLevel.Success, $"Round {r}: Drive emptied. Available free space: {driveInfo.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0):F1} GB reclaimed for next cycle.");
                }
                else
                {
                    EmitLog(LogLevel.Info, $"Round {r}: Retaining files on disk as configured.");
                }

                roundResults.Add(roundResult);
                RoundCompleted?.Invoke(roundResult);
                completedRounds++;

                _sessionState.CompletedRoundsResults = roundResults.ToList();
                _sessionState.CurrentRound = r + 1;
                _sessionState.CurrentPhase = TestPhase.Writing;
                _sessionState.Save(driveRoot, _config.TestFolderName);

                // Clear resume state once initial resumed round finishes
                resumeState = null;
            }
        }
        catch (OperationCanceledException)
        {
            aborted = true;
            EmitLog(LogLevel.Warning, "Test was cancelled by user.");
            if (_sessionState != null)
            {
                _sessionState.Status = SessionStatus.Aborted;
                _sessionState.Save(driveRoot, _config.TestFolderName);
            }
        }
        catch (Exception ex)
        {
            aborted = true;
            EmitLog(LogLevel.Error, $"Critical error during drive test: {ex.Message}");
            criticalErrors.Add($"Fatal exception: {ex.Message}");
            if (_sessionState != null)
            {
                _sessionState.Status = SessionStatus.Aborted;
                _sessionState.Save(driveRoot, _config.TestFolderName);
            }
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

        if (!aborted && completedRounds == _config.Rounds)
        {
            if (_sessionState != null)
            {
                _sessionState.Status = SessionStatus.Completed;
                if (_config.EmptyFilesAfterEachRound)
                {
                    TestSessionState.Delete(driveRoot, _config.TestFolderName);
                }
                else
                {
                    _sessionState.Save(driveRoot, _config.TestFolderName);
                }
            }
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
        string driveRoot,
        List<TestFileInfo> filePlan,
        int roundNumber,
        List<TestFileInfo> writtenFiles,
        RoundResult roundResult,
        long overallTotalBytes,
        Stopwatch overallStopwatch,
        TestSessionState? resumeState,
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

        // If resuming an interrupted session that was NOT paused gracefully:
        // Clean/recreate the last written file or any truncated files
        if (resumeState != null)
        {
            HandleUngracefulInterruptionRecovery(roundDir, filePlan, resumeState);
        }

        for (int i = 0; i < filePlan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            WaitIfPaused();

            var fileInfo = filePlan[i];
            var filePath = Path.Combine(roundDir, fileInfo.FileName);

            // If file already exists and matches expected size, reuse it!
            if (File.Exists(filePath))
            {
                var fi = new FileInfo(filePath);
                if (fi.Length == fileInfo.SizeBytes)
                {
                    writtenFiles.Add(fileInfo);
                    roundResult.BytesWritten += fileInfo.SizeBytes;
                    phaseBytesProcessed += fileInfo.SizeBytes;
                    _overallBytesProcessed += fileInfo.SizeBytes;
                    EmitLog(LogLevel.Info, $"[RESUME] Reusing existing valid file: {fileInfo.FileName} ({fileInfo.SizeMB:F1} MB)");
                    continue;
                }
                else
                {
                    EmitLog(LogLevel.Warning, $"[RESUME] File size mismatch ({fi.Length} vs {fileInfo.SizeBytes}). Recreating {fileInfo.FileName}...");
                    try { File.Delete(filePath); } catch { }
                }
            }

            // Save in-flight state to manifest
            if (_sessionState != null)
            {
                _sessionState.InFlightFileName = fileInfo.FileName;
                _sessionState.InFlightFileIndex = fileInfo.FileIndex;
                _sessionState.Save(driveRoot, _config.TestFolderName);
            }

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

    private void HandleUngracefulInterruptionRecovery(string roundDir, List<TestFileInfo> filePlan, TestSessionState resumeState)
    {
        if (resumeState.IsGracefullyPaused) return;

        // If not paused gracefully: recreate the last written or in-flight file
        if (!string.IsNullOrEmpty(resumeState.InFlightFileName))
        {
            var inFlightPath = Path.Combine(roundDir, resumeState.InFlightFileName);
            if (File.Exists(inFlightPath))
            {
                EmitLog(LogLevel.Warning, $"[RESUME] Test was interrupted mid-write. Recreating last written file '{resumeState.InFlightFileName}' to ensure complete integrity...");
                try { File.Delete(inFlightPath); } catch { }
            }
        }
        else if (Directory.Exists(roundDir))
        {
            // If in-flight file name wasn't captured, identify the file with highest index on disk and recreate it
            var tstFiles = Directory.GetFiles(roundDir, "*.tst");
            if (tstFiles.Length > 0)
            {
                Array.Sort(tstFiles);
                var lastFile = tstFiles[^1];
                EmitLog(LogLevel.Warning, $"[RESUME] Ungraceful interruption detected. Recreating last written file '{Path.GetFileName(lastFile)}' to ensure integrity...");
                try { File.Delete(lastFile); } catch { }
            }
        }

        // Also clean any truncated files in this round
        if (Directory.Exists(roundDir))
        {
            foreach (var planned in filePlan)
            {
                var path = Path.Combine(roundDir, planned.FileName);
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    if (fi.Length != planned.SizeBytes)
                    {
                        EmitLog(LogLevel.Warning, $"[RESUME] Incomplete file '{planned.FileName}' ({fi.Length} / {planned.SizeBytes} bytes). Deleting to recreate...");
                        try { File.Delete(path); } catch { }
                    }
                }
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
                    var files = Directory.GetFiles(roundDir, "*.*", SearchOption.AllDirectories);
                    EmitLog(LogLevel.Info, $"Round {roundNumber}: Deleting {files.Length} test files to reclaim drive space...");
                    int deletedCount = 0;

                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                            deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            EmitLog(LogLevel.Warning, $"Could not delete {Path.GetFileName(file)}: {ex.Message}");
                        }

                        if (deletedCount % 20 == 0 || deletedCount == files.Length)
                        {
                            ProgressUpdated?.Invoke(new TestProgressUpdate
                            {
                                CurrentRound = roundNumber,
                                TotalRounds = _config.Rounds,
                                CurrentPhase = TestPhase.Emptying,
                                PhaseBytesProcessed = deletedCount,
                                PhaseTotalBytes = Math.Max(1, files.Length),
                                StatusMessage = $"Emptying Round {roundNumber} ({deletedCount}/{files.Length} files deleted)"
                            });
                        }
                    }

                    for (int attempt = 1; attempt <= 5; attempt++)
                    {
                        try
                        {
                            if (Directory.Exists(roundDir))
                            {
                                Directory.Delete(roundDir, recursive: true);
                            }
                            break;
                        }
                        catch when (attempt < 5)
                        {
                            Thread.Sleep(200);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EmitLog(LogLevel.Warning, $"Could not delete directory {roundDir}: {ex.Message}");
            }
        }, ct);
    }

    private async Task CleanPreviousRoundsAsync(string testRootDir, int currentRound, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(testRootDir)) return;

                var roundDirs = Directory.GetDirectories(testRootDir, "Round_*");
                foreach (var dir in roundDirs)
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.StartsWith("Round_", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(dirName.AsSpan("Round_".Length), out int roundNum) &&
                        roundNum < currentRound)
                    {
                        EmitLog(LogLevel.Warning, $"[SPACE RECLAIM] Found uncleaned files from Round {roundNum} ({dirName}). Deleting to reclaim space for Round {currentRound}...");

                        try
                        {
                            var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
                            foreach (var f in files)
                            {
                                ct.ThrowIfCancellationRequested();
                                try
                                {
                                    File.SetAttributes(f, FileAttributes.Normal);
                                    File.Delete(f);
                                }
                                catch { }
                            }

                            for (int attempt = 1; attempt <= 5; attempt++)
                            {
                                try
                                {
                                    if (Directory.Exists(dir))
                                    {
                                        Directory.Delete(dir, recursive: true);
                                    }
                                    break;
                                }
                                catch when (attempt < 5)
                                {
                                    Thread.Sleep(200);
                                }
                            }

                            EmitLog(LogLevel.Success, $"[SPACE RECLAIM] Successfully removed {dirName}. Storage reclaimed for Round {currentRound}.");
                        }
                        catch (Exception ex)
                        {
                            EmitLog(LogLevel.Warning, $"Could not completely delete {dirName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EmitLog(LogLevel.Warning, $"Error during previous round cleanup: {ex.Message}");
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

    public static TestSessionState? TryGetResumableSession(DriveTargetInfo? drive, string testFolderName = "DriveTester_IntegrityTest")
    {
        if (drive == null) return null;
        var driveRoot = drive.DriveLetter;
        if (!driveRoot.EndsWith('\\')) driveRoot += "\\";

        var state = TestSessionState.TryLoad(driveRoot, testFolderName);
        if (state != null && state.Status == SessionStatus.InProgress)
        {
            return state;
        }

        // If session_state.json was missing (e.g. from an earlier build that crashed before state saving),
        // inspect existing test files on the drive to reconstruct the session state directly from file headers.
        try
        {
            var testRootDir = Path.Combine(driveRoot, testFolderName);
            if (Directory.Exists(testRootDir))
            {
                var roundDirs = Directory.GetDirectories(testRootDir, "Round_*");
                Array.Sort(roundDirs);
                var activeRoundDir = roundDirs.Length > 0 ? roundDirs[^1] : null;

                if (activeRoundDir != null)
                {
                    var tstFiles = Directory.GetFiles(activeRoundDir, "*.tst");
                    if (tstFiles.Length > 0)
                    {
                        Array.Sort(tstFiles);

                        foreach (var file in tstFiles)
                        {
                            var fi = new FileInfo(file);
                            if (fi.Length >= PatternDataGenerator.HEADER_SIZE)
                            {
                                byte[] header = new byte[PatternDataGenerator.HEADER_SIZE];
                                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                {
                                    int read = fs.Read(header, 0, header.Length);
                                    if (read == header.Length)
                                    {
                                        ulong magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0, 8));
                                        if (magic == PatternDataGenerator.MAGIC_SIGNATURE)
                                        {
                                            int round = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
                                            int fileIdx = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
                                            ulong blockSeed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32, 8));
                                            ulong recoveredBaseSeed = blockSeed ^ (ulong)round ^ ((ulong)fileIdx << 16);

                                            var lastFile = tstFiles[^1];
                                            var lastFileName = Path.GetFileName(lastFile);

                                            var reconstructed = new TestSessionState
                                            {
                                                BaseSeed = recoveredBaseSeed,
                                                PlannedRounds = Math.Max(2, round),
                                                CurrentRound = round,
                                                CurrentPhase = TestPhase.Writing,
                                                InFlightFileName = lastFileName,
                                                InFlightFileIndex = tstFiles.Length,
                                                IsGracefullyPaused = false, // Ungraceful crash: recreate last file
                                                SizePreset = FileSizePreset.Balanced,
                                                TargetBytesPerRound = tstFiles.Sum(f => new FileInfo(f).Length),
                                                EmptyFilesAfterEachRound = true,
                                                Status = SessionStatus.InProgress
                                            };

                                            reconstructed.Save(driveRoot, testFolderName);
                                            return reconstructed;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }

    public static void DiscardSession(DriveTargetInfo? drive, string testFolderName = "DriveTester_IntegrityTest")
    {
        if (drive == null) return;
        var driveRoot = drive.DriveLetter;
        if (!driveRoot.EndsWith('\\')) driveRoot += "\\";
        TestSessionState.Delete(driveRoot, testFolderName);
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
