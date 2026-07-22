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
- **M4 (UI, early start)** — WinUI 3 shell builds and runs from the CLI (no Visual Studio needed): monitor picker, audio toggles, codec/fps, record button with live stats, global stop hotkey. `dotnet run --project src/Recorder.App` — or use `tools/Recorder.DevCli` for scripting.

## Stack (summary)

C# / .NET 8 · WinUI 3 · Windows.Graphics.Capture · Direct3D 11 · Media Foundation hardware encoding (NVENC / AMF / Quick Sync) · WASAPI audio. Full rationale and alternatives in [docs/PLAN.md](docs/PLAN.md).
