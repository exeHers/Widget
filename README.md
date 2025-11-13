# SideHUD

A production-ready Windows desktop widget that displays real-time system metrics (CPU, GPU, temperatures, FPS, power) in an elegant, frameless panel that docks on the right side of your screen.

## Features

- **Frameless, transparent widget** with rounded corners and subtle shadows
- **Always-on-top** without stealing focus
- **Adaptive theme** that extracts colors from your current wallpaper
- **Live metrics** updated every 500ms:
  - CPU usage % and temperature °C
  - GPU usage % and temperature °C
  - Overall temperature (max of CPU, GPU, motherboard/VRM)
  - FPS (auto-shows when gaming, hidden otherwise)
  - PSU Power or Estimated System Power
- **Click-through mode** toggle via tray icon
- **Multiple sensor sources** with automatic fallback:
  - HWiNFO Shared Memory (primary)
  - RTSS Shared Memory (for FPS)
  - NVML (NVIDIA GPU fallback)
  - PerformanceCounter (CPU usage fallback)

## Requirements

- Windows 10/11
- .NET 8.0 Runtime
- For best results:
  - **HWiNFO64** with Shared Memory enabled (Settings → Safety → Shared Memory Support)
  - **RTSS (RivaTuner Statistics Server)** for FPS monitoring (optional)
  - NVIDIA GPU with NVML drivers (for fallback GPU monitoring)

## Building

1. Ensure you have .NET 8.0 SDK installed
2. Open a terminal in the project directory
3. Run:
   ```bash
   dotnet restore
   dotnet build
   dotnet run
   ```

## Configuration

Edit `appsettings.json` to customize:

- `Opacity`: Window opacity (0.0 - 1.0)
- `FontSize`: Text size in points
- `RightMarginPx`: Margin from right edge
- `TempWarn`: Temperature warning threshold (°C)
- `TempHot`: Temperature hot threshold (°C)
- `OverheadWatts`: Power overhead for estimated system power
- `ThemeMode`: "AutoFromWallpaper", "ForceDark", or "ForceLight"
- `SaturationBoost`: Color saturation boost (0.0 - 1.0)
- `TintOpacity`: Background tint opacity (0.0 - 1.0)
- `UpdateIntervalMs`: Sensor update interval in milliseconds

## Usage

1. Launch the application
2. The widget appears on the right side of your primary display
3. Right-click the tray icon to access:
   - Toggle Click-through
   - Toggle Always on Top
   - Re-sample Wallpaper Colors
   - Reload Sensors
   - Exit

## Notes

- The widget automatically adapts to wallpaper changes
- FPS row only appears when a 3D game is active (RTSS detected)
- Temperature colors: Green (< 70°C), Orange (70-85°C), Red (> 85°C)
- Window does not appear in Alt+Tab
- Click-through mode allows mouse events to pass through to desktop

## Troubleshooting

- **No sensor data**: Ensure HWiNFO64 is running with Shared Memory enabled
- **No FPS**: Ensure RTSS is running and monitoring a game
- **GPU data missing**: Try enabling HWiNFO or ensure NVIDIA drivers are installed for NVML fallback
- **Theme not updating**: Use "Re-sample Wallpaper Colors" from tray menu

