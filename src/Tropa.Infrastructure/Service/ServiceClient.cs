using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Tropa.Ipc;

namespace Tropa.Infrastructure.Service;

/// <summary>Служба отказала или связь с ней потеряна. Сообщение уже понятно пользователю.</summary>
public sealed class ServiceException(string message) : Exception(message);

/// <summary>
/// Клиент канала управления службой. Перед работой проверяет, что на другом конце — служба
/// Тропы из каталога программы, а не самозванец, создавший канал с тем же именем.
/// </summary>
public sealed partial class ServiceClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Reply>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private Task? _reader;

    private ServiceClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public bool TunSupported { get; private set; }

    public string RuleSetDirectory { get; private set; } = "";

    public string ServiceVersion { get; private set; } = "";

    public event EventHandler<StatusEvent>? Status;

    public event EventHandler<string>? Log;

    public event EventHandler? Disconnected;

    public bool IsConnected => _pipe.IsConnected;

    /// <summary>
    /// Подключается к службе. null — службы нет (не установлена или не запущена): тогда интерфейс
    /// работает сам в режиме «Только браузеры».
    /// </summary>
    public static async Task<ServiceClient?> TryConnectAsync(string pipeName, string? expectedServerPath, TimeSpan timeout, CancellationToken ct)
    {
        // Identification: служба может узнать, кто подключился, но не может действовать от имени пользователя.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        if (expectedServerPath is not null && ServerPath(pipe.SafePipeHandle) is var actual
            && !string.Equals(actual, Path.GetFullPath(expectedServerPath), StringComparison.OrdinalIgnoreCase))
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new ServiceException($"Канал Тропы открыт чужой программой ({actual ?? "неизвестно"}). Подключение через службу отменено.");
        }

        var client = new ServiceClient(pipe);
        client._reader = Task.Run(() => client.ReadLoopAsync(client._cts.Token), CancellationToken.None);
        var hello = await client.SendAsync(new HelloRequest(IpcProtocol.Version, typeof(ServiceClient).Assembly.GetName().Version?.ToString(3) ?? "0"), ct).ConfigureAwait(false);
        if (hello is not HelloReply info)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new ServiceException(hello.Error ?? "Служба не ответила на приветствие.");
        }

        client.TunSupported = info.TunSupported;
        client.RuleSetDirectory = info.RuleSetDirectory;
        client.ServiceVersion = info.ServiceVersion;
        return client;
    }

    private static string? ServerPath(SafePipeHandle handle)
    {
        if (!GetNamedPipeServerProcessId(handle, out var pid))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.MainModule?.FileName is { } path ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException)
        {
            // Процесс службы под SYSTEM: пользователь может не иметь права читать его модули.
            // Тогда сверяем путь через QueryFullProcessImageName, которому достаточно ограниченного доступа.
            return QueryImagePath(pid);
        }
    }

    private static string? QueryImagePath(uint pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid)
            return null;
        var buffer = new char[1024];
        var size = buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? Path.GetFullPath(new string(buffer, 0, size)) : null;
    }

    public async Task<Reply> SendAsync(Request request, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Framing.WriteAsync(_pipe, request with { Id = id }, IpcJsonContext.Default.Request, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _pending.TryRemove(id, out _);
            throw new ServiceException("Связь со службой Тропы потеряна.");
        }
        finally
        {
            _write.Release();
        }

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await Framing.ReadAsync(_pipe, IpcJsonContext.Default.ServerMessage, ct).ConfigureAwait(false);
                switch (message)
                {
                    case null:
                        return;
                    case Reply reply when _pending.TryRemove(reply.Id, out var tcs):
                        tcs.TrySetResult(reply);
                        break;
                    case StatusEvent status:
                        Status?.Invoke(this, status);
                        break;
                    case LogEvent log:
                        Log?.Invoke(this, log.Line);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ProtocolViolationException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            foreach (var (_, tcs) in _pending)
                tcs.TrySetException(new ServiceException("Связь со службой Тропы потеряна."));
            if (!ct.IsCancellationRequested)
                Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_reader is not null)
        {
            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _write.Dispose();
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, [Out] char[] buffer, ref int size);
}
