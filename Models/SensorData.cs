namespace SideHUD.Models;

public class SensorData
{
    public double CpuUsage { get; set; }
    public double CpuTemp { get; set; }
    public double CpuPower { get; set; }
    public double GpuUsage { get; set; }
    public double GpuTemp { get; set; }
    public double GpuPower { get; set; }
    public double OverallTemp { get; set; }
    public double Fps { get; set; }
    public double SystemPower { get; set; }
    public bool HasPsuPower { get; set; }
    public bool IsGaming { get; set; }
}

