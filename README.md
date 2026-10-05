# DriveTester Pro - Storage Integrity & Benchmark Suite

A modern Windows desktop application designed to test, validate, and benchmark storage drives (especially high-capacity external USB SSDs and flash drives). It is specifically engineered to detect fake/counterfeit drives, bad flash blocks, thermal throttling, and sustained throughput degradation.

---

## 🚀 Key Features

1. **Intelligent Drive Detection & Safety**:
   - Enumerates all mounted storage drives.
   - Detects USB / External bus types, drive model names, file systems (exFAT, NTFS, FAT32), and free vs. total capacity.
   - Automatically prioritizes external USB drives.
   - Safeguards system drives (e.g., `C:\`) with prominent warnings and confirmation prompts.

2. **Multi-Pass Fill / Verify / Empty Cycles**:
   - **Configurable Rounds**: Fill and empty the drive across multiple cycles (1, 2, 3, 5, 10, or custom).
   - **Varied File Sizes**: Automatically plans and generates a realistic mix of:
     - **Large Files (512 MB – 1 GB)**: Tests sustained sequential bandwidth and fills capacity quickly.
     - **Medium Files (16 MB – 64 MB)**: Tests standard file-transfer performance and cluster allocation.
     - **Small Files (128 KB – 4 MB)**: Stresses directory metadata, FAT/MFT tables, and random write performance.
   - **Direct I/O (Write-Through)**: Bypasses Windows file system RAM cache so that all writes commit directly to physical NAND.

3. **Fake Drive & Corruption Detection Engine**:
   - Generates high-entropy deterministic pseudo-random data to prevent hardware-level controller compression from faking storage capacity or speeds.
   - Each 64 KB sub-block embeds a 48-byte cryptographic verification header (`DRVTEST!`, round number, file index, block offset, unique seed, and 64-bit checksum).
   - **Ghost Wrap-Around Detection**: Detects fake drives that loop storage addresses (e.g., a hacked 32 GB drive marketed as 1 TB). If an earlier block contains data from a later file, it immediately flags `"Looping/Fake capacity detected"`.
   - **Dropped Writes / Zero Detection**: Flags drives that silently drop writes and return all zeroes.
   - **Bit-for-Bit Verification**: Catches individual cell degradation and bit flips before proceeding to the next round.

4. **Live Telemetry & Performance Metrics**:
   - **Real-Time Speed**: Instantaneous MB/s and rolling average throughput.
   - **Average & Peak Speeds**: Separate tracking for write phases and read/verification phases.
   - **Progress Tracking**: Dual progress indicators for overall multi-round progress, current round phase, and current file progress.
   - **Real-Time Throughput Graph**: Live sparkline chart showing whether write speed plummets (e.g., when the SSD's pseudo-SLC write cache exhausts or the controller overheats).
   - **Error Counter**: Real-time counter of corrupted blocks.

5. **Diagnostic Reporting & Export**:
   - **Rounds Summary Table**: Clean tabular view of written bytes, verified bytes, write/read speeds, duration, and error counts for each cycle.
   - **Detailed Diagnostic Report**: Automatically evaluates whether the drive is genuine and healthy or counterfeit/failing.
   - **Export Formats**: One-click export to standalone HTML report or Markdown / text.

---

## 🛠️ How to Run

### Method 1: Precompiled Release Executable
The compiled release executable is ready in:
```
publish\DriveTester.exe
```
Simply double-click `DriveTester.exe` to launch the application.

### Method 2: Launch via .NET CLI / Visual Studio
```powershell
dotnet run -c Release
```
Or open `DriveTester.csproj` in Visual Studio 2022 and press **F5**.

---

## 📋 Recommended Workflow to Test Your 1TB USB SSD

1. **Plug in your 1TB USB SSD**:
   - Plug the SSD into a fast USB 3.2 Gen 2 / USB-C port to get maximum test throughput (400–1000+ MB/s).
2. **Launch DriveTester**:
   - The app will auto-detect your external USB SSD in the dropdown.
3. **Configure the Test**:
   - **Rounds**: Set to **2** or **3** cycles (or more for thorough burn-in).
   - **Capacity Target**: Choose **Safe Free Space (95%)** to fill almost the entire drive while leaving minimal room for filesystem overhead, or **Full Free Space (100%)**.
   - **File Size Mix**: Choose **Balanced (1MB - 1GB)** (recommended) or **High Speed (512MB - 1GB)**.
   - Ensure **Direct Write-Through** and **Empty test files after each round** are checked.
4. **Click `[▶ START INTEGRITY TEST]`**:
   - Phase 1 will fill the drive with structured test files of varied sizes.
   - Phase 2 will read back and verify every single byte and checksum.
   - Phase 3 will empty the test files and prepare for the next round.
5. **Review and Save the Report**:
   - Once completed, check the **Final Diagnostic Report** tab.
   - Click **Save HTML / Markdown** to keep a permanent verification certificate of your drive!
