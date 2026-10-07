using Microsoft.Win32;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>Значение автозапуска пользователя. Абстракция для тестов без реестра.</summary>
public interface IRunKey
{
    string? Read(string name);

    void Write(string name, string? value);
}

/// <summary>
/// Запуск интерфейса вместе с Windows (info.ru.json: autostart): значение в
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Служба стартует сама — её ставит установщик.
/// Пишем только для установленной программы (из Program Files), чтобы отладочные сборки
/// не прописывались в автозагрузку. Удаляет значение деинсталлятор.
/// </summary>
public sealed class Autostart(IRunKey key)
{
    public const string ValueName = "Tropa";
    public const string Argument = "--autostart";

    public static bool IsInstalledCopy(string exePath) =>
        exePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Приводит автозагрузку в соответствие с настройкой. Возвращает true, если что-то поменялось.</summary>
    public bool Sync(bool enabled, string exePath)
    {
        var wanted = enabled ? $"\"{exePath}\" {Argument}" : null;
        var current = key.Read(ValueName);
        // Чужое значение с нашим именем не трогаем — только своё (путь к нашему exe).
        if (current is not null && !current.Contains(exePath, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(current, wanted, StringComparison.Ordinal))
            return false;
        key.Write(ValueName, wanted);
        return true;
    }
}

public sealed class RegistryRunKey : IRunKey
{
    private const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Path, writable: false);
        return k?.GetValue(name) as string;
    }

    public void Write(string name, string? value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Path, writable: true);
        if (value is null)
            k.DeleteValue(name, throwOnMissingValue: false);
        else
            k.SetValue(name, value, RegistryValueKind.String);
    }
}
