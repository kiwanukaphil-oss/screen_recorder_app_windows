# M1 Verification Report — 2026-07-22

Hardware: NVIDIA GeForce RTX 3090, single 3840×2160 monitor, Windows 11 (22631).
Build: commit `f32e3f5` (M1 recording engine), Release.

## 1. Functional recording test

10-second recording via `Recorder.DevCli --seconds 10`, system audio on, desktop workload.

| Check | Result |
|---|---|
| Container/streams (ffprobe) | ✅ MP4, H.264 High 3840×2160, AAC-LC 48 kHz stereo |
| Hardware encoder used | ✅ NVENC MFT selected by sink writer (CPU stayed near-idle) |
| Dropped frames | ✅ 0 of 301 |
| Timestamp monotonicity | ✅ 601 video packets, 0 non-monotonic DTS |
| Frame content spot-check | ✅ frame at t=5 s pixel-correct, right-side up, correct colors |
| Idle-efficiency behavior | ✅ static desktop delivered ~30 fps of change events, not a forced 60 |

Observation: output is constant-frame-rate (~60 fps, 601 packets/10.04 s) — Media
Foundation duplicates frames to honor the declared frame rate. Correct but wasteful
for idle screens; revisit in M2 (declare actual rate or pace frames explicitly).

## 2. A/V sync measurement

Method: generated a "digital clapperboard" clip (100 ms white flash + 1 kHz beep at
every second boundary, ffmpeg lavfi), played it fullscreen with ffplay while
recording, then located flashes (`blackdetect`) and beeps (`silencedetect`) in the
recording.

Result over 9 clean events:

- Audio leads video by **~80 ms mean**, jitter **±8 ms**, **no cumulative drift** over 10 s.

Interpretation: stability (jitter/drift) is the recorder's responsibility and passes
cleanly. The constant 80 ms bias mixes player-side display latency, compositor
latency, and any real pipeline skew — this method cannot separate them.

**Follow-up (M2):** build a dedicated sync probe (D3D swapchain flip + WASAPI beep
issued from the same QPC read, no media player involved) to measure true pipeline
skew, then apply a calibration offset in the muxer if needed. Target: |offset| ≤ 40 ms.

## 3. Soak test #1 (15 min, 4K30) — FAILED, found a critical bug

The first soak (with system audio from an active call) ballooned **private/commit
memory from 0.8 GB to 29 GB in ~4.5 minutes** while writing only 1.7 MB of output,
exhausted the pagefile, and took the whole machine down (blank screen, forced
restart). The interrupted MP4 was unplayable (`moov atom not found`) — live
confirmation of the M3 crash-recovery requirement.

**Root cause:** WASAPI loopback delivers *no data at all* while the system is silent.
The audio timeline only advanced when a chunk arrived, so when audio stopped, the MF
sink writer — which buffers streams to interleave them — held every incoming raw 4K
BGRA frame (~33 MB each) in memory waiting for audio that never came (~100 MB/s of
commit growth on a light desktop). Short functional tests never caught it because
audio happened to be playing continuously through all of them.

**Fixes (RecordingSession):**
1. *Audio clock driven by the mux loop* — silence is synthesized whenever written
   audio trails written video by > 200 ms, independent of loopback delivery.
2. *Frame pacing* — capture is decimated to the configured FPS on a fixed cadence,
   so the encoder never receives faster-than-declared input.
3. *Memory fail-safe* — the mux thread aborts the recording cleanly if process
   commit memory exceeds 4 GB (a stall can never take the machine down again).

**Regression test (20 s, fps 30, audio playing 5 s then silent):** memory plateaued
at ~200 MB (previously unbounded), 439 silence gaps filled, output valid — video
20.33 s / audio 20.18 s (within the 200 ms design lag), 610 frames = exactly 30 FPS.

## 4. Soak test #2 (15 min, 4K30, fixed build)

15-minute unattended recording, 20 s sampling of working set / private memory / free
disk, external watchdog killing the process if private memory exceeds 8 GB.

**PASSED** — and under the harshest condition: the system was completely silent for
the entire run (0 real audio chunks), i.e. soak #1's fatal scenario end to end.

| Metric | Result |
|---|---|
| Duration | 900.07 s video / 899.90 s audio (Δ160 ms, within 200 ms design lag) |
| Frames | 10,631 written, **0 dropped** (~12 fps avg — event-driven idle desktop) |
| Silence fills | 10,627 (audio clock driven by mux loop throughout) |
| Private memory | peak 699 MB, mean 421 MB over 45 samples — flat, no leak |
| Working set | ~160 MB flat |
| Output | 794 MB MP4, validates in ffprobe, watchdog never triggered |

## 5. Exit-gate recording (1 hour, 4K30)

Unattended 1-hour recording during normal machine use (mixed real audio and
silence), 60 s sampling, watchdog armed for memory > 8 GB or free disk < 8 GB.

**PASSED:**

| Metric | Result |
|---|---|
| Duration | 3600.23 s video / 3600.43 s audio — tracks matched over a full hour |
| Frames | 31,368 written, **0 dropped** |
| Audio | 28,639 real chunks + 11,829 silence fills (alternating audio/silence handled) |
| Private memory | peak 598 MB, mean 478 MB over 60 samples — flat for the entire hour |
| Output | 3.45 GB MP4, validates in ffprobe; watchdog never triggered |

## Verdict

M1 engine verified on NVIDIA hardware, including the 1-hour exit-gate recording.
Remaining exit-gate items (owner): second Windows 11 machine (ideally AMD/Intel GPU
for the encoder-vendor criterion), human playback check of a recording, sign-off on
the deferred minimal window (CLI + global hotkey stands in until the M4 UI).

## Known gaps deferred inside M1

- Minimal UI window: deferred until the WinUI 3 project lands (no VS templates on
  this machine); DevCli + global hotkey covers M1's control needs.
- 2-hour soak on a second machine: owner task (M0/M1 exit-gate item).
