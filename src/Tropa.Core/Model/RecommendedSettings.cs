namespace Tropa.Core.Model;

/// <summary>
/// Автонастройка (info.ru.json: autoSetup): рекомендуемые значения всех базовых пунктов, чтобы
/// пользователю оставалось только вставить ссылку. Своё не трогаем: правила, серверы, горячую
/// клавишу, тему, исключения Store, служебные отметки.
/// </summary>
public static class RecommendedSettings
{
    public static AppSettings Apply(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var defaults = new AppSettings();
        return current with
        {
            General = defaults.General with
            {
                // Весь компьютер, автозапуск и автоподключение — «включил и работает».
                Autostart = true,
                Autoconnect = true,
                StartMin = true,
                WaitNet = true,
                Reconnect = true,
                Ipv6Block = true,
                LocalPass = true,
                // Kill switch не включаем сами: при сбое он оставляет без интернета, это решение пользователя.
                KillSwitch = current.General.KillSwitch,
                Hotkey = current.General.Hotkey,
                Theme = current.General.Theme,
                Lang = current.General.Lang,
                OnboardingDone = current.General.OnboardingDone,
                AutoSetupAsked = current.General.AutoSetupAsked,
            },
            // Без службы Тропа сама перейдёт на «Только браузеры» при подключении.
            Connection = defaults.Connection with { Mode = CaptureMode.Tun, UwpLoopback = current.Connection.UwpLoopback },
            Dns = defaults.Dns with { Hosts = current.Dns.Hosts },
            // Мягкий обход DPI: фрагментация только для YouTube и связанных доменов, когда они идут напрямую.
            Dpi = defaults.Dpi.WithPreset(DpiPreset.Soft),
            Cores = defaults.Cores with
            {
                LastManifestSequence = current.Cores.LastManifestSequence,
                LastUpdateCheck = current.Cores.LastUpdateCheck,
            },
            Expert = defaults.Expert,
            Subscriptions = defaults.Subscriptions,
            Routing = current.Routing with
            {
                Preset = RoutePreset.ExceptRu,
                BlockQuic = true,
                UdpProxy = true,
            },
        };
    }
}
