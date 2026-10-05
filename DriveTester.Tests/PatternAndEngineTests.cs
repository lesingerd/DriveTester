using System.IO;
using DriveTester.Models;
using DriveTester.Services;

namespace DriveTester.Tests;

[TestClass]
public class PatternAndEngineTests
{
    [TestMethod]
    public void PatternDataGenerator_FillAndVerify_ExactMatch()
    {
        int size = 256 * 1024; // 256 KB
        byte[] buffer = new byte[size];
        ulong seed = 0x123456789ABCDEF0UL;
        int round = 1;
        int fileIndex = 5;
        long offset = 0;

        PatternDataGenerator.FillBuffer(buffer, round, fileIndex, offset, seed);

        var (isValid, error) = PatternDataGenerator.VerifyBuffer(buffer, round, fileIndex, offset, seed);

        Assert.IsTrue(isValid, "Buffer verification should succeed for unmodified data.");
        Assert.IsNull(error, "Error message should be null on success.");
    }

    [TestMethod]
    public void PatternDataGenerator_DetectsBitCorruption()
    {
        int size = 128 * 1024; // 128 KB
        byte[] buffer = new byte[size];
        ulong seed = 0x9876543210FEDCBAUL;

        PatternDataGenerator.FillBuffer(buffer, 1, 1, 0, seed);

        // Corrupt a byte in the data body (after the 48-byte header)
        buffer[100] ^= 0x01;

        var (isValid, error) = PatternDataGenerator.VerifyBuffer(buffer, 1, 1, 0, seed);

        Assert.IsFalse(isValid, "Verification should fail when a bit is flipped.");
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "Checksum failure", "Error should identify checksum mismatch.");
    }

    [TestMethod]
    public void PatternDataGenerator_DetectsFakeDriveLooping()
    {
        int size = 64 * 1024;
        byte[] buffer = new byte[size];
        ulong seed = 0x5555AAAA5555AAAAUL;

        // Written with File #42
        PatternDataGenerator.FillBuffer(buffer, round: 1, fileIndex: 42, fileOffsetStart: 0, seed);

        // But we were expecting File #1 (simulating drive looping back to previous data)
        var (isValid, error) = PatternDataGenerator.VerifyBuffer(buffer, expectedRound: 1, expectedFileIndex: 1, expectedFileOffsetStart: 0, seed);

        Assert.IsFalse(isValid, "Looping data from another file should fail verification.");
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "Looping/Fake capacity detected", "Should explicitly flag fake capacity wrap-around.");
    }

    [TestMethod]
    public void PatternDataGenerator_DetectsZeroFilledDroppedWrites()
    {
        int size = 64 * 1024;
        byte[] buffer = new byte[size]; // All zeros

        var (isValid, error) = PatternDataGenerator.VerifyBuffer(buffer, 1, 1, 0, 0x12345UL);

        Assert.IsFalse(isValid);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "contains all zeroes", "Should detect dropped/unwritten zero blocks.");
    }

    [TestMethod]
    public void FileSizePlanner_DistributionAndTotalSum()
    {
        long targetBytes = 100L * 1024 * 1024; // 100 MB
        var files = FileSizePlanner.PlanFiles(targetBytes, FileSizePreset.Balanced, round: 1);

        Assert.IsTrue(files.Count > 0, "Should generate files.");
        long sum = files.Sum(f => f.SizeBytes);
        Assert.AreEqual(targetBytes, sum, "Total planned bytes must exactly match requested target.");

        // Check file names and positive sizes
        foreach (var file in files)
        {
            Assert.IsTrue(file.SizeBytes > 0);
            Assert.IsTrue(file.FileName.EndsWith(".tst"));
            StringAssert.Contains(file.FileName, "test_r01_f");
        }
    }

    [TestMethod]
    public void FinalReport_HtmlAndMarkdownGeneration()
    {
        var drive = new DriveTargetInfo
        {
            DriveLetter = "E:\\",
            ModelName = "Test Portable SSD 1TB",
            BusType = "USB",
            DriveFormat = "exFAT",
            TotalSizeBytes = 1000L * 1024 * 1024 * 1024,
            FreeSpaceBytes = 950L * 1024 * 1024 * 1024,
            IsUsb = true
        };

        var config = new TestConfiguration
        {
            TargetDrive = drive,
            Rounds = 2
        };

        var report = new FinalTestReport
        {
            Drive = drive,
            Configuration = config,
            StartTime = DateTime.Now.AddMinutes(-30),
            EndTime = DateTime.Now,
            PlannedRounds = 2,
            CompletedRounds = 2,
            TotalBytesWritten = 200L * 1024 * 1024 * 1024,
            TotalBytesVerified = 200L * 1024 * 1024 * 1024,
            TotalErrorsCount = 0,
            OverallAvgWriteSpeedMBps = 460.5,
            OverallAvgReadSpeedMBps = 512.2,
            Rounds = new List<RoundResult>
            {
                new()
                {
                    RoundNumber = 1,
                    BytesWritten = 100L * 1024 * 1024 * 1024,
                    BytesVerified = 100L * 1024 * 1024 * 1024,
                    AvgWriteSpeedMBps = 455.0,
                    AvgReadSpeedMBps = 510.0,
                    ErrorCount = 0
                },
                new()
                {
                    RoundNumber = 2,
                    BytesWritten = 100L * 1024 * 1024 * 1024,
                    BytesVerified = 100L * 1024 * 1024 * 1024,
                    AvgWriteSpeedMBps = 466.0,
                    AvgReadSpeedMBps = 514.4,
                    ErrorCount = 0
                }
            }
        };

        Assert.IsTrue(report.IsPassed);
        StringAssert.Contains(report.IntegrityVerdict, "GENUINE / HEALTHY");

        string md = report.GenerateMarkdown();
        StringAssert.Contains(md, "# Drive Integrity & Stress Benchmark Report");
        StringAssert.Contains(md, "Test Portable SSD 1TB");
        StringAssert.Contains(md, "460.5 MB/s");

        string html = report.GenerateHtml();
        StringAssert.Contains(html, "<!DOCTYPE html>");
        StringAssert.Contains(html, "DriveTester Verification Report");
        StringAssert.Contains(html, "PASSED");
    }

    [TestMethod]
    public async Task DriveTestEngine_MockFolder_RunsWriteVerifyEmptyCycles()
    {
        // Create a temporary mock drive directory
        var tempFolder = Path.Combine(Path.GetTempPath(), "DriveTester_UnitTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(tempFolder)!);

            var mockDrive = new DriveTargetInfo
            {
                DriveLetter = tempFolder, // Engine uses DriveLetter
                ModelName = "Mock Drive",
                BusType = "Virtual",
                DriveFormat = "NTFS",
                TotalSizeBytes = driveInfo.TotalSize,
                FreeSpaceBytes = driveInfo.AvailableFreeSpace,
                IsUsb = false
            };

            var config = new TestConfiguration
            {
                TargetDrive = mockDrive,
                Rounds = 2,
                TargetMode = CapacityTargetMode.CustomGB,
                CustomCapacityGB = 0.01, // ~10 MB for fast unit test
                SizePreset = FileSizePreset.DiverseStress,
                FlushBuffersDirectly = false,
                EmptyFilesAfterEachRound = true
            };

            var engine = new DriveTestEngine(config);
            var logs = new List<string>();
            var roundResults = new List<RoundResult>();

            engine.LogEmitted += entry => logs.Add(entry.Message);
            engine.RoundCompleted += r => roundResults.Add(r);

            var report = await engine.RunAsync();

            Assert.IsNotNull(report);
            Assert.AreEqual(2, report.CompletedRounds);
            Assert.AreEqual(0, report.TotalErrorsCount);
            Assert.IsTrue(report.IsPassed);
            Assert.AreEqual(2, roundResults.Count);

            // Verify files were emptied
            var testDir = Path.Combine(tempFolder, config.TestFolderName);
            if (Directory.Exists(testDir))
            {
                var remainingFiles = Directory.GetFiles(testDir, "*.*", SearchOption.AllDirectories);
                Assert.AreEqual(0, remainingFiles.Length, "Test directory should be emptied after round completion.");
            }
        }
        finally
        {
            if (Directory.Exists(tempFolder))
            {
                try { Directory.Delete(tempFolder, recursive: true); } catch { }
            }
        }
    }
}
