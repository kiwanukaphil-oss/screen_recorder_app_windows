# M1 Verification Report — 2026-07-22 (rev. 2)

Revised per [review](2026-07-22-m1-verification-review.md); rev. 1 is in git history.

Hardware: NVIDIA GeForce RTX 3090, single 3840×2160 monitor, Windows 11 (22631).
Tools: ffmpeg/ffprobe 8.1.2 (Gyan build), .NET SDK 8.0.423, Release configuration.
Raw evidence: [data/](data/) (memory CSVs, sync event timestamps, NVENC utilization).

**Builds under test** (per review finding 1 — each test lists its build):

| Build | Identity | Role |
|---|---|---|
| A | commit `f32e3f5` | M1 engine as first committed — **failing build** (soak #1 crash) |
| B | uncommitted working tree: `f32e3f5` + stall fixes with inline pacing | regression test + soak #2 (functionally equivalent to C; pacing later extracted to `FramePacer` without behavior change) |
| C | commit `34480f7` | fixed build — soak #3 (1 h) and moving-scene test |

## Requirements matrix (roadmap M1)

| Requirement | Status |
|---|---|
| 2-hour desktop soak, zero drops, bounded memory | **Passed** (§6) |
| WGC capture → GPU convert → H.264 hardware MFT → MP4 | **Passed** (builds A–C) |
| Hardware encoder auto-selection: NVENC | **Passed** — 57–61 % NVENC utilization measured (§5) |
| Hardware encoder: AMD AMF / Intel QSV | **Not run** — no such hardware here (owner: second machine) |
| Software encoder fallback | **Not run** — needs a GPU-less environment or forced-off test |
| WASAPI system-audio loopback, single mixed track | **Passed** (soak #3 interleaved real audio + silence) |
| QPC timestamping end-to-end | **Passed** — zero timestamp gaps (§5); stability verified (§2) |
| Start/stop global hotkey | **Passed** (manual check during development) |
| Start/stop minimal window | **Deferred — owner decision pending** (CLI stands in; UI is M4) |
| Output folder selection | **Partial** — CLI `--output` only; picker UI deferred with the window |
| Fixed sensible defaults | **Passed** |
| Synthetic moving-scene validation (duration/fps/res/gaps) | **Passed** (§5) |
| A/V sync offset < 40 ms | **Inconclusive** — measured −80 ms including player-side bias (§2) |
| A/V duration lock over long recordings | **Passed** — Δ7 ms over 2 h (§6) |
| 1-hour 4K30 recording plays flawlessly | **Passed instrumentally** (§4); human playback check pending (owner) |
| 10-min 1080p60 game recording | **Not run** (needs a game session; owner) |

## 1. Functional recording test (build A)

10 s, `Recorder.DevCli --seconds 10`, defaults (4K60), system audio on, desktop workload.

ffprobe: MP4, H.264 High 3840×2160, AAC-LC 48 kHz stereo, 10.04 s; 601 video
packets, 0 non-monotonic DTS; extracted frame pixel-correct and right-side up.
301 samples were submitted but 601 packets encoded — MF duplicated frames to reach
the declared 60 fps CFR. Fixed by pacing in build B/C (§5 shows 1:1).

## 2. A/V sync measurement (build A)

Method: 10 s clip with a 100 ms white flash + 1 kHz beep at each second (ffmpeg
lavfi), played fullscreen with ffplay while recording; flashes located with
`blackdetect`, beeps with `silencedetect`. Event timestamps: [data/avsync-events.txt](data/avsync-events.txt).

Result over 9 clean events: audio leads video by 70–89 ms (mean ≈ −80 ms), jitter
±8 ms, no cumulative drift. A 10th event at the clip boundary is an artifact and
was excluded.

**Status vs the < 40 ms target: INCONCLUSIVE.** The measurement includes ffplay's
own audio/display alignment and the compositor path, which cannot be separated from
recorder-side skew with this method. M2 will use a player-free probe (D3D swapchain
flip + WASAPI submission from one QPC read) and, if needed, a calibration offset.
What this test does establish: timestamp *stability* (jitter/drift) is good.

## 3. Soak #1, 15 min 4K30 — CRASHED the machine (build A) and found the M1-critical bug

With system audio active at start (a live call), then silence: private/commit memory
grew from 0.78 GB to 29.2 GB in ~4.5 min (~100 MB/s), working set stayed ~115 MB,
output stalled at 1.7 MB, the pagefile exhausted the disk and the machine went down.
Partial raw samples: [data/soak1-crash-memory-partial.csv](data/soak1-crash-memory-partial.csv).
The interrupted MP4 had no moov atom — unplayable (confirms the M3 crash-recovery
requirement).

**Root cause:** WASAPI loopback delivers nothing during silence; the audio timeline
only advanced on data arrival; the MF sink writer buffers video samples (raw 33 MB
BGRA textures) while waiting to interleave the lagging audio stream → unbounded.

**Fixes (build B/C):** mux-loop-driven audio clock (silence synthesized whenever
audio trails video > 200 ms); frame pacing to the configured FPS (`FramePacer`,
unit-tested); memory fail-safe aborting the recording if private memory exceeds
4 GB. The fail-safe *bounds the known failure mode while the mux loop is making
progress*; it cannot fire while the mux thread is blocked inside a native call, so
it is a mitigation, not a guarantee.

Regression test (build B; 20 s, fps 30, tone for 5 s then silence): memory plateau
~200 MB, 439 silence fills, valid output, video 20.33 s / audio 20.18 s, 610
samples submitted = 30 fps.

## 4. Soak #2 (15 min, silent) and Soak #3 (1 hour, mixed) — builds B and C

Raw samples: [data/soak2-silent-15min-memory.csv](data/soak2-silent-15min-memory.csv),
[data/soak3-1hour-memory.csv](data/soak3-1hour-memory.csv) (20 s / 60 s cadence;
watchdogs armed at 8 GB private / < 8 GB disk free — never triggered).

Soak #2 (build B) is a **silent-idle regression soak**, not a throughput test: the
desktop was idle (~12 fps of change events reached the writer) and the system was
silent for all 15 min (0 real audio chunks — soak #1's fatal condition end-to-end).
Result: 10,631 samples written, 0 queue drops, 10,627 silence fills, valid 794 MB
MP4, video 900.066667 s / audio 899.903646 s (Δ163 ms, within the 200 ms design lag).

Soak #3 (build C, 1 h, normal machine use with real audio on and off): 31,368
samples written, 0 queue drops, 28,639 real audio chunks + 11,829 silence fills,
valid 3.45 GB MP4, video 3600.233333 s / audio 3600.426271 s (Δ193 ms).

**Memory (per review finding 3 — "flat" was not previously demonstrated):**

| Run | first → last | min / max | steady-state linear-fit slope |
|---|---|---|---|
| Soak #2 (15 min) | 249 → 478 MB | 249 / 699 MB | **+2.8 MB/min** (t ≥ 300 s) |
| Soak #3 (1 h) | 252 → 513 MB | 252 / 598 MB | **+3.9 MB/min** (t ≥ 900 s) |

Memory is **bounded but not flat**: soak #3 stepped 405 → 575 MB between minutes
20–45, then held ~575 MB for the final 10 minutes. Consistent with heap/buffer
growth reaching a plateau rather than an unbounded leak, but the 2-hour soak must
confirm the plateau before the "flat memory" criterion can be marked passed.
Watch item for M2 (buffer pooling will also change this profile).

Counter definitions (per review finding 4): "written" = samples submitted to the
sink writer; "dropped" = bounded-queue overflow evictions only. Frames rejected by
`FramePacer` and WGC's own delivery rate are not separately counted yet — counters
for both are an M2 diagnostics item.

## 5. Moving-scene throughput + encoder identification (build C)

60 s recording (`--fps 30 --no-audio`) of fullscreen ffplay `testsrc2` 1080p60
(continuous motion), NVENC utilization sampled via `nvidia-smi` every 8 s.

- Sustained throughput: **1,803 samples written in 60.1 s = 30.0 fps, 0 drops**.
- Encoded packets = 1,803 = samples submitted (1:1, no duplication — pacing fix).
- `avg_frame_rate` 30/1 at 3840×2160; **max inter-frame gap 33.3 ms** (= exactly one
  frame interval → zero timestamp gaps).
- **NVENC utilization 0 % → 57–61 % during recording**
  ([data/moving-scene-nvenc-utilization.txt](data/moving-scene-nvenc-utilization.txt)) —
  direct evidence the NVIDIA hardware encoder MFT is doing the encoding.
- Output SHA-256 (first 16 hex): `48412fb33e76b8c…`; file deleted after validation
  (disk constraints), hash retained here.

## 6. Soak #4 — 2 hours, 4K30, active workload (build C)

The roadmap's full-length soak, run during normal active machine use (video content
playing much of the time: ~25.5 fps average reached the writer; 114,054 real audio
chunks + 2,126 silence fills). Raw samples (60 s cadence):
[data/soak4-2hour-memory.csv](data/soak4-2hour-memory.csv).

| Metric | Result |
|---|---|
| Duration | 7200.433 s video / 7200.427 s audio — **Δ7 ms over 2 hours** |
| Frames | 183,470 written, **0 queue drops** |
| Private memory | 254 → 482 MB; **hour-2 linear-fit slope +0.26 MB/min** — the plateau is confirmed (the earlier +3.9 MB/min was warm-up/step behavior, not a leak) |
| Output | 14.5 GB MP4, streams validate in ffprobe; deleted after validation (hash not retained due to disk pressure), memory CSV preserved |
| Watchdogs | never triggered |

This closes the review's memory finding: memory is bounded with a confirmed
plateau under both idle and active workloads. Note the roadmap wording "flat
memory" is interpreted as "bounded after warm-up" — warm-up allocation to ~450 MB
happens over the first ~30 min.

## Verdict

**The silent-audio regression is verified fixed on NVIDIA hardware; sustained-30fps
throughput, hardware encoding, the 2-hour soak with confirmed memory plateau, and
Δ7 ms A/V duration lock over 2 hours are all demonstrated. Full M1 acceptance
remains open on:** A/V sync absolute offset (inconclusive, M2 probe), AMD/Intel +
software-fallback coverage, game recording, human playback check, and the owner's
decision on the deferred minimal window.
