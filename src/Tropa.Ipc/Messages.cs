using System.Text.Json.Serialization;

namespace Tropa.Ipc;

// Протокол App ↔ Service (docs/03-architecture.md, §3). Любое несовместимое изменение
// повышает IpcProtocol.Version: старый клиент не должен управлять новой службой.

/// <summary>Режим, который служба должна обеспечить.</summary>
public enum CaptureModeDto { Tun, SystemProxy, PortsOnly }

public enum ServiceState { Idle, Starting, Running, Stopping, Failed }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloRequest), "hello")]
[JsonDerivedType(typeof(StartRequest), "start")]
[JsonDerivedType(typeof(StopRequest), "stop")]
[JsonDerivedType(typeof(StatusRequest), "status")]
public abstract record Request
{
    /// <summary>Номер запроса: ответ приходит с тем же номером.</summary>
    public int Id { get; init; }
}

public sealed record HelloRequest(int Protocol, string ClientVersion) : Request;

/// <summary>
/// Запуск ядра. Конфиг генерирует интерфейс (там доступны секреты пользователя, ADR-006),
/// служба повторно проверяет его ConfigGuard и ничему в нём не доверяет.
/// </summary>
public sealed record StartRequest(
    string SingBoxConfig,
    CaptureModeDto Mode,
    int ReadinessPort,
    bool AllowLanInbound,
    bool DisableSmartNameResolution) : Request;

public sealed record StopRequest : Request;

public sealed record StatusRequest : Request;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Reply), "reply")]
[JsonDerivedType(typeof(HelloReply), "helloReply")]
[JsonDerivedType(typeof(StatusEvent), "statusEvent")]
[JsonDerivedType(typeof(LogEvent), "logEvent")]
public record ServerMessage;

/// <summary>Ответ на запрос: успех или понятная пользователю причина отказа (без секретов).</summary>
public record Reply(int Id, bool Ok, string? Error) : ServerMessage;

public sealed record HelloReply(int Id, int Protocol, string ServiceVersion, bool TunSupported, string RuleSetDirectory)
    : Reply(Id, true, null);

public sealed record StatusEvent(ServiceState State, string? Message, DateTimeOffset? Since) : ServerMessage;

/// <summary>Строка журнала ядра, уже очищенная от секретов на стороне службы.</summary>
public sealed record LogEvent(string Line) : ServerMessage;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(ServerMessage))]
public sealed partial class IpcJsonContext : JsonSerializerContext;
