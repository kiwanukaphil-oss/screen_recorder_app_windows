Build a Professional 4K Windows Screen Recorder

You are a senior Windows software architect and graphics engineer with expertise in DirectX, Media Foundation, FFmpeg, GPU video encoding (NVENC, AMD AMF, Intel Quick Sync), and high-performance desktop application development.

I want to build a professional Windows screen recording application focused on **lossless or visually lossless 4K gameplay recording** with minimal impact on gaming performance. The application should be designed to compete with software such as OBS Studio, NVIDIA ShadowPlay, AMD Adrenalin Recording, Xbox Game Bar, and Bandicam while remaining much simpler and easier to use.

## Primary Goal

Design and build a lightweight Windows desktop application capable of recording:

* Native 4K resolution (3840×2160)
* Up to 120 FPS (minimum 60 FPS)
* Near-zero quality loss
* Extremely low CPU usage
* Minimal GPU overhead
* Stable recording during long gaming sessions
* No frame drops
* No audio/video synchronization issues

The application should always prioritize recording quality while maintaining excellent game performance.

---

## Core Features

### Screen Capture

* Capture the entire display
* Capture a selected monitor
* Capture a specific window
* Capture games using DirectX/OpenGL/Vulkan
* Multi-monitor support
* HDR support if available
* High refresh rate support (60/120/144/240 Hz where possible)

### Recording

* One-click recording
* Global hotkeys
* Pause and resume recording
* Instant replay (save the last X minutes)
* Scheduled recording
* Automatic recording when a game launches
* Background recording
* Recording timer
* Recording status overlay (optional)

### Video Quality

Support:

* Lossless recording
* Visually lossless recording
* H.264
* H.265 (HEVC)
* AV1 (if hardware supports it)

Allow users to configure:

* Resolution
* FPS
* Bitrate
* Constant Bitrate (CBR)
* Variable Bitrate (VBR)
* Constant Quality (CQ)
* Encoder presets
* Keyframe interval
* Color space
* HDR options

Automatically detect and use:

* NVIDIA NVENC
* AMD AMF
* Intel Quick Sync

Fallback to software encoding if hardware encoding is unavailable.

---

## Audio

Record:

* System audio
* Microphone
* Separate audio tracks
* Push-to-talk
* Noise suppression
* Volume mixer per source
* Audio monitoring
* Synchronization correction

---

## Storage

Allow users to:

* Choose the recording folder
* Automatically create dated folders
* Customize file names
* Estimate remaining recording time based on free disk space
* Automatically split large recordings
* Recover unfinished recordings after crashes

Support:

* MP4
* MKV
* MOV

---

## User Interface

Design a modern Windows 11 interface that is:

* Minimal
* Fast
* Easy to understand
* Dark mode
* Light mode
* System tray support
* Notification support
* Drag-and-drop simplicity

---

## Performance

The application should:

* Use GPU memory whenever possible
* Avoid unnecessary RAM copies
* Minimize CPU usage
* Use asynchronous processing
* Support multithreading
* Handle memory efficiently
* Prevent frame drops
* Recover gracefully from encoder failures

Explain every performance optimization used.

---

## Additional Features

Include ideas for features such as:

* Webcam overlay
* Face camera recording
* Mouse click visualization
* Keyboard input overlay
* FPS counter
* Performance statistics
* Recording history
* Video trimming
* Screenshot capture
* GIF export
* Custom recording profiles
* Streaming mode
* Automatic updates
* Plugin architecture
* Crash reporting
* Portable mode
* Multi-language support

Recommend any additional features that would make this application significantly better than existing screen recorders.

---

## Technical Requirements

Recommend the best technology stack and justify every choice.

Consider:

* C#
* C++
* Rust
* WinUI 3
* WPF
* Windows App SDK
* Direct3D 11
* Direct3D 12
* DXGI Desktop Duplication
* Windows.Graphics.Capture
* FFmpeg
* Media Foundation
* DirectComposition

For every recommendation, explain:

* Advantages
* Disadvantages
* Performance implications
* Ease of development
* Long-term maintainability

---

## Software Architecture

Design the application using a scalable architecture.

Include:

* Project structure
* Folder organization
* Module breakdown
* Class diagrams
* Recording pipeline
* Encoding pipeline
* Thread model
* Data flow
* Error handling
* Logging strategy

---

## Development Roadmap

Create a complete development roadmap divided into milestones.

For each milestone include:

* Objectives
* Features to build
* Estimated complexity
* Dependencies
* Testing requirements

---

## Code Generation

When writing code:

* Produce production-quality code.
* Follow modern best practices.
* Explain every important design decision.
* Avoid shortcuts.
* Optimize for performance and maintainability.
* Use clean architecture and SOLID principles.
* Include unit and integration testing where appropriate.

Do not skip implementation details. Assume this application will eventually be released commercially, and provide the level of engineering detail expected from a professional software team.
