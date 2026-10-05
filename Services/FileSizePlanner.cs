using DriveTester.Models;

namespace DriveTester.Services;

public class TestFileInfo
{
    public int FileIndex { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public double SizeMB => SizeBytes / (1024.0 * 1024.0);
}

public static class FileSizePlanner
{
    public static List<TestFileInfo> PlanFiles(long totalTargetBytes, FileSizePreset preset, int round)
    {
        var files = new List<TestFileInfo>();
        if (totalTargetBytes <= 0) return files;

        long remaining = totalTargetBytes;
        int fileIndex = 1;
        var rng = new Random(42 + round * 1000);

        // Predefined pools based on preset
        long[] largePool = { 1024L * 1024 * 1024, 768L * 1024 * 1024, 512L * 1024 * 1024 };
        long[] mediumPool = { 64L * 1024 * 1024, 32L * 1024 * 1024, 16L * 1024 * 1024 };
        long[] smallPool = { 4L * 1024 * 1024, 2L * 1024 * 1024, 1L * 1024 * 1024, 512L * 1024 };

        while (remaining > 0)
        {
            long chosenSize;

            if (remaining < 512L * 1024)
            {
                chosenSize = remaining;
            }
            else
            {
                int roll = rng.Next(100);

                var fitLarge = largePool.Where(s => s <= remaining).ToArray();
                var fitMedium = mediumPool.Where(s => s <= remaining).ToArray();
                var fitSmall = smallPool.Where(s => s <= remaining).ToArray();

                if (preset == FileSizePreset.FastSequential)
                {
                    if (roll < 92 && fitLarge.Length > 0)
                        chosenSize = fitLarge[rng.Next(fitLarge.Length)];
                    else if (roll < 98 && fitMedium.Length > 0)
                        chosenSize = fitMedium[rng.Next(fitMedium.Length)];
                    else if (fitSmall.Length > 0)
                        chosenSize = fitSmall[rng.Next(fitSmall.Length)];
                    else
                        chosenSize = remaining;
                }
                else if (preset == FileSizePreset.DiverseStress)
                {
                    if (roll < 40 && fitLarge.Length > 0)
                        chosenSize = fitLarge[rng.Next(fitLarge.Length)];
                    else if (roll < 75 && fitMedium.Length > 0)
                        chosenSize = fitMedium[rng.Next(fitMedium.Length)];
                    else if (fitSmall.Length > 0)
                        chosenSize = fitSmall[rng.Next(fitSmall.Length)];
                    else
                        chosenSize = remaining;
                }
                else // Balanced
                {
                    if (roll < 70 && fitLarge.Length > 0)
                        chosenSize = fitLarge[rng.Next(fitLarge.Length)];
                    else if (roll < 90 && fitMedium.Length > 0)
                        chosenSize = fitMedium[rng.Next(fitMedium.Length)];
                    else if (fitSmall.Length > 0)
                        chosenSize = fitSmall[rng.Next(fitSmall.Length)];
                    else
                        chosenSize = remaining;
                }

                if (chosenSize > remaining)
                {
                    chosenSize = remaining;
                }
            }

            var sizeMbStr = chosenSize >= 1024L * 1024 * 1024
                ? $"{chosenSize / (1024.0 * 1024.0 * 1024.0):F1}GB"
                : (chosenSize >= 1024L * 1024 ? $"{chosenSize / (1024.0 * 1024.0):F0}MB" : $"{chosenSize / 1024.0:F0}KB");

            var fileName = $"test_r{round:D2}_f{fileIndex:D5}_{sizeMbStr}.tst";

            files.Add(new TestFileInfo
            {
                FileIndex = fileIndex,
                FileName = fileName,
                SizeBytes = chosenSize
            });

            remaining -= chosenSize;
            fileIndex++;
        }

        return files;
    }
}
