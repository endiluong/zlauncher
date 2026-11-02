using System.Text;
using LibreHardwareMonitor.Hardware;

namespace ZMonitor;

sealed class Snapshot
{
    public string CpuName = "CPU", GpuName = "GPU";
    public double? CpuLoad, CpuPower, CpuClockGHz, CpuTemp;
    public double? GpuLoad, GpuPower, GpuClockGHz, GpuTemp, VramUsedMb, VramTotalMb;
    public double RamUsedGb, RamTotalGb, RamLoad;
}

sealed class HardwareSensors : IDisposable
{
    readonly Computer _computer = new() { IsCpuEnabled = true, IsGpuEnabled = true };
    IHardware? _cpu, _gpu;

    public void Open()
    {
        _computer.Open();
        _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        // Prefer a discrete GPU over an iGPU.
        _gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia)
            ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
    }

    public Snapshot Read()
    {
        var s = new Snapshot();
        if (_cpu != null)
        {
            _cpu.Update();
            s.CpuName = ShortName(_cpu.Name);
            s.CpuLoad = Value(_cpu, SensorType.Load, "CPU Total");
            s.CpuPower = Value(_cpu, SensorType.Power, "CPU Package");
            s.CpuTemp = Value(_cpu, SensorType.Temperature, "CPU Package", "Core Max", "Core Average", "Core (Tctl/Tdie)");
            // Average core clock; on hybrid Intel parts use the P-cores, which is what games run on.
            var cores = Sensors(_cpu).Where(x => x.SensorType == SensorType.Clock && x.Name.Contains("Core #") && x.Value is > 0).ToList();
            if (cores.Any(x => x.Name.StartsWith("P-Core"))) cores = cores.Where(x => x.Name.StartsWith("P-Core")).ToList();
            var clocks = cores.Select(x => (double)x.Value!.Value).ToList();
            if (clocks.Count > 0) s.CpuClockGHz = clocks.Average() / 1000.0;
        }
        if (_gpu != null)
        {
            _gpu.Update();
            s.GpuName = ShortName(_gpu.Name);
            s.GpuLoad = Value(_gpu, SensorType.Load, "GPU Core", "D3D 3D");
            s.GpuPower = Value(_gpu, SensorType.Power, "GPU Package", "GPU PPT", "GPU Power", "GPU Core");
            s.GpuClockGHz = Value(_gpu, SensorType.Clock, "GPU Core") / 1000.0;
            s.GpuTemp = Value(_gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot", "GPU Edge");
            s.VramUsedMb = Value(_gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            s.VramTotalMb = Value(_gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        }

        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref mem))
        {
            s.RamTotalGb = mem.ullTotalPhys / 1073741824.0;
            s.RamUsedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
            s.RamLoad = mem.dwMemoryLoad;
        }
        return s;
    }

    static IEnumerable<ISensor> Sensors(IHardware hw) => hw.Sensors.Concat(hw.SubHardware.SelectMany(Sensors));

    static double? Value(IHardware hw, SensorType type, params string[] names)
    {
        var all = Sensors(hw).Where(x => x.SensorType == type).ToList();
        foreach (var n in names)
        {
            var v = all.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Value;
            if (v.HasValue && !float.IsNaN(v.Value)) return v.Value;
        }
        return null;
    }

    static string ShortName(string name) => name
        .Replace("Intel(R) Core(TM) ", "").Replace("Intel Core ", "").Replace("AMD Ryzen", "Ryzen")
        .Replace("AMD Radeon ", "Radeon ").Replace("NVIDIA GeForce ", "").Replace("(R)", "").Replace("(TM)", "").Trim();

    /// <summary>Writes every sensor LibreHardwareMonitor exposes, for picking names on new hardware.</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
            sb.AppendLine($"== {hw.HardwareType}: {hw.Name}");
            foreach (var x in Sensors(hw).OrderBy(x => x.SensorType).ThenBy(x => x.Index))
                sb.AppendLine($"  {x.SensorType,-12} {x.Name,-36} {x.Value}");
        }
        return sb.ToString();
    }

    public void Dispose() => _computer.Close();
}
