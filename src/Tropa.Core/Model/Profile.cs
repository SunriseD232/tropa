using Tropa.Core.Security;

namespace Tropa.Core.Model;

public enum Protocol { Vless, Vmess, Trojan, Shadowsocks, Hysteria2 }

public enum TransportType { Tcp, Xhttp, Ws, Grpc, HttpUpgrade }

public enum SecurityType { None, Tls, Reality }

public enum VlessFlow { None, XtlsRprxVision }

public enum XhttpMode { Auto, PacketUp, StreamUp, StreamOne }

/// <summary>Транспорт: как байты протокола упаковываются при передаче.</summary>
public sealed record TransportSettings
{
    public static TransportSettings Tcp { get; } = new() { Type = TransportType.Tcp };

    public required TransportType Type { get; init; }

    /// <summary>Путь для WS / XHTTP / HTTPUpgrade.</summary>
    public string? Path { get; init; }

    /// <summary>Заголовок Host для WS / XHTTP / HTTPUpgrade.</summary>
    public string? Host { get; init; }

    /// <summary>Имя сервиса gRPC.</summary>
    public string? ServiceName { get; init; }

    public XhttpMode XhttpMode { get; init; } = XhttpMode.Auto;
}

public sealed record RealitySettings
{
    /// <summary>Публичный ключ X25519 сервера (base64url, 43 символа).</summary>
    public required string PublicKey { get; init; }

    /// <summary>Short ID: 0–16 шестнадцатеричных символов.</summary>
    public string ShortId { get; init; } = "";

    public string? SpiderX { get; init; }
}

public sealed record SecuritySettings
{
    public static SecuritySettings None { get; } = new() { Type = SecurityType.None };

    public required SecurityType Type { get; init; }

    public string? Sni { get; init; }

    /// <summary>ALPN через запятую в каноническом виде, например «h2,http/1.1».</summary>
    public string? Alpn { get; init; }

    /// <summary>uTLS-отпечаток (chrome, firefox, …). null — значение по умолчанию из настроек.</summary>
    public string? Fingerprint { get; init; }

    public bool AllowInsecure { get; init; }

    public RealitySettings? Reality { get; init; }
}

/// <summary>Сервер. Секреты хранятся в <see cref="Secret"/> и не попадают в ToString().</summary>
public sealed record Profile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    public Guid? SubscriptionId { get; init; }

    public required Protocol Protocol { get; init; }

    public required string Address { get; init; }

    public required int Port { get; init; }

    /// <summary>UUID для VLESS/VMess, пароль для Trojan, Shadowsocks и Hysteria2.</summary>
    public required Secret Credential { get; init; }

    public VlessFlow Flow { get; init; } = VlessFlow.None;

    /// <summary>Шифр VMess: auto, aes-128-gcm, chacha20-poly1305, none, zero.</summary>
    public string VmessCipher { get; init; } = "auto";

    /// <summary>Метод Shadowsocks (2022-blake3-… или AEAD). Только для Shadowsocks.</summary>
    public string? SsMethod { get; init; }

    /// <summary>Пароль маскировки Salamander для Hysteria2; null — без маскировки.</summary>
    public Secret? Obfs { get; init; }

    public TransportSettings Transport { get; init; } = TransportSettings.Tcp;

    public SecuritySettings Security { get; init; } = SecuritySettings.None;

    /// <summary>Цепочка: сначала подключаться через этот профиль.</summary>
    public Guid? ChainVia { get; init; }

    /// <summary>Ключ для сопоставления при обновлении подписки (docs/04-domain-model.md, §1).</summary>
    public string IdentityKey =>
        $"{Protocol}|{Address.ToLowerInvariant()}|{Port}|{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Credential.Reveal())))[..16]}";
}
