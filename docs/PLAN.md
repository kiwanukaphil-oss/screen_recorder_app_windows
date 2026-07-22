# Project Plan — Professional 4K Windows Screen Recorder

> Source requirements: [Prompt.md](../Prompt.md)
> Companion document: [ROADMAP.md](ROADMAP.md)

## 1. Vision

A lightweight, professional Windows screen recorder that produces **visually lossless 4K video at up to 120 FPS** with minimal CPU/GPU overhead. It must be dramatically simpler than OBS Studio while matching or beating its recording quality.

**Scope clarification (owner decision, 2026-07-22):** the app is a *general-purpose* screen recorder. Recording ordinary desktop content — tutorials, meetings, office apps, browser sessions, multi-hour work captures — is a first-class use case. Gameplay recording is the *performance stress case* that drives the architecture, not the only target. Practical consequences:

- Capture must work reliably on regular desktop windows, occluded/minimized-then-restored windows, and mixed-DPI multi-monitor setups — not only exclusive/borderless fullscreen games.
- Long-session stability (2+ hour recordings, file splitting, crash recovery) is as important as peak FPS.
- Default presets should favor "just works" desktop recording (30/60 FPS, VBR) with gaming presets (120 FPS, CQ/CBR high bitrate) one click away.
- Idle-screen efficiency matters: when the screen content is static (typical desktop use), the pipeline should approach zero CPU/GPU cost (WGC only delivers frames on change — we exploit that).

## 2. Guiding Principles

1. **GPU-resident pipeline.** A captured frame stays in VRAM from capture (D3D11 texture) to hardware encoder input. No CPU readback on the hot path, ever.
2. **Never block the capture thread.** Encoding, muxing, and disk I/O run on their own threads behind lock-free/bounded queues. Backpressure drops encode work gracefully rather than stalling capture.
3. **Timestamps are truth.** All audio and video samples are stamped from the same QPC (QueryPerformanceCounter) clock at capture time. A/V sync is achieved by correct timestamps, never by sleeping or nudging streams.
4. **Fail soft.** Encoder loss, device removal (`DXGI_ERROR_DEVICE_REMOVED`), display topology changes, and disk-full conditions recover or degrade without losing the recording made so far.
5. **Simple by default, deep on demand.** One-click record with smart defaults; every encoder knob available in an "Advanced" pane.

## 3. Technology Stack

### 3.1 Recommended stack (Option A — single-language C#)

| Layer | Choice | Notes |
|---|---|---|
| Language | **C# / .NET 8+** | Entire app, including capture/encode pipeline via interop |
| UI | **WinUI 3 (Windows App SDK)** | Native Windows 11 look, dark/light theming built in |
| Capture | **Windows.Graphics.Capture (WGC)** | Primary API for monitor + window capture |
| Capture fallback | **DXGI Desktop Duplication** | Monitor-only fallback, Win 8.1+, no yellow border on older builds |
| Graphics | **Direct3D 11** (via interop: TerraFX / SharpDX-free bindings or CsWin32) | WGC hands us D3D11 textures; encoders accept them zero-copy |
| Encoding | **Media Foundation** hardware MFTs (H.264 / HEVC / AV1) + **Sink Writer** for MP4 | In-box, zero-copy D3D11 input via `IMFDXGIDeviceManager`; automatically selects NVENC / AMF / Quick Sync MFTs |
| Software fallback | Media Foundation software H.264 MFT (v1) → optional FFmpeg/x264 (later) | |
| Audio | **WASAPI** loopback (system) + capture (mic) | Shared mode, per-source volume, QPC-stamped |
| Containers | MP4 via MF Sink Writer (v1); **MKV / fragmented-MP4 via FFmpeg remux layer** (later, for crash resilience) | |
| Logging | Serilog (rolling files + in-app diagnostics view) | |
| Testing | xUnit + integration harness that records synthetic D3D scenes | |

