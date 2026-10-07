using System.Runtime.InteropServices;

namespace LocalMediaAI.Infrastructure;

/// <summary>How long since the user last touched the keyboard or mouse in this session; null when Windows cannot say.</summary>
public static class UserInputIdle
{
    public static TimeSpan? Current()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return null;
        // Both values are milliseconds on the same 32-bit tick counter; unsigned subtraction survives the 49.7-day wrap.
        var idleMilliseconds = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
