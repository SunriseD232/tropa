using System.Globalization;
using Microsoft.Win32;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>Значения в одном ключе реестра. Абстракция для тестов без доступа к HKLM.</summary>
public interface IRegistryValues
{
    /// <summary>DWORD-значения ключа (null — значения нет).</summary>
    Dictionary<string, string?> Read(IReadOnlyCollection<string> names);

    void Write(IReadOnlyDictionary<string, string?> values);
}

/// <summary>
/// Политика DNS-клиента Windows (info.ru.json: smartNameRes). По умолчанию Windows отправляет
/// DNS-запрос сразу во все сетевые карты, и запрос уходит к провайдеру мимо туннеля.
/// Служба выключает это на время работы и возвращает исходные значения через журнал.
/// </summary>
public sealed class DnsClientPolicy(IRegistryValues registry, ChangeJournal journal)
{
    public const string JournalKind = "dns-client-policy";

    internal static readonly string[] Names = ["DisableSmartNameResolution", "DisableParallelAandAAAA"];

    public void Apply(DateTimeOffset now)
    {
        journal.Record(JournalKind, registry.Read(Names), now);
        registry.Write(Names.ToDictionary(n => n, _ => (string?)"1"));
    }

    public void Restore()
    {
        var entry = journal.Pending().FirstOrDefault(e => e.Kind == JournalKind);
        if (entry is null)
            return;
        registry.Write(entry.Original);
        journal.Complete(JournalKind);
    }
}

/// <summary>HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient. Писать сюда может только служба.</summary>
public sealed class DnsClientPolicyRegistry : IRegistryValues
{
    private const string KeyPath = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";

    public Dictionary<string, string?> Read(IReadOnlyCollection<string> names)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: false);
        return names.ToDictionary(n => n, n => key?.GetValue(n) is int v ? v.ToString(CultureInfo.InvariantCulture) : null);
    }

    public void Write(IReadOnlyDictionary<string, string?> values)
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        foreach (var (name, value) in values)
        {
            if (value is null)
                key.DeleteValue(name, throwOnMissingValue: false);
            else
                key.SetValue(name, int.Parse(value, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        }
    }
}
