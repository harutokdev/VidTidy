using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LocalMediaAI.Infrastructure;

/// <summary>Cumulative counters for one physical disk since boot, as reported by the disk driver.</summary>
public sealed record DiskPerformanceSnapshot(
    int DiskNumber,
    long BytesRead,
    long BytesWritten,
    long ReadCount,
    long WriteCount,
    TimeSpan IdleTime,
    DateTimeOffset QueryTime)
{
    /// <summary>Activity between two snapshots of the same disk. Busy time is wall time minus driver idle time.</summary>
    public DiskActivity Since(DiskPerformanceSnapshot earlier)
    {
        if (earlier.DiskNumber != DiskNumber) throw new ArgumentException("Snapshots belong to different disks.", nameof(earlier));
        var wall = QueryTime - earlier.QueryTime;
        var idle = IdleTime - earlier.IdleTime;
        var busy = wall - idle;
        if (busy < TimeSpan.Zero) busy = TimeSpan.Zero;
        return new DiskActivity(DiskNumber, BytesRead - earlier.BytesRead, BytesWritten - earlier.BytesWritten,
            ReadCount - earlier.ReadCount, WriteCount - earlier.WriteCount, wall, busy);
    }
}

public sealed record DiskActivity(int DiskNumber, long BytesRead, long BytesWritten, long ReadCount, long WriteCount, TimeSpan Wall, TimeSpan Busy)
{
    public double BusyPercent => Wall <= TimeSpan.Zero ? 0 : Math.Clamp(Busy / Wall * 100, 0, 100);
}

/// <summary>
/// Maps paths to physical disks and reads per-disk driver counters. Both calls open the device with no
/// data access rights, so they work without elevation; every failure returns null instead of throwing.
/// </summary>
public static class PhysicalDisk
{
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint IoctlDiskPerformance = 0x00070020;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint FileDeviceDisk = 0x7;

    /// <summary>Physical disk number backing the volume that holds <paramref name="path"/>, or null for network, spanned or unknown volumes.</summary>
    public static int? TryGetDiskNumber(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length < 2 || root[1] != ':') return null;
            using var handle = OpenDevice(@"\\.\" + root[..2]);
            if (handle.IsInvalid) return null;
            // Other device types (CD-ROM, ...) number their devices separately; never map them onto PhysicalDriveN.
            return DeviceIoControl(handle, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, out StorageDeviceNumber number, (uint)Marshal.SizeOf<StorageDeviceNumber>(), out _, IntPtr.Zero)
                && number.DeviceType == FileDeviceDisk
                ? (int)number.DeviceNumber
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    public static DiskPerformanceSnapshot? TryRead(int diskNumber)
    {
        if (!OperatingSystem.IsWindows() || diskNumber < 0) return null;
        using var handle = OpenDevice(@"\\.\PhysicalDrive" + diskNumber);
        if (handle.IsInvalid) return null;
        if (!DeviceIoControl(handle, IoctlDiskPerformance, IntPtr.Zero, 0, out DiskPerformance performance, (uint)Marshal.SizeOf<DiskPerformance>(), out _, IntPtr.Zero))
            return null;
        if (performance.QueryTime <= 0 || performance.IdleTime < 0) return null;
        return new DiskPerformanceSnapshot(diskNumber, performance.BytesRead, performance.BytesWritten, performance.ReadCount, performance.WriteCount,
            TimeSpan.FromTicks(performance.IdleTime), DateTimeOffset.FromFileTime(performance.QueryTime));
    }

    private static SafeFileHandle OpenDevice(string device) => CreateFile(device, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskPerformance
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public ushort[] StorageManagerName;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize, out StorageDeviceNumber outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize, out DiskPerformance outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
