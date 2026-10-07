using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Runs media tools below the user's own work: below-normal CPU priority and low I/O priority, so a drive the user is
/// using (copying files, playing a video) serves them first. Lowering priority needs no elevation; any failure is
/// ignored because it only affects politeness, never correctness.
/// </summary>
internal static class BackgroundPriority
{
    private const int ProcessIoPriority = 33;
    private const int IoPriorityLow = 1;

    public static void TryApply(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var priority = IoPriorityLow;
            _ = NtSetInformationProcess(process.Handle, ProcessIoPriority, ref priority, sizeof(int));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or EntryPointNotFoundException or DllNotFoundException) { }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int informationClass, ref int information, int length);
}
