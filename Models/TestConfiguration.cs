namespace DriveTester.Models;

public class TestConfiguration
{
    public DriveTargetInfo? TargetDrive { get; set; }
    public int Rounds { get; set; } = 2;
    public CapacityTargetMode TargetMode { get; set; } = CapacityTargetMode.SafeFreeSpace95Percent;
    public double CustomCapacityGB { get; set; } = 100.0;
    public FileSizePreset SizePreset { get; set; } = FileSizePreset.Balanced;
    public bool FlushBuffersDirectly { get; set; } = true;
    public bool StopOnFirstError { get; set; } = false;
    public bool EmptyFilesAfterEachRound { get; set; } = true;
    public string TestFolderName { get; set; } = "DriveTester_IntegrityTest";

    public long CalculateTargetBytes(long availableFreeBytes)
    {
        long target = TargetMode switch
        {
            CapacityTargetMode.FullFreeSpace => availableFreeBytes,
            CapacityTargetMode.SafeFreeSpace95Percent => (long)(availableFreeBytes * 0.95),
            CapacityTargetMode.Fixed10GB => 10L * 1024 * 1024 * 1024,
            CapacityTargetMode.Fixed50GB => 50L * 1024 * 1024 * 1024,
            CapacityTargetMode.Fixed100GB => 100L * 1024 * 1024 * 1024,
            CapacityTargetMode.Fixed500GB => 500L * 1024 * 1024 * 1024,
            CapacityTargetMode.Fixed1000GB => 1000L * 1024 * 1024 * 1024,
            CapacityTargetMode.CustomGB => (long)(Math.Max(0.1, CustomCapacityGB) * 1024 * 1024 * 1024),
            _ => (long)(availableFreeBytes * 0.95)
        };

        // Don't exceed available free space with 100MB safety buffer
        long maxSafe = Math.Max(0, availableFreeBytes - 100L * 1024 * 1024);
        return Math.Min(target, maxSafe);
    }
}
