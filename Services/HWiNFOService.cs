using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using SideHUD.Models;

namespace SideHUD.Services;

public class HWiNFOService : IDisposable
{
    private const string MapFileName = "HWiNFO_SENSORS_MAP_FILE";
    private const string MutexName = "HWiNFO_SENSORS_MUTEX";
    private MemoryMappedFile? _mmf;
    private bool _disposed = false;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HWiNFOSensor
    {
        public uint dwSensorID;
        public uint dwSensorInst;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szSensorName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szSensorNameInst;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HWiNFORecord
    {
        public uint dwRecordID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szLabel;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szUnit;
        public double Value;
        public double ValueMin;
        public double ValueMax;
        public double ValueAvg;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HWiNFOSharedMem
    {
        public uint dwSignature;
        public uint dwVersion;
        public uint dwRevision;
        public long poll_time;
        public uint dwOffsetOfSensorSection;
        public uint dwOffsetOfReadingSection;
        public uint dwSizeOfSensorElement;
        public uint dwSizeOfReadingElement;
        public uint dwNumSensorElements;
        public uint dwNumReadingElements;
    }

    public bool IsAvailable()
    {
        try
        {
            // First check if HWiNFO64 process is running
            var processes = System.Diagnostics.Process.GetProcessesByName("HWiNFO64");
            if (processes.Length == 0)
            {
                // Also try HWiNFO (without 64)
                processes = System.Diagnostics.Process.GetProcessesByName("HWiNFO");
                if (processes.Length == 0)
                {
                    return false;
                }
            }
            
            // Process is running, now check if shared memory is available
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(MapFileName, MemoryMappedFileRights.Read);
                return true;
            }
            catch (FileNotFoundException)
            {
                // Shared memory not created yet - HWiNFO might be starting
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // Shared memory exists but we can't access it - might need admin rights
                ErrorLogger.Log("HWiNFO shared memory exists but access denied. Make sure Shared Memory Support is enabled in HWiNFO settings.");
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Error checking HWiNFO availability", ex);
            return false;
        }
    }
    
    public string GetStatus()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("HWiNFO64");
            if (processes.Length == 0)
            {
                processes = System.Diagnostics.Process.GetProcessesByName("HWiNFO");
            }
            
            bool processRunning = processes.Length > 0;
            bool sharedMemoryAvailable = false;
            
            if (processRunning)
            {
                try
                {
                    using var mmf = MemoryMappedFile.OpenExisting(MapFileName, MemoryMappedFileRights.Read);
                    sharedMemoryAvailable = mmf != null;
                }
                catch (FileNotFoundException)
                {
                    sharedMemoryAvailable = false;
                }
                catch (UnauthorizedAccessException)
                {
                    sharedMemoryAvailable = false;
                }
            }
            
            if (!processRunning)
            {
                return "HWiNFO64 is not running. Please start HWiNFO64.";
            }
            
            if (!sharedMemoryAvailable)
            {
                return "HWiNFO64 is running but Shared Memory is not available.\n\n" +
                       "To fix:\n" +
                       "1. Open HWiNFO64\n" +
                       "2. Go to Settings → Safety\n" +
                       "3. Enable 'Shared Memory Support'\n" +
                       "4. Make sure the Sensors window is open";
            }
            
            return $"HWiNFO64 is running and Shared Memory is accessible (Process ID: {processes[0].Id})";
        }
        catch (Exception ex)
        {
            return $"Error checking HWiNFO status: {ex.Message}";
        }
    }

