using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DriveTester.Models;
using DriveTester.Services;
using Microsoft.Win32;

namespace DriveTester.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly object _logsLock = new();
    private DriveTestEngine? _engine;
    private DriveTargetInfo? _selectedDrive;
    private TestSessionState? _resumableSession;

    private int _rounds = 2;
    private CapacityTargetMode _selectedTargetMode = CapacityTargetMode.SafeFreeSpace95Percent;
    private double _customCapacityGB = 100.0;
    private FileSizePreset _selectedPreset = FileSizePreset.Balanced;
    private bool _flushBuffersDirectly = true;
    private bool _stopOnFirstError = false;
    private bool _emptyFilesAfterEachRound = true;

    private bool _isTesting;
    private bool _isPaused;
    private TestPhase _currentPhase = TestPhase.Idle;

    private string _statusMessage = "Ready to test drive. Select a drive and configure rounds.";
    private string _currentRoundDisplay = "Round - / -";
    private string _currentFileDisplay = "No active file";
    private double _fileProgressPercent;
    private double _phaseProgressPercent;
    private double _overallProgressPercent;

    private string _currentSpeedDisplay = "0.0 MB/s";
    private string _avgSpeedDisplay = "0.0 MB/s";
    private string _peakSpeedDisplay = "0.0 MB/s";
    private string _processedSizeDisplay = "0.0 GB / 0.0 GB";
    private string _elapsedTimeDisplay = "00:00:00";
    private string _etaDisplay = "--:--:--";
    private int _errorCount;
    private string _verdictText = "Test not started yet.";
    private string _reportMarkdown = string.Empty;
    private FinalTestReport? _lastReport;

    private readonly List<double> _speedHistory = new();

    public ObservableCollection<DriveTargetInfo> Drives { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();
    public ObservableCollection<RoundResult> RoundResults { get; } = new();

    public IReadOnlyList<CapacityTargetMode> TargetModes { get; } = Enum.GetValues<CapacityTargetMode>();
    public IReadOnlyList<FileSizePreset> SizePresets { get; } = Enum.GetValues<FileSizePreset>();

    public DriveTargetInfo? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value))
            {
                OnPropertyChanged(nameof(CanStartTest));
                OnPropertyChanged(nameof(IsSystemDriveSelected));
                OnPropertyChanged(nameof(DriveSummaryText));
                OnPropertyChanged(nameof(TargetCapacityCalculationText));
                CheckResumableSession();
            }
        }
    }

    public int Rounds
    {
        get => _rounds;
        set => SetProperty(ref _rounds, Math.Clamp(value, 1, 50));
    }

    public CapacityTargetMode SelectedTargetMode
    {
        get => _selectedTargetMode;
        set
        {
            if (SetProperty(ref _selectedTargetMode, value))
            {
                OnPropertyChanged(nameof(IsCustomCapacityVisible));
                OnPropertyChanged(nameof(TargetCapacityCalculationText));
            }
        }
    }

    public bool IsCustomCapacityVisible => SelectedTargetMode == CapacityTargetMode.CustomGB;

    public double CustomCapacityGB
    {
        get => _customCapacityGB;
        set
        {
            if (SetProperty(ref _customCapacityGB, Math.Max(0.1, value)))
            {
                OnPropertyChanged(nameof(TargetCapacityCalculationText));
            }
        }
    }

    public FileSizePreset SelectedPreset
    {
        get => _selectedPreset;
        set => SetProperty(ref _selectedPreset, value);
    }

    public bool FlushBuffersDirectly
    {
        get => _flushBuffersDirectly;
        set => SetProperty(ref _flushBuffersDirectly, value);
    }

    public bool StopOnFirstError
    {
        get => _stopOnFirstError;
        set => SetProperty(ref _stopOnFirstError, value);
    }

    public bool EmptyFilesAfterEachRound
    {
        get => _emptyFilesAfterEachRound;
        set => SetProperty(ref _emptyFilesAfterEachRound, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                OnPropertyChanged(nameof(CanStartTest));
                OnPropertyChanged(nameof(CanConfigure));
                OnPropertyChanged(nameof(CanStopTest));
                OnPropertyChanged(nameof(CanPauseResume));
                OnPropertyChanged(nameof(CanExportReport));
                OnPropertyChanged(nameof(HasResumableSession));
                OnPropertyChanged(nameof(CanResumeTest));
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseResumeButtonText));
            }
        }
    }

    public bool CanConfigure => !IsTesting;
    public bool CanStartTest => !IsTesting && SelectedDrive != null;
    public bool CanStopTest => IsTesting;
    public bool CanPauseResume => IsTesting;
    public bool CanExportReport => _lastReport != null;
    public bool IsSystemDriveSelected => SelectedDrive?.IsSystemDrive ?? false;

    public bool HasResumableSession => _resumableSession != null && !IsTesting;
    public bool CanResumeTest => HasResumableSession && !IsTesting;
    public string ResumableSessionBanner => _resumableSession != null
        ? $"Interrupted test found on {SelectedDrive?.DriveLetter}: Round {_resumableSession.CurrentRound} of {_resumableSession.PlannedRounds} (Phase: {_resumableSession.CurrentPhase})"
        : string.Empty;

    public string PauseResumeButtonText => IsPaused ? "Resume Test" : "Pause Test";

    public TestPhase CurrentPhase
    {
        get => _currentPhase;
        private set
        {
            if (SetProperty(ref _currentPhase, value))
            {
                OnPropertyChanged(nameof(PhaseBadgeBrush));
                OnPropertyChanged(nameof(PhaseBadgeText));
            }
        }
    }

    public Brush PhaseBadgeBrush => CurrentPhase switch
    {
        TestPhase.Writing => new SolidColorBrush(Color.FromRgb(59, 130, 246)),      // Blue
        TestPhase.Verifying => new SolidColorBrush(Color.FromRgb(16, 185, 129)),   // Emerald Green
        TestPhase.Emptying => new SolidColorBrush(Color.FromRgb(245, 158, 11)),    // Amber
        TestPhase.Paused => new SolidColorBrush(Color.FromRgb(168, 85, 247)),      // Purple
        TestPhase.Completed => new SolidColorBrush(Color.FromRgb(34, 197, 94)),    // Green
        TestPhase.Failed => new SolidColorBrush(Color.FromRgb(239, 68, 68)),       // Red
        TestPhase.Cancelled => new SolidColorBrush(Color.FromRgb(107, 114, 128)),  // Gray
        _ => new SolidColorBrush(Color.FromRgb(71, 85, 105))                       // Slate
    };

    public string PhaseBadgeText => CurrentPhase switch
    {
        TestPhase.Writing => "WRITING DATA",
        TestPhase.Verifying => "VERIFYING DATA",
        TestPhase.Emptying => "EMPTYING DRIVE",
        TestPhase.Paused => "PAUSED",
        TestPhase.Completed => "COMPLETED",
        TestPhase.Failed => "FAILED",
        TestPhase.Cancelled => "CANCELLED",
        _ => "IDLE"
    };

    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
    public string CurrentRoundDisplay { get => _currentRoundDisplay; set => SetProperty(ref _currentRoundDisplay, value); }
    public string CurrentFileDisplay { get => _currentFileDisplay; set => SetProperty(ref _currentFileDisplay, value); }
    public double FileProgressPercent { get => _fileProgressPercent; set => SetProperty(ref _fileProgressPercent, value); }
    public double PhaseProgressPercent { get => _phaseProgressPercent; set => SetProperty(ref _phaseProgressPercent, value); }
    public double OverallProgressPercent { get => _overallProgressPercent; set => SetProperty(ref _overallProgressPercent, value); }
    public string CurrentSpeedDisplay { get => _currentSpeedDisplay; set => SetProperty(ref _currentSpeedDisplay, value); }
    public string AvgSpeedDisplay { get => _avgSpeedDisplay; set => SetProperty(ref _avgSpeedDisplay, value); }
    public string PeakSpeedDisplay { get => _peakSpeedDisplay; set => SetProperty(ref _peakSpeedDisplay, value); }
    public string ProcessedSizeDisplay { get => _processedSizeDisplay; set => SetProperty(ref _processedSizeDisplay, value); }
    public string ElapsedTimeDisplay { get => _elapsedTimeDisplay; set => SetProperty(ref _elapsedTimeDisplay, value); }
    public string EtaDisplay { get => _etaDisplay; set => SetProperty(ref _etaDisplay, value); }

    public int ErrorCount
    {
        get => _errorCount;
        set
        {
            if (SetProperty(ref _errorCount, value))
            {
                OnPropertyChanged(nameof(ErrorBadgeBrush));
                OnPropertyChanged(nameof(ErrorBadgeText));
            }
        }
    }

    public Brush ErrorBadgeBrush => ErrorCount == 0
        ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
        : new SolidColorBrush(Color.FromRgb(239, 68, 68));

    public string ErrorBadgeText => ErrorCount == 0
        ? "0 Errors (Integrity Clean)"
        : $"! {ErrorCount} Corrupt Blocks Detected";

    public string VerdictText { get => _verdictText; set => SetProperty(ref _verdictText, value); }
    public string ReportMarkdown { get => _reportMarkdown; set => SetProperty(ref _reportMarkdown, value); }

    public string DriveSummaryText
    {
        get
        {
            if (SelectedDrive == null) return "No drive selected.";
            return $"{SelectedDrive.ModelName} | {SelectedDrive.DriveFormat} | {SelectedDrive.FreeSpaceGB:F1} GB free of {SelectedDrive.TotalSizeGB:F1} GB";
        }
    }

    public string TargetCapacityCalculationText
    {
        get
        {
            if (SelectedDrive == null) return "Target: --";
            var cfg = new TestConfiguration
            {
                TargetMode = SelectedTargetMode,
                CustomCapacityGB = CustomCapacityGB
            };
            long targetBytes = cfg.CalculateTargetBytes(SelectedDrive.FreeSpaceBytes);
            return $"Per round target: ~{targetBytes / (1024.0 * 1024.0 * 1024.0):F1} GB (Total across {Rounds} rounds: ~{targetBytes * Rounds / (1024.0 * 1024.0 * 1024.0):F1} GB written + verified)";
        }
    }

    public IReadOnlyList<double> SpeedHistory => _speedHistory;
    public event Action? SpeedHistoryChanged;

    public ICommand RefreshDrivesCommand { get; }
    public ICommand StartTestCommand { get; }
    public ICommand ResumeTestCommand { get; }
    public ICommand DiscardSessionCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand StopTestCommand { get; }
    public ICommand CleanTestFilesCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand CopyReportCommand { get; }
    public ICommand ClearLogsCommand { get; }

    public MainViewModel()
    {
        BindingOperations.EnableCollectionSynchronization(Logs, _logsLock);

        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        StartTestCommand = new RelayCommand(async () => await StartTestAsync(resume: false), () => CanStartTest);
        ResumeTestCommand = new RelayCommand(async () => await StartTestAsync(resume: true), () => CanResumeTest);
        DiscardSessionCommand = new RelayCommand(DiscardInterruptedSession, () => HasResumableSession);
        PauseResumeCommand = new RelayCommand(TogglePauseResume, () => CanPauseResume);
        StopTestCommand = new RelayCommand(StopTest, () => CanStopTest);
        CleanTestFilesCommand = new RelayCommand(async () => await CleanTestFilesAsync(), () => !IsTesting && SelectedDrive != null);
        ExportReportCommand = new RelayCommand(ExportReport, () => CanExportReport);
        CopyReportCommand = new RelayCommand(CopyReport, () => CanExportReport);
        ClearLogsCommand = new RelayCommand(() =>
        {
            lock (_logsLock) { Logs.Clear(); }
        });

        RefreshDrives();
    }

    public void CheckResumableSession()
    {
        _resumableSession = DriveTestEngine.TryGetResumableSession(SelectedDrive);
        OnPropertyChanged(nameof(HasResumableSession));
        OnPropertyChanged(nameof(CanResumeTest));
        OnPropertyChanged(nameof(ResumableSessionBanner));
    }

    private void DiscardInterruptedSession()
    {
        if (SelectedDrive == null) return;
        var result = MessageBox.Show(
            $"Discard interrupted test session for drive {SelectedDrive.DriveLetter}?\n\nExisting partial test files will be cleaned if you choose 'Clean Leftover Test Files'.",
            "Discard Interrupted Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            DriveTestEngine.DiscardSession(SelectedDrive);
            CheckResumableSession();
            AddLog(LogLevel.Info, $"Discarded previous test session state on {SelectedDrive.DriveLetter}.");
        }
    }

    public void RefreshDrives()
    {
        var currentLetter = SelectedDrive?.DriveLetter;
        Drives.Clear();

        // 1. Instant load using native DriveInfo (sub-millisecond, no WMI UI block)
        var detected = DriveDetector.GetAvailableDrivesFast();
        foreach (var drive in detected)
        {
            Drives.Add(drive);
        }

        if (currentLetter != null)
        {
            SelectedDrive = Drives.FirstOrDefault(d => d.DriveLetter == currentLetter) ?? Drives.FirstOrDefault();
        }
        else
        {
            SelectedDrive = Drives.FirstOrDefault();
        }

        AddLog(LogLevel.Info, $"Found {Drives.Count} active storage drive(s).");
        CheckResumableSession();

        // 2. Enrich WMI metadata (model, USB bus) asynchronously in background
        _ = Task.Run(async () =>
        {
            try
            {
                await DriveDetector.EnrichDrivesAsync(detected);
                if (Application.Current != null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        OnPropertyChanged(nameof(DriveSummaryText));
                        OnPropertyChanged(nameof(SelectedDrive));
                    });
                }
            }
            catch { }
        });
    }

    private async Task StartTestAsync(bool resume = false)
    {
        if (SelectedDrive == null) return;

        if (!resume && HasResumableSession && _resumableSession != null)
        {
            var res = MessageBox.Show(
                $"An interrupted test session was found on drive {SelectedDrive.DriveLetter}:\n\n" +
                $"• Round: {_resumableSession.CurrentRound} of {_resumableSession.PlannedRounds}\n" +
                $"• Phase: {_resumableSession.CurrentPhase}\n\n" +
                "Would you like to RESUME this test from where it stopped?\n\n" +
                "• Click 'Yes' to RESUME (any partially written file will be recreated).\n" +
                "• Click 'No' to DISCARD old session and start a new test from scratch.\n" +
                "• Click 'Cancel' to abort.",
                "Interrupted Test Detected",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Cancel) return;
            if (res == MessageBoxResult.Yes)
            {
                resume = true;
            }
            else
            {
                DriveTestEngine.DiscardSession(SelectedDrive);
                CheckResumableSession();
            }
        }

        if (SelectedDrive.IsSystemDrive)
        {
            var msgResult = MessageBox.Show(
                $"WARNING: You have selected drive {SelectedDrive.DriveLetter}, which is your WINDOWS SYSTEM DRIVE!\n\n" +
                "DriveTester fills drive free space with stress-testing files. While it will not delete non-test files, filling the system drive completely can cause Windows or running apps to become unstable.\n\n" +
                "Are you absolutely certain you want to proceed with testing this drive?",
                "System Drive Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (msgResult != MessageBoxResult.Yes)
                return;
        }

        IsTesting = true;
        IsPaused = false;
        CurrentPhase = TestPhase.Preparing;
        RoundResults.Clear();
        _speedHistory.Clear();
        SpeedHistoryChanged?.Invoke();
        ErrorCount = 0;
        VerdictText = "Testing in progress...";
        ReportMarkdown = string.Empty;
        _lastReport = null;

        TestSessionState? resumeState = null;
        TestConfiguration config;

        if (resume && _resumableSession != null)
        {
            resumeState = _resumableSession;
            config = new TestConfiguration
            {
                TargetDrive = SelectedDrive,
                Rounds = resumeState.PlannedRounds,
                TargetMode = resumeState.TargetMode,
                CustomCapacityGB = resumeState.CustomCapacityGB,
                SizePreset = resumeState.SizePreset,
                FlushBuffersDirectly = FlushBuffersDirectly,
                StopOnFirstError = StopOnFirstError,
                EmptyFilesAfterEachRound = EmptyFilesAfterEachRound
            };

            if (resumeState.CompletedRoundsResults != null)
            {
                foreach (var prevRound in resumeState.CompletedRoundsResults)
                {
                    RoundResults.Add(prevRound);
                }
            }
            for (int pr = 1; pr < resumeState.CurrentRound && pr <= resumeState.PlannedRounds; pr++)
            {
                if (!RoundResults.Any(r => r.RoundNumber == pr))
                {
                    RoundResults.Add(new RoundResult
                    {
                        RoundNumber = pr,
                        BytesWritten = resumeState.TargetBytesPerRound,
                        BytesVerified = resumeState.TargetBytesPerRound,
                        ErrorCount = 0
                    });
                }
            }
        }
        else
        {
            config = new TestConfiguration
            {
                TargetDrive = SelectedDrive,
                Rounds = Rounds,
                TargetMode = SelectedTargetMode,
                CustomCapacityGB = CustomCapacityGB,
                SizePreset = SelectedPreset,
                FlushBuffersDirectly = FlushBuffersDirectly,
                StopOnFirstError = StopOnFirstError,
                EmptyFilesAfterEachRound = EmptyFilesAfterEachRound
            };
        }

        _engine = new DriveTestEngine(config, resumeState?.BaseSeed);
        _engine.LogEmitted += OnEngineLogEmitted;
        _engine.ProgressUpdated += OnEngineProgressUpdated;
        _engine.RoundCompleted += OnEngineRoundCompleted;
        _engine.TestCompleted += OnEngineTestCompleted;

        try
        {
            await _engine.RunAsync(resumeState);
        }
        finally
        {
            IsTesting = false;
            IsPaused = false;
            CheckResumableSession();
        }
    }

    private void TogglePauseResume()
    {
        if (_engine == null || !IsTesting) return;

        if (IsPaused)
        {
            _engine.Resume();
            IsPaused = false;
            CurrentPhase = _lastPhaseBeforePause;
        }
        else
        {
            _lastPhaseBeforePause = CurrentPhase;
            _engine.Pause();
            IsPaused = true;
            CurrentPhase = TestPhase.Paused;
        }
    }
    private TestPhase _lastPhaseBeforePause = TestPhase.Writing;

    private void StopTest()
    {
        if (_engine != null && IsTesting)
        {
            _engine.Stop();
        }
    }

    private async Task CleanTestFilesAsync()
    {
        if (SelectedDrive == null) return;

        var result = MessageBox.Show(
            $"This will scan and delete any test data folder ('DriveTester_IntegrityTest') left on {SelectedDrive.DriveLetter}.\n\nProceed?",
            "Clean Leftover Test Files",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        AddLog(LogLevel.Info, $"Cleaning leftover test files on {SelectedDrive.DriveLetter}...");
        await DriveTestEngine.CleanAllTestFilesAsync(SelectedDrive, "DriveTester_IntegrityTest", msg => AddLog(LogLevel.Info, msg));
        DriveTestEngine.DiscardSession(SelectedDrive);
        RefreshDrives();
    }

    private void OnEngineLogEmitted(LogEntry entry)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            lock (_logsLock)
            {
                Logs.Add(entry);
                if (Logs.Count > 5000)
                {
                    Logs.RemoveAt(0);
                }
            }
        }, DispatcherPriority.Background);
    }

    private void OnEngineProgressUpdated(TestProgressUpdate p)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (!IsPaused)
            {
                CurrentPhase = p.CurrentPhase;
            }

            CurrentRoundDisplay = $"Round {p.CurrentRound} of {p.TotalRounds}";
            CurrentFileDisplay = p.CurrentFileName;
            FileProgressPercent = p.FileProgressPercent;
            PhaseProgressPercent = p.PhaseProgressPercent;
            OverallProgressPercent = p.OverallProgressPercent;

            CurrentSpeedDisplay = $"{p.CurrentSpeedMBps:F1} MB/s";
            AvgSpeedDisplay = $"{p.AverageSpeedMBps:F1} MB/s";
            PeakSpeedDisplay = $"{p.PeakSpeedMBps:F1} MB/s";

            double processedGB = p.PhaseBytesProcessed / (1024.0 * 1024.0 * 1024.0);
            double totalGB = p.PhaseTotalBytes / (1024.0 * 1024.0 * 1024.0);
            ProcessedSizeDisplay = $"{processedGB:F1} GB / {totalGB:F1} GB";

            ElapsedTimeDisplay = p.ElapsedTime.ToString(@"hh\:mm\:ss");
            EtaDisplay = p.EstimatedTimeRemaining > TimeSpan.Zero
                ? p.EstimatedTimeRemaining.ToString(@"hh\:mm\:ss")
                : "--:--:--";

            ErrorCount = p.ErrorCount;
            StatusMessage = p.StatusMessage;

            // Track speed history for live graph (keep latest 150 samples)
            if (p.CurrentSpeedMBps > 0)
            {
                _speedHistory.Add(p.CurrentSpeedMBps);
                if (_speedHistory.Count > 150)
                {
                    _speedHistory.RemoveAt(0);
                }
                SpeedHistoryChanged?.Invoke();
            }
        });
    }

    private void OnEngineRoundCompleted(RoundResult r)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            RoundResults.Add(r);
        });
    }

    private void OnEngineTestCompleted(FinalTestReport report)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _lastReport = report;
            CurrentPhase = report.IsPassed ? TestPhase.Completed : (report.TotalErrorsCount > 0 ? TestPhase.Failed : TestPhase.Cancelled);
            VerdictText = report.IntegrityVerdict;
            ReportMarkdown = report.GenerateMarkdown();
            StatusMessage = report.IntegrityVerdict;
            OnPropertyChanged(nameof(CanExportReport));
        });
    }

    private void ExportReport()
    {
        if (_lastReport == null) return;

        var sfd = new SaveFileDialog
        {
            Title = "Save Drive Test Diagnostic Report",
            Filter = "HTML Report (*.html)|*.html|Markdown Report (*.md)|*.md|Text Report (*.txt)|*.txt",
            FileName = $"DriveTester_Report_{_lastReport.Drive.DriveLetter.Replace(":", "")}_{DateTime.Now:yyyyMMdd_HHmmss}.html"
        };

        if (sfd.ShowDialog() == true)
        {
            try
            {
                var ext = Path.GetExtension(sfd.FileName).ToLowerInvariant();
                string content = ext switch
                {
                    ".html" => _lastReport.GenerateHtml(),
                    _ => _lastReport.GenerateMarkdown()
                };

                File.WriteAllText(sfd.FileName, content);
                AddLog(LogLevel.Success, $"Report exported to: {sfd.FileName}");
                MessageBox.Show($"Report saved successfully to:\n{sfd.FileName}", "Report Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save report: {ex.Message}", "Error Saving Report", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CopyReport()
    {
        if (_lastReport == null) return;
        try
        {
            Clipboard.SetText(_lastReport.GenerateMarkdown());
            AddLog(LogLevel.Info, "Report markdown copied to clipboard.");
            MessageBox.Show("Report copied to clipboard!", "Report Copied", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not copy report: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddLog(LogLevel level, string msg)
    {
        var entry = new LogEntry { Level = level, Message = msg };
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            lock (_logsLock)
            {
                Logs.Add(entry);
                if (Logs.Count > 5000) Logs.RemoveAt(0);
            }
        }, DispatcherPriority.Background);
    }
}
