# M1 Verification Report Review — 2026-07-22

Reviewed document: [2026-07-22-m1-verification.md](2026-07-22-m1-verification.md)

## Summary

The verification report contains valuable functional results and a useful analysis of the silent-audio memory failure. However, it currently supports a narrower conclusion than its verdict claims. In particular, the tested build is misidentified, the memory-leak conclusion lacks sufficient supporting data, and several M1 acceptance criteria remain incomplete or inconclusive.

The defensible current verdict is:

> The silent-audio regression is verified on NVIDIA hardware. Full M1 acceptance remains open.

## Findings

### 1. High — The tested build is misidentified

The report header names commit `f32e3f5`, but the fixes and successful soak were introduced in commit `34480f7`. As written, the report attributes successful post-fix results to the broken build.

Record the exact commit separately for every test, particularly for the before-fix and after-fix runs. If a test used an uncommitted working tree, record that explicitly and include the diff or resulting commit.

Reference: verification report, line 4.

### 2. High — The verdict overstates M1 completion

The roadmap requires more than the report's listed remaining exit-gate items. Outstanding or inconclusive requirements include:

- A/V offset below 40 ms. The report measured approximately 80 ms, so this criterion is currently failed or inconclusive—not passed.
- A 2-hour desktop recording soak with zero drops and flat memory.
- Operation on NVIDIA and at least one AMD or Intel encoder machine.
- A 1-hour 4K30 desktop recording.
- A 10-minute 1080p60 game recording.
- Synthetic moving-pattern validation, including duration, frame rate, resolution, and timestamp-gap checks.
- Minimal-window and output-folder-picker functionality.
- Automatic encoder selection and software-fallback coverage.

The report should distinguish among feature completion, test criteria, formal exit gates, and owner sign-off. Deferring the sync target or UI work also requires an explicit roadmap or owner decision rather than being treated as an implicit pass.

References: verification report, lines 86–96; roadmap, lines 28–33.

### 3. High — “Flat, no leak” is not demonstrated by peak and mean

The fixed soak reports peak private memory of 699 MB and a mean of 421 MB across 45 samples. Those two statistics do not establish that memory was flat: they could also describe steady linear growth.

Because flat memory is a formal M1 test criterion and the original defect involved catastrophic growth, the report should include:

- Initial, final, minimum, and maximum private memory.
- Memory-growth slope in MB/min.
- The raw timestamped samples or a linked CSV.
- Preferably a small chart showing private memory and working set over time.

Reference: verification report, line 82.

### 4. Medium — The second soak verifies the silence bug, not full 4K30 throughput

Only about 12 source frames per second reached the writer during the idle-desktop soak. Consequently, “0 dropped” establishes that the video queue did not overflow under that workload, but it does not establish sustained capture at 30 FPS.

Frames intentionally rejected by `FramePacer` are also not included in the dropped-frame counter. The report should define precisely what “dropped” measures and distinguish among:

- Frames delivered by WGC.
- Frames rejected by pacing.
- Frames discarded because the bounded queue overflowed.
- Frames submitted to the sink writer.
- Encoded packets in the output file.

Rename this test to a “silent-idle regression soak” and add a separate moving-scene test that continuously supplies 30 FPS.

References: verification report, lines 74–84; `RecordingSession.HandleFrameReady`.

### 5. Medium — The evidence is not independently reproducible

The report does not include or link the exact commands, output media, hashes, logs, raw `ffprobe` output, memory samples, or detected sync-event timestamps. This makes its conclusions difficult to audit or repeat.

For each test, preserve:

- Exact build commit and configuration.
- Full command line and tool versions.
- Output filename, size, and SHA-256 hash.
- Relevant recorder logs.
- Raw `ffprobe` output.
- Memory-sampling CSV and watchdog log.
- Flash and beep timestamps used in the sync calculation.
- Encoder/MFT identification evidence.

“CPU stayed near-idle” is supporting evidence for hardware acceleration, but it does not by itself prove that the NVENC MFT was selected.

### 6. Low — Numeric and absolute claims need tightening

- The displayed durations `900.07 s` and `899.90 s` differ by 170 ms, not 160 ms. Either increase the displayed precision or report a delta consistent with the rounded values.
- “610 frames = exactly 30 FPS” should state the calculation basis and distinguish submitted frames from encoded packets.
- “A stall can never take the machine down again” is too absolute. The memory watchdog cannot execute while the mux thread is blocked inside a native writer call. Prefer wording such as “bounds the known failure mode while the mux loop continues making progress.”
- The M1 roadmap requires a 2-hour soak but does not specifically assign that soak to a second machine. The report currently conflates this with the separate M0 second-machine requirement.

## Recommended report changes

1. Split the build metadata by test and identify `f32e3f5` as the failing build and `34480f7` as the fixed build.
2. Change the A/V sync result to **inconclusive against the M1 target** until the recorder's contribution can be isolated or the measured offset is below 40 ms.
3. Attach raw memory samples and calculate the start-to-end change and MB/min slope before claiming that memory is flat.
4. Reclassify the 15-minute silent test as a focused regression test rather than the full M1 soak.
5. Add a requirements matrix with `Passed`, `Failed`, `Inconclusive`, `Not run`, or `Deferred by owner` for every M1 feature, test criterion, and exit-gate item.
6. Revise the verdict to state that the silent-audio regression has passed on NVIDIA hardware while M1 acceptance remains open.

