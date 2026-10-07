using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tropa.Service;

/// <summary>Решает, можно ли этому клиенту управлять службой.</summary>
internal interface IClientVerifier
{
    /// <summary>null — можно; иначе причина отказа (пишется в журнал службы, клиенту не раскрывается).</summary>
    string? Verify(NamedPipeServerStream pipe);
}

/// <summary>
/// Управлять службой может только Tropa.exe из каталога программы (docs/02-security.md, §3.3).
/// Каталог в Program Files доступен на запись только администраторам, поэтому подложить туда
/// свою программу обычный пользователь или вирус без прав администратора не сможет.
/// </summary>
internal sealed partial class ExecutablePathVerifier(string expectedPath) : IClientVerifier
{
    public string? Verify(NamedPipeServerStream pipe)
    {
        var pid = PipeProcess.ClientProcessId(pipe.SafePipeHandle);
        string? path;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            path = process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException)
        {
            return $"не удалось определить программу клиента (pid {pid}): {ex.Message}";
        }

        if (path is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            return $"чужая программа: {path}";
        return null;
    }
}

internal static partial class PipeProcess
{
    public static uint ClientProcessId(SafePipeHandle handle)
    {
        if (!GetNamedPipeClientProcessId(handle, out var pid))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return pid;
    }

    public static uint ServerProcessId(SafePipeHandle handle)
    {
        if (!GetNamedPipeServerProcessId(handle, out var pid))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return pid;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
