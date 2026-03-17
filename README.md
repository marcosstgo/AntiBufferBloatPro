# Anti-BufferBloat Pro

**Network optimizer for Windows gamers and streamers.** Reduces latency, jitter, and bufferbloat — no manual registry editing required.

![Anti-BufferBloat Pro](https://corillo.live/assets/antibufferbloat-pro/screenshot-main.jpg)

## Download

**[→ Anti.BufferBloat.Pro.exe](https://github.com/marcosstgo/AntiBufferBloatPro/releases/latest/download/Anti.BufferBloat.Pro.exe)**

Single `.exe` file — no installer. Run as Administrator.

> Requires Windows 10/11 and [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

---

## Features

### 🎮 Gaming Profile
One click applies the minimum-latency profile: Nagle OFF, Auto-Tuning restricted, timestamps OFF, InitialRTO 1s. **Reset** restores all Windows defaults.

### ⚡ TCP Actions
Granular control over individual settings:
- **Auto-Tuning**: Normal / Restricted / OFF
- **RSS**: ON / OFF
- **Backup TCP** — save current config before making changes
- **Restore TCP** — revert to saved backup
- **Diagnóstico** — full TCP parameter readout in the terminal

### 🖥 PC Gaming Boost
- **Ultimate Performance** power plan
- **Xbox DVR** disabled (reduces input lag)
- **GPU Hardware Scheduling (HAGS)** toggle
- **Windows Defender exclusion** for your game process

### 📈 Real-Time Monitor
Live ping graph with the last 120 data points. Shows ping, jitter, packet loss, auto-tuning state, external IP, and internal IP — updated every 5 seconds.

### 📡 BufferBloat Test
Built-in 3-phase load test (Idle · Download · Upload, ~60s total). Grades your connection **A–F** with specific recommendations.

### 📋 History
Keeps a log of all BufferBloat test results with timestamps and grades.

### ↑ Auto-Update
Checks GitHub Releases on startup. If a new version is available, a badge appears in the header — click to download and install automatically.

---

## Usage

1. Download `Anti.BufferBloat.Pro.exe`
2. Right-click → **Run as administrator**
3. Apply the **Gaming** profile or run individual TCP actions
4. Use the **BufferBloat Test** to verify the improvement
5. Enable system tray to monitor in the background

---

## Building from source

```
dotnet build AntiBufferBloatPro/AntiBufferBloatPro.csproj -c Release
```

Publish single-file exe:
```
dotnet publish AntiBufferBloatPro/AntiBufferBloatPro.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

---

## License

MIT
