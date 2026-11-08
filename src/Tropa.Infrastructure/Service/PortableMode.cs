using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Tropa.Infrastructure.Service;

/// <summary>
/// Портативная Тропа (ADR-063): папка из zip, ничего не устанавливается. Признак — файл-метка рядом с
/// Tropa.exe. Данные лежат в папке <c>data</c> рядом с программой. Без прав администратора работает только
/// «Системный прокси»; с правами Тропа сама запускает службу из своей папки, как v2rayN запускает ядро.
/// </summary>
public static class PortableMode
{
    public const string MarkerFile = "Tropa.portable";

    public const string DataFolder = "data";

    /// <summary>Каталог программы, если это портативная Тропа; иначе null.</summary>
    public static string? Directory { get; } = Detect(AppContext.BaseDirectory);

    public static bool IsPortable => Directory is not null;

    public static bool IsAdmin { get; } = CheckAdmin();

    internal static string? Detect(string baseDir) =>
        File.Exists(Path.Combine(baseDir, MarkerFile)) ? Path.GetFullPath(baseDir) : null;

    public static EnginePaths Paths(string dir) =>
        new(Path.Combine(dir, DataFolder), Path.Combine(dir, DataFolder, "local"));

    public static string ServiceDataDirectory(string dir) => Path.Combine(dir, DataFolder, "service");

    private static bool CheckAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>
/// Служба портативной Тропы: Tropa.Service.exe из папки программы, запущенный потомком интерфейса с правами
/// администратора. Канал — своё случайное имя на каждый запуск; клиент сверяет путь и номер процесса службы,
/// служба — путь и номер процесса интерфейса. Закрылся интерфейс — служба останавливается сама и откатывает
/// изменения системы.
/// </summary>
public sealed class PortableService(string directory)
{
    private readonly string _pipeName = "Tropa.Portable." + Ipc.IpcProtocol.Version + "." + Guid.NewGuid().ToString("N");
    private readonly Lock _lock = new();
    private Process? _process;

    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(15);

    /// <summary>Почему служба не запустилась (для подсказки «Весь компьютер недоступен»); null — не пытались или всё в порядке.</summary>
    public string? LastError { get; private set; }

    public string ServiceExe => Path.Combine(directory, "Tropa.Service.exe");

    public Func<CancellationToken, Task<ServiceClient?>> Connector() => ConnectAsync;

    private async Task<ServiceClient?> ConnectAsync(CancellationToken ct)
    {
        if (!PortableMode.IsAdmin)
            return null;
        Process? process;
        try
        {
            process = EnsureStarted();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            LastError = "Не удалось запустить службу из папки Тропы: " + ex.Message;
            return null;
        }

        if (process is null)
            return null;
        var until = DateTime.UtcNow + StartWait;
        while (true)
        {
            if (process.HasExited)
            {
                LastError = $"Служба из папки Тропы сразу завершилась (код {process.ExitCode}).";
                return null;
            }

            var client = await ServiceClient.TryConnectAsync(_pipeName, ServiceExe, TimeSpan.FromMilliseconds(500), ct, process.Id).ConfigureAwait(false);
            if (client is not null)
            {
                LastError = null;
                return client;
            }

            if (DateTime.UtcNow > until)
            {
                LastError = "Служба из папки Тропы не ответила.";
                return null;
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    private Process? EnsureStarted()
    {
        lock (_lock)
        {
            if (_process is { HasExited: false })
                return _process;
            _process?.Dispose();
            _process = null;
            if (!File.Exists(ServiceExe))
            {
                LastError = "В папке Тропы нет Tropa.Service.exe — распакуйте архив целиком.";
                return null;
            }

            var info = new ProcessStartInfo(ServiceExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory,
            };
            info.ArgumentList.Add("--portable");
            info.ArgumentList.Add("--pipe");
            info.ArgumentList.Add(_pipeName);
            info.ArgumentList.Add("--parent");
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--data");
            info.ArgumentList.Add(PortableMode.ServiceDataDirectory(directory));
            _process = Process.Start(info);
            return _process;
        }
    }
}
