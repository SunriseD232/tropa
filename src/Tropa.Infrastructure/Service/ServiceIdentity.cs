using System.Runtime.InteropServices;

namespace Tropa.Infrastructure.Service;

/// <summary>
/// Кто на другом конце канала, когда путь процесса службы прочитать нельзя. Процесс службы работает под
/// SYSTEM, и обычному пользователю Windows не даёт открыть его даже на чтение пути (ошибка 5) — из-за этого
/// Тропа без прав администратора считала свою же службу «чужой программой». Диспетчер служб читать может
/// любой пользователь: берём из него номер процесса службы «Tropa» и путь к её exe. Изменить запись службы
/// может только администратор, поэтому совпадение номера процесса и пути — такая же надёжная проверка.
/// </summary>
internal static partial class ServiceIdentity
{
    /// <summary>Путь к exe службы, если её процесс — именно <paramref name="pid"/>; иначе null.</summary>
    public static string? PathIfServiceProcess(string serviceName, uint pid)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return null;
        try
        {
            var service = OpenServiceW(scm, serviceName, ServiceQueryStatus | ServiceQueryConfig);
            if (service == IntPtr.Zero)
                return null;
            try
            {
                var status = new byte[64];
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, status, status.Length, out _))
                    return null;
                // SERVICE_STATUS_PROCESS: семь DWORD состояния, затем dwProcessId.
                if (BitConverter.ToUInt32(status, 28) != pid)
                    return null;

                QueryServiceConfigW(service, IntPtr.Zero, 0, out var needed);
                if (needed <= 0 || needed > 64 * 1024)
                    return null;
                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!QueryServiceConfigW(service, buffer, needed, out _))
                        return null;
                    // QUERY_SERVICE_CONFIGW: три DWORD, затем указатель lpBinaryPathName.
                    var binary = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 16 : 12));
                    return ExePath(binary);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>Путь к exe из командной строки службы: «"C:\…\Tropa.Service.exe" --ключ» → C:\…\Tropa.Service.exe.</summary>
    internal static string? ExePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;
        var s = commandLine.Trim();
        string exe;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 0)
                return null;
            exe = s[1..end];
        }
        else
        {
            var space = s.IndexOf(' ', StringComparison.Ordinal);
            exe = space < 0 ? s : s[..space];
        }

        try
        {
            return Path.GetFullPath(exe);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(IntPtr service, int level, [Out] byte[] buffer, int size, out int needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfigW(IntPtr service, IntPtr buffer, int size, out int needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
