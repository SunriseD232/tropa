using System.Text.Json;
using System.Text.Json.Serialization;
using Tropa.Infrastructure.Storage;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>Исходное значение, которое нужно вернуть при откате.</summary>
public sealed record JournalEntry(string Kind, Dictionary<string, string?> Original, DateTimeOffset At);

/// <summary>
/// Журнал изменений системы (docs/02-security.md, §3.6): у интерфейса свой (прокси Windows),
/// у службы свой (политика DNS, позже брандмауэр).
/// Исходное значение записывается на диск ДО изменения. Если Тропа упадёт, при следующем
/// запуске журнал не пуст — значит, нужно откатить, и только потом работать.
/// </summary>
public sealed class ChangeJournal(string path)
{
    private readonly Lock _lock = new();

    public IReadOnlyList<JournalEntry> Pending()
    {
        lock (_lock)
            return Read();
    }

    /// <summary>Запоминает исходное состояние. Повторная запись того же вида не затирает первое значение.</summary>
    public void Record(string kind, Dictionary<string, string?> original, DateTimeOffset now)
    {
        lock (_lock)
        {
            var entries = Read();
            if (entries.Any(e => e.Kind == kind))
                return; // уже изменено нами — исходным остаётся самое первое значение
            entries.Add(new JournalEntry(kind, original, now));
            Write(entries);
        }
    }

    public void Complete(string kind)
    {
        lock (_lock)
        {
            var entries = Read();
            if (entries.RemoveAll(e => e.Kind == kind) > 0)
                Write(entries);
        }
    }

    private List<JournalEntry> Read()
    {
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), JournalJsonContext.Default.ListJournalEntry) ?? [];
        }
        catch (JsonException)
        {
            // Повреждённый журнал не должен блокировать запуск, но и молча теряться не должен:
            // сохраняем копию для диагностики.
            File.Copy(path, path + ".corrupt", overwrite: true);
            return [];
        }
    }

    private void Write(List<JournalEntry> entries)
    {
        if (entries.Count == 0)
        {
            File.Delete(path);
            return;
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(entries, JournalJsonContext.Default.ListJournalEntry));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<JournalEntry>))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
