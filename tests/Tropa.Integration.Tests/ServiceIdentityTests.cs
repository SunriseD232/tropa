using System.Diagnostics;
using Tropa.Infrastructure.Service;

namespace Tropa.Integration.Tests;

/// <summary>
/// Проверка службы через диспетчер служб: без прав администратора путь процесса службы (SYSTEM) прочитать
/// нельзя, и Тропа считала свою службу «чужой программой». Живой тест — на установленной Тропе, если она есть.
/// </summary>
public sealed class ServiceIdentityTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Tropa\\Tropa.Service.exe\"", "C:\\Program Files\\Tropa\\Tropa.Service.exe")]
    [InlineData("\"C:\\Program Files\\Tropa\\Tropa.Service.exe\" --flag", "C:\\Program Files\\Tropa\\Tropa.Service.exe")]
    [InlineData("C:\\Tropa\\Tropa.Service.exe --flag", "C:\\Tropa\\Tropa.Service.exe")]
    [InlineData("\"C:\\broken", null)]
    [InlineData("", null)]
    public void Exe_path_is_taken_from_service_command_line(string commandLine, string? expected) =>
        Assert.Equal(expected, ServiceIdentity.ExePath(commandLine));

    [Fact]
    public void Installed_service_is_recognized_without_admin_rights()
    {
        var service = Process.GetProcessesByName("Tropa.Service").FirstOrDefault();
        if (service is null)
            Assert.Skip("Служба Тропы не установлена или не запущена.");
        var path = ServiceIdentity.PathIfServiceProcess(Ipc.IpcProtocol.ServiceName, (uint)service.Id);
        Assert.NotNull(path);
        Assert.EndsWith("Tropa.Service.exe", path, StringComparison.OrdinalIgnoreCase);
        // Чужой номер процесса не подходит.
        Assert.Null(ServiceIdentity.PathIfServiceProcess(Ipc.IpcProtocol.ServiceName, (uint)Environment.ProcessId));
    }
}
