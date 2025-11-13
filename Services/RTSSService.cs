using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SideHUD.Models;

namespace SideHUD.Services;

public class RTSSService : IDisposable
{
    private const string MapFileName = "RTSSSharedMemory";
    private MemoryMappedFile? _mmf;
    private bool _disposed = false;
    private readonly HashSet<string> _loggedOsdFormats = new HashSet<string>();

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct RTSSSharedMemory
    {
        public uint dwSignature;
        public uint dwVersion;
        public uint dwTime0;
        public uint dwTime1;
        public uint dwOSDEntrySize;
        public uint dwOSDArrSize;
        public uint dwOSDFrame;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    private struct RTSSOSDEntry
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szOSD;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szOSDOwner;
    }

    public bool IsAvailable()
    {
        try
        {
            if (_mmf == null)
            {
                _mmf = MemoryMappedFile.OpenExisting(MapFileName, MemoryMappedFileRights.Read);
            }
            return _mmf != null;
        }
        catch
        {
            _mmf = null;
            return false;
        }
    }
    
    public string GetStatus()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("RTSS");
            if (processes.Length == 0)
            {
                processes = System.Diagnostics.Process.GetProcessesByName("RivaTunerStatisticsServer");
            }
            
            bool processRunning = processes.Length > 0;
            bool sharedMemoryAvailable = false;
            string memoryDetails = "";
            
            if (processRunning)
            {
                try
                {
                    if (_mmf == null)
                    {
                        _mmf = MemoryMappedFile.OpenExisting(MapFileName, MemoryMappedFileRights.Read);
                    }
                    
                    if (_mmf != null)
                    {
                        sharedMemoryAvailable = true;
                        using var accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                        var mem = ReadStruct<RTSSSharedMemory>(accessor, 0);
                        
                        if (mem.dwSignature == 0x52545353)
                        {
                            memoryDetails = $"\nRTSS v{mem.dwVersion}, EntrySize={mem.dwOSDEntrySize}, ArrSize={mem.dwOSDArrSize}, Frame={mem.dwOSDFrame}";
                        }
                        else
                        {
                            memoryDetails = $"\nRTSS signature invalid: 0x{mem.dwSignature:X8}";
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    sharedMemoryAvailable = false;
                }
                catch (Exception ex)
                {
                    memoryDetails = $"\nError reading RTSS memory: {ex.Message}";
                }
            }
            
            if (!processRunning)
            {
                return "RTSS (RivaTuner Statistics Server) is not running.\n\n" +
                       "To fix:\n" +
                       "1. Download and install RTSS from: https://www.guru3d.com/files-details/rtss-rivatuner-statistics-server-download.html\n" +
                       "2. Start RTSS\n" +
                       "3. Make sure OSD is enabled in RTSS settings";
            }
            
            if (!sharedMemoryAvailable)
            {
                return $"RTSS is running (Process ID: {processes[0].Id}) but Shared Memory is not accessible.{memoryDetails}\n\n" +
                       "To fix:\n" +
                       "1. Make sure RTSS is fully started\n" +
                       "2. Check RTSS settings for OSD/Shared Memory options\n" +
                       "3. Try restarting RTSS";
            }
            
            return $"RTSS is running and Shared Memory is accessible (Process ID: {processes[0].Id}){memoryDetails}";
        }
        catch (Exception ex)
        {
            return $"Error checking RTSS status: {ex.Message}";
        }
    }

