namespace Tropa.Core.Model;

// Значения по умолчанию — из docs/06-features.md. Имена свойств совпадают с ключами справки в info.ru.json.

public enum CaptureMode { Tun, SystemProxy, PortsOnly }

public enum TunStackKind { Mixed, Gvisor, System }

public enum CoreChoice { Auto, SingBox, Xray }

public enum UserAgentMode { Tropa, V2rayN, SingBox, Custom }

public enum DomainStrategy { AsIs, IpIfNonMatch, IpOnDemand }

public enum CoreLogLevel { Warning, Info, Debug }

public enum LocalDnsRule { Russian, DirectRules, None }

public enum AppTheme { Dark, Light, System }

/// <summary>Вид главного окна (ADR-051). Первое значение — вид Тропы: в старом settings.json поля нет, и читается 0.</summary>
public enum WindowLayout { Tropa, V2rayN }

public sealed record GeneralSettings
{
    public bool Autostart { get; init; } = true;
    public bool Autoconnect { get; init; } = true;
    public bool StartMin { get; init; } = true;
    public bool WaitNet { get; init; } = true;
    public bool Reconnect { get; init; } = true;
    public bool KillSwitch { get; init; }
    public bool Ipv6Block { get; init; } = true;
    public bool LocalPass { get; init; } = true;
    public string Hotkey { get; init; } = "Ctrl+Alt+P";
    public bool NotifySwitch { get; init; } = true;

    /// <summary>
    /// Не показывать страну, из которой сайты видят подключение (по умолчанию показывается: проверка раз
    /// в минуту через сервер). Флаг «наоборот»: в старых settings.json его нет, а отсутствующее
    /// init-свойство при чтении становится false — значит, «показывать».
    /// </summary>
    public bool HideExitRegion { get; init; }
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>Вид окна: свой или «как в v2rayN» — меняется только представление, логика одна.</summary>
    public WindowLayout Layout { get; init; }
    public string Lang { get; init; } = "ru";

    /// <summary>Мастер первого запуска пройден или пропущен.</summary>
    public bool OnboardingDone { get; init; }

    /// <summary>Вопрос «Настроить автоматически?» уже задан при запуске.</summary>
    public bool AutoSetupAsked { get; init; }
}

public sealed record ConnectionSettings
{
    public CaptureMode Mode { get; init; } = CaptureMode.Tun;
    public TunStackKind TunStack { get; init; } = TunStackKind.Mixed;
    public int Mtu { get; init; } = 9000;
    public bool StrictRoute { get; init; } = true;
    public bool LanBypass { get; init; } = true;
    public string SysBypass { get; init; } = "localhost;127.*;10.*;172.16.*;192.168.*;*.local";
    /// <summary>SID-ы приложений Store, которым на время подключения разрешён loopback (info.ru.json: uwpLoopback).</summary>
    public IReadOnlyList<string> UwpLoopback { get; init; } = [];
    /// <summary>Порт mixed-inbound (SOCKS5 и HTTP на одном порту).</summary>
    public int SocksPort { get; init; } = 10880;
    /// <summary>Отдельный порт для устройств в локальной сети, всегда с паролем.</summary>
    public int LanPort { get; init; } = 10881;
    public bool RandomPorts { get; init; }
    public bool LanAllow { get; init; }
    public string TestUrl { get; init; } = "https://www.gstatic.com/generate_204";
    public string SpeedUrl { get; init; } = "https://speed.cloudflare.com/__down?bytes=10000000";
    public int SpeedSizeMb { get; init; } = 10;
    public bool UdpTestOn { get; init; } = true;
    public string StunServer { get; init; } = "stun.l.google.com:19302";
    public int Parallel { get; init; } = 5;
}

public sealed record DnsSettings
{
    public string RemoteDns { get; init; } = "https://1.1.1.1/dns-query";
    public string LocalDns { get; init; } = "77.88.8.8";
    public LocalDnsRule LocalDnsRule { get; init; } = LocalDnsRule.Russian;
    public bool Fakeip { get; init; } = true;
    public bool Sniffing { get; init; } = true;
    public bool RouteOnly { get; init; }
    public bool DnsHijack { get; init; } = true;
    public bool SmartNameRes { get; init; } = true;
    public bool DnsCache { get; init; } = true;
    public string Hosts { get; init; } = "";
}

/// <summary>
/// Параметры самого TLS-соединения: отпечаток uTLS (нужен для Reality и обычного TLS) и мультиплексирование.
/// Не обход DPI сторонними средствами — только то, что неотделимо от подключения.
/// </summary>
public sealed record DpiSettings
{
    /// <summary>Отпечаток TLS-рукопожатия (uTLS). Нужен Reality и помогает обычному VLESS+TLS выглядеть как браузер.</summary>
    public string Utls { get; init; } = "chrome";
    public bool AllowInsecureWarn { get; init; } = true;
    public bool Mux { get; init; }
    public int MuxConc { get; init; } = 8;

    /// <summary>
    /// Фрагментация соединения ДО VPN-сервера (ADR-055, как «Включить фрагментацию» в v2rayN): первое
    /// TLS-сообщение уходит кусками, чтобы ТСПУ не узнал и не оборвал подключение к серверу. Прямой трафик
    /// к сайтам не трогается — это не обход без VPN (ADR-037). Только встроенными средствами ядер.
    /// По умолчанию выключено; в старом settings.json поля нет — читается false.
    /// </summary>
    public bool ServerFragment { get; init; }

