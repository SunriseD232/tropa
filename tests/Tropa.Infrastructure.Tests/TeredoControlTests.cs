using Tropa.Infrastructure.SystemIntegration;

namespace Tropa.Infrastructure.Tests;

public sealed class TeredoControlTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tropa-teredo");

    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class FakeHost : ITeredoHost
    {
        public string Type = "disabled";
        public string Server = "win1910.ipv6.microsoft.com";
        public int Restarts;
        public List<string> Calls { get; } = [];

        public string ShowState() =>
            $"Teredo Parameters\n---\nType : {Type}\nServer Name : {Server}\nClient Refresh Interval : 30 seconds\nClient Port : unspecified\nState : {(Type == "disabled" ? "offline" : "qualified")}\n";

        public void SetState(string type, string? server)
        {
            Calls.Add($"{type}|{server}");
            Type = type;
            if (server is not null)
                Server = server;
        }

        public void RestartIpHelper() => Restarts++;
    }

    [Fact]
    public void Enable_records_original_and_uninstall_restores_it()
    {
        var host = new FakeHost();
        var journal = new ChangeJournal(Path.Combine(_dir.FullName, "journal.json"));
        var control = new TeredoControl(host, journal);

        var after = control.Enable(DateTimeOffset.UtcNow);
        Assert.True(after!.Working);
        Assert.Equal("enterpriseclient|win1910.ipv6.microsoft.com", host.Calls[^1]);
        Assert.Equal(1, host.Restarts);

        // Повторное включение не затирает исходное «disabled».
        control.Enable(DateTimeOffset.UtcNow);
        var entry = Assert.Single(journal.Pending());
        Assert.Equal("disabled", entry.Original["type"]);

        control.Restore();
        Assert.Equal("disabled|", host.Calls[^1]); // у disabled сервер не задаётся
        Assert.Empty(journal.Pending());
        Assert.Equal("disabled", host.Type);
    }

    [Fact]
    public void Restart_only_restarts_service_and_nothing_to_restore()
    {
        var host = new FakeHost { Type = "enterpriseclient" };
        var journal = new ChangeJournal(Path.Combine(_dir.FullName, "journal.json"));
        var control = new TeredoControl(host, journal);
        Assert.True(control.Restart()!.Working);
        Assert.Equal(1, host.Restarts);
        control.Restore();
        Assert.Empty(host.Calls);
    }
}

public sealed class NetshDecodeTests
{
    [Fact]
    public void Utf8_and_console_codepage_both_read()
    {
        const string text = "Ошибка: основной сервер teredo недостижим через UDP";
        Assert.Equal(text, NetshTeredoHost.Decode(System.Text.Encoding.UTF8.GetBytes(text)));
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var cp866 = System.Text.Encoding.GetEncoding(866).GetBytes(text);
        // В 866 русские буквы — не корректный UTF-8: читается как кодировка консоли (у этой Windows — 866).
        if (System.Globalization.CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ru")
            Assert.Equal(text, NetshTeredoHost.Decode(cp866));
    }
}
