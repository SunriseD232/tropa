using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Tropa.App.Services;

/// <summary>
/// Права, под которыми запущен интерфейс, и перезапуск от имени администратора (ADR-052).
/// Обычно интерфейс работает без прав: всё системное делает служба (docs/02-security.md, §3.3).
/// </summary>
internal static class Elevation
{
    /// <summary>Аргумент нового экземпляра: дождаться, пока старый отпустит блокировку единственного экземпляра.</summary>
    public const string WaitInstanceArgument = "--wait-instance";

    /// <summary>Аргумент нового экземпляра: старый был подключён — подключиться снова.</summary>
    public const string ReconnectArgument = "--reconnect-after-restart";

    public static bool IsAdmin { get; } = CheckAdmin();

    public static bool CanElevate => !IsAdmin;

    /// <summary>Заголовок окна: сразу видно, если Тропу запустили от имени администратора.</summary>
    public static string WindowTitle => IsAdmin ? "Тропа — администратор" : "Тропа — обычный пользователь";

    private static bool CheckAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Запускает копию Тропы с запросом прав (окно UAC). true — Windows приняла запуск, текущий экземпляр
    /// должен завершиться; false — пользователь отказался в окне UAC.
    /// </summary>
    public static bool StartElevatedCopy(bool reconnect)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к программе.");
        var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(WaitInstanceArgument);
        if (reconnect)
            info.ArgumentList.Add(ReconnectArgument);
        try
        {
            using var _ = Process.Start(info);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: «Нет» в окне UAC
        {
            return false;
        }
    }
}
