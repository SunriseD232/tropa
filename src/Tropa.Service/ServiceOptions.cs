using System.Security.Principal;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.SystemIntegration;

namespace Tropa.Service;

/// <summary>Параметры службы. В тестах подменяются: другое имя канала, временный каталог, свой проверяльщик клиента.</summary>
internal sealed record ServiceOptions
{
    public required string PipeName { get; init; }

    /// <summary>Каталог данных: %ProgramData%\Tropa у службы, отдельный каталог пользователя в режиме разработки.</summary>
    public required string DataDirectory { get; init; }

    public required CoreLocations Cores { get; init; }

    public required IClientVerifier ClientVerifier { get; init; }

    /// <summary>Есть ли права на TUN и системные настройки (служба под SYSTEM или запуск от администратора).</summary>
    public required bool Privileged { get; init; }

    /// <summary>Политика DNS в HKLM; null — недоступна (нет прав).</summary>
    public IRegistryValues? DnsRegistry { get; init; }

    public string GeoDirectory => Path.Combine(DataDirectory, "geo");
    public string RunDirectory => Path.Combine(DataDirectory, "run");
    public string JournalPath => Path.Combine(DataDirectory, "journal.json");

    public static bool IsPrivileged()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static ServiceOptions Production()
    {
        var privileged = IsPrivileged();
        var baseDir = AppContext.BaseDirectory;
        var expectedClient = Path.Combine(baseDir, "Tropa.exe");
#if DEBUG
        // Только для разработки: интерфейс собирается в свой каталог, а не рядом со службой.
        if (Environment.GetEnvironmentVariable("TROPA_DEV_CLIENT") is { Length: > 0 } devClient)
            expectedClient = Path.GetFullPath(devClient);
#endif
        var dataDir = privileged
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tropa")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tropa", "service-dev");
        return new ServiceOptions
        {
            PipeName = Ipc.IpcProtocol.PipeName,
            DataDirectory = dataDir,
            Cores = CoreLocations.Default(),
            ClientVerifier = new ExecutablePathVerifier(expectedClient),
            Privileged = privileged,
            DnsRegistry = privileged ? new DnsClientPolicyRegistry() : null,
        };
    }
}
