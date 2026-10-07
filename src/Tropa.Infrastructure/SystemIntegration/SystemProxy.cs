using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>Где хранятся настройки прокси. Абстракция нужна для тестов без изменения реального реестра.</summary>
public interface IProxySettingsStore
{
    Dictionary<string, string?> Read();

    void Write(IReadOnlyDictionary<string, string?> values);
}

/// <summary>
/// Системный прокси Windows (HKCU, режим «Только браузеры»). Каждое включение сначала
/// записывает исходные настройки в <see cref="UserJournal"/>, выключение возвращает их.
/// </summary>
public sealed class SystemProxy(IProxySettingsStore store, UserJournal journal)
{
    public const string JournalKind = "system-proxy";

    internal const string Enable = "ProxyEnable";
    internal const string Server = "ProxyServer";
    internal const string Override = "ProxyOverride";
    internal const string AutoConfig = "AutoConfigURL";

    public void Apply(int port, string bypass, DateTimeOffset now)
    {
        journal.Record(JournalKind, store.Read(), now);
        store.Write(new Dictionary<string, string?>
        {
            [Enable] = "1",
            [Server] = $"127.0.0.1:{port}",
            // <local> — стандартное исключение для адресов без точки (имена в локальной сети).
            [Override] = string.IsNullOrWhiteSpace(bypass) ? "<local>" : bypass.Trim().TrimEnd(';') + ";<local>",
            // PAC-скрипт имеет приоритет над прокси: на время работы отключаем, при откате вернём.
            [AutoConfig] = null,
        });
    }

    /// <summary>Возвращает настройки, которые были до Тропы. Безопасно вызывать многократно.</summary>
    public void Restore()
    {
        var entry = journal.Pending().FirstOrDefault(e => e.Kind == JournalKind);
        if (entry is null)
            return;
        store.Write(entry.Original);
        journal.Complete(JournalKind);
    }

    public bool IsAppliedByUs => journal.Pending().Any(e => e.Kind == JournalKind);
}

/// <summary>Реальный реестр: HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings.</summary>
public sealed partial class RegistryProxySettingsStore : IProxySettingsStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public Dictionary<string, string?> Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return new Dictionary<string, string?>
        {
            [SystemProxy.Enable] = key?.GetValue(SystemProxy.Enable) is int e ? e.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            [SystemProxy.Server] = key?.GetValue(SystemProxy.Server) as string,
            [SystemProxy.Override] = key?.GetValue(SystemProxy.Override) as string,
            [SystemProxy.AutoConfig] = key?.GetValue(SystemProxy.AutoConfig) as string,
        };
    }

    public void Write(IReadOnlyDictionary<string, string?> values)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true))
        {
            foreach (var (name, value) in values)
            {
                if (value is null)
                    key.DeleteValue(name, throwOnMissingValue: false);
                else if (name == SystemProxy.Enable)
                    key.SetValue(name, value == "1" ? 1 : 0, RegistryValueKind.DWord);
                else
                    key.SetValue(name, value, RegistryValueKind.String);
            }
        }

        // Сообщаем WinINet, что настройки изменились, иначе браузеры подхватят их не сразу.
        _ = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
