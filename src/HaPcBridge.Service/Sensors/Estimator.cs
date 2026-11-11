using HaPcBridge.Common;

namespace HaPcBridge.Service.Sensors;

/// <summary>Pure calculations on sensor readings (no hardware access, unit-tested on any OS).</summary>
public static class Estimator
{
    /// <summary>The GPU reported as "the" GPU: by name if configured, else NVIDIA, else the card with the most VRAM.</summary>
    public static GpuReading? PrimaryGpu(IReadOnlyList<GpuReading> gpus, string? preferred)
    {
        if (gpus.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = gpus.FirstOrDefault(g => g.Name.Contains(preferred.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return gpus.FirstOrDefault(g => g.Vendor == "nvidia")
               ?? gpus.OrderByDescending(g => g.MemoryTotalGb ?? 0).ThenByDescending(g => g.Power ?? 0).First();
    }

    /// <summary>
    /// Real wall power from a PSU with telemetry (Corsair HXi/RMi, NZXT …) when present;
    /// otherwise (CPU package + discrete GPUs) / PSU efficiency + baseline for the rest of the system.
    /// </summary>
    public static (double? Watts, string Source) TotalPower(HardwareReading hw, PowerConfig cfg)
    {
        if (hw.PsuPower is > 0) return (hw.PsuPower, $"psu: {hw.PsuName}");

        // An iGPU's power is already part of the CPU package, so only discrete cards are added.
        var gpuW = hw.Gpus.Where(g => g.Vendor == "nvidia" || (g.MemoryTotalGb ?? 0) >= 2).Sum(g => g.Power ?? 0);
        if (hw.CpuPower is null && gpuW == 0) return (null, "unavailable");

        var dc = (hw.CpuPower ?? 0) + gpuW;
        return (dc / cfg.PsuEfficiency + cfg.BaselineWatts, "estimate");
    }

    public static double? ToDisplayTemperature(double? celsius, bool fahrenheit) =>
        celsius is null ? null : fahrenheit ? celsius * 9 / 5 + 32 : celsius;
}
