# Jeilo Recorder

<p align="center">
  <img src="src/Jeilo.App/Assets/jeilo.ico" width="80" height="80" alt="Jeilo Recorder Logo" />
</p>

<p align="center">
  <strong>Fast, private, and hardware-accelerated screen & audio recorder for Windows.</strong><br>
  No watermarks. No time limits. No cloud uploads. 100% offline.
</p>

<p align="center">
  <a href="#features">Features</a> •
  <a href="#download--installation">Download</a> •
  <a href="#system-requirements">Requirements</a> •
  <a href="#building-from-source">Build from Source</a> •
  <a href="#architecture">Architecture</a> •
  <a href="#privacy--security">Privacy</a>
</p>

---

## Overview

**Jeilo Recorder** is a native, modern Windows desktop application built with **.NET 8** and **Avalonia UI 12**. Designed for content creators, developers, educators, and gamers, Jeilo delivers studio-grade screen, window, and dual-channel audio capture powered by hardware acceleration—while keeping your recordings completely private and local to your PC.

---

## ✨ Features

- **🚀 Hardware-Accelerated Video Encoding**
  - Seamless auto-detection and support for GPU-accelerated encoders: **NVIDIA NVENC** (`h264_nvenc`), **Intel QuickSync** (`h264_qsv`), and **AMD AMF** (`h264_amf`), with an optimized CPU fallback (`libx264`).
  - Capture silky-smooth video at **24, 30, or 60 FPS** across customizable quality presets (Low, Balanced, High, Ultra).

- **🎙️ Studio-Grade Dual Audio Mixing**
  - Simultaneous mixing of your **Microphone input** and **System audio loopback** at 48 kHz stereo via WASAPI.
  - Live, color-zoned **Peak VU Meters** with real-time level monitoring and clipping alerts.

- **🖥️ Display or Window Targeting**
  - Select an entire monitor or isolate an individual application window to eliminate surrounding desktop clutter.
  - Optional mouse cursor capture.

- **⏱️ Pre-Recording Countdown & Auto-Minimize**
  - Configurable 3-second or 5-second animated countdown overlay with quick cancellation.
  - Optional auto-minimize to taskbar when capture starts, automatically restoring upon stop.

- **📂 Built-in Library Manager**
  - Instant thumbnail previews, video duration badges, file sizes, and creation dates.
  - Real-time search bar and sort filter (Newest, Oldest, Largest Size, Name).
  - Quick file actions: Play, Highlight in Explorer, Copy Path to Clipboard, and Permanent Delete with confirmation.

- **🎨 Tailored Appearance**
  - Instant theme switching between **Obsidian Dark Mode**, **Crisp Light Mode**, or **Follow Windows System**.

- **🔒 100% Offline & Private**
  - Zero telemetry, analytics, cloud synchronization, or tracking. Recordings never leave your computer.

---

## 📥 Download & Installation

### Option 1: Windows Installer (.exe)
1. Download the latest `JeiloRecorder-0.1.0-win-x64-setup.exe` from [Releases](../../releases).
2. Run the installer and follow the prompt. A desktop shortcut and Start Menu entry will be created.

### Option 2: Portable (.zip)
1. Download `JeiloRecorder-0.1.0-win-x64.zip` from [Releases](../../releases).
2. Extract the archive into any folder.
3. Launch `Jeilo.App.exe` directly without installation.

---

## ⌨️ Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| <kbd>Ctrl</kbd> + <kbd>R</kbd> | Start / Stop recording instantly |
| <kbd>Ctrl</kbd> + <kbd>1</kbd> | Switch to **Record** tab |
| <kbd>Ctrl</kbd> + <kbd>2</kbd> | Switch to **Library** tab |
| <kbd>Ctrl</kbd> + <kbd>3</kbd> | Switch to **Settings** tab |

---

## 💻 System Requirements

### Minimum Requirements
* **OS**: Windows 10 (Version 2004 / Build 19041 or higher) or Windows 11 (64-bit)
* **Processor**: 64-bit Dual-Core 1.8 GHz CPU
* **Memory**: 4 GB RAM
* **Graphics**: Direct3D 11 compatible GPU
* **Storage**: 250 MB free disk space

