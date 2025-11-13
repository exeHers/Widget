using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using SideHUD.Models;
using SideHUD.Services;

namespace SideHUD.Services;

public class SensorAggregatorService : IDisposable
{
    private readonly HWiNFOService _hwinfo;
    private readonly RTSSService _rtss;
    private readonly NVMLService _nvml;
    private readonly CpuUsageService _cpuUsage;
    private readonly CpuTempService _cpuTemp;
    private readonly GameDetectionService _gameDetection;
    private readonly FpsEstimationService _fpsEstimation;
    private readonly ConfigurationService _config;
    private System.Timers.Timer? _timer;
    private SensorData _currentData = new();
    private bool _disposed = false;
    private int _failedReadings = 0;
    private bool _wasGaming = false;

    public event EventHandler<SensorData>? DataUpdated;

    public SensorAggregatorService(ConfigurationService config)
    {
        _config = config;
        _hwinfo = new HWiNFOService();
        _rtss = new RTSSService();
        _nvml = new NVMLService();
        _cpuUsage = new CpuUsageService();
        _cpuTemp = new CpuTempService();
        _gameDetection = new GameDetectionService();
        _fpsEstimation = new FpsEstimationService();
    }

    public void Start()
    {
        var config = _config.GetConfig();
        _timer = new System.Timers.Timer(config.UpdateIntervalMs);
        _timer.Elapsed += OnTimerElapsed;
        _timer.AutoReset = true;
        _timer.Start();
        
        // Initial read
        OnTimerElapsed(null, null!);
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            var data = new SensorData();
            var appConfig = _config.GetConfig();

            // Try HWiNFO first
            if (_hwinfo.IsAvailable())
            {
                var hwinfoData = _hwinfo.ReadSensors();
                data = hwinfoData;
                
                // If CPU temp is still 0, try fallback
                if (data.CpuTemp == 0 && _cpuTemp.IsAvailable())
                {
                    var fallbackTemp = _cpuTemp.GetCpuTemp();
                    if (fallbackTemp > 0)
                    {
                        data.CpuTemp = fallbackTemp;
                    }
                }
            }
            else
            {
                // Fallback: use NVML for GPU and PerformanceCounter for CPU
                if (_nvml.IsAvailable())
                {
                    var nvmlData = _nvml.ReadSensors();
                    data.GpuTemp = nvmlData.GpuTemp;
                    data.GpuUsage = nvmlData.GpuUsage;
                    data.GpuPower = nvmlData.GpuPower;
                }

                if (_cpuUsage.IsAvailable())
                {
                    data.CpuUsage = _cpuUsage.GetCpuUsage();
                }
                
                // Try CPU temp fallback
                if (_cpuTemp.IsAvailable())
                {
                    var fallbackTemp = _cpuTemp.GetCpuTemp();
                    if (fallbackTemp > 0)
                    {
                        data.CpuTemp = fallbackTemp;
                    }
                }
            }

            // Always calculate overall temp as max of CPU, GPU, and any motherboard/VRM temps
            data.OverallTemp = Math.Max(data.OverallTemp, Math.Max(data.CpuTemp, data.GpuTemp));

            // Read FPS from RTSS - try multiple times if needed
            // Note: HWiNFO64 can display FPS in its OSD, but the FPS data comes from RTSS shared memory
            // Both HWiNFO64 and RTSS should be running for FPS monitoring to work
            var fps = 0.0;
            
            // Check if RTSS is available (required for FPS monitoring)
            if (_rtss.IsAvailable())
            {
                fps = _rtss.ReadFps();
                
                // Additional validation: reject low values that are likely false positives
                // Values 1-9 are almost never actual FPS (they're usually frame counters or other metrics)
                if (fps >= 1 && fps < 10)
                {
                    ErrorLogger.Log($"Rejecting low FPS value {fps} (likely false positive - frame counter or other metric)");
                    fps = 0; // Treat as no FPS
                }
                
                // If RTSS returned 0 or invalid, try reading again (sometimes RTSS needs a moment)
                if (fps == 0)
                {
                    System.Threading.Thread.Sleep(10); // Small delay
                    var retryFps = _rtss.ReadFps();
                    
                    // Validate retry result too
                    if (retryFps >= 1 && retryFps < 10)
                    {
                        ErrorLogger.Log($"Rejecting low retry FPS value {retryFps} (likely false positive)");
                        retryFps = 0;
                    }
                    
                    fps = retryFps;
                }
            }
            else
            {
                // RTSS not available - check if HWiNFO64 is running (user might have installed it for FPS)
                if (_hwinfo.IsAvailable())
                {
                    // HWiNFO64 is running but RTSS is not - FPS won't work without RTSS
                    // Log this once per minute to avoid spam
                    if (DateTime.Now.Second % 60 == 0)
                    {
                        ErrorLogger.Log("HWiNFO64 is running but RTSS is not available. FPS monitoring requires RTSS (RivaTuner Statistics Server). Please install and start RTSS for FPS monitoring.");
                    }
                }
            }
            
            // Detect if a game is running using multiple methods:
            // 1. RTSS reports FPS >= FpsMinThreshold (configurable, default 10)
            // 2. Game detection service (checks for fullscreen apps, high GPU usage, etc.)
            bool rtssDetected = fps >= appConfig.FpsMinThreshold;
            
            // Validate GPU usage before passing to game detection
            var validGpuUsage = ValidateValue(data.GpuUsage, 0, 100);
            var validFps = ValidateValue(fps, 0, 10000);
            
            bool gameDetected = _gameDetection.IsGameRunning(validGpuUsage, validFps);
            
            // IsGaming: Either RTSS detected FPS OR game detection service found a game
            data.IsGaming = rtssDetected || gameDetected;
            
            // Always update FPS estimation (it tracks frame timing internally)
            // This ensures we have a fallback if RTSS doesn't provide data
            var estimatedFps = _fpsEstimation.EstimateFps();
            
            // If game is detected but RTSS didn't provide FPS, use estimation
            if (data.IsGaming && fps == 0 && estimatedFps >= 1)
            {
                fps = estimatedFps;
                // Only log once to avoid spam
                if (!_wasGaming || (DateTime.Now.Second % 5 == 0))
                {
                    ErrorLogger.Log($"Using estimated FPS: {fps:F2} (RTSS not providing data)");
                }
            }
            
            // Reset estimation when transitioning from gaming to not gaming
            if (!data.IsGaming && _wasGaming)
            {
                _fpsEstimation.Reset();
            }
            
            _wasGaming = data.IsGaming;
            data.Fps = fps;

            // Calculate system power if not from PSU
            if (!data.HasPsuPower)
            {
                data.SystemPower = Math.Max(0, data.CpuPower + data.GpuPower + appConfig.OverheadWatts);
            }
            
            // Validate all values are in reasonable ranges and not NaN/Infinity
            data.CpuUsage = ValidateValue(data.CpuUsage, 0, 100);
            data.GpuUsage = validGpuUsage; // Use already validated value to avoid double validation
            data.CpuTemp = ValidateValue(data.CpuTemp, 0, 200);
            data.GpuTemp = ValidateValue(data.GpuTemp, 0, 200);
            data.OverallTemp = ValidateValue(data.OverallTemp, 0, 200);
            data.CpuPower = ValidateValue(data.CpuPower, 0, 1000);
            data.GpuPower = ValidateValue(data.GpuPower, 0, 1000);
            data.SystemPower = ValidateValue(data.SystemPower, 0, 2000);
            data.Fps = ValidateValue(data.Fps, 0, 10000);

            // Track failed readings for auto-debugging
            if (data.CpuTemp == 0 && data.GpuTemp == 0 && data.OverallTemp == 0)
            {
                _failedReadings++;
                
                // Auto-retry sensor reading if we have consecutive failures
                if (_failedReadings >= 5)
                {
                    ErrorLogger.Log($"Multiple sensor failures detected ({_failedReadings}). Attempting recovery...");
                    
                    // Try to re-read sensors with a small delay
                    Task.Run(() =>
                    {
                        Thread.Sleep(1000);
                        // Force a new read cycle by directly calling the method
                        try
                        {
                            // Create a dummy timer event
                            var timer = new System.Timers.Timer();
                            timer.Elapsed += OnTimerElapsed;
                            timer.Start();
                            Thread.Sleep(100);
                            timer.Stop();
                            timer.Dispose();
                        }
                        catch { }
                    });
                }
            }
            else
            {
                _failedReadings = 0; // Reset on success
            }

            _currentData = data;
            
            // Only fire event if data is valid
            if (ValidateSensorData(data))
            {
                DataUpdated?.Invoke(this, data);
            }
            else
            {
                ErrorLogger.Log("Invalid sensor data - not updating UI");
                // Still fire event with current data to keep UI responsive
                try
                {
                    DataUpdated?.Invoke(this, _currentData);
                }
                catch
                {
                    // Ignore event handler errors
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but don't crash
            ErrorLogger.Log("Sensor read error", ex);
            _failedReadings++;
            
            // Still fire event with empty data to keep UI responsive
            try
            {
                DataUpdated?.Invoke(this, _currentData);
            }
            catch
            {
                // Ignore event handler errors
            }
        }
    }
    
    private static double ValidateValue(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;
        return Math.Max(min, Math.Min(max, value));
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
                double.IsNaN(data.Fps) || double.IsInfinity(data.Fps) ||
                double.IsNaN(data.SystemPower) || double.IsInfinity(data.SystemPower))
            {
                return false;
            }
            
            // Check for reasonable ranges
            if (data.CpuUsage < 0 || data.CpuUsage > 100 ||
                data.GpuUsage < 0 || data.GpuUsage > 100 ||
                data.CpuTemp < 0 || data.CpuTemp > 200 ||
                data.GpuTemp < 0 || data.GpuTemp > 200 ||
                data.OverallTemp < 0 || data.OverallTemp > 200 ||
                data.Fps < 0 || data.Fps > 10000 ||
                data.SystemPower < 0 || data.SystemPower > 2000)
            {
                return false;
            }
            
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    public int GetFailedReadingsCount() => _failedReadings;

    public SensorData GetCurrentData() => _currentData;

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _hwinfo.Dispose();
            _rtss.Dispose();
            _nvml.Dispose();
            _cpuUsage.Dispose();
            _cpuTemp.Dispose();
            _disposed = true;
        }
    }
}

