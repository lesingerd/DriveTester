using System.IO;
using System.Management;
using DriveTester.Models;

namespace DriveTester.Services;

public class DriveDetector
{
    /// <summary>
    /// Instantly returns all ready drives using fast native DriveInfo (zero delay, sub-millisecond).
    /// </summary>
    public static List<DriveTargetInfo> GetAvailableDrivesFast()
    {
        var result = new List<DriveTargetInfo>();
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\').ToUpperInvariant() ?? "C:";

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                // Skip optical / network drives
                if (drive.DriveType == System.IO.DriveType.CDRom || drive.DriveType == System.IO.DriveType.Network)
                    continue;

                var driveLetter = drive.Name.TrimEnd('\\').ToUpperInvariant();
                var isSystem = driveLetter.Equals(systemRoot, StringComparison.OrdinalIgnoreCase);
                var isRemovable = drive.DriveType == System.IO.DriveType.Removable;

                var info = new DriveTargetInfo
                {
                    DriveLetter = drive.Name,
                    VolumeLabel = string.IsNullOrEmpty(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                    DriveFormat = drive.DriveFormat,
                    DriveType = drive.DriveType.ToString(),
                    TotalSizeBytes = drive.TotalSize,
                    FreeSpaceBytes = drive.AvailableFreeSpace,
                    IsSystemDrive = isSystem,
                    IsUsb = isRemovable,
                    BusType = isRemovable ? "USB" : "Fixed",
                    ModelName = isRemovable ? "External Storage" : (isSystem ? "System Drive" : "Local Storage"),
                    SerialNumber = string.Empty
                };

                result.Add(info);
            }
            catch
            {
                // Access denied or unformatted drive
            }
        }

        return result
            .OrderByDescending(d => d.IsUsb)
            .ThenBy(d => d.IsSystemDrive)
            .ThenBy(d => d.DriveLetter)
            .ToList();
    }

    /// <summary>
    /// Enriches drive models with detailed WMI bus type, hardware model, and serial number in background.
    /// </summary>
    public static async Task EnrichDrivesAsync(List<DriveTargetInfo> drives)
    {
        await Task.Run(() =>
        {
            try
            {
                var wmiDisks = GetWmiDiskInfoMap();
                foreach (var d in drives)
                {
                    var driveLetter = d.DriveLetter.TrimEnd('\\').ToUpperInvariant();
                    if (wmiDisks.TryGetValue(driveLetter, out var wmiDetails))
                    {
                        if (!string.IsNullOrWhiteSpace(wmiDetails.Model))
                            d.ModelName = wmiDetails.Model;
                        if (!string.IsNullOrWhiteSpace(wmiDetails.BusType) && wmiDetails.BusType != "Unknown")
                            d.BusType = wmiDetails.BusType;
                        if (wmiDetails.BusType.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
                            wmiDetails.MediaType.Contains("External", StringComparison.OrdinalIgnoreCase))
                        {
                            d.IsUsb = true;
                        }
                        if (!string.IsNullOrWhiteSpace(wmiDetails.SerialNumber))
                            d.SerialNumber = wmiDetails.SerialNumber;
                    }
                }
            }
            catch
            {
                // Silently ignore WMI query failures
            }
        });
    }

    private class WmiDiskDetail
    {
        public string Model { get; set; } = string.Empty;
        public string BusType { get; set; } = "Unknown";
        public string MediaType { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
    }

    private static Dictionary<string, WmiDiskDetail> GetWmiDiskInfoMap()
    {
        var map = new Dictionary<string, WmiDiskDetail>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var diskDrives = new Dictionary<string, WmiDiskDetail>();
            using (var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new SelectQuery("Win32_DiskDrive"),
                new System.Management.EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(3) }))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    var devId = mo["DeviceID"]?.ToString() ?? "";
                    var model = mo["Model"]?.ToString()?.Trim() ?? "";
                    var iface = mo["InterfaceType"]?.ToString()?.Trim() ?? "Unknown";
                    var media = mo["MediaType"]?.ToString()?.Trim() ?? "";
                    var serial = mo["SerialNumber"]?.ToString()?.Trim() ?? "";

                    if (!string.IsNullOrEmpty(devId))
                    {
                        diskDrives[devId] = new WmiDiskDetail
                        {
                            Model = model,
                            BusType = iface,
                            MediaType = media,
                            SerialNumber = serial
                        };
                    }
                }
            }

            using (var logSearcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new SelectQuery("SELECT DeviceID FROM Win32_LogicalDisk WHERE DriveType = 2 OR DriveType = 3"),
                new System.Management.EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(3) }))
            {
                foreach (ManagementObject logDisk in logSearcher.Get())
                {
                    var logId = logDisk["DeviceID"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(logId)) continue;

                    try
                    {
                        var partitionQuery = $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{logId}'}} WHERE AssocClass = Win32_LogicalDiskToPartition";
                        using var partQuery = new ManagementObjectSearcher(partitionQuery);
                        foreach (ManagementObject partition in partQuery.Get())
                        {
                            var partId = partition["DeviceID"]?.ToString() ?? "";
                            var driveQuery = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition";
                            using var driveQuerySearcher = new ManagementObjectSearcher(driveQuery);
                            foreach (ManagementObject diskDrive in driveQuerySearcher.Get())
                            {
                                var devId = diskDrive["DeviceID"]?.ToString() ?? "";
                                if (diskDrives.TryGetValue(devId, out var detail))
                                {
                                    map[logId] = detail;
                                }
                                else
                                {
                                    map[logId] = new WmiDiskDetail
                                    {
                                        Model = diskDrive["Model"]?.ToString()?.Trim() ?? "",
                                        BusType = diskDrive["InterfaceType"]?.ToString()?.Trim() ?? "Unknown",
                                        MediaType = diskDrive["MediaType"]?.ToString()?.Trim() ?? "",
                                        SerialNumber = diskDrive["SerialNumber"]?.ToString()?.Trim() ?? ""
                                    };
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore associator failure for specific drive
                    }
                }
            }
        }
        catch
        {
            // WMI failed or unavailable
        }

        return map;
    }
}
