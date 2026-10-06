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

    [TestMethod]
    public void TestSessionState_SaveAndTryLoad_PreservesState()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "DriveTester_SessionTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var state = new TestSessionState
            {
                BaseSeed = 0xDEADBEEFCAFE1234UL,
                PlannedRounds = 3,
                CurrentRound = 2,
                CurrentPhase = TestPhase.Writing,
                InFlightFileName = "test_r02_f000005_1GB.tst",
                InFlightFileIndex = 5,
                IsGracefullyPaused = false,
                SizePreset = FileSizePreset.Balanced,
                TargetBytesPerRound = 100L * 1024 * 1024
            };

            state.Save(tempFolder, "DriveTester_IntegrityTest");

            var loaded = TestSessionState.TryLoad(tempFolder, "DriveTester_IntegrityTest");
            Assert.IsNotNull(loaded);
            Assert.AreEqual(0xDEADBEEFCAFE1234UL, loaded.BaseSeed);
            Assert.AreEqual(3, loaded.PlannedRounds);
            Assert.AreEqual(2, loaded.CurrentRound);
            Assert.AreEqual(TestPhase.Writing, loaded.CurrentPhase);
            Assert.AreEqual("test_r02_f000005_1GB.tst", loaded.InFlightFileName);
            Assert.IsFalse(loaded.IsGracefullyPaused);

            TestSessionState.Delete(tempFolder, "DriveTester_IntegrityTest");
            var afterDelete = TestSessionState.TryLoad(tempFolder, "DriveTester_IntegrityTest");
            Assert.IsNull(afterDelete);
        }
        finally
        {
            if (Directory.Exists(tempFolder))
            {
                try { Directory.Delete(tempFolder, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public async Task DriveTestEngine_ResumesInterruptedSession_RecreatesUngracefulFile()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "DriveTester_ResumeTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(tempFolder)!);

            var mockDrive = new DriveTargetInfo
            {
                DriveLetter = tempFolder,
                ModelName = "Mock SSD For Resume",
                BusType = "Virtual",
                DriveFormat = "NTFS",
                TotalSizeBytes = driveInfo.TotalSize,
                FreeSpaceBytes = driveInfo.AvailableFreeSpace,
                IsUsb = false
            };

            var config = new TestConfiguration
            {
                TargetDrive = mockDrive,
                Rounds = 1,
                TargetMode = CapacityTargetMode.CustomGB,
                CustomCapacityGB = 0.005, // ~5 MB test dataset
                SizePreset = FileSizePreset.DiverseStress,
                FlushBuffersDirectly = false,
                EmptyFilesAfterEachRound = false
            };

            ulong baseSeed = 0xCAFEBABE11223344UL;
            long targetBytes = config.CalculateTargetBytes(driveInfo.AvailableFreeSpace);
            var filePlan = FileSizePlanner.PlanFiles(targetBytes, config.SizePreset, 1);
            Assert.IsTrue(filePlan.Count >= 2, "Test dataset should have at least 2 files.");

            // Simulate partial write before crash:
            // File 1 is fully and correctly written
            var testDir = Path.Combine(tempFolder, config.TestFolderName);
            var roundDir = Path.Combine(testDir, "Round_01");
            Directory.CreateDirectory(roundDir);

            var file1 = filePlan[0];
            var file1Path = Path.Combine(roundDir, file1.FileName);
            byte[] file1Buf = new byte[file1.SizeBytes];
            PatternDataGenerator.FillBuffer(file1Buf, 1, file1.FileIndex, 0, baseSeed);
            await File.WriteAllBytesAsync(file1Path, file1Buf);

            // File 2 was in-flight and only partially written (truncated to 10 bytes)
            var file2 = filePlan[1];
            var file2Path = Path.Combine(roundDir, file2.FileName);
            await File.WriteAllBytesAsync(file2Path, new byte[10]);

            // Create interrupted session state
            var sessionState = new TestSessionState
            {
                BaseSeed = baseSeed,
                PlannedRounds = 1,
                CurrentRound = 1,
                CurrentPhase = TestPhase.Writing,
                InFlightFileName = file2.FileName,
                InFlightFileIndex = file2.FileIndex,
                IsGracefullyPaused = false,
                SizePreset = config.SizePreset,
                TargetBytesPerRound = targetBytes,
                EmptyFilesAfterEachRound = false
            };
            sessionState.Save(tempFolder, config.TestFolderName);

            // Resume the test engine with the interrupted session state
            var engine = new DriveTestEngine(config, baseSeed);
            var logs = new List<string>();
            engine.LogEmitted += entry => logs.Add(entry.Message);

            var report = await engine.RunAsync(sessionState);

            Assert.IsNotNull(report);
            Assert.AreEqual(1, report.CompletedRounds);
            Assert.AreEqual(0, report.TotalErrorsCount, "Verification must succeed after recreating the ungraceful file!");
            Assert.IsTrue(report.IsPassed);

            // Verify file 2 on disk now has the full expected planned size
            var fi2 = new FileInfo(file2Path);
            Assert.AreEqual(file2.SizeBytes, fi2.Length, "Truncated in-flight file should have been recreated to full planned size.");

            // Verify the log contains the recreation notice
            bool foundRecreationLog = logs.Any(l => l.Contains("Recreating") || l.Contains("recreate"));
            Assert.IsTrue(foundRecreationLog, "Engine should log that the interrupted file was recreated.");
        }
        finally
        {
            if (Directory.Exists(tempFolder))
            {
                try { Directory.Delete(tempFolder, recursive: true); } catch { }
            }
        }
    }

    [TestMethod]
    public void DriveTestEngine_TryGetResumableSession_RecoversFromRawFileHeaders()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "DriveTester_RawHeaderTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);

        try
        {
            var mockDrive = new DriveTargetInfo
            {
                DriveLetter = tempFolder,
                ModelName = "Mock Drive",
                TotalSizeBytes = 100L * 1024 * 1024 * 1024,
                FreeSpaceBytes = 50L * 1024 * 1024 * 1024
            };

            var testDir = Path.Combine(tempFolder, "DriveTester_IntegrityTest");
            var roundDir = Path.Combine(testDir, "Round_01");
            Directory.CreateDirectory(roundDir);

            // Write a test file with genuine header, but NO session_state.json
            ulong expectedBaseSeed = 0xFEEDBEEF01020304UL;
            int round = 1;
            int fileIndex = 3;
            var filePath = Path.Combine(roundDir, "test_r01_f00003_16MB.tst");
            byte[] fileBuf = new byte[64 * 1024];
            PatternDataGenerator.FillBuffer(fileBuf, round, fileIndex, 0, expectedBaseSeed);
            File.WriteAllBytes(filePath, fileBuf);

            // Verify session_state.json does NOT exist yet
            Assert.IsFalse(File.Exists(Path.Combine(testDir, "session_state.json")));

            // TryGetResumableSession should discover and reconstruct the session from file header
            var recoveredSession = DriveTestEngine.TryGetResumableSession(mockDrive);
            Assert.IsNotNull(recoveredSession);
            Assert.AreEqual(expectedBaseSeed, recoveredSession.BaseSeed);
            Assert.AreEqual(1, recoveredSession.CurrentRound);
            Assert.AreEqual(TestPhase.Writing, recoveredSession.CurrentPhase);
            Assert.IsFalse(recoveredSession.IsGracefullyPaused);

            // It should also have generated session_state.json
            Assert.IsTrue(File.Exists(Path.Combine(testDir, "session_state.json")));
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
