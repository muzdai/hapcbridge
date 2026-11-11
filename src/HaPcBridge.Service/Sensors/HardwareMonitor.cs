using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;

namespace HaPcBridge.Service.Sensors;

/// <summary>
/// CPU, AMD/Intel GPU and PSU telemetry through LibreHardwareMonitor (ADL for AMD, IGCL/D3D for Intel, NvAPI for NVIDIA fallback).
/// NVIDIA cards are read through NVML first because it is more precise.
/// </summary>
internal sealed class HardwareMonitor : IDisposable
{
    readonly ILogger _log;
    readonly Computer _computer;
    readonly NvmlGpu _nvml;
    bool _opened;

    public HardwareMonitor(ILogger log)
    {
        _log = log;
        _nvml = new NvmlGpu(log);
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsPsuEnabled = true,
            IsMotherboardEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsMemoryEnabled = false,
            IsControllerEnabled = false,
        };
        try
        {
            _computer.Open();
            _opened = true;
            foreach (var hw in _computer.Hardware)
                _log.LogInformation("LHM hardware: {Type} {Name}", hw.HardwareType, hw.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LibreHardwareMonitor failed to open (is the PawnIO driver installed?)");
        }
    }

    public HardwareReading Read()
    {
        var r = new HardwareReading();
        if (_opened)
        {
            foreach (var hw in _computer.Hardware)
            {
                try { Update(hw); } catch { continue; }
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        ReadCpu(hw, r);
                        break;
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        r.Gpus.Add(ReadGpu(hw, hw.HardwareType == HardwareType.GpuAmd ? "amd" : "intel"));
                        break;
                    case HardwareType.GpuNvidia when !_nvml.Available:
                        r.Gpus.Add(ReadGpu(hw, "nvidia"));
                        break;
                    case HardwareType.Psu:
                        ReadPsu(hw, r);
                        break;
                }
            }
        }

        if (_nvml.Available) r.Gpus.InsertRange(0, _nvml.Read());
        return r;
    }

    static void Update(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) sub.Update();
    }

    static IEnumerable<ISensor> All(IHardware hw) => hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors));

    static double? Find(IHardware hw, SensorType type, params string[] names)
    {
        var sensors = All(hw).Where(s => s.SensorType == type && s.Value.HasValue).ToList();
        foreach (var n in names)
        {
            var s = sensors.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s.Value;
        }
        return null;
    }

    static void ReadCpu(IHardware hw, HardwareReading r)
    {
        // Several CPU packages (dual-socket) → average load, max temp, sum power.
        var load = Find(hw, SensorType.Load, "CPU Total");
        var temp = Find(hw, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core Max", "Core Average")
                   ?? All(hw).Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).Select(s => (double?)s.Value).Max();
        var power = Find(hw, SensorType.Power, "Package", "CPU Package");

        r.CpuName ??= hw.Name;
        if (load.HasValue) r.CpuLoad = r.CpuLoad.HasValue ? (r.CpuLoad + load) / 2 : load;
        if (temp.HasValue) r.CpuTemp = r.CpuTemp.HasValue ? Math.Max(r.CpuTemp.Value, temp.Value) : temp;
        if (power.HasValue) r.CpuPower = (r.CpuPower ?? 0) + power;
    }

    static GpuReading ReadGpu(IHardware hw, string vendor)
    {
        var used = Find(hw, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        var total = Find(hw, SensorType.SmallData, "GPU Memory Total");
        return new GpuReading
        {
            Name = hw.Name,
            Vendor = vendor,
            Load = Find(hw, SensorType.Load, "GPU Core", "D3D 3D"),
            Temperature = Find(hw, SensorType.Temperature, "GPU Core", "GPU Hot Spot"),
            Power = Find(hw, SensorType.Power, "GPU Package", "GPU Power", "GPU Board Power", "GPU Core"),
            MemoryUsedGb = used / 1024.0,
            MemoryTotalGb = total / 1024.0,
        };
    }

    static void ReadPsu(IHardware hw, HardwareReading r)
    {
        // Corsair HXi/RMi, NZXT etc. report input (wall) and output power; input is the largest value.
        var powers = All(hw).Where(s => s.SensorType == SensorType.Power && s.Value is > 0).ToList();
        if (powers.Count == 0) return;
        var input = powers.FirstOrDefault(s => s.Name.Contains("input", StringComparison.OrdinalIgnoreCase))
                    ?? powers.FirstOrDefault(s => s.Name.Contains("total", StringComparison.OrdinalIgnoreCase))
                    ?? powers.MaxBy(s => s.Value);
        r.PsuPower = (r.PsuPower ?? 0) + input!.Value;
        r.PsuName ??= hw.Name;
    }

    public void Dispose()
    {
        if (_opened) { try { _computer.Close(); } catch { } _opened = false; }
        _nvml.Dispose();
    }
}
