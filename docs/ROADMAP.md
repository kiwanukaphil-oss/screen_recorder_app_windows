# Development Roadmap — 4K Windows Screen Recorder

> Companion document: [PLAN.md](PLAN.md). Complexity scale: S (days), M (1–2 weeks), L (3–5 weeks), XL (6+ weeks) for a single developer working with AI assistance. Every milestone ends with a demo + owner sign-off gate before the next begins.

---

## M0 — Foundation & Walking Skeleton

**Objective:** a buildable, testable solution skeleton that proves the riskiest interop works.

| | |
|---|---|
| Features | Solution/project scaffold per PLAN §4.1 · CsWin32/WinRT interop setup · D3D11 device management · WGC proof-of-concept: capture one monitor frame to a texture and save a PNG · Serilog logging · settings persistence (JSON) · GitHub Actions CI (build + unit tests) |
| Complexity | **M** |
| Dependencies | None |
| Testing | CI green on clean clone; unit tests for settings/clock/ring buffer; manual: PNG screenshot matches screen on single- and multi-monitor, 100 %/150 % DPI |

**Exit gate:** screenshot demo works on the dev machine + one other Windows 11 machine.

---

## M1 — MVP Recorder (desktop-first)

**Objective:** one-click monitor recording to MP4 with system audio — usable daily for normal desktop capture.

| | |
|---|---|
| Features | Continuous WGC monitor capture → BGRA→NV12 GPU convert → **H.264 hardware MFT** → MF Sink Writer MP4 · automatic NVENC/AMF/QSV selection + software fallback · WASAPI system-audio loopback (single mixed track) · QPC timestamping end-to-end · start/stop via minimal window + one global hotkey · fixed sensible defaults (monitor res, 30/60 FPS, VBR) · output folder picker |
| Complexity | **L** — this is the heart of the product |
| Dependencies | M0 |
| Testing | Integration: record synthetic moving-pattern scene, validate with ffprobe (duration, FPS, resolution, no timestamp gaps) · A/V sync test with clapper video (< 40 ms offset) · 2-hour desktop recording soak: zero drops, flat memory · works on NVIDIA + at least one of AMD/Intel |

**Exit gate:** a 1-hour 4K30 desktop recording and a 10-min 1080p60 game recording both play back flawlessly.

---

## M2 — Performance & Quality Core

**Objective:** hit the headline numbers — 4K60 comfortably, 4K120 where hardware allows — and expose real quality controls.

| | |
|---|---|
| Features | Texture/sample pooling, zero-allocation steady state · bounded queues + drop-oldest policy + drop counters · HEVC encoding · rate-control modes: CBR / VBR / CQ · encoder preset, bitrate, keyframe interval, FPS, resolution scaling controls · microphone capture + **separate audio tracks** · per-source volume · `Recorder.Bench` harness + in-app diagnostics panel (live FPS, queue depth, drops) |
| Complexity | **L** |
| Dependencies | M1 |
| Testing | Bench gate (PLAN §5.3): sustained 4K120, < 5 % drops, p99 latency < 1 frame on mid-range GPU · GC pause monitoring (no gen2 during recording) · game overhead measurement: < 5 % average FPS loss in a GPU-bound title · HEVC/H.264 outputs validated across players (Windows, VLC, YouTube upload) |

**Exit gate:** benchmark report reviewed → **go/no-go decision on C# pipeline vs. native core port** (PLAN §3.2).

---

## M3 — Robust Long-Form Recording

**Objective:** trustworthy for multi-hour, unattended, and unlucky sessions.

| | |
|---|---|
| Features | Window capture (incl. occlusion, minimize/restore handling) · pause/resume · automatic file splitting by size/duration · **MKV + fragmented-MP4 via FFmpeg muxing layer** · crash-recovery journal + startup recovery scan · disk-space estimation + low-space auto-stop · dated folders + filename templates · device-removed/display-change recovery (PLAN §4.3 table complete) · MOV output |
| Complexity | **L** |
| Dependencies | M2 |
| Testing | Kill -9 during recording → file playable to last fragment · unplug monitor / change resolution / driver update mid-recording → session survives · 8-hour soak with 4 GB splits · window capture across DPI scales and occlusion cases |

**Exit gate:** torture-test checklist 100 % green.

---

## M4 — Product UX

**Objective:** the "much simpler than OBS" promise, delivered.

| | |
|---|---|
| Features | Full WinUI 3 UI: home (big record button, source picker with live thumbnails), settings, recording history with open/trim/delete · dark/light mode · system tray (record/pause/stop from tray) · toast notifications · full global-hotkey editor · recording status overlay (optional, click-through) · custom recording profiles (Desktop / Gaming / Meeting presets) · screenshot capture · first-run experience |
| Complexity | **L** |
| Dependencies | M1 (UI can start in parallel with M2/M3 core work) |
| Testing | UX walkthrough with 2–3 fresh users ("record your screen, find the file, trim it") · accessibility pass (keyboard nav, narrator, contrast) · view-model unit tests · overlay verified invisible in recordings |

**Exit gate:** a non-technical user records and finds a video without help.

---

## M5 — Gaming & Advanced Capture

**Objective:** the gamer feature set + capture breadth.

| | |
|---|---|
| Features | **Instant replay buffer** (save last X min, compressed ring, hotkey) · game-launch detection + auto-record · scheduled recording · **HDR capture** (P010 → HEVC/AV1 HDR10 with metadata) · **AV1 encoding** on capable GPUs · webcam overlay + separate face-cam track · mouse-click & keyboard input visualization · FPS counter/performance stats overlay · push-to-talk · noise suppression · audio monitoring & sync-offset correction |
| Complexity | **XL** (parallelizable feature clusters) |
| Dependencies | M2 (replay, HDR, AV1) · M4 (overlays UI) |
| Testing | Replay: hours of background buffering, flat memory, instant save · HDR output verified on HDR display + tone-mapped SDR players · AV1 on RTX 40 / RX 7000 / Arc · overlay & webcam sync checks · per-title auto-record matrix (10 popular games incl. borderless/exclusive fullscreen) |

**Exit gate:** side-by-side quality/overhead comparison vs. ShadowPlay & OBS published as a report.

---

## M6 — Release Engineering

**Objective:** commercial-grade shipping vehicle.

| | |
|---|---|
| Features | MSIX or Inno installer + code signing · automatic updates · crash reporting (minidump + opt-in upload) · portable mode · multi-language scaffolding (en + 1 more) · telemetry (opt-in, minimal) · docs/website · licensing decision if commercial |
| Complexity | **M** |
| Dependencies | M4 (M5 features can land in point releases after 1.0) |
| Testing | Clean-machine install matrix (Win10 22H2, Win11, N editions) · update-in-place test · crash-report round trip · uninstall leaves no residue |

**Exit gate:** 1.0 release candidate installed and used by external beta testers for one week.

---

## Post-1.0 Backlog (unscheduled)

Video trimming/editor beyond simple trim · GIF export · streaming mode (RTMP) · plugin architecture · region capture with crop UI · cloud upload integrations · live "highlight" markers via hotkey · per-app audio capture (Windows 11 `ActivateAudioInterfaceAsync` process loopback).

## Sequencing Notes

- Critical path: **M0 → M1 → M2 → M3**; M4 UI work can overlap M2/M3 once M1's core API stabilizes.
- The two highest technical risks are front-loaded: WGC interop (M0) and the 4K120 pipeline (M2 gate).
- Per owner's global workflow rules: no milestone starts without explicit confirmation, and nothing is committed/pushed without approval.
