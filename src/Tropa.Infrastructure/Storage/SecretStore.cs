using System.Security.Cryptography;
using System.Text.Json;

namespace Tropa.Infrastructure.Storage;

/// <summary>
/// Хранилище секретов (docs/02-security.md, §3.4): UUID, пароли, URL подписок, логин локального прокси.
/// Лежит отдельно от настроек и шифруется DPAPI для текущего пользователя Windows: файл,
/// скопированный на другой компьютер или прочитанный другим пользователем, бесполезен.
/// </summary>
public sealed class SecretStore
{
    // Энтропия привязывает шифротекст к Тропе: чужая программа того же пользователя
    // не расшифрует файл вызовом DPAPI без этого значения.
    private static readonly byte[] Entropy = "Tropa.SecretStore.v1"u8.ToArray();

    private readonly string _path;
    private readonly Dictionary<string, string> _values;

    private SecretStore(string path, Dictionary<string, string> values)
    {
        _path = path;
        _values = values;
    }

    public static SecretStore Open(string path)
    {
        if (!File.Exists(path))
            return new SecretStore(path, new Dictionary<string, string>(StringComparer.Ordinal));

        var encrypted = File.ReadAllBytes(path);
        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("Секреты не удалось расшифровать: файл повреждён или создан другим пользователем Windows.", ex);
        }

        try
        {
            var values = JsonSerializer.Deserialize(plain, StorageJsonContext.Default.DictionaryStringString)
                ?? new Dictionary<string, string>();
            return new SecretStore(path, new Dictionary<string, string>(values, StringComparer.Ordinal));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(value);
        _values[key] = value;
    }

    public void Remove(string key) => _values.Remove(key);

    public IEnumerable<string> Keys => _values.Keys;

    /// <summary>Все значения — для <see cref="Core.Security.SecretScrubber"/>.</summary>
    public IEnumerable<string> Values => _values.Values;

    /// <summary>Удаляет секреты, на которые больше никто не ссылается.</summary>
    public void RetainOnly(IReadOnlySet<string> keys)
    {
        foreach (var key in _values.Keys.Where(k => !keys.Contains(k)).ToList())
            _values.Remove(key);
    }

    public void Save()
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(_values, StorageJsonContext.Default.DictionaryStringString);
        try
        {
            AtomicFile.WriteAllBytes(_path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Случайная строка для логинов, паролей и секретов API.</summary>
    public static string NewRandomToken(int bytes = 24) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
