# Screen Recorder App (Windows)

A lightweight, professional Windows screen recorder targeting visually lossless 4K at up to 120 FPS — for everyday desktop recording (tutorials, meetings, long work sessions) and high-performance gameplay capture alike.

## Documentation

- [Original requirements brief](Prompt.md)
- [Project plan & architecture](docs/PLAN.md) — tech stack decisions, module breakdown, recording pipeline, error handling, performance strategy
- [Development roadmap](docs/ROADMAP.md) — milestones M0–M6 with objectives, complexity, dependencies, and testing gates

## Status

- **M0 (foundation)** — complete: solution skeleton, D3D11/WGC interop, screenshot PoC verified at 4K, unit tests, CI.
- **M1 (MVP recorder)** — all machine-testable criteria pass ([verification report](docs/test-reports/2026-07-22-m1-verification.md)): hardware H.264 → MP4 with AAC, 2-hour soak, +8.8 ms A/V sync, zero drops. Owner items open: second-machine/AMD-Intel test, game recording, playback check.
- **M2 (performance & quality)** — in progress: HEVC + CBR/VBR/CQ done, mic + separate audio tracks done, `Recorder.Bench` 4K120 encode gate **passed** (H.264 1.27×, HEVC 1.95× realtime on RTX 3090), player-free sync probe done. Remaining: per-source volume, texture pooling, resolution scaling.
- **M3 (robust long-form)** — largely complete: crash-safe fragmented MP4 by default (kill-tested: file stays playable), single-window capture with live-resize survival, pause/resume with seamless timeline, automatic file splitting, disk-space auto-stop, MKV/MOV via ffmpeg remux. Known gaps: GPU device-removed recovery; fragmented container limited to one audio track (two-track sessions fall back to standard MP4).
- **M4 (product UX, in progress)** — responsive WinUI 3 control center with source presentation, quality presets, audio gain controls, output-folder picker and storage estimate, advanced codec/FPS/scale/quality controls, countdown, guarded recording lifecycle, lifetime global start/stop and pause/resume hotkeys (available while minimized or gaming), a capture-excluded always-on-top recording controller, close-to-notification-area behavior, live health stats, recorder-window exclusion, and a post-recording result surface. `dotnet run --project src/Recorder.App` — or use `tools/Recorder.DevCli` for scripting.

## Stack (summary)

C# / .NET 8 · WinUI 3 · Windows.Graphics.Capture · Direct3D 11 · Media Foundation hardware encoding (NVENC / AMF / Quick Sync) · WASAPI audio. Full rationale and alternatives in [docs/PLAN.md](docs/PLAN.md).
