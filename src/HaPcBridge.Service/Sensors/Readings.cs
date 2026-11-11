namespace HaPcBridge.Service.Sensors;

public sealed class GpuReading
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public double? Load { get; set; }
    public double? Temperature { get; set; }
    public double? Power { get; set; }
    public double? MemoryUsedGb { get; set; }
    public double? MemoryTotalGb { get; set; }
}

public sealed class HardwareReading
{
    public string? CpuName { get; set; }
    public double? CpuLoad { get; set; }
    public double? CpuTemp { get; set; }
    public double? CpuPower { get; set; }
    public List<GpuReading> Gpus { get; } = new();
    public double? PsuPower { get; set; }
    public string? PsuName { get; set; }
}
