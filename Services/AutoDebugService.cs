using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SideHUD.Models;

namespace SideHUD.Services;

/// <summary>
/// Auto-debug service that automatically detects and fixes issues
/// </summary>
public class AutoDebugService : IDisposable
{
    private readonly ConfigurationService _configService;
    private readonly SensorAggregatorService? _sensorService;
    private System.Timers.Timer? _healthCheckTimer;
    private bool _disposed = false;
    
    // Health tracking
    private int _consecutiveSensorFailures = 0;
    private int _consecutiveFpsFailures = 0;
    private DateTime _lastSuccessfulSensorRead = DateTime.Now;
    private DateTime _lastSuccessfulFpsRead = DateTime.Now;
    private bool _sensorServiceRestarted = false;
    
    // Auto-fix attempts tracking
    private int _sensorRestartAttempts = 0;
    private int _rtssRestartAttempts = 0;
    private const int MAX_RESTART_ATTEMPTS = 3;
    
    public event EventHandler<string>? AutoFixApplied;
    public event EventHandler<string>? IssueDetected;

    public AutoDebugService(ConfigurationService configService, SensorAggregatorService? sensorService = null)
    {
        _configService = configService;
        _sensorService = sensorService;
        
        // Start health check timer (every 5 seconds)
        _healthCheckTimer = new System.Timers.Timer(5000);
        _healthCheckTimer.Elapsed += OnHealthCheck;
        _healthCheckTimer.AutoReset = true;
        _healthCheckTimer.Start();
    }