    public SensorData ReadSensors()
    {
        var data = new SensorData();
        
        if (_mmf == null && !IsAvailable())
            return data;

        try
        {
            using var accessor = _mmf!.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var header = ReadStruct<HWiNFOSharedMem>(accessor, 0);

            if (header.dwSignature != 0x4F494E57) // "HWIN" in reverse
                return data;

            var sensorSize = header.dwSizeOfSensorElement;
            var readingSize = header.dwSizeOfReadingElement;
            var sensorOffset = header.dwOffsetOfSensorSection;
            var readingOffset = header.dwOffsetOfReadingSection;

            // Validate sizes to prevent buffer overruns
            if (sensorSize == 0 || readingSize == 0 || 
                header.dwNumSensorElements > 10000 || 
                header.dwNumReadingElements > 100000)
            {
                ErrorLogger.Log("Invalid HWiNFO header data - suspicious values detected");
                return data;
            }

            var sensors = new Dictionary<uint, string>();
            
            // Read sensors with bounds checking
            for (uint i = 0; i < header.dwNumSensorElements; i++)
            {
                try
                {
                    var offset = sensorOffset + (i * sensorSize);
                    if (offset < 0 || offset > (int)accessor.Capacity - (int)sensorSize)
                        continue;
                        
                    var sensor = ReadStruct<HWiNFOSensor>(accessor, (int)offset);
                    var key = (sensor.dwSensorID << 16) | sensor.dwSensorInst;
                    sensors[key] = (sensor.szSensorName ?? "") + " " + (sensor.szSensorNameInst ?? "");
                }
                catch
                {
                    // Skip invalid sensor entries
                    continue;
                }
            }

            // Read readings with bounds checking
            for (uint i = 0; i < header.dwNumReadingElements; i++)
            {
                try
                {
                    var offset = readingOffset + (i * readingSize);
                    if (offset < 0 || offset > (int)accessor.Capacity - (int)readingSize)
                        continue;
                        
                    var record = ReadStruct<HWiNFORecord>(accessor, (int)offset);
                    
                    var sensorKey = record.dwRecordID >> 16;
                    if (!sensors.ContainsKey(sensorKey))
                        continue;

                    var sensorName = sensors[sensorKey];
                    var label = record.szLabel ?? "";
                    var fullName = $"{sensorName} {label}";
                    var value = record.Value;
                    var unit = (record.szUnit ?? "").Trim();
                    
                    // Validate value is not NaN/Infinity
                    if (double.IsNaN(value) || double.IsInfinity(value))
                        continue;

                    // Match sensors by name contains (case-insensitive)
                    var nameLower = fullName.ToLowerInvariant();
                    var labelLower = label.ToLowerInvariant();
                    var sensorNameLower = sensorName.ToLowerInvariant();

                    // CPU Package temp - VERY AGGRESSIVE matching
                    // Check if it's a temperature reading first
                    bool isTemp = (unit == "°C" || unit == "C" || unit.Contains("°C") || unit.Contains("C") || 
                                  unit.Trim().Length == 0 || unit.ToLowerInvariant().Contains("celsius"));
                    
                    if (isTemp && value > 0 && value < 150) // Sanity check: reasonable CPU temp range
                    {
                        bool isCpuTemp = false;
                        
                        // Very broad CPU matching - check sensor name OR label
                        bool sensorIsCpu = sensorNameLower.Contains("cpu") && !sensorNameLower.Contains("gpu");
                        bool labelIsCpu = labelLower.Contains("cpu") && !labelLower.Contains("gpu");
                        
                        // Check for CPU temperature indicators
                        bool hasTempIndicator = labelLower.Contains("temp") || 
                                              labelLower.Contains("temperature") ||
                                              labelLower.Contains("package") ||
                                              labelLower.Contains("tdie") ||
                                              labelLower.Contains("tctl") ||
                                              labelLower.Contains("core") ||
                                              labelLower.Contains("die");
                        
                        // If sensor name suggests CPU and has temp indicator, it's likely CPU temp
                        if ((sensorIsCpu || labelIsCpu) && hasTempIndicator)
                        {
                            isCpuTemp = true;
                        }
                        // Also check for common CPU temp labels
                        else if (labelLower.Contains("cpu package") ||
                                labelLower.Contains("package") ||
                                labelLower.Contains("core max") ||
                                labelLower.Contains("tdie") ||
                                labelLower.Contains("tctl/tdie") ||
                                labelLower.Contains("cpu (tctl/tdie)") ||
                                (labelLower.Contains("core") && (labelLower.Contains("temp") || labelLower.Contains("temperature"))))
                        {
                            isCpuTemp = true;
                        }
                        // If sensor is CPU-related and value is in CPU temp range, assume it's CPU temp
                        else if (sensorIsCpu && value > 20 && value < 100)
                        {
                            isCpuTemp = true;
                        }
                        
                        if (isCpuTemp)
                        {
                            data.CpuTemp = Math.Max(data.CpuTemp, value);
                        }
                    }

                    // CPU Usage
                    if ((nameLower.Contains("cpu usage") || nameLower.Contains("cpu utilization") || nameLower.Contains("cpu total")) && unit == "%")
                    {
                        data.CpuUsage = Math.Max(data.CpuUsage, value);
                    }

                    // CPU Package Power
                    if (nameLower.Contains("cpu package power") && unit == "W")
                    {
                        data.CpuPower = value;
                    }

                    // GPU Temperature - try multiple patterns with flexible unit matching
                    bool isGpuTemp = false;
                    if (unit == "°C" || unit == "C" || unit.Contains("°C") || unit.Contains("C"))
                    {
                        if (labelLower.Contains("gpu temperature") ||
                            labelLower.Contains("gpu hot spot") ||
                            labelLower.Contains("gpu temp") ||
                            labelLower.Contains("temperature") ||
                            (sensorNameLower.Contains("gpu") && (labelLower.Contains("temperature") || labelLower.Contains("temp"))))
                        {
                            isGpuTemp = true;
                        }
                    }

                    if (isGpuTemp && value > 0)
                    {
                        data.GpuTemp = Math.Max(data.GpuTemp, value);
                    }

                    // GPU Core Load
                    if (nameLower.Contains("gpu core load") && unit == "%")
                    {
                        data.GpuUsage = value;
                    }

                    // GPU Power
                    if ((nameLower.Contains("gpu board power") || nameLower.Contains("gpu power")) && unit == "W")
                    {
                        data.GpuPower = value;
                    }

                    // Motherboard/VRM temps - flexible unit matching
                    if ((unit == "°C" || unit == "C" || unit.Contains("°C") || unit.Contains("C")) && 
                        (nameLower.Contains("motherboard") || nameLower.Contains("vrm")) && value > 0)
                    {
                        data.OverallTemp = Math.Max(data.OverallTemp, value);
                    }

                    // PSU Power
                    if (nameLower.Contains("psu") && nameLower.Contains("power") && unit == "W")
                    {
                        data.SystemPower = value;
                        data.HasPsuPower = true;
                    }
                }
                catch
                {
                    // Skip invalid reading entries
                    continue;
                }
            }

            // Calculate overall temp - ensure it's always at least the max of CPU and GPU
            // This handles cases where motherboard/VRM temps aren't found
            var maxCpuGpu = Math.Max(data.CpuTemp, data.GpuTemp);
            data.OverallTemp = Math.Max(data.OverallTemp, maxCpuGpu);
            
            // Safety: if OverallTemp is still 0 but we have CPU or GPU temp, use that
            if (data.OverallTemp == 0 && maxCpuGpu > 0)
            {
                data.OverallTemp = maxCpuGpu;
            }

        }
        catch (Exception ex)
        {
            ErrorLogger.Log("HWiNFO read error", ex);
        }

        return data;
    }

