using System.Text.Json.Serialization;

namespace Tropa.Core.Telemetry;

/// <summary>
/// Одно событие журнала для разработчика (ADR-050): время (мс Unix), код из <see cref="Ev"/> и поля.
/// Поля — короткие строки без секретов; что именно попадает в журнал, решает отправитель.
/// </summary>
public sealed record TelemetryEvent(
    [property: JsonPropertyName("t")] long Time,
    [property: JsonPropertyName("c")] string Code,
    [property: JsonPropertyName("d")] IReadOnlyDictionary<string, string>? Data = null);

/// <summary>Пакет событий одного пользователя — тело POST-запроса на сервер журналов.</summary>
public sealed record TelemetryBatch
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Случайный идентификатор установки (32 hex), не связан с компьютером и учётной записью.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>Имя, которое пользователь сам указал в настройках, или пусто.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("app")]
    public string App { get; init; } = "";

    [JsonPropertyName("events")]
    public IReadOnlyList<TelemetryEvent> Events { get; init; } = [];
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TelemetryEvent))]
[JsonSerializable(typeof(TelemetryBatch))]
public sealed partial class TelemetryJsonContext : JsonSerializerContext;

/// <summary>
/// Классификатор событий: код «раздел.номер» вместо текста — в журнале «2.3», а расшифровку берёт
/// сервер из tools/tropa-telemetry-codes.json (тест следит, чтобы файл совпадал с этим списком).
/// Коды не переиспользуются: устаревшее событие остаётся в списке с пометкой.
/// </summary>
public static class Ev
{
    // 1 — приложение и служба
    public const string AppStart = "1.1";
    public const string AppExit = "1.2";
    public const string AppCrash = "1.3";
    public const string ServiceAttached = "1.4";
    public const string ServiceLost = "1.5";
    public const string StartupWarning = "1.6";

    // 2 — подключение
    public const string ConnectStart = "2.1";
    public const string Connected = "2.2";
    public const string ConnectFailed = "2.3";
    public const string Disconnect = "2.4";
    public const string Reconnect = "2.5";
    public const string CoreExited = "2.6";
    public const string ServiceCoreFailed = "2.7";
    public const string ServiceStoppedOutside = "2.8";
    public const string ServerUnhealthy = "2.9";
    public const string EmergencyRollback = "2.10";
    public const string BlockingReleased = "2.11";
    public const string StaleProxyDisabled = "2.12";
    public const string ServiceWait = "2.13";
    public const string BlockingChanged = "2.14";

    // 3 — окружение: сеть, другие VPN
    public const string ForeignVpn = "3.1";
    public const string NetworkChanged = "3.2";
    public const string WokeUp = "3.3";
    public const string ExitRegion = "3.4";
    public const string Adapters = "3.5";
    public const string WindowsProxy = "3.6";
    public const string UdpFlow = "3.7";

    // 4 — настройки
    public const string SettingsSnapshot = "4.1";
    public const string SettingsChanged = "4.2";

    // 5 — серверы и подписки
    public const string ActiveServer = "5.1";
    public const string ServerSaved = "5.2";
    public const string ServersRemoved = "5.3";
    public const string Import = "5.4";
    public const string SubscriptionAdded = "5.5";
    public const string SubscriptionUpdated = "5.6";
    public const string ServerCore = "5.7";
    public const string ServerTest = "5.8";
    public const string SubscriptionRemoved = "5.9";

    // 6 — диагностика
    public const string Diagnostics = "6.1";

    // 7 — обновления
    public const string UpdateCheck = "7.1";
    public const string CoreUpdateInstalled = "7.2";
    public const string AppUpdateDownloaded = "7.3";
    public const string CoreVersionPicked = "7.4";

    // 8 — журнал ядер и движка
    public const string LogLine = "8.1";
    public const string LogSuppressed = "8.2";

    /// <summary>Расшифровка кодов. Порядок — как в файле для сервера.</summary>
    public static IReadOnlyList<(string Code, string Text)> Catalog { get; } =
    [
        (AppStart, "Запуск Тропы"),
        (AppExit, "Выход из Тропы"),
        (AppCrash, "Необработанная ошибка в Тропе"),
        (ServiceAttached, "Служба Тропы на связи"),
        (ServiceLost, "Связь со службой Тропы потеряна"),
        (StartupWarning, "Предупреждение при запуске"),
        (ConnectStart, "Подключение: начало"),
        (Connected, "Подключено"),
        (ConnectFailed, "Подключение не удалось"),
        (Disconnect, "Отключение"),
        (Reconnect, "Переподключение"),
        (CoreExited, "Ядро неожиданно остановилось"),
        (ServiceCoreFailed, "Служба остановила подключение из-за сбоя ядра"),
        (ServiceStoppedOutside, "Службу остановили извне"),
        (ServerUnhealthy, "Фоновая проверка: сервер работает плохо"),
        (EmergencyRollback, "Аварийный возврат настроек сети"),
        (BlockingReleased, "Снята блокировка kill switch"),
        (StaleProxyDisabled, "Выключен оставшийся системный прокси Тропы"),
        (ServiceWait, "Ожидание запуска службы"),
        (BlockingChanged, "Служба держит/сняла блокировку"),
        (ForeignVpn, "Работает другой VPN или клиент обхода"),
        (NetworkChanged, "Сменилась сеть"),
        (WokeUp, "Компьютер проснулся"),
        (ExitRegion, "Страна выхода"),
        (Adapters, "Сетевые адаптеры"),
        (WindowsProxy, "Прокси Windows"),
        (UdpFlow, "Поток UDP за минуту (звонки, видео)"),
        (SettingsSnapshot, "Все настройки"),
        (SettingsChanged, "Изменены настройки"),
        (ActiveServer, "Выбран сервер"),
        (ServerSaved, "Сервер добавлен или изменён"),
        (ServersRemoved, "Серверы удалены"),
        (Import, "Импорт ссылок"),
        (SubscriptionAdded, "Подписка добавлена"),
        (SubscriptionUpdated, "Подписка обновлена"),
        (ServerCore, "Выбрано ядро для сервера"),
        (ServerTest, "Проверка серверов"),
        (SubscriptionRemoved, "Подписка удалена"),
        (Diagnostics, "Диагностика"),
        (UpdateCheck, "Проверка обновлений"),
        (CoreUpdateInstalled, "Установлено обновление ядер"),
        (AppUpdateDownloaded, "Скачано обновление Тропы"),
        (CoreVersionPicked, "Выбрана версия ядра"),
        (LogLine, "Строка журнала"),
        (LogSuppressed, "Пропущены строки журнала (слишком много)"),
    ];
}
