<h1>
  <img src="docs/images/airmirror-logo.png" alt="AirMirror logo" width="64" align="left" />
  &nbsp;AirMirror
</h1>

<br clear="left" />

**AirMirror** is a native Windows app that turns your PC into an AirPlay receiver — mirror your iPhone, iPad, or Mac screen, stream YouTube/Apple TV videos, and play music wirelessly. It bundles the excellent [UxPlay](https://github.com/FDH2/UxPlay) AirPlay core inside a polished WPF interface with a system-tray workflow, so there's no MSYS2/terminal setup for end users.

![Screenshot placeholder](docs/images/screenshot-placeholder.png)

> The screenshot above is a placeholder — a real one is coming soon.

---

## Features

- **iOS / macOS screen mirroring** — full AirPlay mirroring, hardware-accelerated D3D12 renderer.
- **AirPlay video handoff** — YouTube, Apple TV, Safari etc. cast as proper HLS streams (toggleable). Plays in either UxPlay's native d3d12 window or AirMirror's own VLC-based player.
- **AirPlay audio** — stream music from any AirPlay-aware app to your PC speakers (or back to the source device).
- **Fullscreen UX** — auto-fullscreen on video-app playback, `F11` / `Alt+Enter` to toggle, `Esc` to leave fullscreen.
- **System tray** — runs out of the way; "Exit to tray when window is closed" keeps the receiver alive.
- **Start with Windows** — auto-launch AirMirror when Windows boots (HKCU `Run` entry, on by default, toggle in Settings).
- **Auto-restart** — if UxPlay crashes mid-session it's relaunched automatically in 3 seconds.
- **Per-monitor display config** — pick resolution / refresh rate or let it follow the primary monitor.
- **Audio routing** — choose between PC speakers or sending audio back to the iPhone/Mac.
- **Friendly Bonjour name** — pick what your devices see (e.g. `Mohanad's PC`) during install or change it any time in Settings.
- **Crash-resilient settings** — JSON in `%LocalAppData%\AirMirror\settings.json`.

---

## Installation

1. Go to the [**Releases**](../../releases) tab.
2. Download the installer for your CPU:
   - **Windows x64**: `AirMirror-Setup-<version>-x64.exe` *(if you don't know which to pick, choose this one)*
   - **Windows ARM64**: `AirMirror-Setup-<version>-arm64.exe`
3. Run the installer. The wizard will:
   - Ask where to install AirMirror.
   - Let you pick the **AirPlay server name** (the name your iPhone/Mac will see in the AirPlay menu — defaults to `<your Windows username>'s PC`).
   - Offer to create a desktop shortcut.
4. Launch AirMirror. It will appear in the system tray; click "Start" in the main window to begin advertising itself over AirPlay on your network.
5. On your iPhone/iPad/Mac, open Control Center → **Screen Mirroring** (or use the AirPlay button in YouTube / Music / Photos) and pick your PC.

To **uninstall**, use *Settings → Apps → Installed apps → AirMirror → Uninstall* (or run the `unins000.exe` in the install folder). The uninstaller also removes the auto-start registry entry; your settings under `%LocalAppData%\AirMirror` are kept so a reinstall remembers your preferences (delete that folder manually if you want a clean wipe).

---

## How to Build & Compile from Source

You only need this section if you want to hack on AirMirror or build the installer yourself.

### Prerequisites

| Tool | Why |
| --- | --- |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Builds the WPF app (`win-x64`). |
| [MSYS2](https://www.msys2.org/) with the **UCRT64** environment | Builds UxPlay (the C/C++ AirPlay core). |
| [Inno Setup 6](https://jrsoftware.org/isinfo.php) | Compiles the Windows installer. Optional unless you want to ship a setup `.exe`. |
| Git | To clone this repo. |

### 1. Clone

```powershell
git clone https://github.com/MuhannadYT/AirMirror.git
cd AirMirror
```

### 2. Build UxPlay (the AirPlay receiver core)

Open an **MSYS2 UCRT64** shell and install dependencies:

```bash
pacman -S --needed mingw-w64-ucrt-x86_64-toolchain \
                   mingw-w64-ucrt-x86_64-cmake \
                   mingw-w64-ucrt-x86_64-ninja \
                   mingw-w64-ucrt-x86_64-gstreamer \
                   mingw-w64-ucrt-x86_64-gst-plugins-base \
                   mingw-w64-ucrt-x86_64-gst-plugins-good \
                   mingw-w64-ucrt-x86_64-gst-plugins-bad \
                   mingw-w64-ucrt-x86_64-gst-plugins-ugly \
                   mingw-w64-ucrt-x86_64-gst-libav \
                   mingw-w64-ucrt-x86_64-openssl \
                   mingw-w64-ucrt-x86_64-libplist
```

Then configure & build:

```bash
cd third_party/UxPlay
cmake -S . -B build-ucrt64 -G Ninja
cmake --build build-ucrt64
```

Stage the resulting `uxplay.exe` so the WPF project picks it up:

```powershell
Copy-Item -Force third_party\UxPlay\build-ucrt64\uxplay.exe `
                 src\AirMirror\tools\uxplay\uxplay.exe
```

### 3. Build the WPF app

From a normal PowerShell prompt at the repo root:

```powershell
dotnet build src\AirMirror\AirMirror.csproj -c Release -r win-x64
```

Run it directly (skips installer):

```powershell
& "src\AirMirror\bin\Release\net8.0-windows10.0.19041.0\win-x64\AirMirror.exe"
```

### 4. (Optional) Build the installer

```powershell
.\scripts\build-installer.ps1
```

This publishes a self-contained `win-x64` build under `src\AirMirror\bin\Release\...\publish\` and runs Inno Setup (`installer\AirMirror.iss`) to produce `dist\AirMirror-Setup-<version>-x64.exe`.

If `ISCC.exe` isn't on your `PATH`, set `$env:ISCC` to its full path (typically `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`).

---

## Repository Layout

```
src/AirMirror/      # WPF app (.NET 8, win-x64)
third_party/UxPlay/ # Vendored UxPlay sources + Windows patches
installer/          # Inno Setup script
scripts/            # PowerShell helpers (build UxPlay, package, build installer)
docs/images/        # Logo + screenshots used by this README
```

---

## Credits

Made with ❤ by [**MuhannadYT**](https://github.com/MuhannadYT).

Powered by [**UxPlay**](https://github.com/FDH2/UxPlay) — thanks to all of its developers ❤. UxPlay is licensed under the GPL; see [`third_party/UxPlay/LICENSE`](third_party/UxPlay/LICENSE).

---

## License

AirMirror is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**. See [LICENSE](LICENSE) for the full text.

In short:

- You are free to use, study, modify, and share AirMirror.
- If you distribute it **or run a modified version that users interact with over a network**, you must publish your modified source code under AGPL-3.0 as well.
- This means you cannot take AirMirror, fold it into a closed-source product, and ship that — any derivative work must remain open source under AGPL-3.0.

The bundled UxPlay binary inside the installer remains licensed under **GPL-3.0** (its original license, compatible with AGPL-3.0). Its source lives under [`third_party/UxPlay/`](third_party/UxPlay).
