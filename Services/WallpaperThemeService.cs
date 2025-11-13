using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace SideHUD.Services;

public class WallpaperThemeService
{
    private readonly ConfigurationService _config;
    private string? _lastWallpaperPath;
    private System.Timers.Timer? _pollTimer;
    private ThemeColors? _currentTheme;

    public event EventHandler<ThemeColors>? ThemeChanged;

    public WallpaperThemeService(ConfigurationService config)
    {
        _config = config;
        
        // Subscribe to wallpaper changes
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        
        // Poll for wallpaper changes every 5 seconds
        _pollTimer = new System.Timers.Timer(5000);
        _pollTimer.Elapsed += (s, e) => CheckWallpaperChange();
        _pollTimer.Start();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Desktop)
        {
            CheckWallpaperChange();
        }
    }

    private void CheckWallpaperChange()
    {
        var currentPath = GetWallpaperPath();
        if (currentPath != _lastWallpaperPath)
        {
            _lastWallpaperPath = currentPath;
            UpdateTheme();
        }
    }

    public void UpdateTheme()
    {
        var config = _config.GetConfig();
        
        if (config.ThemeMode == "ForceDark")
        {
            ApplyTheme(new ThemeColors
            {
                Background = System.Windows.Media.Color.FromArgb(200, 20, 20, 20),
                Foreground = System.Windows.Media.Colors.White,
                Accent = System.Windows.Media.Color.FromArgb(255, 100, 150, 255)
            });
            return;
        }
        
        if (config.ThemeMode == "ForceLight")
        {
            ApplyTheme(new ThemeColors
            {
                Background = System.Windows.Media.Color.FromArgb(200, 240, 240, 240),
                Foreground = System.Windows.Media.Colors.Black,
                Accent = System.Windows.Media.Color.FromArgb(255, 50, 100, 200)
            });
            return;
        }

        // Auto from wallpaper
        var wallpaperPath = GetWallpaperPath();
        if (string.IsNullOrEmpty(wallpaperPath) || !File.Exists(wallpaperPath))
        {
            // Fallback to dark theme
            ApplyTheme(new ThemeColors
            {
                Background = System.Windows.Media.Color.FromArgb(200, 20, 20, 20),
                Foreground = System.Windows.Media.Colors.White,
                Accent = System.Windows.Media.Color.FromArgb(255, 100, 150, 255)
            });
            return;
        }

        try
        {
            var colors = ExtractColorsFromWallpaper(wallpaperPath);
            ApplyTheme(colors);
        }
        catch
        {
            // Fallback on error
            ApplyTheme(new ThemeColors
            {
                Background = System.Windows.Media.Color.FromArgb(200, 20, 20, 20),
                Foreground = System.Windows.Media.Colors.White,
                Accent = System.Windows.Media.Color.FromArgb(255, 100, 150, 255)
            });
        }
    }

    private ThemeColors ExtractColorsFromWallpaper(string path)
    {
        using var bitmap = new Bitmap(path);
        
        // Downscale for performance
        var maxSize = 200;
        var scale = Math.Min(1.0, (double)maxSize / Math.Max(bitmap.Width, bitmap.Height));
        var scaledWidth = (int)(bitmap.Width * scale);
        var scaledHeight = (int)(bitmap.Height * scale);
        
        using var scaled = new Bitmap(scaledWidth, scaledHeight);
        using var g = Graphics.FromImage(scaled);
        g.DrawImage(bitmap, 0, 0, scaledWidth, scaledHeight);

        // Extract palette using simple K-Means
        var palette = ExtractPalette(scaled, 5);
        
        if (palette == null || palette.Count == 0)
        {
            // Fallback: use average color
            var avgColor = GetAverageColor(scaled);
            return CreateThemeFromColor(avgColor);
        }

        // Find dominant (most frequent/largest cluster)
        var dominant = palette.OrderByDescending(c => c.Population).First().Color;
        
        // Find accent (highest saturation, farthest hue from dominant)
        var dominantHue = GetHue(dominant);
        var accent = palette
            .OrderByDescending(c => GetSaturation(c.Color))
            .ThenByDescending(c => Math.Abs(GetHue(c.Color) - dominantHue))
            .First().Color;
        
        // Find neutral (mid-lightness)
        var neutral = palette
            .OrderBy(c => Math.Abs(GetLightness(c.Color) - 0.5))
            .First().Color;

        return CreateThemeFromColors(dominant, accent, neutral);
    }

    private List<ColorCluster> ExtractPalette(Bitmap bitmap, int k)
    {
        // Sample pixels (every nth pixel for performance)
        var pixels = new List<(int R, int G, int B)>();
        var step = Math.Max(1, Math.Max(bitmap.Width, bitmap.Height) / 50);
        
        for (int x = 0; x < bitmap.Width; x += step)
        {
            for (int y = 0; y < bitmap.Height; y += step)
            {
                var pixel = bitmap.GetPixel(x, y);
                pixels.Add((pixel.R, pixel.G, pixel.B));
            }
        }

        if (pixels.Count == 0)
            return new List<ColorCluster>();

        // Initialize centroids randomly
        var random = new Random();
        var centroids = new List<(int R, int G, int B)>();
        for (int i = 0; i < k && i < pixels.Count; i++)
        {
            centroids.Add(pixels[random.Next(pixels.Count)]);
        }

        // K-Means iterations
        for (int iter = 0; iter < 10; iter++)
        {
            var clusters = new List<List<(int R, int G, int B)>>();
            for (int i = 0; i < centroids.Count; i++)
                clusters.Add(new List<(int R, int G, int B)>());

            // Assign pixels to nearest centroid
            foreach (var pixel in pixels)
            {
                var minDist = double.MaxValue;
                var nearest = 0;
                for (int i = 0; i < centroids.Count; i++)
                {
                    var dist = ColorDistance(pixel, centroids[i]);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearest = i;
                    }
                }
                clusters[nearest].Add(pixel);
            }

            // Update centroids
            var newCentroids = new List<(int R, int G, int B)>();
            for (int i = 0; i < clusters.Count; i++)
            {
                if (clusters[i].Count > 0)
                {
                    var avgR = (int)clusters[i].Average(p => p.R);
                    var avgG = (int)clusters[i].Average(p => p.G);
                    var avgB = (int)clusters[i].Average(p => p.B);
                    newCentroids.Add((avgR, avgG, avgB));
                }
            }

            if (newCentroids.Count == 0)
                break;

            centroids = newCentroids;
        }

        // Create color clusters with population
        var result = new List<ColorCluster>();
        var allClusters = new List<List<(int R, int G, int B)>>();
        for (int i = 0; i < centroids.Count; i++)
            allClusters.Add(new List<(int R, int G, int B)>());

        foreach (var pixel in pixels)
        {
            var minDist = double.MaxValue;
            var nearest = 0;
            for (int i = 0; i < centroids.Count; i++)
            {
                var dist = ColorDistance(pixel, centroids[i]);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = i;
                }
            }
            allClusters[nearest].Add(pixel);
        }

        foreach (var cluster in allClusters)
        {
            if (cluster.Count > 0)
            {
                var avgR = (int)cluster.Average(p => p.R);
                var avgG = (int)cluster.Average(p => p.G);
                var avgB = (int)cluster.Average(p => p.B);
                result.Add(new ColorCluster
                {
                    Color = System.Drawing.Color.FromArgb(avgR, avgG, avgB),
                    Population = cluster.Count
                });
            }
        }

        return result;
    }

    private double ColorDistance((int R, int G, int B) c1, (int R, int G, int B) c2)
    {
        var dr = c1.R - c2.R;
        var dg = c1.G - c2.G;
        var db = c1.B - c2.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    private System.Drawing.Color GetAverageColor(Bitmap bitmap)
    {
        long r = 0, g = 0, b = 0;
        int count = 0;
        
        for (int x = 0; x < bitmap.Width; x++)
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                var pixel = bitmap.GetPixel(x, y);
                r += pixel.R;
                g += pixel.G;
                b += pixel.B;
                count++;
            }
        }
        
        return System.Drawing.Color.FromArgb(
            (int)(r / count),
            (int)(g / count),
            (int)(b / count)
        );
    }

    private ThemeColors CreateThemeFromColor(System.Drawing.Color color)
    {
        return CreateThemeFromColors(color, color, color);
    }

    private ThemeColors CreateThemeFromColors(System.Drawing.Color dominant, System.Drawing.Color accent, System.Drawing.Color neutral)
    {
        var config = _config.GetConfig();
        
        // Boost saturation if needed
        if (config.SaturationBoost > 0)
        {
            dominant = BoostSaturation(dominant, config.SaturationBoost);
            accent = BoostSaturation(accent, config.SaturationBoost);
        }

        // Create background with tint opacity
        var bgColor = System.Windows.Media.Color.FromArgb(
            (byte)(255 * config.TintOpacity),
            dominant.R,
            dominant.G,
            dominant.B
        );

        // Calculate foreground with WCAG contrast
        var foreground = CalculateContrastSafeForeground(bgColor, neutral);

        return new ThemeColors
        {
            Background = bgColor,
            Foreground = foreground,
            Accent = System.Windows.Media.Color.FromRgb(accent.R, accent.G, accent.B)
        };
    }

    private System.Windows.Media.Color CalculateContrastSafeForeground(System.Windows.Media.Color bg, System.Drawing.Color neutral)
    {
        // Try white first
        var white = System.Windows.Media.Colors.White;
        if (GetContrastRatio(bg, white) >= 4.5)
            return white;

        // Try black
        var black = System.Windows.Media.Colors.Black;
        if (GetContrastRatio(bg, black) >= 4.5)
            return black;

        // Adjust tint lightness until we get good contrast
        var adjusted = bg;
        for (int i = 0; i < 20; i++)
        {
            if (GetContrastRatio(adjusted, white) >= 4.5)
                return white;
            if (GetContrastRatio(adjusted, black) >= 4.5)
                return black;

            // Darken background
            adjusted = System.Windows.Media.Color.FromArgb(
                adjusted.A,
                (byte)Math.Max(0, adjusted.R - 10),
                (byte)Math.Max(0, adjusted.G - 10),
                (byte)Math.Max(0, adjusted.B - 10)
            );
        }

        return white; // Fallback
    }

    private double GetContrastRatio(System.Windows.Media.Color c1, System.Windows.Media.Color c2)
    {
        var l1 = GetRelativeLuminance(c1);
        var l2 = GetRelativeLuminance(c2);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private double GetRelativeLuminance(System.Windows.Media.Color c)
    {
        var r = GetLuminanceComponent(c.R / 255.0);
        var g = GetLuminanceComponent(c.G / 255.0);
        var b = GetLuminanceComponent(c.B / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private double GetLuminanceComponent(double c)
    {
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private System.Drawing.Color BoostSaturation(System.Drawing.Color color, double boost)
    {
        var hsl = RgbToHsl(color);
        hsl.S = Math.Min(1.0, hsl.S + boost);
        return HslToRgb(hsl);
    }

    private (double H, double S, double L) RgbToHsl(System.Drawing.Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        var l = (max + min) / 2.0;
        double h = 0, s = 0;

        if (delta != 0)
        {
            s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);

            if (max == r)
                h = ((g - b) / delta + (g < b ? 6 : 0)) / 6.0;
            else if (max == g)
                h = ((b - r) / delta + 2) / 6.0;
            else
                h = ((r - g) / delta + 4) / 6.0;
        }

        return (h, s, l);
    }

    private System.Drawing.Color HslToRgb((double H, double S, double L) hsl)
    {
        var (h, s, l) = hsl;
        double r, g, b;

        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;

            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }

        return System.Drawing.Color.FromArgb(
            (int)(r * 255),
            (int)(g * 255),
            (int)(b * 255)
        );
    }

    private double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private double GetHue(System.Drawing.Color color)
    {
        return RgbToHsl(color).H;
    }

    private double GetSaturation(System.Drawing.Color color)
    {
        return RgbToHsl(color).S;
    }

    private double GetLightness(System.Drawing.Color color)
    {
        return RgbToHsl(color).L;
    }

    private void ApplyTheme(ThemeColors colors)
    {
        _currentTheme = colors;
        ThemeChanged?.Invoke(this, colors);
    }

    public ThemeColors? GetCurrentTheme() => _currentTheme;

    private string? GetWallpaperPath()
    {
        try
        {
            // Try IDesktopWallpaper COM interface first (Windows 8+)
            var path = GetWallpaperPathFromCOM();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;

            // Fallback to registry/SystemParametersInfo
            path = GetWallpaperPathFromRegistry();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;
        }
        catch
        {
        }

        return null;
    }

    private string? GetWallpaperPathFromCOM()
    {
        try
        {
            var type = Type.GetTypeFromProgID("DesktopWallpaper.DesktopWallpaper");
            if (type == null)
                return null;

            var wallpaper = Activator.CreateInstance(type);
            if (wallpaper == null)
                return null;

            // Get primary monitor wallpaper
            var method = type.GetMethod("GetWallpaper");
            if (method == null)
                return null;

            var monitorId = Marshal.StringToBSTR("");
            try
            {
                var result = method.Invoke(wallpaper, new object[] { monitorId });
                return result?.ToString();
            }
            finally
            {
                Marshal.FreeBSTR(monitorId);
            }
        }
        catch
        {
            return null;
        }
    }

    private string? GetWallpaperPathFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var path = key?.GetValue("WallPaper")?.ToString();
            return path;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error disposing WallpaperThemeService", ex);
        }
    }
}

public class ColorCluster
{
    public System.Drawing.Color Color { get; set; }
    public int Population { get; set; }
}

public class ThemeColors
{
    public System.Windows.Media.Color Background { get; set; }
    public System.Windows.Media.Color Foreground { get; set; }
    public System.Windows.Media.Color Accent { get; set; }
}

