namespace DriveTester.Models;

public enum TestPhase
{
    Idle,
    Preparing,
    Writing,
    Verifying,
    Emptying,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public enum CapacityTargetMode
{
    FullFreeSpace,
    SafeFreeSpace95Percent,
    Fixed10GB,
    Fixed50GB,
    Fixed100GB,
    Fixed500GB,
    Fixed1000GB,
    CustomGB
}

public enum FileSizePreset
{
    Balanced,           // 70% 512MB-1GB, 20% 32MB-64MB, 10% 1MB-4MB
    FastSequential,     // 95% 1GB, 5% 64MB (fastest throughput for 1TB)
    DiverseStress       // 50% 256MB-1GB, 30% 16MB-64MB, 20% 256KB-4MB
}

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}
