using System.Net.NetworkInformation;
using Microsoft.Win32;
using Tropa.Core.Diagnostics;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>Настройки прокси одной учётной записи: Internet Settings и два двоичных значения из Connections.</summary>
public sealed record AccountProxyValues(int? ProxyEnable, string? ProxyServer, byte[]? DefaultConnectionSettings, byte[]? SavedLegacySettings);

/// <summary>Где хранятся настройки прокси служебных учётных записей. Абстракция для тестов без реестра.</summary>
public interface IServiceAccountProxyStore
{
    /// <summary>null — профиля учётной записи нет (куст не загружен).</summary>
    AccountProxyValues? Read(string sid);

    void Write(string sid, AccountProxyValues values);
}

/// <summary>
/// Прокси служебных учётных записей Windows (ADR-064). Клиенты обхода, запущенные от администратора или
/// как служба, включают прокси и для LocalSystem, а при закрытии не убирают. Службы Windows с таким
/// прокси (например, «Службы игр» Xbox) висят на каждом соединении. Читать может любой пользователь,
/// выключать — только служба Тропы (или Тропа с правами администратора).
/// </summary>
public sealed class ServiceAccountProxies(IServiceAccountProxyStore store)
{
    /// <summary>SID и понятное имя. HKU\.DEFAULT — это тот же профиль, что и S-1-5-18.</summary>
    public static readonly IReadOnlyList<(string Sid, string Name)> Accounts =
    [
        ("S-1-5-18", "LocalSystem"),
        ("S-1-5-19", "LocalService"),
        ("S-1-5-20", "NetworkService"),
    ];

    public IReadOnlyList<ServiceAccountProxy> Snapshot()
    {
        var list = new List<ServiceAccountProxy>();
        foreach (var (sid, name) in Accounts)
        {
            if (Read(sid) is { } values)
                list.Add(ToSnapshot(name, values));
        }

        return list;
    }

    /// <summary>
    /// Выключает «мёртвые» прокси (<see cref="Diagnosis.IsDeadServiceProxy"/>): флаг ProxyEnable и флаг прокси
    /// в двоичных значениях; адрес остаётся. В журнал изменений не пишется — вернуть его значило бы снова
    /// сломать службы (как ADR-044). Возвращает «учётная запись → адрес» для журнала программы.
    /// </summary>
    public IReadOnlyList<string> DisableDead(Func<int, bool> isListening)
    {
        var done = new List<string>();
        foreach (var (sid, name) in Accounts)
        {
            if (Read(sid) is not { } values)
                continue;
            var snapshot = ToSnapshot(name, values);
            if (!Diagnosis.IsDeadServiceProxy(snapshot, isListening))
                continue;
            store.Write(sid, new AccountProxyValues(
                values.ProxyEnable is null ? null : 0,
                values.ProxyServer,
                WinInetConnectionBlob.WithoutProxy(values.DefaultConnectionSettings) ?? values.DefaultConnectionSettings,
                WinInetConnectionBlob.WithoutProxy(values.SavedLegacySettings) ?? values.SavedLegacySettings));
            done.Add($"{name} → {snapshot.Server}");
        }

        return done;
    }

    /// <summary>Слушает ли кто-нибудь TCP-порт на этом компьютере.</summary>
    public static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    private AccountProxyValues? Read(string sid)
    {
        try
        {
            return store.Read(sid);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static ServiceAccountProxy ToSnapshot(string name, AccountProxyValues values)
    {
        // WinINet верит двоичному значению; ProxyEnable — его старая копия. Включён, если включён хоть где-то.
        var blob = WinInetConnectionBlob.Parse(values.DefaultConnectionSettings);
        var enabled = values.ProxyEnable == 1 || blob is { ProxyEnabled: true };
        var server = !string.IsNullOrWhiteSpace(values.ProxyServer) ? values.ProxyServer : blob?.Server;
        return new ServiceAccountProxy(name, enabled, server);
    }
}

/// <summary>Реальный реестр: HKEY_USERS\&lt;SID&gt;\Software\Microsoft\Windows\CurrentVersion\Internet Settings.</summary>
public sealed class RegistryServiceAccountProxyStore : IServiceAccountProxyStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string Default = "DefaultConnectionSettings";
    private const string Legacy = "SavedLegacySettings";

    public AccountProxyValues? Read(string sid)
    {
        using var key = Registry.Users.OpenSubKey($@"{sid}\{KeyPath}", writable: false);
        if (key is null)
            return null;
        using var connections = key.OpenSubKey("Connections", writable: false);
        return new AccountProxyValues(
            key.GetValue(SystemProxy.Enable) as int?,
            key.GetValue(SystemProxy.Server) as string,
            connections?.GetValue(Default) as byte[],
            connections?.GetValue(Legacy) as byte[]);
    }

    public void Write(string sid, AccountProxyValues values)
    {
        using var key = Registry.Users.OpenSubKey($@"{sid}\{KeyPath}", writable: true);
        if (key is null)
            return;
        if (values.ProxyEnable is { } enable)
            key.SetValue(SystemProxy.Enable, enable, RegistryValueKind.DWord);
        using var connections = key.OpenSubKey("Connections", writable: true);
        if (connections is null)
            return;
        if (values.DefaultConnectionSettings is { } d)
            connections.SetValue(Default, d, RegistryValueKind.Binary);
        if (values.SavedLegacySettings is { } l)
            connections.SetValue(Legacy, l, RegistryValueKind.Binary);
    }
}
