using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Tropa.Infrastructure.SystemIntegration;

namespace Tropa.Infrastructure.Tests;

/// <summary>Автозапуск портативной Тропы (ADR-063): задача Планировщика с правами, как в v2rayN.</summary>
public sealed class PortableAutostartTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Task_xml_runs_this_exe_at_logon_with_highest_rights()
    {
        var xml = XDocument.Parse(PortableAutostart.TaskXml(@"C:\Users\a&b\Tropa <x>\Tropa.exe", @"PC\user"));
        Assert.Equal("HighestAvailable", xml.Descendants(Ns + "RunLevel").Single().Value);
        Assert.Equal(@"PC\user", xml.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value);
        Assert.Equal("\"C:\\Users\\a&b\\Tropa <x>\\Tropa.exe\"", xml.Descendants(Ns + "Command").Single().Value);
        Assert.Equal(Autostart.Argument, xml.Descendants(Ns + "Arguments").Single().Value);
    }

    [Fact]
    public void Task_name_depends_on_folder()
    {
        Assert.Equal(PortableAutostart.TaskName(@"C:\A\Tropa.exe"), PortableAutostart.TaskName(@"c:\a\tropa.exe"));
        Assert.NotEqual(PortableAutostart.TaskName(@"C:\A\Tropa.exe"), PortableAutostart.TaskName(@"C:\B\Tropa.exe"));
    }

    private sealed class MemoryRunKey : IRunKey
    {
        public Dictionary<string, string?> Values { get; } = [];

        public string? Read(string name) => Values.GetValueOrDefault(name);

        public void Write(string name, string? value) => Values[name] = value;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Without_admin_rights_autostart_is_not_changed(bool enabled)
    {
        var key = new MemoryRunKey();
        var message = PortableAutostart.Sync(enabled, @"C:\Nonexistent\Tropa.exe", isAdmin: false, Path.GetTempPath(), key);
        Assert.Contains("от имени администратора", message, StringComparison.Ordinal);
        Assert.Empty(key.Values);
    }

    [Fact]
    public void Windows_accepts_the_task_xml()
    {
        // Без прав задачу «с наивысшими правами» не зарегистрировать — проверяем ту же разметку с обычным уровнем
        // под временным именем и сразу удаляем.
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var xml = PortableAutostart.TaskXml(@"C:\Nonexistent\Tropa.exe", identity.Name).Replace("HighestAvailable", "LeastPrivilege", StringComparison.Ordinal);
        var file = Path.Combine(Path.GetTempPath(), $"tropa-task-{Guid.NewGuid():N}.xml");
        var name = "Tropa Test " + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            Assert.Equal(0, Schtasks("/Create", "/TN", name, "/XML", file, "/F"));
        }
        finally
        {
            Schtasks("/Delete", "/TN", name, "/F");
            File.Delete(file);
        }
    }

    private static int Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
