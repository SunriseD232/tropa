using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Core.Security;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Testing;

namespace Tropa.Integration.Tests;

/// <summary>
/// Shadowsocks-2022 и Hysteria2 по-настоящему: сервер sing-box на 127.0.0.1 (для Hysteria2 —
/// самоподписанный сертификат), клиентская сторона из нашего генератора, «сайт» — локальный ответчик.
/// </summary>
public sealed class NewProtocolTests : IAsyncLifetime
{
    private const string SsKey = "AAECAwQFBgcICQoLDA0ODw==";
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-proto-");
    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _site = new(IPAddress.Loopback, 0);
    private Process? _server;
    private int _ssPort;
    private int _hyPort;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static CoreLocations Cores => new(Path.Combine(RepoRoot(), "cores"), Path.Combine(RepoRoot(), "geo"));

    private static bool CoresPresent => File.Exists(Path.Combine(Cores.CoresDirectory, "sing-box.exe"));

    private static int FreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    public async ValueTask InitializeAsync()
    {
        if (!CoresPresent)
            return;
        _site.Start();
        _ = Task.Run(SiteLoopAsync);

        // Самоподписанный сертификат для QUIC-сервера Hysteria2.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=hy.test", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("hy.test");
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certPath = Path.Combine(_work.FullName, "cert.pem");
        var keyPath = Path.Combine(_work.FullName, "key.pem");
        await File.WriteAllTextAsync(certPath, cert.ExportCertificatePem());
        await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());

        _ssPort = FreeTcpPort();
        _hyPort = FreeUdpPort();
        var cfg = Path.Combine(_work.FullName, "server.json");
        var esc = (string p) => p.Replace(@"\", @"\\", StringComparison.Ordinal);
        await File.WriteAllTextAsync(cfg, $$"""
        { "log": { "level": "warn" },
          "inbounds": [
            { "type": "shadowsocks", "listen": "127.0.0.1", "listen_port": {{_ssPort}}, "method": "2022-blake3-aes-128-gcm", "password": "{{SsKey}}" },
            { "type": "hysteria2", "listen": "127.0.0.1", "listen_port": {{_hyPort}}, "users": [ { "password": "hy-pass" } ],
              "obfs": { "type": "salamander", "password": "hy-obfs" },
              "tls": { "enabled": true, "server_name": "hy.test", "alpn": ["h3"], "certificate_path": "{{esc(certPath)}}", "key_path": "{{esc(keyPath)}}" } }
          ],
          "outbounds": [ { "type": "direct" } ] }
        """);
        var psi = new ProcessStartInfo(Path.Combine(Cores.CoresDirectory, "sing-box.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "run", "-c", cfg })
            psi.ArgumentList.Add(a);
        _server = Process.Start(psi);
        await Task.Delay(1500);
    }

    private async Task SiteLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _site.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var s = client.GetStream();
                            var buffer = new byte[4096];
                            var head = "";
                            while (!head.Contains("\r\n\r\n", StringComparison.Ordinal))
                            {
                                var n = await s.ReadAsync(buffer);
                                if (n == 0)
                                    return;
                                head += System.Text.Encoding.ASCII.GetString(buffer, 0, n);
                            }

                            await s.WriteAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray());
                        }
                        catch (IOException)
                        {
                        }
                    }
                });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    [Fact]
    public async Task Shadowsocks2022_and_hysteria2_carry_traffic()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        var ss = ShareLink.Parse($"ss://2022-blake3-aes-128-gcm:{Uri.EscapeDataString(SsKey)}@127.0.0.1:{_ssPort}#SS").Profile!;
        var hy = ShareLink.Parse($"hysteria2://hy-pass@127.0.0.1:{_hyPort}?sni=hy.test&insecure=1&obfs=salamander&obfs-password=hy-obfs#HY").Profile!;
        var settings = new AppSettings
        {
            Connection = new ConnectionSettings { TestUrl = $"http://127.0.0.1:{((IPEndPoint)_site.LocalEndpoint).Port}/" },
        };
        var run = Directory.CreateDirectory(Path.Combine(_work.FullName, "run")).FullName;

        var results = await ServerTester.TestAsync([ss, hy], [ss, hy], settings, TestKinds.Tcp | TestKinds.Delay, Cores, run,
            SecretScrubber.PatternsOnly, _ => { }, null, ct);

        Assert.True(results[ss.Id].DelayMs is not null, "Shadowsocks-2022: " + results[ss.Id].Error);
        Assert.NotNull(results[ss.Id].TcpMs);
        Assert.True(results[hy.Id].DelayMs is not null, "Hysteria2: " + results[hy.Id].Error);
        Assert.Null(results[hy.Id].TcpMs); // у QUIC нет TCP-порта — пинг пропускается

        // Неверный пароль Hysteria2 — рукопожатие не проходит.
        var wrong = hy with { Id = Guid.NewGuid(), Credential = new Secret("wrong") };
        var bad = await ServerTester.TestAsync([wrong], [wrong], settings, TestKinds.Delay, Cores, run, SecretScrubber.PatternsOnly, _ => { }, null, ct,
            new TesterOptions { RequestTimeout = TimeSpan.FromSeconds(3) });
        Assert.Null(bad[wrong.Id].DelayMs);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _site.Stop();
        _site.Dispose();
        if (_server is { HasExited: false })
            _server.Kill();
        _server?.Dispose();
        _stop.Dispose();
        try
        {
            _work.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
