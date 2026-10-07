using System.Diagnostics;
using Tropa.Core.Security;

namespace Tropa.Infrastructure.Cores;

/// <summary>
/// Где лежат ядра и исходные наборы правил и каким хэшам они должны соответствовать.
/// По умолчанию — файлы рядом с программой и вшитые lock-файлы; после обновления — каталог
/// обновления и списки из подписанного манифеста (ADR-023).
/// </summary>
public sealed record CoreLocations(string CoresDirectory, string GeoSourceDirectory)
{
    public Tropa.Core.Cores.CoresLock Cores { get; init; } = PinnedFiles.Cores;

    public Tropa.Core.Cores.GeoLock Geo { get; init; } = PinnedFiles.Geo;

    /// <summary>Номер манифеста, из которого взяты файлы; 0 — файлы из установщика.</summary>
    public long Sequence { get; init; }

    public string Versions => $"sing-box {Cores.Get("sing-box").Version} · Xray {Cores.Get("xray").Version}";

    /// <summary>
    /// Рядом с программой. В отладочной сборке, если рядом пусто, — каталоги cores/ и geo/ репозитория
    /// (их наполняют tools/fetch-cores.ps1 и tools/fetch-geo.ps1 с проверкой хэшей).
    /// </summary>
    public static CoreLocations Default()
    {
        var baseDir = AppContext.BaseDirectory;
        var cores = Path.Combine(baseDir, "cores");
        var geo = Path.Combine(baseDir, "geo");
#if DEBUG
        if (!Directory.Exists(cores))
        {
            var dir = new DirectoryInfo(baseDir);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
                dir = dir.Parent;
            if (dir is not null)
                return new CoreLocations(Path.Combine(dir.FullName, "cores"), Path.Combine(dir.FullName, "geo"));
        }
#endif
        return new CoreLocations(cores, geo);
    }
}

/// <summary>Ядро не запустилось. Сообщение уже очищено от секретов.</summary>
public sealed class CoreStartException(string message) : Exception(message);

/// <summary>
/// Запущенный процесс ядра sing-box (docs/03-architecture.md, §1). На этапе 2 запускается
/// приложением без прав администратора; на этапе 3 этот код переедет в службу.
/// </summary>
public sealed class CoreProcess : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(8);

    private readonly Process _process;
    private readonly JobObject _job;
    private readonly SecretScrubber _scrubber;
    private readonly Action<string> _log;
    private readonly Queue<string> _tail = new();

    private CoreProcess(Process process, JobObject job, SecretScrubber scrubber, Action<string> log)
    {
        _process = process;
        _job = job;
        _scrubber = scrubber;
        _log = log;
    }

    /// <summary>Ядро завершилось само (упало или было убито).</summary>
    public event EventHandler<int>? Exited;

    public bool HasExited => _process.HasExited;

    /// <summary>
    /// Проверяет хэш sing-box.exe, пишет конфиг во временный файл, запускает ядро, ждёт, пока
    /// <paramref name="readinessPort"/> на 127.0.0.1 начнёт принимать соединения, и удаляет конфиг:
    /// ключи доступа не остаются лежать на диске. Строке «started» в логе не доверяем:
    /// при уровне warn её нет.
    /// </summary>
    public static Task<CoreProcess> StartSingBoxAsync(
        CoreLocations locations, string configJson, int readinessPort, string runDirectory, SecretScrubber scrubber, Action<string> log, CancellationToken ct) =>
        StartAsync("sing-box", config => ["run", "-c", config, "-D", runDirectory, "--disable-color"],
            locations, configJson, readinessPort, runDirectory, scrubber, log, ct);

    /// <summary>Xray (гибрид, docs/05-config-generation.md, §1). Те же гарантии: хэш, Job Object, удаление конфига.</summary>
    public static Task<CoreProcess> StartXrayAsync(
        CoreLocations locations, string configJson, int readinessPort, string runDirectory, SecretScrubber scrubber, Action<string> log, CancellationToken ct) =>
        StartAsync("xray", config => ["run", "-c", config],
            locations, configJson, readinessPort, runDirectory, scrubber, log, ct);

    /// <summary>Путь к закреплённому исполняемому файлу ядра.</summary>
    public static string ExecutablePath(CoreLocations locations, string coreName)
    {
        ArgumentNullException.ThrowIfNull(locations);
        return Path.Combine(locations.CoresDirectory, locations.Cores.Get(coreName).Files[0].Target);
    }

    private static async Task<CoreProcess> StartAsync(string coreName, Func<string, string[]> arguments,
        CoreLocations locations, string configJson, int readinessPort, string runDirectory, SecretScrubber scrubber, Action<string> log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var entry = locations.Cores.Get(coreName);
        var exePath = Path.Combine(locations.CoresDirectory, entry.Files[0].Target);

        Directory.CreateDirectory(runDirectory);
        var configPath = Path.Combine(runDirectory, $"config-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, configJson, ct).ConfigureAwait(false);

        CoreProcess? core = null;
        try
        {
            // Файл ядра держим открытым на чтение без права записи для других, пока процесс не создан.
            using (PinnedFiles.OpenVerified(exePath, entry.Files[0].Sha256))
            {
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = runDirectory,
                };
                foreach (var arg in arguments(configPath))
                    psi.ArgumentList.Add(arg);

                var job = new JobObject();
                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                core = new CoreProcess(process, job, scrubber, log);
                process.OutputDataReceived += (_, e) => core.OnLine(e.Data);
                process.ErrorDataReceived += (_, e) => core.OnLine(e.Data);
                process.Exited += (_, _) => core.OnExited();

                if (!process.Start())
                    throw new CoreStartException($"Не удалось запустить {coreName}.");
                job.Assign(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            await core.WaitReadyAsync(coreName, readinessPort, ct).ConfigureAwait(false);
            return core;
        }
        catch
        {
            if (core is not null)
                await core.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            TryDelete(configPath);
        }
    }

    private void OnLine(string? line)
    {
        if (line is null)
            return;
        var clean = _scrubber.Scrub(line);
        lock (_tail)
        {
            _tail.Enqueue(clean);
            while (_tail.Count > 20)
                _tail.Dequeue();
        }

        _log(clean);
    }

    private void OnExited() => Exited?.Invoke(this, _process.ExitCode);

    private async Task WaitReadyAsync(string coreName, int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new CoreStartException($"Ядро {coreName} завершилось при запуске:\n" + Tail());
            try
            {
                using var probe = new System.Net.Sockets.TcpClient();
                await probe.ConnectAsync(System.Net.IPAddress.Loopback, port, ct).ConfigureAwait(false);
                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        throw new CoreStartException($"Ядро {coreName} не запустилось за 8 секунд:\n" + Tail());
    }

    private string Tail()
    {
        lock (_tail)
            return string.Join('\n', _tail);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Exited = null; // штатная остановка — не авария
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            _process.Dispose();
            _job.Dispose();
        }
    }
}
