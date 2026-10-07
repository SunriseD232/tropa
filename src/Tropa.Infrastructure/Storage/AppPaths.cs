namespace Tropa.Infrastructure.Storage;

/// <summary>
/// Единственные каталоги, в которые Тропа имеет право писать (docs/02-security.md, §3.7).
/// </summary>
public static class AppPaths
{
    private const string AppFolder = "Tropa";

    /// <summary>Настройки и секреты пользователя (%APPDATA%\Tropa).</summary>
    public static string Roaming { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);

    /// <summary>Логи и гео-базы пользователя (%LOCALAPPDATA%\Tropa).</summary>
    public static string Local { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder);

    /// <summary>Данные службы (%ProgramData%\Tropa), доступ только SYSTEM.</summary>
    public static string Machine { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolder);
}