    /// <summary>
    /// Полоса Hysteria2 по умолчанию (Мбит/с, к серверу и от сервера) для серверов, где она не задана.
    /// Без полосы Hysteria2 на «узком» плече даёт ~10 Мбит/с вместо 150. В старом settings.json полей нет
    /// и читается 0 — это значит «по умолчанию».
    /// </summary>
    public int Hy2UpMbps { get => _hy2Up > 0 ? _hy2Up : DefaultHy2UpMbps; init => _hy2Up = value; }

    public int Hy2DownMbps { get => _hy2Down > 0 ? _hy2Down : DefaultHy2DownMbps; init => _hy2Down = value; }

    public const int DefaultHy2UpMbps = 50;
    public const int DefaultHy2DownMbps = 200;

    private readonly int _hy2Up;
    private readonly int _hy2Down;

    /// <summary>Полоса, с которой ядро запустит сервер Hysteria2: своя у сервера или по умолчанию; 0 — без Brutal.</summary>
    public (int Up, int Down) Hy2Bandwidth(Profile p) => (p.UpMbps ?? Hy2UpMbps, p.DownMbps ?? Hy2DownMbps);
}

public sealed record CoreSettings
{
    public CoreChoice CoreChoice { get; init; } = CoreChoice.Auto;
    public bool GeoUpdate { get; init; } = true;
    public bool GeoViaProxy { get; init; } = true;
    public string AppChannel { get; init; } = "stable";
    public bool AppCheck { get; init; } = true;

    /// <summary>Самый новый номер манифеста обновлений, который Тропа уже видела (защита от отката).</summary>
    public long LastManifestSequence { get; init; }

    public DateTimeOffset? LastUpdateCheck { get; init; }
}

public sealed record ExpertSettings
{
    public bool SniffHttp { get; init; } = true;
    public bool SniffTls { get; init; } = true;
    public bool SniffQuic { get; init; }
    public DomainStrategy DomainStrategy { get; init; } = DomainStrategy.IpIfNonMatch;
    public CoreLogLevel LogLevel { get; init; } = CoreLogLevel.Warning;
    public bool Template { get; init; }
    public string TemplateText { get; init; } = "";
}

public sealed record SubscriptionDefaults
{
    public int UpdateHours { get; init; } = 12;

    /// <summary>
    /// Частое обновление в минутах (15); 0 — по <see cref="UpdateHours"/>. В settings.json прошлых версий
    /// поля нет и читается 0 — то есть остаётся прежний интервал в часах.
    /// </summary>
    public int UpdateMinutes { get; init; }

    /// <summary>Свой интервал (не из списка «15 минут / 6 / 12 / 24 часа / вручную»): от 5 минут до недели.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CustomInterval => UpdateMinutes is not (0 or 15) || (UpdateMinutes == 0 && UpdateHours is not (0 or 6 or 12 or 24));

    public const int MinCustomMinutes = 5;
    public const int MaxCustomMinutes = 7 * 24 * 60;

    /// <summary>Интервал автообновления; null — только вручную.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan? Interval => UpdateMinutes > 0 ? TimeSpan.FromMinutes(UpdateMinutes)
        : UpdateHours > 0 ? TimeSpan.FromHours(UpdateHours) : null;
    public bool SubViaProxy { get; init; } = true;
    public UserAgentMode SubUA { get; init; } = UserAgentMode.Tropa;
    public string SubUACustom { get; init; } = "";
    public bool Hwid { get; init; }
}

public sealed record AppSettings
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; init; } = CurrentSchema;
    public GeneralSettings General { get; init; } = new();
    public ConnectionSettings Connection { get; init; } = new();
    public DnsSettings Dns { get; init; } = new();
    public DpiSettings Dpi { get; init; } = new();
    public CoreSettings Cores { get; init; } = new();
    public ExpertSettings Expert { get; init; } = new();
    public SubscriptionDefaults Subscriptions { get; init; } = new();
    public RoutingSettings Routing { get; init; } = new();

    /// <summary>
    /// Публикация для друзей (ADR-048). Отсутствующий в старом settings.json раздел source-generated
    /// System.Text.Json передаёт как null — поэтому null превращается в значения по умолчанию.
    /// </summary>
    public AdminPublishSettings Admin { get => _admin; init => _admin = value ?? new(); }

    private readonly AdminPublishSettings _admin = new();

    /// <summary>Журнал для разработчика (ADR-050). null из старого settings.json — значения по умолчанию.</summary>
    public TelemetrySettings Telemetry { get => _telemetry; init => _telemetry = value ?? new(); }

    private readonly TelemetrySettings _telemetry = new();
}

/// <summary>
/// Журнал работы для разработчика (ADR-050): что происходит в Тропе уходит на сервер владельца сборки.
/// Флаг «наоборот»: отсутствующее свойство читается как false, то есть «отправлять».
/// </summary>
public sealed record TelemetrySettings
{
    public bool Off { get; init; }

    /// <summary>Имя, по которому разработчик узнает пользователя в журнале. Необязательно.</summary>
    public string Name { get => _name; init => _name = value ?? ""; }

    private readonly string _name = "";
}

/// <summary>
/// Администратор: Тропа сама публикует «Мои» серверы, выбранный сервер и правила по адресу для друзей.
/// Адрес и ключ публикации — секреты, они в secrets.bin, а не здесь.
/// </summary>
public sealed record AdminPublishSettings
{
    public bool Enabled { get; init; }

    /// <summary>Как часто Тропа друзей проверяет документ, минуты. 0 (старый файл) — 5.</summary>
    public int RefreshMinutes { get; init; } = 5;

    public string Title { get; init; } = "";

    /// <summary>Публиковать и правила (экран «Правила»), не только серверы.</summary>
    public bool PublishRules { get; init; } = true;
}
