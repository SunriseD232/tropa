using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Compatibility;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Infrastructure;

namespace Tropa.App.ViewModels;

/// <summary>Вариант выбора (протокол, транспорт, безопасность) с причиной, по которой он сейчас недоступен.</summary>
internal sealed partial class OptionItem(string key, string title) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string? Reason { get; set; }
}

internal sealed record ChainChoice(Guid? Id, string Title)
{
    public override string ToString() => Title;
}

/// <summary>
/// Окно «Сервер» (docs/06-features.md, §6). Поля собираются в ссылку и разбираются тем же
/// ShareLink.Parse, что и подписки: ручной ввод проходит те же строгие проверки. Несовместимые
/// варианты блокируются по ProfileCompat с объяснением.
/// </summary>
internal sealed partial class EditServerViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private Guid _id;
    private Guid? _subscriptionId;
    private bool _loading;
    private bool _computing;

    public EditServerViewModel(TropaEngine engine, InfoViewModel info)
    {
        _engine = engine;
        _info = info;
        foreach (var o in Protocols.Concat(Transports).Concat(Securities))
            o.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OptionItem.IsChecked) && o.IsChecked)
                    OnOptionChecked(o);
            };
    }

    public ObservableCollection<OptionItem> Protocols { get; } =
        [new("vless", "VLESS"), new("vmess", "VMess"), new("trojan", "Trojan"), new("shadowsocks", "Shadowsocks"), new("hysteria2", "Hysteria2")];
    public IReadOnlyList<string> SsMethods { get; } =
        ["2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305", "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305"];
    public ObservableCollection<OptionItem> Transports { get; } = [new("tcp", "TCP"), new("xhttp", "XHTTP"), new("ws", "WS"), new("grpc", "gRPC"), new("httpupgrade", "HTTPUpgrade")];
    public ObservableCollection<OptionItem> Securities { get; } = [new("none", "Нет"), new("tls", "TLS"), new("reality", "Reality")];
    public IReadOnlyList<string> Fingerprints { get; } = ["chrome", "firefox", "edge", "safari", "random"];
    public IReadOnlyList<string> XhttpModes { get; } = ["auto", "packet-up", "stream-up", "stream-one"];
    public ObservableCollection<ChainChoice> Chains { get; } = [];

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "Сервер";
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string Port { get; set; } = "443";
    [ObservableProperty] public partial string Credential { get; set; } = "";
    [ObservableProperty] public partial bool ShowCredential { get; set; }
    [ObservableProperty] public partial bool Vision { get; set; }
    [ObservableProperty] public partial bool VisionEnabled { get; set; } = true;
    [ObservableProperty] public partial string? VisionReason { get; set; }
    [ObservableProperty] public partial string Path { get; set; } = "/";
    [ObservableProperty] public partial string Host { get; set; } = "";
    [ObservableProperty] public partial string ServiceName { get; set; } = "";
    [ObservableProperty] public partial string XhttpMode { get; set; } = "auto";
    [ObservableProperty] public partial string Sni { get; set; } = "";
    [ObservableProperty] public partial string Alpn { get; set; } = "";
    [ObservableProperty] public partial string Fingerprint { get; set; } = "chrome";
    [ObservableProperty] public partial bool AllowInsecure { get; set; }
    [ObservableProperty] public partial string PublicKey { get; set; } = "";
    [ObservableProperty] public partial string ShortId { get; set; } = "";
    [ObservableProperty] public partial string SpiderX { get; set; } = "/";
    [ObservableProperty] public partial ChainChoice? Chain { get; set; }
    [ObservableProperty] public partial string SsMethod { get; set; } = "2022-blake3-aes-128-gcm";
    [ObservableProperty] public partial string ObfsPassword { get; set; } = "";
    [ObservableProperty] public partial string Link { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string CoreNote { get; set; } = "";
    [ObservableProperty] public partial string? SubscriptionNote { get; set; }
    [ObservableProperty] public partial string? DisabledNotes { get; set; }

    public string Protocol => Protocols.FirstOrDefault(o => o.IsChecked)?.Key ?? "vless";
    public string Transport => Transports.FirstOrDefault(o => o.IsChecked)?.Key ?? "tcp";
    public string Security => Securities.FirstOrDefault(o => o.IsChecked)?.Key ?? "none";

    public bool IsReality => Security == "reality";
    public bool IsTls => Security == "tls";
    public bool IsNone => Security == "none";
    public bool HasPath => Transport is "ws" or "xhttp" or "httpupgrade";
    public bool IsGrpc => Transport == "grpc";
    public bool IsXhttp => Transport == "xhttp";
    public bool IsShadowsocks => Protocol == "shadowsocks";
    public bool IsHysteria2 => Protocol == "hysteria2";
    public bool HasTransport => !IsShadowsocks && !IsHysteria2;
    public bool ShowAlpn => IsTls && !IsHysteria2;
    public bool ShowFingerprint => !IsNone && !IsHysteria2;
    public bool ShowPlainWarning => IsNone && !IsShadowsocks;
    public string CredentialLabel => Protocol switch
    {
        "trojan" or "hysteria2" => "Пароль",
        "shadowsocks" => SsMethod.StartsWith("2022-", StringComparison.Ordinal) ? "Ключ (base64)" : "Пароль",
        _ => "UUID",
    };

    public void OpenNew()
    {
        Load(null);
        Title = "Новый сервер";
        IsOpen = true;
    }

    public void OpenEdit(Profile p)
    {
        Load(p);
        Title = "Сервер · " + p.Name;
        IsOpen = true;
    }

    private void Load(Profile? p)
    {
        _loading = true;
        _id = p?.Id ?? Guid.NewGuid();
        _subscriptionId = p?.SubscriptionId;
        SubscriptionNote = p?.SubscriptionId is null ? null
            : "Это сервер из подписки: при её обновлении параметры вернутся к присланным провайдером. Сохраняется только цепочка.";
        Name = p?.Name ?? "";
        Address = p?.Address ?? "";
        Port = (p?.Port ?? 443).ToString(CultureInfo.InvariantCulture);
        Credential = p?.Credential.Reveal() ?? "";
        ShowCredential = p is null;
        Vision = p?.Flow == VlessFlow.XtlsRprxVision;
        Path = p?.Transport.Path ?? "/";
        Host = p?.Transport.Host ?? "";
        ServiceName = p?.Transport.ServiceName ?? "";
        XhttpMode = ShareLinkMode(p?.Transport.XhttpMode ?? Core.Model.XhttpMode.Auto);
        Sni = p?.Security.Sni ?? "";
        Alpn = p?.Security.Alpn ?? "";
        Fingerprint = p?.Security.Fingerprint ?? "chrome";
        AllowInsecure = p?.Security.AllowInsecure ?? false;
        PublicKey = p?.Security.Reality?.PublicKey ?? "";
        ShortId = p?.Security.Reality?.ShortId ?? "";
        SpiderX = p?.Security.Reality?.SpiderX ?? "/";
        SsMethod = p?.SsMethod ?? "2022-blake3-aes-128-gcm";
        ObfsPassword = p?.Obfs?.Reveal() ?? "";

        Check(Protocols, p?.Protocol switch
        {
            Core.Model.Protocol.Vmess => "vmess",
            Core.Model.Protocol.Trojan => "trojan",
            Core.Model.Protocol.Shadowsocks => "shadowsocks",
            Core.Model.Protocol.Hysteria2 => "hysteria2",
            _ => "vless",
        });
        Check(Transports, p?.Transport.Type switch
        {
            TransportType.Xhttp => "xhttp",
            TransportType.Ws => "ws",
            TransportType.Grpc => "grpc",
            TransportType.HttpUpgrade => "httpupgrade",
            _ => "tcp",
        });
        Check(Securities, p?.Security.Type switch { SecurityType.Tls => "tls", SecurityType.Reality => "reality", _ => p is null ? "reality" : "none" });

        Chains.Clear();
        Chains.Add(new ChainChoice(null, "Напрямую"));
        foreach (var sp in _engine.State.Profiles.Where(x => x.Profile.Id != _id && x.Profile.ChainVia is null))
            Chains.Add(new ChainChoice(sp.Profile.Id, sp.Profile.Name));
        Chain = Chains.FirstOrDefault(c => c.Id == p?.ChainVia) ?? Chains[0];
        Error = null;
        _loading = false;
        Recompute();
    }

    private static void Check(IEnumerable<OptionItem> options, string key)
    {
        foreach (var o in options)
            o.IsChecked = o.Key == key;
    }

    private void OnOptionChecked(OptionItem o)
    {
        var group = Protocols.Contains(o) ? Protocols : Transports.Contains(o) ? Transports : Securities;
        foreach (var other in group.Where(x => x != o))
            other.IsChecked = false;
        // У Shadowsocks и Hysteria2 безопасность и транспорт заданы протоколом — выставляем сами.
        if (group == Protocols && !_loading && o.Key is "shadowsocks" or "hysteria2")
        {
            var wasLoading = _loading;
            _loading = true;
            Check(Transports, "tcp");
            Check(Securities, o.Key == "shadowsocks" ? "none" : "tls");
            _loading = wasLoading;
        }
        if (!_loading)
            Recompute();
    }

    // Любое изменение поля пересобирает ссылку и проверки.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && !_computing && e.PropertyName is not (nameof(Link) or nameof(Error) or nameof(CoreNote) or nameof(VisionEnabled)
                or nameof(VisionReason) or nameof(IsOpen) or nameof(Title) or nameof(ShowCredential) or nameof(SubscriptionNote) or nameof(DisabledNotes)))
            Recompute();
    }

    private void Recompute()
    {
        // Защита от повторного входа: уведомления о вычисляемых свойствах ниже сами вызывают OnPropertyChanged.
        if (_computing)
            return;
        _computing = true;
        try
        {
            RecomputeCore();
        }
        finally
        {
            _computing = false;
        }
    }

    private void RecomputeCore()
    {
        OnPropertyChanged(nameof(IsReality));
        OnPropertyChanged(nameof(IsTls));
        OnPropertyChanged(nameof(IsNone));
        OnPropertyChanged(nameof(HasPath));
        OnPropertyChanged(nameof(IsGrpc));
        OnPropertyChanged(nameof(IsXhttp));
        OnPropertyChanged(nameof(CredentialLabel));
        OnPropertyChanged(nameof(IsShadowsocks));
        OnPropertyChanged(nameof(IsHysteria2));
        OnPropertyChanged(nameof(HasTransport));
        OnPropertyChanged(nameof(ShowAlpn));
        OnPropertyChanged(nameof(ShowFingerprint));
        OnPropertyChanged(nameof(ShowPlainWarning));

        // Недоступные варианты — по тому же ProfileCompat, что и проверка перед запуском.
        var probe = ProbeProfile();
        var disabled = ProfileCompat.DisabledOptions(probe);
        void Mark(IEnumerable<OptionItem> options, string prefix)
        {
            foreach (var o in options)
            {
                var reason = disabled.GetValueOrDefault(prefix + "." + o.Key);
                o.IsEnabled = reason is null || o.IsChecked;
                o.Reason = reason;
            }
        }

        Mark(Protocols, "protocol");
        Mark(Transports, "transport");
        Mark(Securities, "security");
        VisionReason = disabled.GetValueOrDefault("flow");
        var titles = Protocols.Concat(Transports).Concat(Securities).ToDictionary(o => o.Key, o => o.Title);
        var notes = disabled
            .Where(kv => kv.Key != "flow" && (HasTransport || !kv.Key.StartsWith("transport.", StringComparison.Ordinal)))
            .Select(kv => $"«{titles.GetValueOrDefault(kv.Key[(kv.Key.IndexOf('.') + 1)..], kv.Key)}» недоступен: {kv.Value}.")
            .ToList();
        DisabledNotes = notes.Count == 0 ? null : string.Join("\n", notes);
        VisionEnabled = VisionReason is null;

        CoreNote = Transport == "xhttp" ? "Ядро: Xray (XHTTP есть только в Xray)"
            : IsHysteria2 ? "Ядро: sing-box (Hysteria2 есть только в sing-box)"
            : "Ядро: sing-box (авто)";

        var (profile, error) = Build();
        Error = error;
        Link = profile is null ? "" : ShareLink.Build(profile);
    }

    /// <summary>Профиль только для вычисления недоступных вариантов — без проверки значений.</summary>
    private Profile ProbeProfile() => new()
    {
        Name = "probe",
        Protocol = Protocol switch
        {
            "vmess" => Core.Model.Protocol.Vmess,
            "trojan" => Core.Model.Protocol.Trojan,
            "shadowsocks" => Core.Model.Protocol.Shadowsocks,
            "hysteria2" => Core.Model.Protocol.Hysteria2,
            _ => Core.Model.Protocol.Vless,
        },
        SsMethod = IsShadowsocks ? SsMethod : null,
        Address = "probe",
        Port = 1,
        Credential = new Core.Security.Secret("probe"),
        Flow = Vision ? VlessFlow.XtlsRprxVision : VlessFlow.None,
        Transport = new TransportSettings
        {
            Type = Transport switch
            {
                "xhttp" => TransportType.Xhttp,
                "ws" => TransportType.Ws,
                "grpc" => TransportType.Grpc,
                "httpupgrade" => TransportType.HttpUpgrade,
                _ => TransportType.Tcp,
            },
        },
        Security = new SecuritySettings
        {
            Type = Security switch { "tls" => SecurityType.Tls, "reality" => SecurityType.Reality, _ => SecurityType.None },
        },
    };

    /// <summary>Поля → ссылка → ShareLink.Parse: те же проверки, что и для подписок.</summary>
    private (Profile? Profile, string? Error) Build()
    {
        if (string.IsNullOrWhiteSpace(Address) || string.IsNullOrWhiteSpace(Credential))
            return (null, "Укажите адрес сервера и " + (Protocol == "vless" || Protocol == "vmess" ? "UUID." : "пароль."));
        var issues = ProfileCompat.Issues(ProbeProfile());
        if (issues.Count > 0)
            return (null, issues[0]);

        var hostPart = Address.Trim().Contains(':', StringComparison.Ordinal) ? "[" + Address.Trim() + "]" : Address.Trim();
        string link;
        if (IsShadowsocks)
        {
            link = $"ss://{Uri.EscapeDataString(SsMethod)}:{Uri.EscapeDataString(Credential.Trim())}@{hostPart}:{Port.Trim()}#{Uri.EscapeDataString(Name)}";
        }
        else if (IsHysteria2)
        {
            var q = new List<string>();
            if (!string.IsNullOrWhiteSpace(Sni))
                q.Add("sni=" + Uri.EscapeDataString(Sni.Trim()));
            if (!string.IsNullOrWhiteSpace(ObfsPassword))
                q.Add("obfs=salamander&obfs-password=" + Uri.EscapeDataString(ObfsPassword.Trim()));
            if (AllowInsecure)
                q.Add("insecure=1");
            link = $"hysteria2://{Uri.EscapeDataString(Credential.Trim())}@{hostPart}:{Port.Trim()}?{string.Join('&', q)}#{Uri.EscapeDataString(Name)}";
        }
        else if (Protocol == "vmess")
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("v", "2");
                w.WriteString("ps", Name);
                w.WriteString("add", Address.Trim());
                w.WriteString("port", Port.Trim());
                w.WriteString("id", Credential.Trim());
                w.WriteString("aid", "0");
                w.WriteString("net", Transport);
                w.WriteString("type", "none");
                w.WriteString("host", Host.Trim());
                w.WriteString("path", Transport == "grpc" ? ServiceName.Trim() : Path.Trim());
                w.WriteString("tls", Security == "tls" ? "tls" : "");
                w.WriteString("sni", Sni.Trim());
                w.WriteString("alpn", Alpn.Trim());
                w.WriteString("fp", Fingerprint);
                if (AllowInsecure)
                    w.WriteString("allowInsecure", "1");
                w.WriteEndObject();
            }

            link = "vmess://" + Convert.ToBase64String(ms.ToArray());
        }
        else
        {
            var q = new List<string> { "type=" + Transport, "security=" + Security };
            void Add(string k, string? v)
            {
                if (!string.IsNullOrWhiteSpace(v))
                    q.Add(k + "=" + Uri.EscapeDataString(v.Trim()));
            }

            if (Protocol == "vless")
            {
                q.Add("encryption=none");
                if (Vision && VisionEnabled)
                    q.Add("flow=xtls-rprx-vision");
            }

            if (HasPath)
            {
                Add("path", Path);
                Add("host", Host);
            }

            if (IsXhttp)
                Add("mode", XhttpMode);
            if (IsGrpc)
                Add("serviceName", ServiceName);
            if (Security != "none")
            {
                Add("sni", Sni);
                Add("fp", Fingerprint);
            }

            if (IsTls)
            {
                Add("alpn", Alpn);
                if (AllowInsecure)
                    q.Add("allowInsecure=1");
            }

            if (IsReality)
            {
                Add("pbk", PublicKey);
                Add("sid", ShortId);
                Add("spx", SpiderX);
            }

            var host = Address.Trim().Contains(':', StringComparison.Ordinal) ? "[" + Address.Trim() + "]" : Address.Trim();
            link = $"{Protocol}://{Uri.EscapeDataString(Credential.Trim())}@{host}:{Port.Trim()}?{string.Join('&', q)}#{Uri.EscapeDataString(Name)}";
        }

        var parsed = ShareLink.Parse(link);
        if (!parsed.Success)
            return (null, parsed.Error);
        return (parsed.Profile! with { Id = _id, SubscriptionId = _subscriptionId, ChainVia = Chain?.Id }, null);
    }

    private static string ShareLinkMode(Core.Model.XhttpMode m) => m switch
    {
        Core.Model.XhttpMode.PacketUp => "packet-up",
        Core.Model.XhttpMode.StreamUp => "stream-up",
        Core.Model.XhttpMode.StreamOne => "stream-one",
        _ => "auto",
    };

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void ToggleCredential() => ShowCredential = !ShowCredential;

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);

    [RelayCommand]
    private async Task SaveAsync()
    {
        var (profile, error) = Build();
        if (profile is null)
        {
            Error = error;
            return;
        }

        await _engine.SaveProfileAsync(profile);
        IsOpen = false;
    }
}
