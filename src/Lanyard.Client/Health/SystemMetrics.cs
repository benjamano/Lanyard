using System.IO;
using System.Runtime.InteropServices;

namespace Lanyard.Client.Health;

/// <summary>
/// Machine-wide CPU, memory and system-drive readings for the heartbeat, read straight from
/// Win32 so the client needs no performance-counter package. Every reading returns null rather
/// than throwing if it can't be taken.
/// </summary>
public sealed class SystemMetrics
{
    private ulong _lastIdle;
    private ulong _lastTotal;

    /// <summary>
    /// CPU busy percentage since the previous call. The first call has nothing to compare with
    /// and returns null.
    /// </summary>
    public double? SampleCpuPercent()
    {
        if (!GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime))
        {
            return null;
        }

        // Kernel time includes idle time, so kernel + user is the total.
        ulong idle = ToUInt64(idleTime);
        ulong total = ToUInt64(kernelTime) + ToUInt64(userTime);

        ulong previousIdle = _lastIdle;
        ulong previousTotal = _lastTotal;

        _lastIdle = idle;
        _lastTotal = total;

        if (previousTotal == 0 || total <= previousTotal)
        {
            return null;
        }

        double totalDelta = total - previousTotal;
        double idleDelta = idle - previousIdle;

        return Math.Clamp((1 - idleDelta / totalDelta) * 100, 0, 100);
    }

    public static double? MemoryPercent()
    {
        MEMORYSTATUSEX status = new() { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };

        return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : null;
    }

    /// <summary>Free and total bytes on the drive Windows is installed on, where the app and its caches live.</summary>
    public static (long? FreeBytes, long? TotalBytes) SystemDrive()
    {
        try
        {
            string? root = Path.GetPathRoot(Environment.SystemDirectory);

            if (string.IsNullOrEmpty(root))
            {
                return (null, null);
            }

            DriveInfo drive = new(root);

            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static ulong ToUInt64(FILETIME time) => ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
