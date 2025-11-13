using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace SideHUD.Services;

public class GameDetectionService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly string[] _gameKeywords = new[]
    {
        ".exe", // Any executable
        "game", "gaming", "steam", "epic", "origin", "battle.net",
        "unreal", "unity", "directx", "opengl", "vulkan"
    };

    private readonly string[] _nonGameKeywords = new[]
    {
        "explorer", "desktop", "taskbar", "start menu", "program manager",
        "chrome", "firefox", "edge", "opera", "browser",
        "notepad", "word", "excel", "powerpoint", "office",
        "visual studio", "code", "cursor", "intellij", "eclipse"
    };

    public bool IsGameRunning(double gpuUsage, double fps)
    {
        try
        {
            // Validate inputs to prevent NaN/Infinity issues
            if (double.IsNaN(gpuUsage) || double.IsInfinity(gpuUsage))
                gpuUsage = 0;
            if (double.IsNaN(fps) || double.IsInfinity(fps))
                fps = 0;
            
            // Clamp values to reasonable ranges
            gpuUsage = Math.Max(0, Math.Min(100, gpuUsage));
            fps = Math.Max(0, Math.Min(10000, fps));
            
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
                return false;

            // If RTSS reports FPS >= 1, it's definitely a game/app running
            if (fps >= 1)
                return true;

            // Check if window is visible
            if (!IsWindowVisible(foregroundWindow))
                return false;

            // Get window title
            var windowTitle = GetWindowTitle(foregroundWindow);
            
            // Get process name
            var processName = GetProcessName(foregroundWindow);
            
            // Check if it's a known non-game application
            if (IsNonGameApplication(windowTitle, processName))
                return false;

            // Check if it's fullscreen or covers most of the screen
            if (IsFullscreenWindow(foregroundWindow))
            {
                // Fullscreen + high GPU usage = likely game
                if (gpuUsage > 20) // 20% GPU usage threshold
                    return true;
            }

            // Check if process name suggests it's a game
            if (IsGameProcess(processName, windowTitle))
                return true;

            // Check if GPU usage is high (indicates 3D rendering)
            if (gpuUsage > 30) // 30% GPU usage suggests active 3D rendering
            {
                // Additional check: window is large and not a browser
                if (IsLargeWindow(foregroundWindow) && !IsBrowser(windowTitle, processName))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error detecting game", ex);
            return false;
        }
    }

    private string GetWindowTitle(IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero)
                return string.Empty;

            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, 256);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private string GetProcessName(IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero)
                return string.Empty;

            GetWindowThreadProcessId(hWnd, out uint processId);
            if (processId == 0)
                return string.Empty;

            // Validate process ID is in reasonable range
            if (processId > int.MaxValue)
                return string.Empty;

            var process = Process.GetProcessById((int)processId);
            return process?.ProcessName ?? string.Empty;
        }
        catch (ArgumentException)
        {
            // Process doesn't exist anymore - this is normal, just return empty
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool IsFullscreenWindow(IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero)
                return false;

            GetWindowRect(hWnd, out RECT rect);
            var screen = Screen.PrimaryScreen;
            if (screen == null)
                return false;

            var screenBounds = screen.Bounds;
            var windowWidth = rect.Right - rect.Left;
            var windowHeight = rect.Bottom - rect.Top;

            // Validate window dimensions
            if (windowWidth <= 0 || windowHeight <= 0)
                return false;
            
            // Check if window covers >90% of screen
            var screenArea = screenBounds.Width * screenBounds.Height;
            var windowArea = windowWidth * windowHeight;
            
            // Prevent division by zero or invalid calculations
            if (screenArea <= 0)
                return false;

            if (windowArea >= screenArea * 0.90)
            {
                // Also check if it's maximized
                if (IsZoomed(hWnd))
                    return true;

                // Check if window position matches screen bounds (within 5px margin)
                if (Math.Abs(rect.Left - screenBounds.Left) < 5 &&
                    Math.Abs(rect.Top - screenBounds.Top) < 5 &&
                    Math.Abs(rect.Right - screenBounds.Right) < 5 &&
                    Math.Abs(rect.Bottom - screenBounds.Bottom) < 5)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool IsLargeWindow(IntPtr hWnd)
    {
        try
        {
            if (hWnd == IntPtr.Zero)
                return false;

            GetWindowRect(hWnd, out RECT rect);
            var screen = Screen.PrimaryScreen;
            if (screen == null)
                return false;

            var screenBounds = screen.Bounds;
            var windowWidth = rect.Right - rect.Left;
            var windowHeight = rect.Bottom - rect.Top;

            // Validate window dimensions
            if (windowWidth <= 0 || windowHeight <= 0)
                return false;
            
            // Window is large if it covers >50% of screen
            var screenArea = screenBounds.Width * screenBounds.Height;
            var windowArea = windowWidth * windowHeight;
            
            // Prevent division by zero or invalid calculations
            if (screenArea <= 0)
                return false;

            return windowArea >= screenArea * 0.50;
        }
        catch
        {
            return false;
        }
    }

    private bool IsGameProcess(string processName, string windowTitle)
    {
        if (string.IsNullOrEmpty(processName) && string.IsNullOrEmpty(windowTitle))
            return false;

        var combined = $"{processName} {windowTitle}".ToLowerInvariant();

        // Check for non-game keywords first
        if (_nonGameKeywords.Any(keyword => combined.Contains(keyword)))
            return false;

        // Check for game indicators
        // Most executables that are fullscreen and have high GPU usage are games
        // We'll rely on GPU usage + fullscreen detection instead of keyword matching
        // to avoid false positives

        return false; // Don't rely on keywords alone
    }

    private bool IsNonGameApplication(string windowTitle, string processName)
    {
        if (string.IsNullOrEmpty(processName) && string.IsNullOrEmpty(windowTitle))
            return false;

        var combined = $"{processName} {windowTitle}".ToLowerInvariant();

        return _nonGameKeywords.Any(keyword => combined.Contains(keyword));
    }

    private bool IsBrowser(string windowTitle, string processName)
    {
        if (string.IsNullOrEmpty(processName) && string.IsNullOrEmpty(windowTitle))
            return false;

        var combined = $"{processName} {windowTitle}".ToLowerInvariant();

        var browserKeywords = new[] { "chrome", "firefox", "edge", "opera", "brave", "vivaldi", "safari", "browser" };
        return browserKeywords.Any(keyword => combined.Contains(keyword));
    }
}

