using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Tropa.Core.Model;
using Tropa.Infrastructure.Cores;

namespace Tropa.Integration.Tests;

/// <summary>
/// Правки гео-баз (ADR-039) на настоящем sing-box: «набор И НЕ убранное» действительно исключает
/// адреса, добавленное срабатывает, а закреплённые .srs читаются через «rule-set decompile».
/// </summary>
public sealed class GeoEditsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Не найден корень репозитория.");
    }

    private static CoreLocations Locations => new(Path.Combine(RepoRoot(), "cores"), Path.Combine(RepoRoot(), "geo"));

    private static string Exe => Path.Combine(Locations.CoresDirectory, "sing-box.exe");

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Pinned_srs_is_read_through_decompile()
    {
        if (!File.Exists(Exe) || !File.Exists(Path.Combine(Locations.GeoSourceDirectory, "geoip-ru.srs")))
            Assert.Skip("Ядра или наборы правил не скачаны: tools/fetch-cores.ps1, tools/fetch-geo.ps1");
        var ct = TestContext.Current.CancellationToken;
        var run = Directory.CreateTempSubdirectory("tropa-geo-");
        try
        {
            var ip = await GeoListReader.ReadAsync(Locations, "geoip-ru", run.FullName, ct);
            Assert.True(ip.Entries.Count > 1000, $"В geoip-ru всего {ip.Entries.Count} записей.");
            Assert.All(ip.Entries.Take(50), e => Assert.Equal(GeoEntryKind.Cidr, e.Kind));

            var sites = await GeoListReader.ReadAsync(Locations, "geosite-category-ru", run.FullName, ct);
            Assert.Contains(sites.Entries, e => e.Kind is GeoEntryKind.Domain or GeoEntryKind.Suffix);
            Assert.Empty(Directory.EnumerateFiles(run.FullName)); // временный JSON удалён
        }
        finally
        {
            run.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Removed_entries_are_excluded_and_added_entries_match()
    {
        if (!File.Exists(Exe))
            Assert.Skip("sing-box.exe не найден: запустите tools/fetch-cores.ps1");
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("tropa-geo-live-");
        var sitePort = FreePort();
        using var allowed = new MiniSite(IPAddress.Parse("127.0.0.2"), sitePort);  // убран из набора → не блокируется
        using var blocked = new MiniSite(IPAddress.Parse("127.0.0.3"), sitePort);  // в наборе → блокируется
        using var addedSite = new MiniSite(IPAddress.Parse("127.0.1.5"), sitePort); // добавлен → блокируется
        Process? core = null;
        try
        {
            // Набор как закреплённый файл: бинарный .srs.
            var source = Path.Combine(work.FullName, "set.json");
            var srs = Path.Combine(work.FullName, "set.srs");
            await File.WriteAllTextAsync(source, """{ "version": 3, "rules": [ { "ip_cidr": [ "127.0.0.0/24" ] } ] }""", ct);
            Assert.Equal(0, await RunAsync(["rule-set", "compile", source, "-o", srs], ct));

            var port = FreePort();
            var config = Path.Combine(work.FullName, "config.json");
            var srsJson = System.Text.Json.JsonSerializer.Serialize(srs);
            // Та же форма, что строит генератор (golden 14-geo-edits): «добавленное ИЛИ (набор И НЕ убранное)».
            await File.WriteAllTextAsync(config, $$"""
            {
              "log": { "level": "warn" },
              "inbounds": [ { "type": "mixed", "listen": "127.0.0.1", "listen_port": {{port}} } ],
              "outbounds": [ { "type": "direct", "tag": "direct" } ],
              "route": {
                "rule_set": [
                  { "type": "local", "tag": "set", "format": "binary", "path": {{srsJson}} },
                  { "type": "inline", "tag": "set+added", "rules": [ { "ip_cidr": [ "127.0.1.5/32" ] } ] },
                  { "type": "inline", "tag": "set+removed", "rules": [ { "ip_cidr": [ "127.0.0.2/32" ] } ] }
                ],
                "rules": [
                  {
                    "type": "logical", "mode": "or",
                    "rules": [
                      { "rule_set": [ "set+added" ] },
                      { "type": "logical", "mode": "and", "rules": [ { "rule_set": [ "set" ] }, { "rule_set": [ "set+removed" ], "invert": true } ] }
                    ],
                    "action": "reject"
                  }
                ],
                "final": "direct"
              }
            }
            """, ct);
            var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "run", "-c", config })
                psi.ArgumentList.Add(a);
            core = Process.Start(psi)!;
            await Task.Delay(1000, ct);

            using var http = new HttpClient(new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(5) };
            Assert.True(await Reachable(http, $"http://127.0.0.2:{sitePort}/", ct), "Убранный адрес должен идти мимо правила.");
            Assert.False(await Reachable(http, $"http://127.0.0.3:{sitePort}/", ct), "Адрес из набора должен попасть под правило.");
            Assert.False(await Reachable(http, $"http://127.0.1.5:{sitePort}/", ct), "Добавленный адрес должен попасть под правило.");
        }
        finally
        {
            if (core is { HasExited: false })
                core.Kill();
            core?.Dispose();
            work.Delete(recursive: true);
        }
    }

    private static async Task<bool> Reachable(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var r = await http.GetAsync(url, ct);
            return r.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    /// <summary>Крошечный HTTP-сервер: на любой запрос отвечает 200.</summary>
    private sealed class MiniSite : IDisposable
    {
        private readonly TcpListener _listener;

        public MiniSite(IPAddress address, int port)
        {
            _listener = new TcpListener(address, port);
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[4096];
                            _ = await stream.ReadAsync(buffer);
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
