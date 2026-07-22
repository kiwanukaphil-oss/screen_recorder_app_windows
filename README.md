# Screen Recorder App (Windows)

A lightweight, professional Windows screen recorder targeting visually lossless 4K at up to 120 FPS — for everyday desktop recording (tutorials, meetings, long work sessions) and high-performance gameplay capture alike.

## Documentation

- [Original requirements brief](Prompt.md)
- [Project plan & architecture](docs/PLAN.md) — tech stack decisions, module breakdown, recording pipeline, error handling, performance strategy
- [Development roadmap](docs/ROADMAP.md) — milestones M0–M6 with objectives, complexity, dependencies, and testing gates

## Status

Planning phase. No code yet — implementation begins with milestone M0 (see roadmap).

## Stack (summary)

C# / .NET 8 · WinUI 3 · Windows.Graphics.Capture · Direct3D 11 · Media Foundation hardware encoding (NVENC / AMF / Quick Sync) · WASAPI audio. Full rationale and alternatives in [docs/PLAN.md](docs/PLAN.md).
