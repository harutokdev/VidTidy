using System.Runtime.InteropServices;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Windows Shell Recycle Bin boundary. A successful result only states that IFileOperation completed
/// without an abort; SafeFileOperationService must still observe the original source path before it
/// publishes a Recycled lifecycle state. This adapter intentionally contains no permanent-delete or
/// SHFileOperation fallback path.
/// </summary>
public interface IWindowsRecycleBin
{
    Task<WindowsRecycleBinResult> RecycleAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken);
}

public sealed record WindowsRecycleBinResult(bool ShellSucceeded, bool AnyOperationAborted, string? ErrorCode, string? ErrorMessage)
{
    public static WindowsRecycleBinResult Success(bool aborted) => new(true, aborted, null, null);
    public static WindowsRecycleBinResult Failure(string code, string message) => new(false, false, code, message);
}

public sealed class WindowsRecycleBinShell : IWindowsRecycleBin
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint FofxEarlyFailure = 0x00100000;
    private static readonly Guid FileOperationClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    public Task<WindowsRecycleBinResult> RecycleAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedSourcePath);
        if (!Path.IsPathFullyQualified(fullyQualifiedSourcePath))
            throw new ArgumentException("Recycle source path must be fully qualified.", nameof(fullyQualifiedSourcePath));
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(WindowsRecycleBinResult.Failure("RECYCLE_UNAVAILABLE_PLATFORM", "Windows Recycle Bin is unavailable on this platform."));
        return RunOnStaAsync(() => RecycleCore(fullyQualifiedSourcePath));
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "LocalMediaAI.WindowsRecycleBin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static WindowsRecycleBinResult RecycleCore(string sourcePath)
    {
        object? operationObject = null;
        IShellItem? shellItem = null;
        try
        {
            var operationType = Type.GetTypeFromCLSID(FileOperationClassId);
            if (operationType is null) return WindowsRecycleBinResult.Failure("RECYCLE_UNAVAILABLE_SHELL_CLASS", "Windows IFileOperation class is unavailable.");
            operationObject = Activator.CreateInstance(operationType);
            if (operationObject is not IFileOperation operation) return WindowsRecycleBinResult.Failure("RECYCLE_UNAVAILABLE_SHELL_INTERFACE", "Windows IFileOperation interface is unavailable.");
            var shellItemId = typeof(IShellItem).GUID;
            var createResult = NativeMethods.SHCreateItemFromParsingName(sourcePath, IntPtr.Zero, ref shellItemId, out shellItem);
            if (createResult < 0 || shellItem is null)
                return WindowsRecycleBinResult.Failure($"RECYCLE_SHELL_ITEM_0x{createResult:X8}", "Windows Shell could not create a Recycle Bin item from the selected source path.");

            // The explicit recycle flag, plus undo preservation, is required. No destination/collision/delete fallback is supplied.
            operation.SetOperationFlags(FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi | FofxRecycleOnDelete | FofxEarlyFailure);
            operation.DeleteItem(shellItem, IntPtr.Zero);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            return WindowsRecycleBinResult.Success(aborted);
        }
        catch (COMException ex)
        {
            return WindowsRecycleBinResult.Failure($"RECYCLE_SHELL_HRESULT_0x{ex.HResult:X8}", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return WindowsRecycleBinResult.Failure("RECYCLE_SHELL_ACCESS_DENIED", ex.Message);
        }
        catch (IOException ex)
        {
            return WindowsRecycleBinResult.Failure("RECYCLE_SHELL_IO_FAILURE", ex.Message);
        }
        finally
        {
            if (shellItem is not null && Marshal.IsComObject(shellItem)) Marshal.ReleaseComObject(shellItem);
            if (operationObject is not null && Marshal.IsComObject(operationObject)) Marshal.ReleaseComObject(operationObject);
        }
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        internal static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr pfops, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint operationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(uint ownerWindow);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr progressSink);
        void RenameItems(IntPtr items, IntPtr progressSink);
        void MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr progressSink);
        void MoveItems(IntPtr items, IShellItem destinationFolder);
        void CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, IntPtr progressSink);
        void CopyItems(IntPtr items, IShellItem destinationFolder);
        void DeleteItem(IShellItem item, IntPtr progressSink);
        void DeleteItems(IntPtr items);
        void NewItem(IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IntPtr progressSink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
