namespace SideHUD.Models;

public class AppConfig
{
    public double Opacity { get; set; } = 0.65;
    public double FontSize { get; set; } = 14;
    public int RightMarginPx { get; set; } = 12;
    public int TempWarn { get; set; } = 70;
    public int TempHot { get; set; } = 85;
    public int OverheadWatts { get; set; } = 50;
    public string ThemeMode { get; set; } = "AutoFromWallpaper";
    public double SaturationBoost { get; set; } = 0.1;
    public double TintOpacity { get; set; } = 0.25;
    public int UpdateIntervalMs { get; set; } = 500;
    
    // FPS Counter Settings
    public int FpsMinThreshold { get; set; } = 10; // Minimum FPS to display (filters false positives)
    public bool FpsShowWhenGaming { get; set; } = true; // Show FPS overlay when game is detected
    public int FpsOverlayPositionX { get; set; } = 12; // FPS overlay X position (left margin)
    public int FpsOverlayPositionY { get; set; } = 12; // FPS overlay Y position (top margin)
    
    // Widget Position Settings
    public int TopMarginPx { get; set; } = 50; // Top margin from screen edge
    public bool LockToDesktop { get; set; } = true; // Hide widget when fullscreen apps are active
    
    // Display Settings
    public bool ShowCpuUsage { get; set; } = true;
    public bool ShowGpuUsage { get; set; } = true;
    public bool ShowCpuTemp { get; set; } = true;
    public bool ShowGpuTemp { get; set; } = true;
    public bool ShowOverallTemp { get; set; } = true;
    public bool ShowSystemPower { get; set; } = true;
    public bool ShowFpsOverlay { get; set; } = true;
    
    // Advanced Settings
    public bool EnableAutoDebug { get; set; } = true; // Auto-show debug window on sensor issues
    public int AutoDebugThreshold { get; set; } = 10; // Number of failed readings before auto-debug
    public bool EnableErrorLogging { get; set; } = true; // Enable error logging to file
}