### Recommended Requirements (for 60 FPS Capture)
* **OS**: Windows 11 (64-bit)
* **Processor**: Quad-Core 2.5 GHz+ (Intel Core i5 / AMD Ryzen 5 or higher)
* **Memory**: 8 GB RAM
* **Graphics**: GPU with dedicated hardware encoder (NVIDIA GeForce GTX 1050+, Intel Iris Xe / Arc, AMD Radeon RX 500+)
* **Audio**: Stereo headphones or headset to prevent microphone loopback echo

---

## 🏗️ Architecture & Technology Stack

```
screen_recorder/
├── src/
│   ├── Jeilo.App/         # Avalonia UI 12 Desktop Application (MVVM)
│   │   ├── Views/         # MainWindow.axaml & UI Layout
│   │   ├── ViewModels/    # MainWindowViewModel, state & bindings
│   │   ├── Styles/        # Dynamic ThemeDictionaries (Dark / Light)
│   │   └── Services/      # Thumbnail extraction & metadata reader
│   │
│   ├── Jeilo.Core/        # Core Capture & Audio Engine
│   │   ├── Capture/       # Windows.Graphics.Capture & Direct3D11 interop
│   │   ├── Audio/         # NAudio WASAPI loopback & microphone mixer
│   │   ├── Encoding/      # FFmpeg muxer & hardware encoder detector
│   │   ├── Recording/     # RecordingEngine & state synchronization
│   │   └── Settings/      # Local JSON settings store
│   │
├── installer/             # Inno Setup (.iss) installer script
├── scripts/               # PowerShell build & packaging automation
└── tools/                 # Standalone LGPL FFmpeg binaries
```

- **UI Layer**: [Avalonia UI 12](https://avaloniaui.net/) with compiled bindings, modern Fluent styles, and dynamic theming.
- **MVVM Framework**: [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/).
- **Screen Capture**: Native `Windows.Graphics.Capture` (WGC) Direct3D 11 API for low-overhead, GPU-accelerated frame grabbing.
- **Audio Capture**: [NAudio](https://github.com/naudio/NAudio) utilizing WASAPI loopback and microphone endpoints, mixed into a 48 kHz 32-bit float stereo stream.
- **Encoding & Muxing**: Direct pipe to standalone, bundled LGPL [FFmpeg](https://ffmpeg.org/) binaries.

---

## 🔨 Building from Source

### Prerequisites
1. **.NET 8 SDK** (or higher).
2. **Windows 10/11 x64**.
3. (Optional) **Inno Setup 6+** to compile the `.exe` installer.

### Quick Build
Clone the repository and build the solution:
```powershell
# Restore & build
dotnet build JeiloRecorder.sln
```

### Run the App
```powershell
dotnet run --project src/Jeilo.App/Jeilo.App.csproj
```

### Generate Standalone Release & Installer
Run the automated packaging script:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```
The script will output:
* `dist/publish/`: Self-contained, trimmed binaries with bundled FFmpeg.
* `dist/JeiloRecorder-0.1.0-win-x64.zip`: Portable ZIP archive.
* `dist/JeiloRecorder-0.1.0-win-x64-setup.exe`: Inno Setup installer executable.

---

## 🔒 Privacy & Security

Jeilo Recorder is engineered with a strict privacy-first philosophy:
- **No telemetry or analytics**: The application never connects to remote tracking endpoints.
- **No cloud storage**: All audio and video files remain strictly on your local computer.
- **No accounts or sign-ups**: Ready to use immediately upon installation.
- **Clean uninstallation**: Leaves no background services, registry hooks, or lingering daemons.

---

## 📄 License & Attribution

- **Jeilo Recorder**: Licensed under the [MIT License](LICENSE).
- **FFmpeg**: Bundled under the terms of the **GNU Lesser General Public License (LGPL) version 2.1 or later**. The full license text is included in `FFmpeg-LICENSE.txt`.
