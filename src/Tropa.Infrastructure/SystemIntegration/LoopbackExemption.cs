using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>Приложение из Microsoft Store (AppContainer).</summary>
public sealed record AppContainerInfo(string Sid, string DisplayName, string PackageName);

/// <summary>Список AppContainer-ов, которым разрешён loopback. Абстракция для тестов.</summary>
public interface ILoopbackStore
{
    IReadOnlyList<string> Read();

    void Write(IReadOnlyList<string> sids);
}

/// <summary>
/// Loopback-исключения для приложений Store (info.ru.json: uwpLoopback). Без них приложения
/// из Store не могут подключиться к прокси на 127.0.0.1. Служба добавляет только недостающие
/// SID-ы и при откате убирает только их — исключения, сделанные пользователем раньше, не трогаются.
/// </summary>
public sealed partial class LoopbackExemption(ILoopbackStore store, ChangeJournal journal)
{
    public const string JournalKind = "loopback-exemption";

    [GeneratedRegex(@"^S-1-15-2(-\d{1,10}){1,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppContainerSid();

    public static bool IsAppContainerSid(string sid) => AppContainerSid().IsMatch(sid);

    public void Apply(IReadOnlyList<string> requested, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.Any(s => !IsAppContainerSid(s)))
            throw new ArgumentException("Неверный идентификатор приложения Store.", nameof(requested));
        var current = store.Read();
        var added = requested.Except(current, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count == 0)
            return;
        journal.Record(JournalKind, new Dictionary<string, string?> { ["added"] = string.Join(';', added) }, now);
        store.Write([.. current, .. added]);
    }

    public void Restore()
    {
        var entry = journal.Pending().FirstOrDefault(e => e.Kind == JournalKind);
        if (entry is null)
            return;
        var added = (entry.Original.GetValueOrDefault("added") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        store.Write(store.Read().Where(s => !added.Contains(s)).ToList());
        journal.Complete(JournalKind);
    }
}

/// <summary>Настоящий список Windows (FirewallAPI.dll). Изменение требует прав администратора.</summary>
public sealed partial class FirewallLoopbackStore : ILoopbackStore
{
    public IReadOnlyList<string> Read()
    {
        Check(NetworkIsolationGetAppContainerConfig(out var count, out var array));
        var result = new List<string>((int)count);
        var heap = GetProcessHeap();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var sid = Marshal.ReadIntPtr(array, i * SidAndAttributesSize);
                result.Add(new SecurityIdentifier(sid).Value);
                _ = HeapFree(heap, 0, sid);
            }
        }
        finally
        {
            if (array != IntPtr.Zero)
                _ = HeapFree(heap, 0, array);
        }

        return result;
    }

    public void Write(IReadOnlyList<string> sids)
    {
        ArgumentNullException.ThrowIfNull(sids);
        var buffers = new List<IntPtr>();
        var array = Marshal.AllocHGlobal(Math.Max(1, sids.Count) * SidAndAttributesSize);
        try
        {
            for (var i = 0; i < sids.Count; i++)
            {
                var sid = new SecurityIdentifier(sids[i]);
                var bytes = new byte[sid.BinaryLength];
                sid.GetBinaryForm(bytes, 0);
                var p = Marshal.AllocHGlobal(bytes.Length);
                buffers.Add(p);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                Marshal.WriteIntPtr(array, i * SidAndAttributesSize, p);
                Marshal.WriteInt64(array, (i * SidAndAttributesSize) + 8, 0);
            }

            Check(NetworkIsolationSetAppContainerConfig((uint)sids.Count, array));
        }
        finally
        {
            foreach (var p in buffers)
                Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(array);
        }
    }

    /// <summary>Установленные приложения Store. Работает без прав администратора.</summary>
    public static IReadOnlyList<AppContainerInfo> Enumerate()
    {
        Check(NetworkIsolationEnumAppContainers(0, out var count, out var array));
        var result = new List<AppContainerInfo>((int)count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var item = array + (i * AppContainerSize);
                var sid = Marshal.ReadIntPtr(item, 0);
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 16)) ?? "";
                var display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 24)) ?? name;
                if (sid == IntPtr.Zero)
                    continue;
                // Названия вида «@{Package?ms-resource://…}» — ресурс, который без загрузки пакета не прочитать.
                if (display.StartsWith("@{", StringComparison.Ordinal))
                    display = name;
                result.Add(new AppContainerInfo(new SecurityIdentifier(sid).Value, display, name));
            }
        }
        finally
        {
            if (array != IntPtr.Zero)
                _ = NetworkIsolationFreeAppContainers(array);
        }

        return [.. result.DistinctBy(a => a.Sid).OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    private const int SidAndAttributesSize = 16;
    private const int AppContainerSize = 88;

    private static void Check(uint code)
    {
        if (code != 0)
            throw new Win32Exception(unchecked((int)code), $"Не удалось изменить исключения loopback (код {code}).");
    }

    [LibraryImport("FirewallAPI.dll")]
    private static partial uint NetworkIsolationGetAppContainerConfig(out uint count, out IntPtr sids);

    [LibraryImport("FirewallAPI.dll")]
    private static partial uint NetworkIsolationSetAppContainerConfig(uint count, IntPtr sids);

    [LibraryImport("FirewallAPI.dll")]
    private static partial uint NetworkIsolationEnumAppContainers(uint flags, out uint count, out IntPtr containers);

    [LibraryImport("FirewallAPI.dll")]
    private static partial uint NetworkIsolationFreeAppContainers(IntPtr containers);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetProcessHeap();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HeapFree(IntPtr heap, uint flags, IntPtr mem);
}
