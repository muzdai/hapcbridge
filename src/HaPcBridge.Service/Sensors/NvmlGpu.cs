using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HaPcBridge.Service.Sensors;

/// <summary>
/// NVIDIA Management Library (nvml.dll ships with the driver in System32).
/// Gives exact utilisation, temperature, board power and VRAM. Absent on non-NVIDIA systems — that's fine.
/// </summary>
internal sealed class NvmlGpu : IDisposable
{
    const string Dll = "nvml.dll";

    [StructLayout(LayoutKind.Sequential)] struct Utilization { public uint Gpu; public uint Memory; }
    [StructLayout(LayoutKind.Sequential)] struct Memory { public ulong Total; public ulong Free; public ulong Used; }

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")] static extern int Init();
    [DllImport(Dll, EntryPoint = "nvmlShutdown")] static extern int Shutdown();
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")] static extern int GetCount(out uint count);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int GetHandle(uint index, out IntPtr device);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)] static extern int GetName(IntPtr device, StringBuilder name, uint length);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")] static extern int GetUtilization(IntPtr device, out Utilization u);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")] static extern int GetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")] static extern int GetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")] static extern int GetMemoryInfo(IntPtr device, out Memory m);

    readonly List<(IntPtr Handle, string Name)> _devices = new();
    bool _initialized;

    public bool Available => _devices.Count > 0;

    public NvmlGpu(ILogger log)
    {
        try
        {
            if (Init() != 0) return;
            _initialized = true;
            if (GetCount(out var n) != 0) return;
            for (uint i = 0; i < n; i++)
            {
                if (GetHandle(i, out var h) != 0) continue;
                var sb = new StringBuilder(96);
                GetName(h, sb, (uint)sb.Capacity);
                _devices.Add((h, sb.ToString()));
            }
            if (Available) log.LogInformation("NVML: {Gpus}", string.Join(", ", _devices.Select(d => d.Name)));
        }
        catch (DllNotFoundException) { /* no NVIDIA driver */ }
        catch (EntryPointNotFoundException ex) { log.LogWarning("NVML too old: {Msg}", ex.Message); }
        catch (Exception ex) { log.LogWarning(ex, "NVML init failed"); }
    }

    public IEnumerable<GpuReading> Read()
    {
        foreach (var (h, name) in _devices)
        {
            var r = new GpuReading { Name = name, Vendor = "nvidia" };
            if (GetUtilization(h, out var u) == 0) r.Load = u.Gpu;
            if (GetTemperature(h, 0, out var t) == 0) r.Temperature = t;
            if (GetPowerUsage(h, out var mw) == 0) r.Power = mw / 1000.0;
            if (GetMemoryInfo(h, out var m) == 0) { r.MemoryUsedGb = m.Used / 1073741824.0; r.MemoryTotalGb = m.Total / 1073741824.0; }
            yield return r;
        }
    }

    public void Dispose()
    {
        if (_initialized) { try { Shutdown(); } catch { } _initialized = false; }
    }
}

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
