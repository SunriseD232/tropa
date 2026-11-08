namespace Tropa.Infrastructure.Storage;

/// <summary>
/// Перенос настроек установленной Тропы в портативную (ADR-063): серверы, подписки, ключи, правила и настройки
/// лежат в %APPDATA%\Tropa (settings.json, secrets.bin, backups). Остальное (кэш правил, журнал изменений системы,
/// журнал для разработчика) не переносится: журнал изменений принадлежит установленной Тропе, и откатывать её
/// изменения портативной нельзя. Ключи зашифрованы DPAPI для пользователя Windows — на этом компьютере под этой
/// учётной записью они читаются, на другом — нет.
/// Выполняется при запуске, до открытия настроек: работающая Тропа перезаписала бы скопированное при выходе.
/// </summary>
public static class PortableImport
{
    public const string Argument = "--import-installed";

    private static readonly string[] Files = ["settings.json", "secrets.bin"];

    public static bool InstalledDataExists(string installedRoaming) =>
        File.Exists(Path.Combine(installedRoaming, "settings.json"));

    /// <summary>Копирует данные. Прежние файлы портативной Тропы — в data\before-import-&lt;время&gt;. Возвращает итог для человека.</summary>
    public static string Import(string installedRoaming, string portableRoaming, DateTimeOffset now)
    {
        if (!InstalledDataExists(installedRoaming))
            throw new InvalidOperationException("Настройки установленной Тропы не найдены (%APPDATA%\\Tropa\\settings.json).");
        if (string.Equals(Path.GetFullPath(installedRoaming).TrimEnd('\\'), Path.GetFullPath(portableRoaming).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Это и есть каталог установленной Тропы.");

        Directory.CreateDirectory(portableRoaming);
        var keep = Path.Combine(portableRoaming, "before-import-" + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        foreach (var name in Files)
        {
            var existing = Path.Combine(portableRoaming, name);
            if (File.Exists(existing))
            {
                Directory.CreateDirectory(keep);
                File.Move(existing, Path.Combine(keep, name));
            }
        }

        foreach (var name in Files)
        {
            var from = Path.Combine(installedRoaming, name);
            if (File.Exists(from))
                CopyAtomic(from, Path.Combine(portableRoaming, name));
        }

        var backups = Path.Combine(installedRoaming, "backups");
        if (Directory.Exists(backups))
        {
            var target = Path.Combine(portableRoaming, "backups");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(backups, "settings-*.json"))
                CopyAtomic(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return "Настройки, серверы и подписки перенесены из установленной Тропы."
            + (Directory.Exists(keep) ? $" Прежние настройки портативной Тропы сохранены в data\\{Path.GetFileName(keep)}." : "");
    }

    private static void CopyAtomic(string from, string to)
    {
        var temp = to + ".tmp";
        File.Copy(from, temp, overwrite: true);
        File.Move(temp, to, overwrite: true);
    }
}
