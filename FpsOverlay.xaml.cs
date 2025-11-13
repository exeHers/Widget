using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SideHUD;

public partial class FpsOverlay : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x8000000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int DWMWA_EXCLUDED_FROM_PEEK = 12;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public FpsOverlay()
    {
        try
        {
            InitializeComponent();
            this.Loaded += FpsOverlay_Loaded;
            this.Visibility = Visibility.Collapsed;
            this.Topmost = true; // Always on top
            this.ShowInTaskbar = false; // Don't show in taskbar
        }
        catch (Exception ex)
        {
            Services.ErrorLogger.Log("Error initializing FPS overlay", ex);
        }
    }

    private void FpsOverlay_Loaded(object sender, RoutedEventArgs e)
    {
        // Position in top-left corner (configurable via appsettings.json)
        PositionWindow();
        
        // Apply window styles to make it non-intrusive
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
    }

    private void PositionWindow()
    {
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null)
            {
                Services.ErrorLogger.Log("Primary screen not available for FPS overlay");
                this.Left = 12;
                this.Top = 12;
                return;
            }
                
            var workingArea = screen.WorkingArea;
            
            // Position in top-left corner with small margin
            this.Left = workingArea.Left + 12;
            this.Top = workingArea.Top + 12;
        }
        catch (Exception ex)
        {
            Services.ErrorLogger.Log("Error positioning FPS overlay", ex);
            this.Left = 12;
            this.Top = 12;
        }
    }

    public void UpdateFps(double fps)
    {
        try
        {
            // Validate FPS value
            if (double.IsNaN(fps) || double.IsInfinity(fps) || fps < 0)
            {
                fps = 0;
                Services.ErrorLogger.Log("Invalid FPS value detected, resetting to 0");
            }
            
            // Clamp FPS to reasonable range
            if (fps > 10000)
            {
                fps = 10000;
                Services.ErrorLogger.Log("FPS value out of range, clamped to 10000");
            }
                
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Ensure window is still valid
                    if (this == null || FpsTextBlock == null)
                    {
                        Services.ErrorLogger.Log("FPS overlay or text block is null");
                        return;
                    }
                    
                    // Display FPS - only show if >= 10 (filters out false positives like frame counters)
                    // Round to nearest integer for display
                    if (fps >= 10)
                    {
                        var fpsInt = (int)Math.Round(fps);
                        FpsTextBlock.Text = $"FPS  {fpsInt}";
                    }
                    else
                    {
                        // Show "—" for invalid or low FPS values
                        FpsTextBlock.Text = "FPS  —";
                    }
                    
                    // Ensure window is shown and on top
                    if (!this.IsLoaded)
                    {
                        try
                        {
                            this.Show();
                        }
                        catch (Exception ex)
                        {
                            Services.ErrorLogger.Log("Error showing FPS overlay", ex);
                        }
                    }
                    
                    try
                    {
                        this.Topmost = true; // Always keep on top
                    }
                    catch (Exception ex)
                    {
                        Services.ErrorLogger.Log("Error setting FPS overlay topmost", ex);
                    }
                    
                    // Show with fade-in if hidden
                    if (this.Visibility != Visibility.Visible)
                    {
                        try
                        {
                            this.Opacity = 0;
                            this.Visibility = Visibility.Visible;
                            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                            this.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                        }
                        catch (Exception ex)
                        {
                            Services.ErrorLogger.Log("Error animating FPS overlay", ex);
                            // Fallback: just show it
                            this.Visibility = Visibility.Visible;
                            this.Opacity = 1;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Services.ErrorLogger.Log("Error updating FPS overlay UI", ex);
                }
            }, System.Windows.Threading.DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            Services.ErrorLogger.Log("Error in FPS update", ex);
        }
    }

    public void HideFps()
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (this.Visibility == Visibility.Visible)
                    {
                        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
                        fadeOut.Completed += (s, e) => 
                        {
                            try
                            {
                                if (this.Opacity == 0)
                                    this.Visibility = Visibility.Collapsed;
                            }
                            catch
                            {
                                // Ignore animation completion errors
                            }
                        };
                        this.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                    }
                }
                catch (Exception ex)
                {
                    Services.ErrorLogger.Log("Error hiding FPS overlay", ex);
                }
            });
        }
        catch (Exception ex)
        {
            Services.ErrorLogger.Log("Error in FPS hide", ex);
        }
    }
}

