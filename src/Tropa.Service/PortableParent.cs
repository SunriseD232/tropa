using System.ComponentModel;
using System.Diagnostics;

namespace Tropa.Service;

/// <summary>Интерфейс портативной Тропы, запустивший службу (ADR-063).</summary>
internal sealed record PortableParent(int ProcessId)
{
    /// <summary>Канал портативной службы: своё имя на каждый запуск, чтобы не столкнуться с установленной службой.</summary>
    public const string PipePrefix = "Tropa.Portable.";

    public static string? PathOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName is { } path ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Служба портативной Тропы живёт, пока жив её интерфейс: закрылся или упал — останавливаемся штатно,
/// и CoreSupervisor откатывает DNS, loopback-исключения и kill switch.
/// </summary>
internal sealed partial class PortableParentWatcher(PortableParent parent, IHostApplicationLifetime lifetime, ILogger<PortableParentWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var process = Process.GetProcessById(parent.ProcessId);
            await process.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // Родителя уже нет.
        }
        catch (OperationCanceledException)
        {
            return;
        }

        LogParentExited(parent.ProcessId);
        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Интерфейс портативной Тропы (pid {Pid}) закрылся — служба останавливается.")]
    private partial void LogParentExited(int pid);
}
