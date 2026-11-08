using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>
/// Автозапуск портативной Тропы (ADR-063), как в v2rayN: задача Планировщика «при входе этого пользователя,
/// с наивысшими правами» — Windows запускает её без окна UAC, и сразу доступен режим «Весь компьютер».
/// Менять можно только из Тропы с правами администратора. Имя задачи зависит от пути к exe: две копии Тропы
/// в разных папках друг другу не мешают.
/// </summary>
public static class PortableAutostart
{
    public static string TaskName(string exePath) =>
        "Tropa Portable " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(exePath.ToUpperInvariant())))[..8];

    public static string RunValueName(string exePath) => TaskName(exePath);

    /// <summary>
    /// Приводит автозапуск к настройке. null — готово; иначе что сказать человеку. Только с правами администратора
    /// (решение владельца): без прав задачу с наивысшими правами не создать и не убрать, а запуск без прав при входе
    /// давал бы «Весь компьютер» недоступным.
    /// </summary>
    public static string? Sync(bool enabled, string exePath, bool isAdmin, string tempDirectory, IRunKey runKey)
    {
        ArgumentNullException.ThrowIfNull(runKey);
        if (!isAdmin)
            return "Автозапуск портативной Тропы включается и выключается только в Тропе, запущенной от имени администратора.";
        var task = TaskName(exePath);
        // Значение Run от предварительных сборок — убираем, запуск идёт только через задачу.
        runKey.Write(RunValueName(exePath), null);
        if (!enabled)
            return TaskExists(task) && !Schtasks("/Delete", "/TN", task, "/F") ? "Не удалось убрать задачу автозапуска из Планировщика заданий Windows." : null;
        return CreateTask(task, exePath, tempDirectory) ? null : "Не удалось создать задачу автозапуска в Планировщике заданий Windows.";
    }

    internal static string TaskXml(string exePath, string userId) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Тропа (портативная): запуск при входе в Windows с правами администратора — для режима «Весь компьютер».</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(userId)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"{SecurityElement.Escape(exePath)}"</Command>
              <Arguments>{Autostart.Argument}</Arguments>
              <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "")}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    private static bool CreateTask(string task, string exePath, string tempDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        Directory.CreateDirectory(tempDirectory);
        var xml = Path.Combine(tempDirectory, $"autostart-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, TaskXml(exePath, identity.Name), Encoding.Unicode);
            return Schtasks("/Create", "/TN", task, "/XML", xml, "/F");
        }
        finally
        {
            try
            {
                File.Delete(xml);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool TaskExists(string task) => Schtasks("/Query", "/TN", task);

    private static bool Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
                return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