**Why C# end-to-end:** the hot path is *orchestration* of GPU objects, not per-pixel work. Frames never enter managed memory, so the GC has almost nothing to collect if we pre-allocate sample/buffer pools. Development speed, tooling, and maintainability are far better than C++ for one developer, and WGC/MF interop from .NET is well-trodden (CsWin32 generates the COM/Win32 bindings). ShareX, ScreenToGif and several commercial recorders prove the model.

**Costs to accept:** interop boilerplate for Media Foundation COM; care needed to avoid GC pauses on the capture thread (solution: zero allocations in steady state, pooled buffers); AOT/trimming complications (not required to ship).

### 3.2 Alternative stack (Option B — C++ core + C# shell)

Native C++20 DLL owning capture→encode→mux (WGC C++/WinRT, D3D11, NVENC SDK / AMF SDK / oneVPL directly, FFmpeg libav* for muxing), with the WinUI 3 C# app calling it over a narrow C ABI.

- **Advantages:** lowest possible overhead and jitter; direct vendor SDK access unlocks encoder features MF hides (NVENC lookahead, AQ tuning, B-frame reference modes); no GC risk at 120 FPS.
- **Disadvantages:** two build systems, interop boundary to design and debug, roughly 2–3× development effort, harder crash diagnostics across the boundary.
- **Decision:** start with Option A. The architecture isolates the pipeline behind interfaces (§5) so a native core can replace the C# pipeline **per-module** later if profiling shows GC jitter or MF encoder limitations at 4K120. Milestone M2 includes an explicit go/no-go benchmark gate.

### 3.3 Rejected / deferred options

- **WPF:** mature, but dated visuals, no native Win11 styling; only a fallback if WinUI 3 tooling blocks us.
- **Rust:** excellent for a capture core but immature WinUI story and would still be a two-language project — strictly worse than Option B for this team today.
- **D3D12:** no benefit for this workload (we consume textures produced by DWM/WGC, not render); D3D11 is what WGC and MF interop expect.
- **Game hook capture (OBS-style DLL injection):** highest-fidelity game capture, but invasive (anti-cheat risk) and irrelevant to desktop recording. Deferred indefinitely; WGC captures fullscreen-optimized games well on Win11.

## 4. System Architecture

### 4.1 Module breakdown

```
src/
  Recorder.App/            WinUI 3 shell: views, view-models, tray, hotkeys UI, notifications
  Recorder.Core/           Session orchestration, state machine, profiles, scheduling, replay buffer
  Recorder.Capture/        ICaptureSource: WgcMonitorSource, WgcWindowSource, DuplicationSource
  Recorder.Graphics/       D3D11 device mgmt, texture pool, GPU color conversion (RGB→NV12/P010), cursor & overlay composition
  Recorder.Encoding/       IVideoEncoder: MediaFoundationEncoder (H.264/HEVC/AV1), encoder capability probe, software fallback
  Recorder.Audio/          WASAPI loopback + mic capture, mixer, resampler, (later) noise suppression
  Recorder.Muxing/         IMuxer: MfSinkWriterMuxer (MP4), later FfmpegMuxer (MKV/fMP4), file splitting, crash recovery journal
  Recorder.Common/         Logging, settings, QPC clock, ring buffers, diagnostics/metrics
tests/
  Recorder.Tests/          Unit tests
  Recorder.IntegrationTests/  Synthetic-scene end-to-end recording + ffprobe-based output validation
tools/
  Recorder.Bench/          Pipeline benchmark harness (used for the M2 go/no-go gate)
```

### 4.2 Recording pipeline (data flow)

```
[Capture thread — event-driven, WGC FrameArrived]
  WGC frame (D3D11 BGRA texture, QPC timestamp)
      │  copy into pooled texture (GPU copy, ~0 CPU)
      ▼
[GPU convert] BGRA → NV12 (8-bit) / P010 (HDR 10-bit)   ← compute/video-processor pass, stays in VRAM
      │
      ▼  bounded ring of pooled textures (video queue, drop-oldest policy on overflow)
[Encode thread]
  Media Foundation hardware MFT (NVENC/AMF/QSV chosen automatically)
      │  compressed samples (H.264/HEVC/AV1 + timestamps)
      ▼
[Mux thread]                                    [Audio threads]
  MF Sink Writer → MP4 on disk        ◄──────    WASAPI loopback + mic (QPC-stamped,
  (async I/O, split-file logic,                  resampled to 48 kHz, per-track)
   journal for crash recovery)
```