    public List<string> ListAllSensors()
    {
        var list = new List<string>();
        
        if (_mmf == null && !IsAvailable())
            return list;

        try
        {
            using var accessor = _mmf!.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var header = ReadStruct<HWiNFOSharedMem>(accessor, 0);

            if (header.dwSignature != 0x4F494E57)
                return list;

            var sensorSize = header.dwSizeOfSensorElement;
            var readingSize = header.dwSizeOfReadingElement;
            var sensorOffset = header.dwOffsetOfSensorSection;
            var readingOffset = header.dwOffsetOfReadingSection;

            var sensors = new Dictionary<uint, string>();
            
            for (uint i = 0; i < header.dwNumSensorElements; i++)
            {
                var offset = sensorOffset + (i * sensorSize);
                var sensor = ReadStruct<HWiNFOSensor>(accessor, (int)offset);
                var key = (sensor.dwSensorID << 16) | sensor.dwSensorInst;
                sensors[key] = sensor.szSensorName + " " + sensor.szSensorNameInst;
            }

            for (uint i = 0; i < header.dwNumReadingElements; i++)
            {
                var offset = readingOffset + (i * readingSize);
                var record = ReadStruct<HWiNFORecord>(accessor, (int)offset);
                
                var sensorKey = record.dwRecordID >> 16;
                if (!sensors.ContainsKey(sensorKey))
                    continue;

                var sensorName = sensors[sensorKey];
                var fullName = $"{sensorName} {record.szLabel} [{record.szUnit}] = {record.Value}";
                list.Add(fullName);
            }
        }
        catch
        {
        }

        return list;
    }

    private static T ReadStruct<T>(MemoryMappedViewAccessor accessor, int offset) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var bytes = new byte[size];
        accessor.ReadArray(offset, bytes, 0, size);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _mmf?.Dispose();
            _disposed = true;
        }
    }
}

