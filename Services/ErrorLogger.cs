using System;
using System.IO;

namespace SideHUD.Services;

public static class ErrorLogger
{
    private static readonly object _lock = new object();
    private static string? _logPath;

    static ErrorLogger()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = Path.Combine(appData, "SideHUD");
            Directory.CreateDirectory(logDir);
            _logPath = Path.Combine(logDir, "error.log");
        }
        catch
        {
            // If we can't create log file, just use debug output
        }
    }

    public static void Log(string message, Exception? ex = null)
    {
        try
        {
            var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            if (ex != null)
            {
                logMessage += $"\nException: {ex.GetType().Name}: {ex.Message}\nStack: {ex.StackTrace}";
            }

            System.Diagnostics.Debug.WriteLine(logMessage);

            if (_logPath != null)
            {
                lock (_lock)
                {
                    File.AppendAllText(_logPath, logMessage + "\n\n");
                }
            }
        }
        catch
        {
            // Silently fail - don't crash on logging errors
        }
    }
}

