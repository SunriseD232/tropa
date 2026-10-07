using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Tropa.Core.Updates;

// Инструмент выпуска (docs/10-release.md). Закрытый ключ хранится только у владельца, зашифрованным
// паролем, и никогда не попадает в репозиторий.
//
//   tropa-release keygen  --out <файл.pem>                 новый ключ; печатает публичный ключ для tools/update.config.json
//   tropa-release manifest --sequence N [--expires-days 90] [--app-version X --app-url URL --installer setup.exe] --out manifest.json
//   tropa-release sign    --key <файл.pem> --in manifest.json     создаёт manifest.json.sig и сразу проверяет её
//   tropa-release verify  --in manifest.json                       проверка подписи публичным ключом из tools/update.config.json

namespace Tropa.Release;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = Options(args.Skip(1));
            return args.FirstOrDefault() switch
            {
                "keygen" => KeyGen(Required(options, "out")),
                "manifest" => Manifest(options),
                "sign" => Sign(Required(options, "key"), Required(options, "in")),
                "verify" => Verify(Required(options, "in")),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or CryptographicException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Команды: keygen, manifest, sign, verify. Подробности — в docs/10-release.md.");
        return 2;
    }

    private static Dictionary<string, string> Options(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
                key = a[2..];
            else if (key is not null)
            {
                result[key] = a;
                key = null;
            }
        }

        return result;
    }

    private static string Required(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var v) ? v : throw new ArgumentException($"Не указан параметр --{name}.");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new ArgumentException("Запускайте из каталога репозитория Тропы.");
    }

    private static string Password(bool confirm)
    {
        if (Environment.GetEnvironmentVariable("TROPA_SIGNING_PASSWORD") is { Length: >= 12 } fromEnv)
            return fromEnv;
        var first = ReadHidden("Пароль ключа подписи (не короче 12 символов): ");
        if (first.Length < 12)
            throw new ArgumentException("Пароль короче 12 символов.");
        if (confirm && ReadHidden("Ещё раз: ") != first)
            throw new ArgumentException("Пароли не совпадают.");
        return first;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter)
                break;
            if (k.Key == ConsoleKey.Backspace && sb.Length > 0)
                sb.Length--;
            else if (!char.IsControl(k.KeyChar))
                sb.Append(k.KeyChar);
        }

        Console.Error.WriteLine();
        return sb.ToString();
    }

    private static int KeyGen(string output)
    {
        var full = Path.GetFullPath(output);
        if (full.StartsWith(RepoRootOrEmpty(), StringComparison.OrdinalIgnoreCase) && RepoRootOrEmpty().Length > 0)
            throw new ArgumentException("Закрытый ключ нельзя класть в репозиторий. Укажите путь вне него.");
        if (File.Exists(full))
            throw new ArgumentException("Файл уже существует — старый ключ не перезаписываем.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportEncryptedPkcs8PrivateKeyPem(Password(confirm: true),
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, pem);
        Console.WriteLine("Закрытый ключ (зашифрован паролем): " + full);
        Console.WriteLine("Сделайте его копию на флешку или в менеджер паролей: без него нельзя выпускать обновления.");
        Console.WriteLine();
        Console.WriteLine("Публичный ключ для tools/update.config.json (поле publicKey):");
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static string RepoRootOrEmpty()
    {
        try
        {
            return RepoRoot();
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    private static int Manifest(Dictionary<string, string> o)
    {
        var root = RepoRoot();
        var sequence = long.Parse(Required(o, "sequence"), CultureInfo.InvariantCulture);
        var days = o.TryGetValue("expires-days", out var d) ? int.Parse(d, CultureInfo.InvariantCulture) : 90;
        var now = DateTimeOffset.UtcNow;
        var manifest = new JsonObject
        {
            ["schema"] = UpdateManifest.SupportedSchema,
            ["sequence"] = sequence,
            ["issued"] = now.ToString("o", CultureInfo.InvariantCulture),
            ["expires"] = now.AddDays(days).ToString("o", CultureInfo.InvariantCulture),
            ["cores"] = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tools", "cores.lock.json"))),
            ["geo"] = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tools", "geo.lock.json"))),
        };
        if (o.TryGetValue("app-version", out var version))
        {
            var installer = Required(o, "installer");
            using var stream = File.OpenRead(installer);
            manifest["app"] = new JsonObject
            {
                ["version"] = version,
                ["url"] = Required(o, "app-url"),
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(stream)),
                ["notes"] = o.GetValueOrDefault("notes"),
            };
        }

        var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        // Тот же разбор, что и в программе: невалидный манифест не выпускаем.
        UpdateManifest.Parse(bytes, RequiredGeo(root));
        var output = Required(o, "out");
        File.WriteAllBytes(output, bytes);
        Console.WriteLine($"Манифест №{sequence} записан: {output}. Не забудьте bundledSequence = {sequence} в tools/update.config.json для этого выпуска.");
        return 0;
    }

    private static IEnumerable<string> RequiredGeo(string root) =>
        Tropa.Core.Cores.GeoLock.Parse(File.ReadAllText(Path.Combine(root, "tools", "geo.lock.json"))).Files.Select(f => f.Name);

    private static int Sign(string keyPath, string manifestPath)
    {
        var bytes = File.ReadAllBytes(manifestPath);
        UpdateManifest.Parse(bytes, RequiredGeo(RepoRoot()));
        using var key = ECDsa.Create();
        key.ImportFromEncryptedPem(File.ReadAllText(keyPath), Password(confirm: false));
        var sig = ManifestSignature.Sign(bytes, key);
        File.WriteAllText(manifestPath + ".sig", Convert.ToBase64String(sig) + "\n");
        Console.WriteLine("Подпись: " + manifestPath + ".sig");
        return Verify(manifestPath);
    }

    private static int Verify(string manifestPath)
    {
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tools", "update.config.json")))!;
        var publicKey = (string?)config["publicKey"] ?? "";
        var bytes = File.ReadAllBytes(manifestPath);
        var sig = Convert.FromBase64String(File.ReadAllText(manifestPath + ".sig").Trim());
        if (!ManifestSignature.Verify(bytes, sig, publicKey))
        {
            Console.Error.WriteLine("Подпись НЕ проходит проверку публичным ключом из tools/update.config.json.");
            return 3;
        }

        var m = UpdateManifest.Parse(bytes, RequiredGeo(RepoRoot()));
        Console.WriteLine($"Подпись верна. Манифест №{m.Sequence}, действует до {m.Expires:yyyy-MM-dd}, Тропа {m.App?.Version.ToString() ?? "—"}.");
        return 0;
    }
}
