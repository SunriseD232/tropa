namespace Tropa.Core.Model;

// Значения по умолчанию — из docs/06-features.md. Имена свойств совпадают с ключами справки в info.ru.json.

public enum CaptureMode { Tun, SystemProxy, PortsOnly }

public enum TunStackKind { Mixed, Gvisor, System }

public enum CoreChoice { Auto, SingBox, Xray }

public enum DpiPreset { Off, Soft, Hard, Custom }

public enum FragmentScope { List, All }

public enum UserAgentMode { Tropa, V2rayN, SingBox, Custom }

public enum DomainStrategy { AsIs, IpIfNonMatch, IpOnDemand }

public enum CoreLogLevel { Warning, Info, Debug }

public enum LocalDnsRule { Russian, DirectRules, None }

public enum AppTheme { Dark, Light, System }

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
    public AppTheme Theme { get; init; } = AppTheme.Dark;
    public string Lang { get; init; } = "ru";

    /// <summary>Мастер первого запуска пройден или пропущен.</summary>
    public bool OnboardingDone { get; init; }
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
    public bool AutoSelect { get; init; } = true;
    public int AutoIntervalMinutes { get; init; } = 3;
    public int AutoTolMs { get; init; } = 50;
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

public sealed record DpiSettings
{
    public DpiPreset DpiPreset { get; init; } = DpiPreset.Off;
    public bool Fragment { get; init; }
    public string FragPackets { get; init; } = "tlshello";
    public string FragLen { get; init; } = "100-200";
    public string FragInt { get; init; } = "10-20";
    public FragmentScope FragScope { get; init; } = FragmentScope.List;
    public string Utls { get; init; } = "chrome";
    public bool AllowInsecureWarn { get; init; } = true;
    public bool Noise { get; init; }
    public string NoiseType { get; init; } = "rand";
    public string NoiseLen { get; init; } = "10-20";
    public string NoiseDelay { get; init; } = "10-16";
    public bool Mux { get; init; }
    public int MuxConc { get; init; } = 8;

    /// <summary>Наборы (docs/06-features.md, §5): soft — фрагментация по списку, hard — всё и шум; custom — как есть.</summary>
    public DpiSettings WithPreset(DpiPreset preset) => preset switch
    {
        DpiPreset.Soft => this with { DpiPreset = preset, Fragment = true, FragScope = FragmentScope.List, Noise = false },
        DpiPreset.Hard => this with { DpiPreset = preset, Fragment = true, FragScope = FragmentScope.All, Noise = true },
        DpiPreset.Custom => this with { DpiPreset = preset },
        _ => this with { DpiPreset = DpiPreset.Off, Fragment = false, Noise = false },
    };
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
}
