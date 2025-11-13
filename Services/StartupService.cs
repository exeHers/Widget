using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace SideHUD.Services;

public class StartupService
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SideHUD";
    
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
            if (key == null)
                return false;
                
            var value = key.GetValue(AppName);
            return value != null && !string.IsNullOrEmpty(value.ToString());
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error checking startup status", ex);
            return false;
        }
    }
    
    public static bool EnableStartup()
    {
        try
        {
            var exePath = GetExecutablePath();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                ErrorLogger.Log("Cannot find executable path for startup registration");
                return false;
            }
            
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            if (key == null)
            {
                ErrorLogger.Log("Cannot open registry key for startup");
                return false;
            }
            
            // Register with full path in quotes to handle spaces
            key.SetValue(AppName, $"\"{exePath}\"");
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error enabling startup", ex);
            return false;
        }
    }
    
    public static bool DisableStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            if (key == null)
                return false;
                
            key.DeleteValue(AppName, false);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error disabling startup", ex);
            return false;
        }
    }
    
    private static string GetExecutablePath()
    {
        try
        {
            // Method 1: Use Environment.ProcessPath (most reliable for .NET 8)
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
            {
                return Path.GetFullPath(processPath);
            }
            
            // Method 2: Use Process.GetCurrentProcess().MainModule.FileName
            try
            {
                var process = Process.GetCurrentProcess();
                if (process.MainModule != null && !string.IsNullOrEmpty(process.MainModule.FileName))
                {
                    var mainModulePath = process.MainModule.FileName;
                    if (File.Exists(mainModulePath))
                    {
                        return Path.GetFullPath(mainModulePath);
                    }
                }
            }
            catch
            {
                // Ignore - try next method
            }
            
            // Method 3: Use AppContext.BaseDirectory (works for single-file apps)
            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                var baseExeName = Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName);
                if (string.IsNullOrEmpty(baseExeName) || !baseExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    baseExeName = "SideHUD.exe";
                }
                var baseExePath = Path.Combine(baseDir, baseExeName);
                if (File.Exists(baseExePath))
                {
                    return Path.GetFullPath(baseExePath);
                }
            }
            
            // Method 4: Use Assembly.Location (works for non-published apps, but not single-file)
            var assembly = Assembly.GetExecutingAssembly();
            var location = assembly.Location;
            
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                return Path.GetFullPath(location);
            }
            
            // Method 5: Try to find the .exe in common locations
            var assemblyExeName = Path.GetFileNameWithoutExtension(assembly.GetName().Name) + ".exe";
            var currentDir = Directory.GetCurrentDirectory();
            
            // Check current directory
            var currentExePath = Path.Combine(currentDir, assemblyExeName);
            if (File.Exists(currentExePath))
            {
                return Path.GetFullPath(currentExePath);
            }
            
            // Try bin/Debug/net8.0-windows
            var debugPath = Path.Combine(currentDir, "bin", "Debug", "net8.0-windows", assemblyExeName);
            if (File.Exists(debugPath))
            {
                return Path.GetFullPath(debugPath);
            }
            
            // Try bin/Release/net8.0-windows
            var releasePath = Path.Combine(currentDir, "bin", "Release", "net8.0-windows", assemblyExeName);
            if (File.Exists(releasePath))
            {
                return Path.GetFullPath(releasePath);
            }
            
            ErrorLogger.Log($"Cannot find executable: {assemblyExeName}. Tried: {currentDir}, ProcessPath: {processPath}, Location: {location}");
            return string.Empty;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error getting executable path", ex);
            return string.Empty;
        }
    }
}