    public double ReadFps()
    {
        if (_mmf == null && !IsAvailable())
            return 0;

        try
        {
            using var accessor = _mmf!.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var mem = ReadStruct<RTSSSharedMemory>(accessor, 0);

            if (mem.dwSignature != 0x52545353) // "RTSS"
                return 0;

            if (mem.dwOSDEntrySize == 0 || mem.dwOSDArrSize == 0)
                return 0;

            // RTSS OSD entries are at offset 0x2000 (8192 bytes) - this is the standard
            // But also try headerSize offset as fallback
            int headerSize = Marshal.SizeOf<RTSSSharedMemory>();
            int[] possibleOffsets = { 0x2000, headerSize, 0x1000, 0x3000 }; // Try multiple offsets
            int maxEntries = Math.Min(256, (int)(mem.dwOSDArrSize / Math.Max(1, mem.dwOSDEntrySize)));

            // Try reading from different offsets
            foreach (int baseOffset in possibleOffsets)
            {
                // Read all OSD entries and try to find FPS
                for (int i = 0; i < maxEntries; i++)
            {
                try
                {
                    int entryOffset = baseOffset + (i * (int)mem.dwOSDEntrySize);
                    
                    if (entryOffset < 0 || entryOffset >= (int)accessor.Capacity)
                        break;
                    
                    // Read the OSD entry
                    var entry = ReadStruct<RTSSOSDEntry>(accessor, entryOffset);

                    if (string.IsNullOrWhiteSpace(entry.szOSD))
                        continue;

                    var osdText = entry.szOSD.Trim();
                    
                    // Log first time we see each unique OSD format
                    if (!_loggedOsdFormats.Contains(osdText))
                    {
                        ErrorLogger.Log($"RTSS OSD[{i}]: '{osdText}' (Owner: '{entry.szOSDOwner ?? ""}')");
                        _loggedOsdFormats.Add(osdText);
                        if (_loggedOsdFormats.Count > 20)
                            _loggedOsdFormats.Clear();
                    }

                    // Try to parse FPS from this OSD entry
                    double fps = TryParseFps(osdText);
                    if (fps >= 10) // Accept FPS >= 10 (filters out false positives like frame counters)
                    {
                        ErrorLogger.Log($"✓ RTSS FPS found: {fps:F2} from entry {i} at offset 0x{baseOffset:X}: '{osdText}'");
                        return fps;
                    }
                }
                catch
                {
                    continue;
                }
            }
            } // End of offset loop

            // If no FPS found, try reading raw bytes as last resort
            // Sometimes the struct marshalling fails, so read bytes directly
            try
            {
                using var accessor2 = _mmf!.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                var mem2 = ReadStruct<RTSSSharedMemory>(accessor2, 0);
                
                if (mem2.dwSignature == 0x52545353 && mem2.dwOSDEntrySize > 0)
                {
                    // Try reading OSD entries as raw strings
                    foreach (int offset in possibleOffsets)
                    {
                        for (int i = 0; i < Math.Min(10, maxEntries); i++) // Limit to first 10 entries
                        {
                            try
                            {
                                int entryOffset = offset + (i * (int)mem2.dwOSDEntrySize);
                                if (entryOffset < 0 || entryOffset >= (int)accessor2.Capacity - 512)
                                    break;
                                
                                // Read raw bytes and convert to string
                                var bytes = new byte[512];
                                accessor2.ReadArray(entryOffset, bytes, 0, Math.Min(512, (int)accessor2.Capacity - entryOffset));
                                
                                // Find null-terminated string
                                int nullIndex = Array.IndexOf(bytes, (byte)0);
                                if (nullIndex > 0)
                                {
                                    var osdText = Encoding.UTF8.GetString(bytes, 0, nullIndex).Trim();
                                    if (!string.IsNullOrWhiteSpace(osdText) && osdText.Length > 0)
                                    {
                                        double fps = TryParseFps(osdText);
                                        if (fps >= 10) // Accept FPS >= 10 (filters out false positives)
                                        {
                                            ErrorLogger.Log($"✓ RTSS FPS found via raw byte read: {fps:F2} from '{osdText}'");
                                            return fps;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                continue;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore raw byte read errors
            }

            // If no FPS found, log all OSD entries we checked
            ErrorLogger.Log($"RTSS: Checked {maxEntries} OSD entries across {possibleOffsets.Length} offsets, no FPS value found");
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("RTSS read error", ex);
        }

        return 0;
    }

    private double TryParseFps(string osdText)
    {
        if (string.IsNullOrWhiteSpace(osdText))
            return 0;

        try
        {
            // Strategy 1: Regex patterns for common FPS formats
            var patterns = new[]
            {
                @"(\d+\.?\d*)\s*FPS",              // "123.4 FPS" or "123 FPS"
                @"FPS[:\s=]+(\d+\.?\d*)",          // "FPS: 123", "FPS 123", "FPS=123"
                @"(\d+\.?\d*)\s*fps",              // "123.4 fps" (lowercase)
                @"(\d+\.?\d*)\s*Hz",               // "123.4 Hz"
                @"(\d+\.?\d*)\s*hz",               // "123.4 hz"
                @"FPS\s*[:\s=]\s*(\d+\.?\d*)",     // "FPS : 123"
                @"(\d+\.?\d*)\s*FPS\s*",           // "123.4 FPS " (with trailing space)
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(osdText, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    var fpsStr = match.Groups[1].Value.Trim();
                    if (double.TryParse(fpsStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fps))
                    {
                        // Filter out false positives: FPS should be >= 10 for games (2 FPS is likely a frame counter or other metric)
                        // Also check for reasonable upper bound
                        if (fps >= 10 && fps <= 10000)
                        {
                            ErrorLogger.Log($"Parsed FPS via pattern '{pattern}': {fps} from '{osdText}'");
                            return fps;
                        }
                        else if (fps >= 1 && fps < 10)
                        {
                            // Log low values that we're filtering out
                            ErrorLogger.Log($"Filtered out low FPS value {fps} from '{osdText}' (likely not actual FPS)");
                        }
                    }
                }
            }

            // Strategy 2: Split and look for numbers
            var delimiters = new[] { ' ', '\n', '\r', '\t', ':', '=', '|', '/', '\\', '-', '_', '(', ')', '[', ']' };
            var parts = osdText.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var part in parts)
            {
                var cleaned = part.Trim();
                
                // Skip keywords
                if (cleaned.Equals("FPS", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.Equals("Hz", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.Equals("fps", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.Equals("hz", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Try parse as number
                if (double.TryParse(cleaned, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fps))
                {
                    // STRICT filtering: FPS should be >= 10 for games
                    // Reject values 1-9 as they're likely frame counters
                    if (fps >= 10 && fps <= 10000)
                    {
                        ErrorLogger.Log($"Parsed FPS from part '{cleaned}': {fps} from '{osdText}'");
                        return fps;
                    }
                    else if (fps >= 1 && fps < 10)
                    {
                        // Log low values that we're filtering out (especially 2)
                        ErrorLogger.Log($"REJECTED low FPS value {fps} from '{cleaned}' in '{osdText}' (likely frame counter, not actual FPS)");
                    }
                }

                // Skip extracting number from mixed string - too prone to false positives
                // This strategy was causing "2 FPS" false positives
            }

            // Strategy 3: Find first number in entire string (only if it looks like FPS)
            // Only use this if the text contains FPS-related keywords AND the number is reasonable
            if (osdText.IndexOf("FPS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                osdText.IndexOf("fps", StringComparison.OrdinalIgnoreCase) >= 0 ||
                osdText.IndexOf("Hz", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var firstNumber = Regex.Match(osdText, @"\d+\.?\d*");
                if (firstNumber.Success)
                {
                    if (double.TryParse(firstNumber.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fps))
                    {
                        // STRICT filtering: FPS should be >= 10 for games
                        // Reject values 1-9 as they're likely frame counters or other metrics
                        if (fps >= 10 && fps <= 10000)
                        {
                            ErrorLogger.Log($"Parsed FPS as first number (with FPS keyword): {fps} from '{osdText}'");
                            return fps;
                        }
                        else if (fps >= 1 && fps < 10)
                        {
                            // Log low values that we're filtering out (especially 2)
                            ErrorLogger.Log($"REJECTED low FPS value {fps} from '{osdText}' (likely frame counter, not actual FPS)");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log($"Error parsing FPS from '{osdText}'", ex);
        }

        return 0;
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
