using System.Diagnostics;
using System.Text;
using Tropa.Core.Games;
using Tropa.Core.Model;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>Система для Teredo (ADR-065). Абстракция для тестов без прав и без настоящего netsh.</summary>
public interface ITeredoHost
{
    /// <summary>Вывод <c>netsh interface teredo show state</c>.</summary>
    string ShowState();

    /// <summary><c>netsh interface teredo set state type=… [servername=…]</c>. Нужны права администратора.</summary>
    void SetState(string type, string? server);

    /// <summary>Перезапуск службы «Вспомогательная служба IP» (iphlpsvc). Нужны права администратора.</summary>
    void RestartIpHelper();
}

/// <summary>Чтение состояния Teredo без прав: для интерфейса, когда службы нет.</summary>
public static class TeredoProbe
{
    public static TeredoStatus? Read() => TeredoStatus.Parse(NetshTeredoHost.Netsh("interface teredo show state"));
}

/// <summary>
/// Teredo через netsh (ADR-065). Подписи вывода переведены, а значения нет — разбор в <see cref="TeredoStatus"/>.
/// Вывод netsh — в OEM-кодировке консоли (у русской Windows 866).
/// </summary>
public sealed class NetshTeredoHost(Action restartIpHelper) : ITeredoHost
{
    public string ShowState() => Netsh("interface teredo show state");

    public void SetState(string type, string? server)
    {
        var args = "interface teredo set state type=" + Safe(type) + (string.IsNullOrEmpty(server) ? "" : " servername=" + Safe(server));
        var output = Netsh(args);
        if (!output.Contains("OK", StringComparison.OrdinalIgnoreCase) && !output.Contains("ОК", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("netsh не принял настройку Teredo: " + output.Trim());
    }

    public void RestartIpHelper() => restartIpHelper();

    /// <summary>Только буквы, цифры, точка и дефис: значения подставляются в командную строку.</summary>
    private static string Safe(string value)
    {
        if (value.Length is 0 or > 253 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
            throw new ArgumentException("Недопустимое значение для netsh: " + value, nameof(value));
        return value;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetOEMCP();

    internal static string Netsh(string arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("netsh не запустился.");
        var stdout = ReadAllAsync(p.StandardOutput.BaseStream);
        var stderr = ReadAllAsync(p.StandardError.BaseStream);
        if (!p.WaitForExit(15_000))
        {
            p.Kill();
            throw new TimeoutException("netsh не ответил за 15 секунд.");
        }

        return Decode(stdout.GetAwaiter().GetResult()) + Decode(stderr.GetAwaiter().GetResult());
    }

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>
    /// netsh с перенаправленным выводом пишет то UTF-8, то кодировку консоли (у русской Windows — 866):
    /// проверено на компьютере владельца. Корректный UTF-8 читаем как UTF-8, иначе — как кодировку консоли.
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(GetOEMCP()).GetString(bytes);
        }
    }
}

/// <summary>
/// Включение и перезапуск Teredo службой (ADR-065). Прежние тип и сервер пишутся в журнал изменений
/// (правило 5) и возвращаются только при удалении Тропы: возврат при отключении снова ломал бы игры.
/// </summary>
public sealed class TeredoControl(ITeredoHost host, ChangeJournal journal)
{
    public const string JournalKind = "teredo";

    public TeredoStatus? Read() => TeredoStatus.Parse(host.ShowState());

    /// <summary>Включить Teredo с сервером Microsoft (у type=default Windows оставляет его выключенным).</summary>
    public TeredoStatus? Enable(DateTimeOffset now)
    {
        var before = Read();
        if (before is not null)
            journal.Record(JournalKind, new Dictionary<string, string?> { ["type"] = before.Type, ["server"] = before.Server }, now);
        host.SetState("enterpriseclient", GameDefaults.TeredoServer);
        host.RestartIpHelper();
        return Read();
    }

    /// <summary>Перезапустить iphlpsvc: после пересоздания адаптера TUN Teredo сам не поднимается.</summary>
    public TeredoStatus? Restart()
    {
        host.RestartIpHelper();
        return Read();
    }

    /// <summary>Вернуть Teredo, каким он был до Тропы (удаление Тропы).</summary>
    public void Restore()
    {
        var entry = journal.Pending().FirstOrDefault(e => e.Kind == JournalKind);
        if (entry is null)
            return;
        var type = entry.Original.GetValueOrDefault("type");
        if (!string.IsNullOrEmpty(type))
        {
            var server = entry.Original.GetValueOrDefault("server");
            // Сервер у «disabled» не задаётся; у остальных — прежний.
            host.SetState(type, type.Equals("disabled", StringComparison.OrdinalIgnoreCase) ? null : server);
            host.RestartIpHelper();
        }

        journal.Complete(JournalKind);
    }
}
