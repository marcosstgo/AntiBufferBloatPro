# Anti-BufferBloat Pro

**Network optimizer for Windows gamers and streamers.** Reduces latency, jitter, and bufferbloat with one click — no manual registry editing required.

![Anti-BufferBloat Pro](https://corillo.live/assets/antibufferbloat-pro/screenshot-main.jpg)

## Download

**[→ Anti.BufferBloat.Pro.exe](https://github.com/marcosstgo/AntiBufferBloatPro/releases/latest/download/Anti.BufferBloat.Pro.exe)**

Single `.exe` file — no installer. Run as Administrator.

> Requires Windows 10/11 and [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

---

## Features

### ⚡ TCP Optimization
Automatically configures ECN, TOS, nagling, receive window auto-tuning, RSS, and DCA — the settings Windows doesn't expose in any UI.

### 🎮 Network Profiles
Three one-click profiles:
- **Gaming** — minimum latency, aggressive TCP settings
- **Streaming** — high throughput, balanced for upload-heavy workloads
- **Balanced** — general use

### 📡 BufferBloat Test
Built-in load test that measures ping under download, upload, and simultaneous load. Grades your connection A–F with specific recommendations.

### 📈 Real-Time Monitor
Live ping graph with the last 120 data points. Shows jitter, packet loss, auto-tuning state, and external/internal IP — all updated every 5 seconds.

### 🔔 Latency Alerts
System tray notifications when ping exceeds your configured threshold. Monitor your connection without keeping the window open.

### 🖥 PC Gaming Boost
- Set power plan to **Ultimate Performance**
- Disable **Xbox DVR** (reduces input lag in games)
- Enable **GPU Hardware Scheduling (HAGS)**
- Add game process to **Windows Defender** exclusions

### 📋 History
Keeps a log of all BufferBloat test results with timestamps and grades for comparison over time.

### ↑ Auto-Update
Checks GitHub Releases on startup. If a new version is available, a badge appears in the header — click it to download and install automatically.

---

## Usage

1. Download `Anti.BufferBloat.Pro.exe`
2. Right-click → **Run as administrator**
3. Select a profile or run the BufferBloat test
4. Optionally enable system tray to monitor in the background

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
