using System.Diagnostics;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class BackgroundPriorityTests
{
    [Fact]
    public void MediaToolsRunBelowNormalPriority()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            BackgroundPriority.TryApply(process);

            Assert.Equal(ProcessPriorityClass.BelowNormal, process.PriorityClass);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void AnExitedProcessIsIgnored()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();

        BackgroundPriority.TryApply(process);
    }
}
