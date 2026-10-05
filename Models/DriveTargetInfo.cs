namespace DriveTester.Models;

public class DriveTargetInfo
{
    public string DriveLetter { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string DriveFormat { get; set; } = string.Empty;
    public string DriveType { get; set; } = string.Empty;
    public string BusType { get; set; } = "Unknown";
    public string ModelName { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public long FreeSpaceBytes { get; set; }
    public bool IsSystemDrive { get; set; }
    public bool IsUsb { get; set; }

    public double TotalSizeGB { get => TotalSizeBytes / (1024.0 * 1024.0 * 1024.0); set { } }
    public double FreeSpaceGB { get => FreeSpaceBytes / (1024.0 * 1024.0 * 1024.0); set { } }
    public double UsedSpaceGB { get => TotalSizeGB - FreeSpaceGB; set { } }
    public double PercentUsed { get => TotalSizeBytes > 0 ? (1.0 - (double)FreeSpaceBytes / TotalSizeBytes) * 100.0 : 0.0; set { } }

    public string DisplayTitle
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(VolumeLabel) ? "Local Disk" : VolumeLabel;
            var bus = IsUsb ? " [USB]" : (BusType != "Unknown" ? $" [{BusType}]" : "");
            return $"{DriveLetter} ({label}) - {FreeSpaceGB:F1} GB free of {TotalSizeGB:F1} GB{bus}";
        }
        set { }
    }

    public string DetailedSubtitle
    {
        get => $"{ModelName} | {DriveFormat} | {(IsUsb ? "Removable/External USB" : DriveType)}{(IsSystemDrive ? " | [SYSTEM DRIVE - CAUTION]" : "")}";
        set { }
    }

    public override string ToString() => DisplayTitle;
}
