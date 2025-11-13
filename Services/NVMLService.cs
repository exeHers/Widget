using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using SideHUD.Models;

namespace SideHUD.Services;

public class NVMLService : IDisposable
{
    private const string DllName = "nvml.dll";
    private bool _initialized = false;
    private IntPtr _device = IntPtr.Zero;

    [DllImport(DllName, EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit_v2();

    [DllImport(DllName, EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport(DllName, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandleByIndex_v2(uint index, ref IntPtr device);

    [DllImport(DllName, EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int NvmlDeviceGetTemperature(IntPtr device, int sensorType, ref uint temp);

    [DllImport(DllName, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int NvmlDeviceGetUtilizationRates(IntPtr device, ref NvmlUtilization utilization);

    [DllImport(DllName, EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int NvmlDeviceGetPowerUsage(IntPtr device, ref uint power);

    [DllImport(DllName, EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int NvmlDeviceGetCount_v2(ref uint deviceCount);

    [DllImport(DllName, EntryPoint = "nvmlDeviceGetName")]
    private static extern int NvmlDeviceGetName(IntPtr device, StringBuilder name, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string dllToLoad);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint gpu;
        public uint memory;
    }

    private const int NVML_TEMPERATURE_GPU = 0;

    public bool IsAvailable()
    {
        try
        {
            // First check if NVIDIA GPU is present
            if (!IsNvidiaGpuPresent())
            {
                return false;
            }
            
            // Check if nvml.dll exists
            if (!IsNvmlDllPresent())
            {
                ErrorLogger.Log("NVML: nvml.dll not found. NVIDIA drivers may not be installed.");
                return false;
            }
            
            if (!_initialized)
            {
                var result = NvmlInit_v2();
                if (result != 0)
                {
                    ErrorLogger.Log($"NVML initialization failed: {result} (0x{result:X})");
                    return false;
                }

                _initialized = true;

                // Get device handle
                var device = IntPtr.Zero;
                result = NvmlDeviceGetHandleByIndex_v2(0, ref device);
                if (result != 0)
                {
                    NvmlShutdown();
                    _initialized = false;
                    ErrorLogger.Log($"NVML device handle failed: {result} (0x{result:X})");
                    return false;
                }

                _device = device;
            }

            return true;
        }
        catch (DllNotFoundException)
        {
            ErrorLogger.Log("NVML: nvml.dll not found. Make sure NVIDIA drivers are installed.");
            return false;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("NVML availability check error", ex);
            return false;
        }
    }
    
    private bool IsNvidiaGpuPresent()
    {
        try
        {
            // Check for NVIDIA GPU in WMI
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_VideoController WHERE Name LIKE '%NVIDIA%'");
            var results = searcher.Get();
            return results.Count > 0;
        }
        catch
        {
            // Fallback: assume NVIDIA GPU might be present
            return true;
        }
    }
    
    private bool IsNvmlDllPresent()
    {
        try
        {
            // Check common NVIDIA driver paths
            var nvidiaPaths = new[]
            {
                @"C:\Windows\System32\nvml.dll",
                @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll",
                Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\nvml.dll"
            };
            
            foreach (var path in nvidiaPaths)
            {
                if (File.Exists(path))
                    return true;
            }
            
            // Try to load the DLL to see if it's accessible
            try
            {
                var handle = LoadLibrary("nvml.dll");
                if (handle != IntPtr.Zero)
                {
                    FreeLibrary(handle);
                    return true;
                }
            }
            catch
            {
                // DLL not loadable
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    public string GetStatus()
    {
        try
        {
            // Check if NVIDIA GPU is present
            bool nvidiaGpuPresent = IsNvidiaGpuPresent();
            bool nvmlDllPresent = IsNvmlDllPresent();
            bool nvmlInitialized = _initialized;
            
            if (!nvidiaGpuPresent)
            {
                return "No NVIDIA GPU detected.\n\n" +
                       "NVML requires an NVIDIA GPU with NVIDIA drivers installed.";
            }
            
            if (!nvmlDllPresent)
            {
                return "NVIDIA GPU detected but nvml.dll not found.\n\n" +
                       "To fix:\n" +
                       "1. Make sure NVIDIA drivers are installed\n" +
                       "2. Download latest drivers from: https://www.nvidia.com/drivers\n" +
                       "3. Restart your computer after installation";
            }
            
            if (!nvmlInitialized)
            {
                try
                {
                    var result = NvmlInit_v2();
                    if (result != 0)
                    {
                        return $"NVML DLL found but initialization failed (Error: 0x{result:X}).\n\n" +
                               "This usually means:\n" +
                               "• NVIDIA drivers are outdated\n" +
                               "• GPU is not properly detected\n" +
                               "• Try updating NVIDIA drivers";
                    }
                    _initialized = true;
                    
                    // Get device handle
                    var device = IntPtr.Zero;
                    result = NvmlDeviceGetHandleByIndex_v2(0, ref device);
                    if (result != 0)
                    {
                        NvmlShutdown();
                        _initialized = false;
                        return $"NVML initialized but device handle failed (Error: 0x{result:X}).\n" +
                               "GPU may not be properly detected.";
                    }
                    _device = device;
                }
                catch (Exception ex)
                {
                    return $"NVML initialization error: {ex.Message}";
                }
            }
            
            // Try to get GPU count
            uint deviceCount = 0;
            try
            {
                var result = NvmlDeviceGetCount_v2(ref deviceCount);
                if (result == 0 && deviceCount > 0)
                {
                    // Try to get first GPU name
                    try
                    {
                        var name = new StringBuilder(64);
                        result = NvmlDeviceGetName(_device, name, 64);
                        if (result == 0)
                        {
                            return $"NVML is working correctly.\n" +
                                   $"NVIDIA GPU detected: {name}\n" +
                                   $"Device count: {deviceCount}";
                        }
                    }
                    catch
                    {
                        // Ignore errors getting device name
                    }
                    
                    return $"NVML is working correctly.\n" +
                           $"NVIDIA GPU(s) detected: {deviceCount} device(s)";
                }
                else if (result != 0)
                {
                    return $"NVML initialized but device count query failed (Error: 0x{result:X}).\n" +
                           "This may indicate driver issues.";
                }
            }
            catch (Exception ex)
            {
                return $"NVML available but query error: {ex.Message}";
            }
            
            return "NVML is available but could not query GPU information.";
        }
        catch (Exception ex)
        {
            return $"Error checking NVML status: {ex.Message}";
        }
    }

    public SensorData ReadSensors()
    {
        var data = new SensorData();

        if (!IsAvailable())
            return data;

        try
        {
            // Temperature
            uint temp = 0;
            if (NvmlDeviceGetTemperature(_device, NVML_TEMPERATURE_GPU, ref temp) == 0)
                data.GpuTemp = temp;

            // Utilization
            var util = new NvmlUtilization();
            if (NvmlDeviceGetUtilizationRates(_device, ref util) == 0)
                data.GpuUsage = util.gpu;

            // Power (in milliwatts, convert to watts)
            uint powerMw = 0;
            if (NvmlDeviceGetPowerUsage(_device, ref powerMw) == 0)
                data.GpuPower = powerMw / 1000.0;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("NVML read error", ex);
        }

        return data;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try
            {
                NvmlShutdown();
            }
            catch
            {
            }
            _initialized = false;
            _device = IntPtr.Zero;
        }
    }
}

