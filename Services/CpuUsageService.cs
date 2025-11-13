using System;
using System.Diagnostics;

namespace SideHUD.Services;

public class CpuUsageService
{
    private PerformanceCounter? _cpuCounter;
    private bool _initialized = false;

    public bool IsAvailable()
    {
        try
        {
            if (!_initialized)
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue(); // First call returns 0, so call it once
                _initialized = true;
            }
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("CPU Usage service unavailable", ex);
            return false;
        }
    }

    public double GetCpuUsage()
    {
        if (!IsAvailable() || _cpuCounter == null)
            return 0;

        try
        {
            return _cpuCounter.NextValue();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("CPU Usage read error", ex);
            return 0;
        }
    }

    public void Dispose()
    {
        _cpuCounter?.Dispose();
        _initialized = false;
    }
}

