using System;
using System.Management;

namespace SideHUD.Services;

public class CpuTempService : IDisposable
{

    public bool IsAvailable()
    {
        try
        {
            // Try to query WMI for CPU temperature
            using var searcher = new ManagementObjectSearcher(
                "root\\WMI",
                "SELECT * FROM MSAcpi_ThermalZoneTemperature"
            );
            var collection = searcher.Get();
            return collection.Count > 0;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("CPU Temp service unavailable", ex);
            return false;
        }
    }

    public double GetCpuTemp()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\WMI",
                "SELECT * FROM MSAcpi_ThermalZoneTemperature"
            );
            
            double maxTemp = 0;
            foreach (ManagementObject obj in searcher.Get())
            {
                try
                {
                    var temp = Convert.ToDouble(obj["CurrentTemperature"]);
                    // WMI returns temperature in tenths of Kelvin, convert to Celsius
                    var tempC = (temp / 10.0) - 273.15;
                    if (tempC > 0 && tempC < 150) // Sanity check
                    {
                        maxTemp = Math.Max(maxTemp, tempC);
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogger.Log("CPU Temp WMI entry error", ex);
                    // Skip invalid entries
                }
            }
            
            return maxTemp;
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("CPU Temp read error", ex);
            return 0;
        }
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}

