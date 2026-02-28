using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GruppenRemoteAgent.Diagnostics;

public class SystemInfo
{
    private readonly ILogger<SystemInfo> _logger;
    private readonly DateTimeOffset _startTime;
    private PerformanceCounter? _cpuCounter;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint DwLength;
        public uint DwMemoryLoad;
        public ulong UllTotalPhys;
        public ulong UllAvailPhys;
        public ulong UllTotalPageFile;
        public ulong UllAvailPageFile;
        public ulong UllTotalVirtual;
        public ulong UllAvailVirtual;
        public ulong UllAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public SystemInfo(ILogger<SystemInfo> logger)
    {
        _logger = logger;
        _startTime = DateTimeOffset.UtcNow;

        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // First call always returns 0
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not initialize CPU performance counter.");
            _cpuCounter = null;
        }
    }

    public long GetUptimeSeconds()
    {
        return (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds;
    }

    public double GetCpuUsage()
    {
        try
        {
            if (_cpuCounter is not null)
                return Math.Round(_cpuCounter.NextValue(), 1);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading CPU counter.");
        }
        return 0;
    }

    public double GetMemoryUsage()
    {
        try
        {
            var memInfo = new MEMORYSTATUSEX { DwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref memInfo))
            {
                return Math.Round(memInfo.DwMemoryLoad * 1.0, 1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading memory info.");
        }
        return 0;
    }

    public string GetHostname() => Environment.MachineName;

    public string GetOsInfo() => $"{Environment.OSVersion}";
}
