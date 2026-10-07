using Tropa.Ipc;

namespace Tropa.Service;

/// <summary>
/// Корневой фоновый процесс службы. Пока только сообщает о запуске;
/// канал IPC, супервизор ядер и журнал изменений появятся на этапе 3 (docs/08-roadmap.md).
/// </summary>
internal sealed partial class ServiceHost(ILogger<ServiceHost> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(IpcProtocol.Version);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Служба Тропы запущена, протокол IPC v{Version}")]
    private partial void LogStarted(int version);
}
