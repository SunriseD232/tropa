namespace Tropa.Core.Testing;

/// <summary>Итог проверки сервера (docs/07-testing-diagnostics.md, §1). null — замер не делался.</summary>
public sealed record ServerTestResult
{
    public required DateTimeOffset At { get; init; }

    /// <summary>TCP-рукопожатие с сервером, мс.</summary>
    public int? TcpMs { get; init; }

    /// <summary>Реальная задержка HTTP-запроса через сервер, мс.</summary>
    public int? DelayMs { get; init; }

    /// <summary>Скорость скачивания через сервер, Мбит/с.</summary>
    public double? SpeedMbps { get; init; }

    /// <summary>Сработал детектор «заморозки»: пришло немного данных, потом тишина.</summary>
    public bool Frozen { get; init; }

    /// <summary>Сколько байт пришло до «заморозки» — для пояснения пользователю.</summary>
    public long? FrozenAfterBytes { get; init; }

    /// <summary>Ответил ли STUN через сервер (UDP).</summary>
    public bool? UdpOk { get; init; }

    /// <summary>Стабильность: доля потерь 0..1, разброс задержки и медиана.</summary>
    public double? Loss { get; init; }

    public int? JitterMs { get; init; }

    public int? MedianMs { get; init; }

    /// <summary>Причина ошибки на шаге, где проверка остановилась (без секретов).</summary>
    public string? Error { get; init; }
}

public enum HealthStatus { Unknown, Working, Slow, Frozen, HandshakeError, NoResponse, Incompatible }

/// <summary>Статус сервера по результату проверки. Пороги — в одном месте, чтобы интерфейс и авто-выбор не расходились.</summary>
public static class ServerHealth
{
    public const int SlowDelayMs = 400;
    public const double SlowSpeedMbps = 5;

    /// <summary>Сколько живёт результат для авто-выбора: блокировки меняются, вечно доверять старому замеру нельзя.</summary>
    public static readonly TimeSpan ResultLifetime = TimeSpan.FromHours(1);

    public static HealthStatus Classify(ServerTestResult? r)
    {
        if (r is null)
            return HealthStatus.Unknown;
        if (r.Frozen)
            return HealthStatus.Frozen;
        if (r.TcpMs is null && r.DelayMs is null)
            return r.Error is null ? HealthStatus.Unknown : HealthStatus.NoResponse;
        if (r.DelayMs is null)
            return HealthStatus.HandshakeError;
        if (r.DelayMs > SlowDelayMs || r.SpeedMbps is < SlowSpeedMbps)
            return HealthStatus.Slow;
        return HealthStatus.Working;
    }

    /// <summary>Сервер нельзя брать в авто-выбор: не отвечает или «замерзает», и замер ещё свежий.</summary>
    public static bool ShouldExclude(ServerTestResult? r, DateTimeOffset now) =>
        r is not null && now - r.At < ResultLifetime
        && Classify(r) is HealthStatus.Frozen or HealthStatus.NoResponse or HealthStatus.HandshakeError;

    public static string Title(HealthStatus s) => s switch
    {
        HealthStatus.Working => "Работает",
        HealthStatus.Slow => "Медленно",
        HealthStatus.Frozen => "Данные не идут",
        HealthStatus.HandshakeError => "Ошибка рукопожатия",
        HealthStatus.NoResponse => "Нет ответа",
        HealthStatus.Incompatible => "Несовместимые параметры",
        _ => "Не проверен",
    };

    /// <summary>Объяснение для пользователя: что значит статус и что делать.</summary>
    public static string Explain(ServerTestResult? r) => Classify(r) switch
    {
        HealthStatus.Frozen =>
            $"Сервер отвечает, но загрузка замерла после {(r!.FrozenAfterBytes ?? 0) / 1024} КБ. Так выглядит ограничение IP хостинга: пинг зелёный, а сайты не грузятся. Выберите сервер другого хостинга, цепочку через российский сервер или CDN-вариант.",
        HealthStatus.NoResponse =>
            "Сервер не принимает соединения: он выключен или его IP заблокирован полностью.",
        HealthStatus.HandshakeError =>
            "Сервер доступен, но подключиться через него не удалось: устарели ключи в подписке или DPI режет подключение по имени-маске. Обновите подписку.",
        HealthStatus.Slow =>
            r!.SpeedMbps is < SlowSpeedMbps
                ? $"Скорость всего {r.SpeedMbps:0.#} Мбит/с — для видео может не хватить."
                : $"Задержка {r.DelayMs} мс — сайты будут открываться с паузой.",
        HealthStatus.Working => r!.UdpOk == false
            ? "Сайты будут работать, но UDP не проходит: голос в Discord и звонки работать не будут."
            : "Всё в порядке.",
        _ => "Сервер ещё не проверялся.",
    };

    /// <summary>
    /// Чем заменить сервер, который перестал работать: свежепроверенный рабочий сервер с наименьшей
    /// задержкой (медленные — только если рабочих нет). null — заменить нечем.
    /// </summary>
    public static Guid? PickReplacement(IEnumerable<(Guid Id, ServerTestResult? Result)> candidates, Guid current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var fresh = candidates
            .Where(c => c.Id != current && c.Result is not null && now - c.Result.At < ResultLifetime)
            .Select(c => (c.Id, Status: Classify(c.Result), Delay: c.Result!.DelayMs ?? int.MaxValue))
            .ToList();
        return fresh.Where(c => c.Status == HealthStatus.Working).OrderBy(c => c.Delay).Select(c => (Guid?)c.Id).FirstOrDefault()
            ?? fresh.Where(c => c.Status == HealthStatus.Slow).OrderBy(c => c.Delay).Select(c => (Guid?)c.Id).FirstOrDefault();
    }

    /// <summary>Статистика серии замеров: доля потерь, разброс (стандартное отклонение) и медиана.</summary>
    public static (double Loss, int? JitterMs, int? MedianMs) Stability(IReadOnlyList<int?> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            return (0, null, null);
        var ok = samples.Where(x => x is not null).Select(x => x!.Value).Order().ToList();
        var loss = 1.0 - (double)ok.Count / samples.Count;
        if (ok.Count == 0)
            return (loss, null, null);
        var mean = ok.Average();
        var jitter = (int)Math.Round(Math.Sqrt(ok.Sum(x => (x - mean) * (x - mean)) / ok.Count));
        var median = ok.Count % 2 == 1 ? ok[ok.Count / 2] : (ok[ok.Count / 2 - 1] + ok[ok.Count / 2]) / 2;
        return (loss, jitter, median);
    }
}

/// <summary>
/// Детектор «заморозки» (docs/07-testing-diagnostics.md, §1): ТСПУ пропускает первые ~16–20 КБ
/// к IP некоторых хостингов, а потом соединение замирает. Признак: данные были, но меньше
/// порога, и затем тишина дольше <see cref="Silence"/>.
/// </summary>
public sealed class FreezeDetector(TimeProvider time)
{
    public const long Threshold = 64 * 1024;
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(8);

    private long _bytes;
    private long _lastDataTicks = time.GetTimestamp();

    public long Bytes => _bytes;

    public void OnData(int count)
    {
        if (count <= 0)
            return;
        _bytes += count;
        _lastDataTicks = time.GetTimestamp();
    }

    /// <summary>Пора объявить «заморозку».</summary>
    public bool IsFrozen =>
        _bytes > 0 && _bytes < Threshold && time.GetElapsedTime(_lastDataTicks) >= Silence;

    /// <summary>Данных нет совсем — это не «заморозка», а обычный таймаут.</summary>
    public bool NothingReceived => _bytes == 0;
}
