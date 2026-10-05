## Summary
Initial release of **DriveTester Pro** — a high-performance Windows desktop application designed to test, benchmark, and verify the physical integrity and capacity of storage drives (especially high-capacity USB SSDs and flash drives).

## Key Features
- **Drive Detection**: Instantaneous enumeration with asynchronous WMI enrichment for USB bus type, hardware model, and volume properties.
- **Multi-Pass Fill/Verify/Empty**: Configurable cycles of structured file datasets (large, medium, and small files) to test sequential and random flash behavior.
- **Counterfeit Flash Detection**: High-entropy deterministic PRNG prevents controller hardware compression; 64 KB sub-block checksums detect looping wrap-around addresses and dropped zero writes.
- **Live Telemetry & Diagnostics**: Real-time throughput speedometer, live sparkline throughput chart, error logging, and exportable HTML/Markdown diagnostic reports.
- **Safety Safeguards**: Prominent warning prompts and safeguards for system drives (`C:\`).

## Testing Done
- Automated unit test suite with 7 test cases covering:
  - Exact match verification
  - Bit flip / cell degradation detection
  - Fake drive address looping detection
  - Dropped zero write detection
  - File size distribution and byte alignment
  - HTML & Markdown report generation
  - Mock multi-round write/verify/empty lifecycle
