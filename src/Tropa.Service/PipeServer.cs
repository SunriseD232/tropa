using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Tropa.Ipc;

namespace Tropa.Service;

/// <summary>
/// Канал управления службой (docs/03-architecture.md, §3).
/// Права на канал: SYSTEM и администраторы — полностью, интерактивные пользователи — чтение и запись,
/// сетевой доступ запрещён. Каждого клиента дополнительно проверяет <see cref="IClientVerifier"/>.
/// Управлять может один клиент одновременно; когда он отключается, подключение останавливается.
/// </summary>
internal sealed partial class PipeServer(ServiceOptions options, ILogger<PipeServer> logger) : BackgroundService
{
    private readonly Lock _sessionsLock = new();
    private readonly List<Session> _sessions = [];
    private CoreSupervisor? _supervisor;
    private Session? _controller;

    public static string Version => typeof(PipeServer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _supervisor = new CoreSupervisor(options, Broadcast, line => Broadcast(new LogEvent(line)));
        _supervisor.RecoverAfterCrash();
        LogStarted(options.PipeName, options.Privileged);

        var first = true;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = CreatePipe(first);
                }
                catch (UnauthorizedAccessException)
                {
                    // FirstPipeInstance: канал с таким именем уже создан кем-то другим — это попытка
                    // подменить службу. Не работаем, чтобы клиенты не подключились к самозванцу.
                    LogPipeSquatted(options.PipeName);
                    throw;
                }

                first = false;
                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    break;
                }

                var reason = options.ClientVerifier.Verify(pipe);
                if (reason is not null)
                {
                    LogRejected(reason);
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var session = new Session(pipe);
                lock (_sessionsLock)
                    _sessions.Add(session);
                _ = RunSessionAsync(session, stoppingToken);
            }
        }
        finally
        {
            await _supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private NamedPipeServerStream CreatePipe(bool first)
    {
        var security = new PipeSecurity();
        using var me = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        var pipeOptions = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(options.PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, pipeOptions, 0, 0, security);
    }

    private async Task RunSessionAsync(Session session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await Framing.ReadAsync(session.Pipe, IpcJsonContext.Default.Request, ct).ConfigureAwait(false);
                if (request is null)
                    break;
                var reply = await HandleAsync(session, request, ct).ConfigureAwait(false);
                await session.SendAsync(reply, ct).ConfigureAwait(false);
            }
        }
        catch (ProtocolViolationException ex)
        {
            LogProtocolViolation(ex.Message);
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_sessionsLock)
                _sessions.Remove(session);
            if (ReferenceEquals(_controller, session))
            {
                // Интерфейс закрылся или упал: подключение без интерфейса никто не сможет выключить.
                _controller = null;
                await _supervisor!.StopAsync().ConfigureAwait(false);
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ServerMessage> HandleAsync(Session session, Request request, CancellationToken ct)
    {
        if (!session.Greeted && request is not HelloRequest)
            return new Reply(request.Id, false, "Сначала нужно представиться (hello).");

        switch (request)
        {
            case HelloRequest hello:
                if (hello.Protocol != IpcProtocol.Version)
                    return new Reply(hello.Id, false, $"Версии не совпадают: интерфейс {hello.Protocol}, служба {IpcProtocol.Version}. Обновите Тропу.");
                session.Greeted = true;
                return new HelloReply(hello.Id, IpcProtocol.Version, Version, options.Privileged, options.GeoDirectory);

            case StatusRequest status:
                await session.SendAsync(_supervisor!.Status, ct).ConfigureAwait(false);
                return new Reply(status.Id, true, null);

            case StartRequest start:
                lock (_sessionsLock)
                {
                    if (_controller is not null && !ReferenceEquals(_controller, session))
                        return new Reply(start.Id, false, "Тропой уже управляет другой пользователь этого компьютера.");
                    _controller = session;
                }

                var error = await _supervisor!.StartAsync(start, ct).ConfigureAwait(false);
                return new Reply(start.Id, error is null, error);

            case StopRequest stop:
                if (_controller is not null && !ReferenceEquals(_controller, session))
                    return new Reply(stop.Id, false, "Тропой управляет другой пользователь этого компьютера.");
                await _supervisor!.StopAsync().ConfigureAwait(false);
                _controller = null;
                return new Reply(stop.Id, true, null);

            default:
                return new Reply(request.Id, false, "Неизвестная команда.");
        }
    }

    private void Broadcast(ServerMessage message)
    {
        Session[] sessions;
        lock (_sessionsLock)
            sessions = [.. _sessions.Where(s => s.Greeted)];
        foreach (var s in sessions)
            _ = s.SendAsync(message, CancellationToken.None);
    }

    private sealed class Session(NamedPipeServerStream pipe) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public NamedPipeServerStream Pipe { get; } = pipe;

        public bool Greeted { get; set; }

        public async Task SendAsync(ServerMessage message, CancellationToken ct)
        {
            try
            {
                await _write.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                await Framing.WriteAsync(Pipe, message, IpcJsonContext.Default.ServerMessage, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                try
                {
                    _write.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Pipe.DisposeAsync().ConfigureAwait(false);
            _write.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Служба Тропы слушает канал {Pipe}, права системы: {Privileged}")]
    private partial void LogStarted(string pipe, bool privileged);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Клиент отклонён: {Reason}")]
    private partial void LogRejected(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Нарушение протокола: {Reason}")]
    private partial void LogProtocolViolation(string reason);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Канал {Pipe} уже существует — кто-то пытается выдать себя за службу Тропы. Служба остановлена.")]
    private partial void LogPipeSquatted(string pipe);
}
