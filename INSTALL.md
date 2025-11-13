# SideHUD Installation Guide

## Quick Installation (Recommended)

### Step 1: Build the Application

Open PowerShell in the project directory and run:

```powershell
# Build a standalone executable (includes .NET runtime)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

This will create a standalone executable in: `bin\Release\net8.0-windows\win-x64\publish\SideHUD.exe`

### Step 2: Install to a Permanent Location

1. Create a folder on your PC (e.g., `C:\Program Files\SideHUD` or `D:\Applications\SideHUD`)
2. Copy the entire `publish` folder contents to that location
3. The `SideHUD.exe` file is now your installed application

### Step 3: Run Once to Enable Auto-Startup

1. Double-click `SideHUD.exe` to run it
2. **On first run, SideHUD will automatically register itself for Windows startup**
3. You'll see a tray icon in the system tray
4. The widget will appear on the right side of your screen

### Step 4: Verify Startup Registration

1. Press `Win + R` to open Run dialog
2. Type: `shell:startup` and press Enter
3. You should see SideHUD listed (or check Registry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`)

### Step 5: Test Auto-Startup

Restart your PC. SideHUD should start automatically when Windows loads.

---

## Alternative: Framework-Dependent Build (Smaller Size)

If you already have .NET 8.0 Runtime installed, you can use a smaller build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

**Note:** This requires .NET 8.0 Runtime to be installed separately.

---

## Manual Startup Toggle

You can enable/disable startup anytime via the tray icon:
- Right-click the tray icon → "Enable/Disable Startup with Windows"

---

## Troubleshooting

### Application doesn't start on boot:
1. Check if SideHUD.exe exists at the registered path
2. Right-click tray icon → Check "Startup with Windows" status
3. Manually enable via tray menu if needed

### Missing sensor data:
- Ensure **HWiNFO64** is running with Shared Memory enabled
- Ensure **RTSS** is running (for FPS)
- Check the Debug Window (Right-click tray → Show Debug Info)

### Permission issues:
- Run as Administrator if needed (though not typically required)
- Ensure the installation folder is not read-only

---

## Uninstallation

1. Right-click tray icon → "Exit" to close SideHUD
2. Delete the installation folder
3. Startup entry will be automatically removed when you disable it via tray menu, or manually remove from Registry