- **Thread model:** capture (per source), GPU-convert (shares capture thread; the "pass" is just command submission), encode (1), mux (1), audio capture (1 per device), audio mix (1), UI (WinUI dispatcher). All cross-thread handoff via bounded channels; the only permitted drop point is the video queue before the encoder.
- **Replay buffer:** same pipeline, but mux thread writes compressed samples into a RAM/disk ring instead of a file; "Save replay" flushes the ring through a muxer. Encoding once and buffering *compressed* frames keeps 5-minute 4K buffers affordable.
- **Pause/resume:** implemented in the muxer layer by timestamp re-basing (no encoder teardown).

### 4.3 Error handling & recovery

| Failure | Response |
|---|---|
| GPU device removed / driver reset | Recreate device + capture + encoder; recording continues in same file (new GOP); user toast |
| Encoder rejects frame / stalls | Reset MFT once; on repeat, fall back H.265→H.264→software; never silently stop |
| Disk full / write error | Stop cleanly at last safe sample, finalize file, alert with free-space estimate |
| App/system crash | Journal + (later) fMP4/MKV means the file is playable up to the last flushed fragment; recovery scan on next launch |
| Display topology / resolution change | Recreate capture at new size; encoder restart with new stream (split file) |

### 4.4 Logging & diagnostics

Serilog rolling logs (default: warnings+; verbose opt-in). In-app diagnostics panel: live capture FPS, encode FPS, queue depths, dropped frames, GPU/CPU load, disk write rate. A "copy diagnostics report" button for support. Metrics are the same counters the integration tests assert on.

## 5. Key Design Decisions (open items flagged)

1. **WGC vs. Duplication as primary — decided: WGC.** Window capture, HDR, per-window isolation, and dirty-region efficiency for desktop use. Duplication kept as monitor-only fallback (pre-Win10 1903 and edge cases).
2. **MF Sink Writer MP4 first, FFmpeg later — decided,** to ship M1 without native dependencies. FFmpeg enters only as a muxing/remux library when MKV + crash-resilient containers land (M3).
3. **C# pipeline vs. native core — provisionally A, gated.** M2 benchmark gate: sustained 4K120 on a mid-range GPU with < 5 % dropped frames and p99 frame-to-encoder latency < 1 frame. If it fails after optimization, port `Recorder.Encoding`+`Recorder.Graphics` to a native core (Option B) without touching the app layer.
4. **AV1 & HDR (P010 + HDR10 metadata) — deferred to M5;** architecture reserves the paths (P010 conversion stage, codec abstraction) from day one.

## 6. Performance Strategy (summary)

- Zero-copy: WGC → pooled D3D11 texture → GPU color convert → MFT via DXGI device manager. The CPU only moves pointers and timestamps.
- Pre-allocated pools for textures, MF samples, and audio buffers; steady-state managed allocation ≈ 0 → no GC pressure.
- Event-driven capture (no polling): static desktop content costs ~nothing; 120 Hz games are paced by WGC's frame events.
- Bounded queues with drop-oldest ensure a slow encoder can never back-pressure the game's presentation.
- Server GC + `SustainedLowLatency`; capture/encode threads at `AboveNormal` priority, registered with MMCSS ("Capture"/"Playback" classes) where applicable.
- Measure, don't guess: `Recorder.Bench` + ETW traces validate every optimization; the diagnostics counters ship in release builds.

## 7. Repository & Workflow

- Remote: `https://github.com/kiwanukaphil-oss/screen_recorder_app_windows.git` (this folder is the repo root).
- Branching: `main` protected; feature branches per milestone task; PR + CI (build, unit tests, integration smoke on a GPU-less runner with software encoder).
- Milestone gates: each ROADMAP milestone ends with its test checklist green **and owner sign-off before the next phase begins.**
