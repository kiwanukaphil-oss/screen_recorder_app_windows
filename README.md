# Screen Recorder App (Windows)

A lightweight, professional Windows screen recorder targeting visually lossless 4K at up to 120 FPS — for everyday desktop recording (tutorials, meetings, long work sessions) and high-performance gameplay capture alike.

## Documentation

- [Original requirements brief](Prompt.md)
- [Project plan & architecture](docs/PLAN.md) — tech stack decisions, module breakdown, recording pipeline, error handling, performance strategy
- [Development roadmap](docs/ROADMAP.md) — milestones M0–M6 with objectives, complexity, dependencies, and testing gates

## Status

- **M0 (foundation)** — complete: solution skeleton, D3D11/WGC interop, screenshot PoC verified at 4K, unit tests, CI.
- **M1 (MVP recorder)** — engine working: monitor → H.264 (hardware) → MP4 with AAC system audio, verified with ffprobe. Run it with `dotnet run --project tools/Recorder.DevCli` (stop with Ctrl+Shift+F9 or Enter). Remaining M1 items: A/V sync measurement, long-session soak, minimal UI window.

## Stack (summary)

C# / .NET 8 · WinUI 3 · Windows.Graphics.Capture · Direct3D 11 · Media Foundation hardware encoding (NVENC / AMF / Quick Sync) · WASAPI audio. Full rationale and alternatives in [docs/PLAN.md](docs/PLAN.md).
