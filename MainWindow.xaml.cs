using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SideHUD.Models;
using SideHUD.Services;

namespace SideHUD;

public partial class MainWindow : Window
{
    private readonly ConfigurationService _configService;
    private readonly SensorAggregatorService _sensorService;
    private readonly WallpaperThemeService _themeService;
    private readonly TrayIconService _trayService;
    private readonly AutoDebugService _autoDebugService;
    private FpsOverlay? _fpsOverlay;
    private bool _clickThrough = false;
    private int _zeroReadingsCount = 0;
    private const int MAX_ZERO_READINGS = 10; // After 10 readings (5 seconds), show warning

    // Win32 API for window styling
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x8000000;
    private const int DWMWA_EXCLUDED_FROM_PEEK = 12;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const uint LWA_ALPHA = 0x2;
    
    private System.Timers.Timer? _desktopCheckTimer;
    private bool _isDesktopVisible = true;
    private bool _isClosing = false;

    public MainWindow()
    {
        InitializeComponent();
        
        _configService = new ConfigurationService();
        _sensorService = new SensorAggregatorService(_configService);
        _themeService = new WallpaperThemeService(_configService);
        _trayService = new TrayIconService();
        _autoDebugService = new AutoDebugService(_configService, _sensorService);

        _sensorService.DataUpdated += OnSensorDataUpdated;
        _themeService.ThemeChanged += OnThemeChanged;
        
        // Auto-debug event handlers
        _autoDebugService.AutoFixApplied += (s, msg) =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Show brief notification (optional - can be disabled)
                    var config = _configService.GetConfig();
                    if (config.EnableErrorLogging)
                    {
                        ErrorLogger.Log($"Auto-fix applied: {msg}");
                    }
                }
                catch { }
            });
        };
        
        _autoDebugService.IssueDetected += (s, msg) =>
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    ErrorLogger.Log($"Issue detected: {msg}");
                }
                catch { }
            });
        };
        
        _trayService.ToggleClickThrough += (s, e) => ToggleClickThrough();
        _trayService.ToggleAlwaysOnTop += (s, e) => ToggleAlwaysOnTop();
        _trayService.ToggleStartup += (s, e) => ToggleStartup();
        _trayService.ResampleWallpaper += (s, e) => _themeService.UpdateTheme();
        _trayService.ReloadSensors += (s, e) => { /* Sensors auto-reload */ };
        _trayService.ShowDebugInfo += (s, e) => ShowDebugWindow();
        _trayService.Exit += (s, e) => Application.Current.Shutdown();

        _trayService.Initialize(this);
        
        // Create FPS overlay window
        try
        {
            _fpsOverlay = new FpsOverlay();
            _fpsOverlay.Show(); // Show the window (it will be hidden initially)
            _fpsOverlay.Visibility = Visibility.Collapsed;
            _fpsOverlay.Topmost = true; // Ensure it stays on top
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Failed to create FPS overlay", ex);
            _fpsOverlay = null;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Position window on right side
        PositionWindow();
        
        // Apply window styles
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            // Make it not appear in Alt+Tab
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            // Disable peek and transitions
            int value = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_EXCLUDED_FROM_PEEK, ref value, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref value, sizeof(int));
        }

        // Apply initial theme
        try
        {
            _themeService.UpdateTheme();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Failed to apply initial theme", ex);
        }
        
        // Start sensor updates
        try
        {
            _sensorService.Start();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Failed to start sensor service", ex);
            // Show error to user
            MessageBox.Show(
                "Failed to start sensor monitoring. Check debug window for details.",
                "SideHUD Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
        
        // Start desktop visibility monitoring
        StartDesktopMonitoring();
        
        // Auto-enable startup on first run if not already enabled
        try
        {
            if (!StartupService.IsStartupEnabled())
            {
                if (StartupService.EnableStartup())
                {
                    _trayService.StartupEnabled = true;
                    ErrorLogger.Log("Startup automatically enabled on first run");
                }
                else
                {
                    ErrorLogger.Log("Failed to auto-enable startup - user can enable manually via tray menu");
                }
            }
            else
            {
                _trayService.StartupEnabled = true; // Update tray menu to reflect current status
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error auto-enabling startup", ex);
        }
    }
    
    private void StartDesktopMonitoring()
    {
        try
        {
            // Check every 500ms if desktop is visible
            _desktopCheckTimer = new System.Timers.Timer(500);
            _desktopCheckTimer.Elapsed += (s, e) => 
            {
                try
                {
                    CheckDesktopVisibility();
                }
                catch
                {
                    // Ignore timer errors - window might be closing
                }
            };
            _desktopCheckTimer.AutoReset = true;
            _desktopCheckTimer.Start();
            
            // Initial check
            CheckDesktopVisibility();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error starting desktop monitoring", ex);
            // Continue without desktop monitoring - widget will always be visible
        }
    }
    
    private void CheckDesktopVisibility()
    {
        try
        {
            // Don't check if window is closing
            if (_isClosing)
                return;
                
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Double-check window is still valid
                    if (!this.IsLoaded || _isClosing)
                        return;
                        
                    var foregroundWindow = GetForegroundWindow();
                    if (foregroundWindow == IntPtr.Zero)
                    {
                        // No foreground window, assume desktop
                        ShowOnDesktop();
                        return;
                    }
                    
                    // Check if foreground window is desktop/shell
                    var desktopWindow = GetDesktopWindow();
                    var shellWindow = GetShellWindow();
                    
                    if (foregroundWindow == desktopWindow || foregroundWindow == shellWindow)
                    {
                        // Desktop is active
                        ShowOnDesktop();
                        return;
                    }
                    
                    // Check if it's a fullscreen app
                    if (IsFullscreenApp(foregroundWindow))
                    {
                        // Fullscreen app is active, hide widget
                        HideFromDesktop();
                        return;
                    }
                    
                    // Check window title - if it's a known desktop window, show
                    var windowTitle = GetWindowTitle(foregroundWindow);
                    if (string.IsNullOrEmpty(windowTitle) || 
                        windowTitle.Contains("Program Manager") ||
                        windowTitle.Contains("Desktop"))
                    {
                        ShowOnDesktop();
                        return;
                    }
                    
                    // Check if window covers entire screen (likely fullscreen)
                    if (IsWindowFullscreen(foregroundWindow))
                    {
                        HideFromDesktop();
                    }
                    else
                    {
                        // Windowed app, show widget
                        ShowOnDesktop();
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogger.Log("Error checking desktop visibility", ex);
                    // On error, default to showing (safer)
                    ShowOnDesktop();
                }
            });
        }
        catch
        {
            // Ignore timer thread errors
        }
    }
    
    private bool IsFullscreenApp(IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero)
                return false;
                
            // Check if window is maximized and covers entire screen
            if (IsZoomed(hWnd))
            {
                GetWindowRect(hWnd, out RECT rect);
                var screen = System.Windows.Forms.Screen.PrimaryScreen;
                if (screen != null)
                {
                    var screenBounds = screen.Bounds;
                    // If window covers entire screen (within 10px margin), consider it fullscreen
                    if (Math.Abs(rect.Left - screenBounds.Left) < 10 &&
                        Math.Abs(rect.Top - screenBounds.Top) < 10 &&
                        Math.Abs(rect.Right - screenBounds.Right) < 10 &&
                        Math.Abs(rect.Bottom - screenBounds.Bottom) < 10)
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    private bool IsWindowFullscreen(IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero)
                return false;
                
            GetWindowRect(hWnd, out RECT rect);
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen != null)
            {
                var screenBounds = screen.Bounds;
                var windowWidth = rect.Right - rect.Left;
                var windowHeight = rect.Bottom - rect.Top;
                
                // If window covers >95% of screen, consider it fullscreen
                var screenArea = screenBounds.Width * screenBounds.Height;
                var windowArea = windowWidth * windowHeight;
                
                return (windowArea >= screenArea * 0.95);
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    private string GetWindowTitle(IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero)
                return string.Empty;
                
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
    
    private void ShowOnDesktop()
    {
        try
        {
            if (!_isDesktopVisible && this.IsLoaded)
            {
                _isDesktopVisible = true;
                this.Visibility = Visibility.Visible;
                this.Topmost = false; // Don't force on top, let it be desktop-only
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error showing on desktop", ex);
        }
    }
    
    private void HideFromDesktop()
    {
        try
        {
            if (_isDesktopVisible && this.IsLoaded)
            {
                _isDesktopVisible = false;
                this.Visibility = Visibility.Hidden;
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error hiding from desktop", ex);
        }
    }

    private void PositionWindow()
    {
        try
        {
            var config = _configService.GetConfig();
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null)
            {
                ErrorLogger.Log("Primary screen not available, using default position");
                this.Left = SystemParameters.PrimaryScreenWidth - this.Width - config.RightMarginPx;
                this.Top = 50;
                return;
            }
                
            var workingArea = screen.WorkingArea;
            
            // Position on right side, accounting for taskbar
            var targetLeft = workingArea.Right - this.Width - config.RightMarginPx;
            var targetTop = workingArea.Top + config.TopMarginPx; // Use configurable top margin
            
            // Ensure window fits in working area
            var windowHeight = (int)this.Height;
            if (targetTop + windowHeight > workingArea.Bottom)
            {
                targetTop = workingArea.Bottom - windowHeight - 10;
            }
            
            // Ensure window doesn't go off screen
            if (targetLeft < workingArea.Left)
            {
                targetLeft = workingArea.Left + 10;
            }
            if (targetTop < workingArea.Top)
            {
                targetTop = workingArea.Top + 10;
            }
            
            this.Left = targetLeft;
            this.Top = targetTop;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error positioning window", ex);
            // Fallback position
            var config = _configService.GetConfig();
            this.Left = SystemParameters.PrimaryScreenWidth - this.Width - config.RightMarginPx;
            this.Top = config.TopMarginPx;
        }
    }

    private void OnSensorDataUpdated(object? sender, SensorData data)
    {
        try
        {
            if (data == null)
                return;
                
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var config = _configService.GetConfig();
            
            // Auto-detect sensor issues: if CPU temp is 0 for multiple readings, auto-debug
            if (data.CpuTemp == 0)
            {
                _zeroReadingsCount++;
                if (_zeroReadingsCount >= MAX_ZERO_READINGS)
                {
                    // Auto-show debug window if CPU temp isn't working
                    var result = System.Windows.MessageBox.Show(
                        "SideHUD detected that CPU temperature is not being read.\n\n" +
                        "This usually means:\n" +
                        "• HWiNFO64 is not running, or\n" +
                        "• Shared Memory is not enabled in HWiNFO, or\n" +
                        "• Sensors window is not open in HWiNFO\n\n" +
                        "Would you like to open the debug window to diagnose?\n\n" +
                        "(Click 'No' to suppress this warning for this session)",
                        "CPU Temperature Detection Warning",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning
                    );
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        ShowDebugWindow();
                    }
                    _zeroReadingsCount = 0; // Reset to avoid spamming
                }
            }
            else
            {
                _zeroReadingsCount = 0; // Reset counter if we have valid data
            }
            
            // Update CPU (if enabled)
            if (config.ShowCpuUsage || config.ShowCpuTemp)
            {
                var cpuColor = GetTempColor(data.CpuTemp, config);
                var cpuParts = new List<string>();
                if (config.ShowCpuUsage)
                    cpuParts.Add($"{data.CpuUsage:F0}%");
                if (config.ShowCpuTemp)
                    cpuParts.Add($"{data.CpuTemp:F0}°C");
                CpuText.Text = $"CPU  {string.Join("  •  ", cpuParts)}";
                CpuText.Foreground = new SolidColorBrush(cpuColor);
                CpuText.Visibility = Visibility.Visible;
            }
            else
            {
                CpuText.Visibility = Visibility.Collapsed;
            }

            // Update GPU (if enabled)
            if (config.ShowGpuUsage || config.ShowGpuTemp)
            {
                var gpuColor = GetTempColor(data.GpuTemp, config);
                var gpuParts = new List<string>();
                if (config.ShowGpuUsage)
                    gpuParts.Add($"{data.GpuUsage:F0}%");
                if (config.ShowGpuTemp)
                    gpuParts.Add($"{data.GpuTemp:F0}°C");
                GpuText.Text = $"GPU  {string.Join("  •  ", gpuParts)}";
                GpuText.Foreground = new SolidColorBrush(gpuColor);
                GpuText.Visibility = Visibility.Visible;
            }
            else
            {
                GpuText.Visibility = Visibility.Collapsed;
            }

            // Update OVERALL (if enabled)
            if (config.ShowOverallTemp)
            {
                var overallColor = GetTempColor(data.OverallTemp, config);
                OverallText.Text = $"OVERALL  •  {data.OverallTemp:F0}°C";
                OverallText.Foreground = new SolidColorBrush(overallColor);
                OverallText.Visibility = Visibility.Visible;
            }
            else
            {
                OverallText.Visibility = Visibility.Collapsed;
            }

            // Update FPS overlay in top-left corner
            // Check if FPS overlay is enabled in config
            if (config.ShowFpsOverlay)
            {
                // ALWAYS show overlay when game is detected, even if FPS is 0 (will show "FPS —")
                // Also show if FPS >= FpsMinThreshold (configurable, default 10)
                // IMPORTANT: Only accept FPS >= 10 to filter out false positives
                bool hasValidFps = data.Fps >= Math.Max(10, config.FpsMinThreshold);
                bool shouldShowFps = (data.IsGaming && config.FpsShowWhenGaming) || hasValidFps;
                
                if (_fpsOverlay != null)
                {
                    try
                    {
                        if (shouldShowFps)
                        {
                            // Ensure window is on top
                            _fpsOverlay.Topmost = true;
                            
                            // Always update FPS overlay when game is detected
                            // This ensures it shows even if FPS is 0 (will display "FPS —")
                            _fpsOverlay.UpdateFps(data.Fps);
                        }
                        else
                        {
                            _fpsOverlay.HideFps();
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLogger.Log("Error updating FPS overlay", ex);
                        // Try to recreate overlay if it crashed
                        TryRecreateFpsOverlay();
                    }
                }
                else
                {
                    // FPS overlay is null - try to recreate it
                    TryRecreateFpsOverlay();
                }
                }
                else
                {
                    // FPS overlay disabled in config
                    if (_fpsOverlay != null)
                    {
                        _fpsOverlay.HideFps();
                    }
                }
            
            // Log if FPS overlay is null (for debugging)
            if (_fpsOverlay == null && (data.Fps > 0 || data.IsGaming))
            {
                ErrorLogger.Log($"FPS overlay is null but FPS={data.Fps:F0}, IsGaming={data.IsGaming}");
            }
            
            // Keep FPS row in main widget hidden (we use overlay instead)
            FpsText.Visibility = Visibility.Collapsed;

            // Update Power (if enabled)
            if (config.ShowSystemPower)
            {
                var powerLabel = data.HasPsuPower ? "PSU Power" : "Est. System Power";
                PowerText.Text = $"{powerLabel}  {data.SystemPower:F0} W";
                PowerText.Visibility = Visibility.Visible;
            }
            else
            {
                PowerText.Visibility = Visibility.Collapsed;
            }
                }
                catch (Exception ex)
                {
                    ErrorLogger.Log("Error updating UI from sensor data", ex);
                }
            });
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error in sensor data update handler", ex);
        }
    }

    private System.Windows.Media.Color GetTempColor(double temp, AppConfig config)
    {
        if (temp < config.TempWarn)
            return System.Windows.Media.Colors.LightGreen;
        else if (temp < config.TempHot)
            return System.Windows.Media.Colors.Orange;
        else
            return System.Windows.Media.Colors.Red;
    }

    private void OnThemeChanged(object? sender, ThemeColors theme)
    {
        try
        {
            if (theme == null)
                return;
                
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var config = _configService.GetConfig();
            
            // Create gradient background from theme colors
            var gradientBrush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1)
            };
            
            // Use theme colors with opacity for gradient
            var bgColor1 = System.Windows.Media.Color.FromArgb(
                (byte)(255 * config.TintOpacity * 0.8),
                theme.Background.R,
                theme.Background.G,
                theme.Background.B
            );
            
            var bgColor2 = System.Windows.Media.Color.FromArgb(
                (byte)(255 * config.TintOpacity),
                (byte)Math.Min(255, theme.Background.R + 20),
                (byte)Math.Min(255, theme.Background.G + 20),
                (byte)Math.Min(255, theme.Background.B + 20)
            );
            
            gradientBrush.GradientStops.Add(new GradientStop(bgColor1, 0));
            gradientBrush.GradientStops.Add(new GradientStop(bgColor2, 1));
            
            // Animate gradient change
            var bgAnim = new ColorAnimation(bgColor1, TimeSpan.FromMilliseconds(300));
            var bgAnim2 = new ColorAnimation(bgColor2, TimeSpan.FromMilliseconds(300));
            gradientBrush.GradientStops[0].BeginAnimation(GradientStop.ColorProperty, bgAnim);
            gradientBrush.GradientStops[1].BeginAnimation(GradientStop.ColorProperty, bgAnim2);
            
            // Apply to background border
            var bgBorder = (System.Windows.Controls.Border)this.FindName("BackgroundBorder");
            if (bgBorder != null)
            {
                bgBorder.Background = gradientBrush;
            }

            // Animate foreground color change
            var fgBrush = new SolidColorBrush(theme.Foreground);
            var fgAnim = new ColorAnimation(theme.Foreground, TimeSpan.FromMilliseconds(300));
            fgBrush.BeginAnimation(SolidColorBrush.ColorProperty, fgAnim);
            
            CpuText.Foreground = fgBrush;
            GpuText.Foreground = fgBrush;
            OverallText.Foreground = fgBrush;
            FpsText.Foreground = fgBrush;
            PowerText.Foreground = fgBrush;
            
            // Update separator colors
            var separatorColor = System.Windows.Media.Color.FromArgb(100, theme.Foreground.R, theme.Foreground.G, theme.Foreground.B);
            var sepBrush = new SolidColorBrush(separatorColor);
            var sepAnim = new ColorAnimation(separatorColor, TimeSpan.FromMilliseconds(300));
            sepBrush.BeginAnimation(SolidColorBrush.ColorProperty, sepAnim);
            
            var sep1 = (System.Windows.Shapes.Rectangle)this.FindName("Separator1");
            var sep2 = (System.Windows.Shapes.Rectangle)this.FindName("Separator2");
            var sep3 = (System.Windows.Shapes.Rectangle)this.FindName("Separator3");
            if (sep1 != null) sep1.Fill = sepBrush;
            if (sep2 != null) sep2.Fill = sepBrush;
            if (sep3 != null) sep3.Fill = sepBrush;

            // Apply Mica/Acrylic if Windows 11+
            if (Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000)
            {
                ApplyMicaEffect(theme);
            }

            // Set window opacity
            this.Opacity = config.Opacity;

            // Update font size
            var fontSize = config.FontSize;
            CpuText.FontSize = fontSize;
            GpuText.FontSize = fontSize;
            OverallText.FontSize = fontSize;
            FpsText.FontSize = fontSize;
            PowerText.FontSize = fontSize;
                }
                catch (Exception ex)
                {
                    ErrorLogger.Log("Error applying theme", ex);
                }
            });
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error in theme change handler", ex);
        }
    }
    
    private void ShowDebugWindow()
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var hwinfo = new Services.HWiNFOService();
                    var sensors = hwinfo.ListAllSensors();
                    var currentData = _sensorService?.GetCurrentData() ?? new SensorData();
                    var failedReadings = _sensorService?.GetFailedReadingsCount() ?? 0;
            
            var debugWindow = new Window
            {
                Title = "SideHUD - Sensor Debug & Analysis",
                Width = 900,
                Height = 700,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true
            };
            
            var scrollViewer = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            };
            
            var textBlock = new System.Windows.Controls.TextBlock
            {
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                Margin = new Thickness(10),
                TextWrapping = TextWrapping.Wrap
            };
            
            var debugText = $"╔══════════════════════════════════════════════════════════════╗\n";
            debugText += $"║         SideHUD Sensor Debug & Analysis Tool                  ║\n";
            debugText += $"╚══════════════════════════════════════════════════════════════╝\n\n";
            
            // Current Status
            debugText += $"📊 CURRENT SENSOR STATUS:\n";
            debugText += $"   CPU Usage: {currentData.CpuUsage:F1}% {(currentData.CpuUsage > 0 ? "✓" : "✗")}\n";
            debugText += $"   CPU Temp:  {currentData.CpuTemp:F1}°C {(currentData.CpuTemp > 0 ? "✓" : "✗ MISSING!")}\n";
            debugText += $"   CPU Power: {currentData.CpuPower:F1}W {(currentData.CpuPower > 0 ? "✓" : "✗")}\n";
            debugText += $"   GPU Usage: {currentData.GpuUsage:F1}% {(currentData.GpuUsage > 0 ? "✓" : "✗")}\n";
            debugText += $"   GPU Temp:  {currentData.GpuTemp:F1}°C {(currentData.GpuTemp > 0 ? "✓" : "✗")}\n";
            debugText += $"   GPU Power: {currentData.GpuPower:F1}W {(currentData.GpuPower > 0 ? "✓" : "✗")}\n";
            debugText += $"   Overall:   {currentData.OverallTemp:F1}°C {(currentData.OverallTemp > 0 ? "✓" : "✗")}\n";
            debugText += $"   System Pwr: {currentData.SystemPower:F1}W\n";
            debugText += $"   FPS: {currentData.Fps:F1} {(currentData.Fps > 0 ? "✓ Gaming" : "✗")}\n";
            debugText += $"   Failed Readings: {failedReadings}\n\n";
            
            // Data Source Status
            debugText += $"🔌 DATA SOURCE STATUS:\n";
            debugText += $"   {new string('─', 80)}\n";
            var hwinfoStatus = hwinfo.GetStatus();
            debugText += $"   HWiNFO:\n   {hwinfoStatus.Replace("\n", "\n   ")}\n\n";
            var nvml = new Services.NVMLService();
            var nvmlStatus = nvml.GetStatus();
            debugText += $"   NVML:\n   {nvmlStatus.Replace("\n", "\n   ")}\n";
            var cpuTemp = new Services.CpuTempService();
            debugText += $"   WMI CPU Temp: {(cpuTemp.IsAvailable() ? "✓ Available" : "✗ Not Available")}\n";
            var rtss = new Services.RTSSService();
            var rtssStatusDetail = rtss.GetStatus();
            debugText += $"   RTSS:\n   {rtssStatusDetail.Replace("\n", "\n   ")}\n\n";
            
            // Analysis
            debugText += $"🔍 AUTO-ANALYSIS:\n";
            if (currentData.CpuTemp == 0)
            {
                debugText += $"   ⚠️  CPU TEMPERATURE NOT DETECTED!\n";
                debugText += $"   Possible causes:\n";
                if (!hwinfo.IsAvailable())
                {
                    debugText += $"     • HWiNFO64 is not running or Shared Memory is disabled\n";
                    debugText += $"       → Solution: Start HWiNFO64 and enable Shared Memory\n";
                    debugText += $"         (Settings → Safety → Shared Memory Support)\n";
                }
                else
                {
                    debugText += $"     • HWiNFO is running but CPU temp sensor not found\n";
                    debugText += $"       → Check sensor list below for CPU temperature entries\n";
                    debugText += $"       → Make sure Sensors window is open in HWiNFO\n";
                }
                if (cpuTemp.IsAvailable())
                {
                    var wmiTemp = cpuTemp.GetCpuTemp();
                    debugText += $"     • WMI fallback available: {wmiTemp:F1}°C\n";
                }
            }
            else
            {
                debugText += $"   ✓ CPU temperature detected successfully\n";
            }
            
            if (currentData.GpuTemp == 0 && currentData.GpuUsage == 0)
            {
                debugText += $"   ⚠️  GPU DATA NOT DETECTED!\n";
                if (!hwinfo.IsAvailable() && !nvml.IsAvailable())
                {
                    debugText += $"     • Neither HWiNFO nor NVML available\n";
                }
            }
            
            debugText += $"\n";
            
            // RTSS Status (detailed)
            debugText += $"🎮 RTSS (FPS) STATUS:\n";
            debugText += $"   {new string('─', 80)}\n";
            debugText += $"   {rtssStatusDetail.Replace("\n", "\n   ")}\n";
            if (rtss.IsAvailable())
            {
                var rtssFps = rtss.ReadFps();
                if (rtssFps > 0)
                {
                    debugText += $"   ✓ FPS detected: {rtssFps:F2}\n";
                }
                else
                {
                    debugText += $"   ⚠️  RTSS is running but no FPS value found\n";
                    debugText += $"      • Make sure RTSS OSD is enabled for your game\n";
                    debugText += $"      • Check RTSS settings → On-Screen Display\n";
                }
            }
            else
            {
                debugText += $"   ⚠️  RTSS not detected\n";
                debugText += $"      • RTSS (RivaTuner Statistics Server) is optional\n";
                debugText += $"      • Install RTSS to enable FPS counter\n";
                debugText += $"      • FPS overlay will still show when game is detected\n";
            }
            debugText += $"\n";
            
            // Sensor List
            if (sensors.Count > 0)
            {
                debugText += $"📋 ALL HWiNFO SENSORS ({sensors.Count} total):\n";
                debugText += $"   {new string('─', 80)}\n";
                
                // Filter and highlight CPU/GPU sensors
                var cpuSensors = sensors.Where(s => s.ToLowerInvariant().Contains("cpu") && 
                                                   (s.ToLowerInvariant().Contains("temp") || 
                                                    s.ToLowerInvariant().Contains("temperature"))).ToList();
                var gpuSensors = sensors.Where(s => s.ToLowerInvariant().Contains("gpu") && 
                                                   (s.ToLowerInvariant().Contains("temp") || 
                                                    s.ToLowerInvariant().Contains("temperature"))).ToList();
                
                if (cpuSensors.Count > 0)
                {
                    debugText += $"   🟢 CPU TEMPERATURE SENSORS FOUND ({cpuSensors.Count}):\n";
                    foreach (var sensor in cpuSensors)
                    {
                        debugText += $"      → {sensor}\n";
                    }
                    debugText += $"\n";
                }
                else
                {
                    debugText += $"   🔴 NO CPU TEMPERATURE SENSORS FOUND IN HWiNFO!\n\n";
                }
                
                if (gpuSensors.Count > 0)
                {
                    debugText += $"   🟢 GPU TEMPERATURE SENSORS FOUND ({gpuSensors.Count}):\n";
                    foreach (var sensor in gpuSensors.Take(5))
                    {
                        debugText += $"      → {sensor}\n";
                    }
                    if (gpuSensors.Count > 5)
                        debugText += $"      ... and {gpuSensors.Count - 5} more\n";
                    debugText += $"\n";
                }
                
                debugText += $"   All Sensors:\n";
                foreach (var sensor in sensors.Take(100)) // Limit to first 100
                {
                    debugText += $"   {sensor}\n";
                }
                if (sensors.Count > 100)
                    debugText += $"   ... and {sensors.Count - 100} more sensors\n";
            }
            else
            {
                debugText += $"❌ NO HWiNFO SENSORS FOUND\n\n";
                debugText += $"TROUBLESHOOTING STEPS:\n";
                debugText += $"1. Ensure HWiNFO64 is running (not just installed)\n";
                debugText += $"2. Open HWiNFO Settings → Safety → Enable 'Shared Memory Support'\n";
                debugText += $"3. Open the 'Sensors' window in HWiNFO (View → Sensors)\n";
                debugText += $"4. Wait a few seconds for sensors to initialize\n";
                debugText += $"5. Restart SideHUD\n";
            }
            
            textBlock.Text = debugText;
            scrollViewer.Content = textBlock;
            debugWindow.Content = scrollViewer;
            debugWindow.Show();
            
                    nvml.Dispose();
                    cpuTemp.Dispose();
                    rtss.Dispose();
                    hwinfo.Dispose();
                }
                catch (Exception ex)
                {
                    ErrorLogger.Log("Error showing debug window", ex);
                    MessageBox.Show(
                        $"Failed to show debug window: {ex.Message}",
                        "Debug Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            });
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error in ShowDebugWindow", ex);
        }
    }

    private void ApplyMicaEffect(ThemeColors theme)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            // Try Mica (Windows 11 22H2+)
            const int DWMWA_MICA_EFFECT = 1029;
            const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
            const int DWMSBT_MAINWINDOW = 2; // Mica

            int backdropType = DWMSBT_MAINWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

            int micaValue = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref micaValue, sizeof(int));
        }
        catch
        {
            // Fallback to gradient (already handled in XAML)
        }
    }

    private void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        _trayService.ClickThrough = _clickThrough;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        
        if (_clickThrough)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
        }
        else
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, (exStyle & ~WS_EX_TRANSPARENT) | WS_EX_LAYERED);
        }
    }

    private void ToggleAlwaysOnTop()
    {
        _trayService.AlwaysOnTop = !_trayService.AlwaysOnTop;
        // Only set Topmost if desktop is visible (desktop-locked mode)
        if (_isDesktopVisible)
        {
            this.Topmost = _trayService.AlwaysOnTop;
        }
    }
    
    private void ToggleStartup()
    {
        try
        {
            var currentlyEnabled = StartupService.IsStartupEnabled();
            
            if (currentlyEnabled)
            {
                if (StartupService.DisableStartup())
                {
                    _trayService.StartupEnabled = false;
                    MessageBox.Show(
                        "SideHUD will no longer start automatically with Windows.",
                        "Startup Disabled",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
                else
                {
                    MessageBox.Show(
                        "Failed to disable startup. Check error log for details.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            }
            else
            {
                if (StartupService.EnableStartup())
                {
                    _trayService.StartupEnabled = true;
                    MessageBox.Show(
                        "SideHUD will now start automatically when Windows starts.",
                        "Startup Enabled",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
                else
                {
                    MessageBox.Show(
                        "Failed to enable startup. Check error log for details.\n\nMake sure the application is built and the .exe file exists.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error toggling startup", ex);
            MessageBox.Show(
                $"Error toggling startup: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        base.OnClosing(e);
    }
    
    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _desktopCheckTimer?.Stop();
            _desktopCheckTimer?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error stopping desktop timer", ex);
        }
        
        try
        {
            _fpsOverlay?.Close();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error closing FPS overlay", ex);
        }
        
        try
        {
            _sensorService?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error disposing sensor service", ex);
        }
        
        try
        {
            _themeService?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error disposing theme service", ex);
        }
        
        try
        {
            _trayService?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error disposing tray service", ex);
        }
        
        try
        {
            _autoDebugService?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error disposing auto-debug service", ex);
        }
        
        base.OnClosed(e);
    }
    
    private void CreateFpsOverlay()
    {
        try
        {
            // Dispose existing overlay if any
            if (_fpsOverlay != null)
            {
                try
                {
                    _fpsOverlay.Close();
                }
                catch { }
                _fpsOverlay = null;
            }
            
            // Create new overlay
            _fpsOverlay = new FpsOverlay();
            _fpsOverlay.Show(); // Show the window (it will be hidden initially)
            _fpsOverlay.Visibility = Visibility.Collapsed;
            _fpsOverlay.Topmost = true; // Ensure it stays on top
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Failed to create FPS overlay", ex);
            _fpsOverlay = null;
        }
    }
    
    private void TryRecreateFpsOverlay()
    {
        try
        {
            var config = _configService.GetConfig();
            if (!config.ShowFpsOverlay)
                return;
                
            ErrorLogger.Log("Attempting to recreate FPS overlay...");
            CreateFpsOverlay();
            
            if (_fpsOverlay != null)
            {
                ErrorLogger.Log("FPS overlay recreated successfully");
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Failed to recreate FPS overlay", ex);
        }
    }
    
    private bool ValidateSensorData(SensorData data)
    {
        try
        {
            if (data == null)
                return false;
                
            // Check for NaN or Infinity values
            if (double.IsNaN(data.CpuUsage) || double.IsInfinity(data.CpuUsage) ||
                double.IsNaN(data.GpuUsage) || double.IsInfinity(data.GpuUsage) ||
                double.IsNaN(data.CpuTemp) || double.IsInfinity(data.CpuTemp) ||
                double.IsNaN(data.GpuTemp) || double.IsInfinity(data.GpuTemp) ||
                double.IsNaN(data.OverallTemp) || double.IsInfinity(data.OverallTemp) ||
                double.IsNaN(data.Fps) || double.IsInfinity(data.Fps))
            {
                ErrorLogger.Log("Invalid sensor data detected (NaN/Infinity values)");
                return false;
            }
            
            // Check for reasonable ranges
            if (data.CpuUsage < 0 || data.CpuUsage > 100 ||
                data.GpuUsage < 0 || data.GpuUsage > 100 ||
                data.CpuTemp < 0 || data.CpuTemp > 200 ||
                data.GpuTemp < 0 || data.GpuTemp > 200 ||
                data.OverallTemp < 0 || data.OverallTemp > 200 ||
                data.Fps < 0 || data.Fps > 10000)
            {
                ErrorLogger.Log("Invalid sensor data detected (out of range values)");
                return false;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error validating sensor data", ex);
            return false;
        }
    }
}