    private void OnHealthCheck(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            var config = _configService.GetConfig();
            
            // Check sensor health
            CheckSensorHealth(config);
            
            // Check FPS health
            CheckFpsHealth(config);
            
            // Check service availability
            CheckServiceAvailability(config);
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error in auto-debug health check", ex);
        }
    }

    private void CheckSensorHealth(AppConfig config)
    {
        if (!config.EnableAutoDebug)
            return;

        var timeSinceLastRead = DateTime.Now - _lastSuccessfulSensorRead;
        
        // If no successful read in 10 seconds, there's an issue
        if (timeSinceLastRead.TotalSeconds > 10)
        {
            _consecutiveSensorFailures++;
            
            if (_consecutiveSensorFailures >= config.AutoDebugThreshold)
            {
                IssueDetected?.Invoke(this, "Sensor readings have stopped. Attempting auto-fix...");
                AutoFixSensorIssues(config);
            }
        }
        else
        {
            // Reset failure counter on success
            if (_consecutiveSensorFailures > 0)
            {
                _consecutiveSensorFailures = 0;
                _sensorRestartAttempts = 0;
            }
        }
    }

    private void CheckFpsHealth(AppConfig config)
    {
        if (!config.EnableAutoDebug || !config.ShowFpsOverlay)
            return;

        var timeSinceLastFps = DateTime.Now - _lastSuccessfulFpsRead;
        
        // If no FPS read in 30 seconds while gaming, there might be an issue
        // But we don't want to spam fixes if user isn't gaming
        if (timeSinceLastFps.TotalSeconds > 30 && _consecutiveFpsFailures > 5)
        {
            _consecutiveFpsFailures++;
            
            if (_consecutiveFpsFailures >= 10)
            {
                IssueDetected?.Invoke(this, "FPS readings have stopped. Attempting auto-fix...");
                AutoFixFpsIssues(config);
            }
        }
    }

    private void CheckServiceAvailability(AppConfig config)
    {
        if (!config.EnableAutoDebug)
            return;

        try
        {
            // Check HWiNFO availability
            var hwinfo = new HWiNFOService();
            bool hwinfoAvailable = hwinfo.IsAvailable();
            
            if (!hwinfoAvailable && _sensorRestartAttempts < MAX_RESTART_ATTEMPTS)
            {
                // Check if process is running but shared memory isn't available
                var processes = System.Diagnostics.Process.GetProcessesByName("HWiNFO64");
                if (processes.Length == 0)
                {
                    processes = System.Diagnostics.Process.GetProcessesByName("HWiNFO");
                }
                
                if (processes.Length > 0)
                {
                    // Process is running but shared memory not available
                    IssueDetected?.Invoke(this, "HWiNFO64 is running but Shared Memory is not accessible. Please enable Shared Memory Support in HWiNFO settings.");
                }
                else
                {
                    // Process not running - might start soon, try to reconnect
                    Task.Run(() =>
                    {
                        Thread.Sleep(3000); // Wait a bit longer
                        if (hwinfo.IsAvailable())
                        {
                            AutoFixApplied?.Invoke(this, "HWiNFO reconnected successfully");
                            _sensorRestartAttempts = 0;
                        }
                    });
                }
            }
            else if (hwinfoAvailable)
            {
                _sensorRestartAttempts = 0; // Reset on success
            }
            
            // Check RTSS availability
            var rtss = new RTSSService();
            bool rtssAvailable = rtss.IsAvailable();
            
            if (!rtssAvailable && _rtssRestartAttempts < MAX_RESTART_ATTEMPTS)
            {
                // Check if process is running but shared memory isn't available
                var processes = System.Diagnostics.Process.GetProcessesByName("RTSS");
                if (processes.Length == 0)
                {
                    processes = System.Diagnostics.Process.GetProcessesByName("RivaTunerStatisticsServer");
                }
                
                if (processes.Length > 0)
                {
                    // Process is running but shared memory not available
                    IssueDetected?.Invoke(this, "RTSS is running but Shared Memory is not accessible. Make sure RTSS is fully started and OSD is enabled.");
                }
                else
                {
                    // Process not running - might start soon, try to reconnect
                    Task.Run(() =>
                    {
                        Thread.Sleep(3000); // Wait a bit longer
                        if (rtss.IsAvailable())
                        {
                            AutoFixApplied?.Invoke(this, "RTSS reconnected successfully");
                            _rtssRestartAttempts = 0;
                        }
                    });
                }
            }
            else if (rtssAvailable)
            {
                _rtssRestartAttempts = 0; // Reset on success
            }
            
            rtss.Dispose();
            
            // Check NVML availability
            NVMLService? nvml = null;
            try
            {
                nvml = new NVMLService();
                bool nvmlAvailable = nvml.IsAvailable();
                if (!nvmlAvailable)
                {
                    // Check if NVIDIA GPU is present but NVML not working
                    var processes = System.Diagnostics.Process.GetProcessesByName("nvcontainer");
                    if (processes.Length == 0)
                    {
                        // NVIDIA services might not be running
                        IssueDetected?.Invoke(this, "NVML not available. Make sure NVIDIA drivers are installed and NVIDIA services are running.");
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogger.Log("Error checking NVML availability", ex);
            }
            finally
            {
                nvml?.Dispose();
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error checking service availability", ex);
        }
    }

    private void AutoFixSensorIssues(AppConfig config)
    {
        if (_sensorRestartAttempts >= MAX_RESTART_ATTEMPTS)
        {
            ErrorLogger.Log("Max sensor restart attempts reached. Manual intervention may be required.");
            return;
        }

        _sensorRestartAttempts++;
        
        try
        {
            // Strategy 1: Restart sensor service
            if (_sensorService != null && !_sensorServiceRestarted)
            {
                Task.Run(() =>
                {
                    try
                    {
                        _sensorService.Stop();
                        Thread.Sleep(1000);
                        _sensorService.Start();
                        _sensorServiceRestarted = true;
                        AutoFixApplied?.Invoke(this, "Sensor service restarted");
                        ErrorLogger.Log("Auto-fix: Sensor service restarted");
                    }
                    catch (Exception ex)
                    {
                        ErrorLogger.Log("Error restarting sensor service", ex);
                    }
                });
            }
            
            // Strategy 2: Check if HWiNFO is running
            var hwinfoProcesses = Process.GetProcessesByName("HWiNFO64");
            if (hwinfoProcesses.Length == 0)
            {
                IssueDetected?.Invoke(this, "HWiNFO64 is not running. Please start HWiNFO64 and enable Shared Memory.");
            }
            
            // Strategy 3: Try to reconnect to services
            Task.Run(() =>
            {
                Thread.Sleep(2000);
                var hwinfo = new HWiNFOService();
                if (hwinfo.IsAvailable())
                {
                    AutoFixApplied?.Invoke(this, "HWiNFO connection restored");
                    _consecutiveSensorFailures = 0;
                    _sensorRestartAttempts = 0;
                }
            });
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error in auto-fix sensor issues", ex);
        }
    }

    private void AutoFixFpsIssues(AppConfig config)
    {
        if (_rtssRestartAttempts >= MAX_RESTART_ATTEMPTS)
        {
            ErrorLogger.Log("Max RTSS restart attempts reached. Manual intervention may be required.");
            return;
        }

        _rtssRestartAttempts++;
        
        try
        {
            // Strategy 1: Check if RTSS is running
            var rtssProcesses = Process.GetProcessesByName("RTSS");
            if (rtssProcesses.Length == 0)
            {
                IssueDetected?.Invoke(this, "RTSS is not running. Please start RivaTuner Statistics Server.");
            }
            
            // Strategy 2: Try to reconnect to RTSS
            Task.Run(() =>
            {
                Thread.Sleep(2000);
                var rtss = new RTSSService();
                if (rtss.IsAvailable())
                {
                    AutoFixApplied?.Invoke(this, "RTSS connection restored");
                    _consecutiveFpsFailures = 0;
                    _rtssRestartAttempts = 0;
                }
                rtss.Dispose();
            });
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error in auto-fix FPS issues", ex);
        }
    }

    public void RecordSuccessfulSensorRead()
    {
        _lastSuccessfulSensorRead = DateTime.Now;
        _consecutiveSensorFailures = 0;
        _sensorServiceRestarted = false;
    }

    public void RecordSuccessfulFpsRead()
    {
        _lastSuccessfulFpsRead = DateTime.Now;
        _consecutiveFpsFailures = 0;
    }

    public void RecordFailedSensorRead()
    {
        _consecutiveSensorFailures++;
    }

    public void RecordFailedFpsRead()
    {
        _consecutiveFpsFailures++;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _healthCheckTimer?.Stop();
            _healthCheckTimer?.Dispose();
            _disposed = true;
        }
    }
}

