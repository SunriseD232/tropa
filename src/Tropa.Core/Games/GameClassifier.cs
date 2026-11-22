using Tropa.Core.Model;

namespace Tropa.Core.Games;

/// <summary>
/// Похож ли процесс на игру (ADR-065): по папке, куда exe поставил лаунчер. Лаунчеры, оверлеи,
/// античиты и обработчики сбоев игрой не считаются. Без доступа к диску, кроме проверки
/// <c>MicrosoftGame.config</c> рядом с exe (признак игры Xbox / Game Pass).
/// </summary>
public static class GameClassifier
{
    private static readonly (string Fragment, GameLauncher Launcher)[] Folders =
    [
        (@"\steamapps\common\", GameLauncher.Steam),
        (@"\epic games\", GameLauncher.Epic),
        (@"\gog galaxy\games\", GameLauncher.Gog),
        (@"\gog games\", GameLauncher.Gog),
        (@"\ea games\", GameLauncher.Ea),
        (@"\ubisoft game launcher\games\", GameLauncher.Ubisoft),
        (@"\riot games\", GameLauncher.Riot),
    ];

    /// <summary>Части имени exe, которые бывают в папках игр, но игрой не являются.</summary>
    private static readonly string[] NotGameParts =
    [
        "launcher", "crash", "report", "easyanticheat", "eac_", "battleye", "beservice", "anticheat", "vanguard",
        "unitycrashhandler", "cefprocess", "webhelper", "overlay", "updater", "update", "installer", "setup",
        "redist", "vcredist", "dxsetup", "uninstall", "unins0", "helper", "bootstrap", "service", "steamerrorreporter",
    ];

    /// <summary>Сами лаунчеры (их папки не совпадают с папками игр, но на всякий случай).</summary>
    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "epicgameslauncher.exe", "galaxyclient.exe", "eadesktop.exe", "origin.exe", "upc.exe",
        "ubisoftconnect.exe", "riotclientservices.exe", "battle.net.exe", "xboxpcapp.exe", "gamingservices.exe",
    };

    /// <summary>Лаунчер игры или null, если процесс на игру не похож.</summary>
    /// <param name="exePath">Полный путь exe.</param>
    /// <param name="fileExists">Проверка файла (для тестов — без диска).</param>
    public static GameLauncher? Classify(string? exePath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        var name = System.IO.Path.GetFileName(exePath);
        if (Launchers.Contains(name))
            return null;
        var lowerName = name.ToLowerInvariant();
        if (NotGameParts.Any(p => lowerName.Contains(p, StringComparison.Ordinal)))
            return null;

        var lower = exePath.Replace('/', '\\').ToLowerInvariant();
        if (lower.Contains(@"\windowsapps\", StringComparison.Ordinal) || lower.Contains(@"\xboxgames\", StringComparison.Ordinal))
            return HasXboxConfig(exePath, fileExists) ? GameLauncher.Xbox : null;

        foreach (var (fragment, launcher) in Folders)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal))
                return launcher;
        }

        return null;
    }

    /// <summary>MicrosoftGame.config лежит в папке exe или на уровень-два выше (Content\, корень пакета).</summary>
    private static bool HasXboxConfig(string exePath, Func<string, bool> fileExists) => XboxConfigPath(exePath, fileExists) is not null;

    /// <summary>Путь к MicrosoftGame.config игры Xbox или null.</summary>
    public static string? XboxConfigPath(string exePath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var dir = System.IO.Path.GetDirectoryName(exePath);
        for (var i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++)
        {
            var config = System.IO.Path.Combine(dir, "MicrosoftGame.config");
            if (fileExists(config))
                return config;
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    /// Название игры Xbox из MicrosoftGame.config: OverrideDisplayName у её exe, иначе DefaultDisplayName.
    /// Ссылки на ресурсы («ms-resource:…») не годятся. Файл — недоверенный текст: только атрибуты, без DTD.
    /// </summary>
    public static string? XboxDisplayName(string configXml, string exeName)
    {
        try
        {
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 };
            using var reader = System.Xml.XmlReader.Create(new StringReader(configXml), settings);
            string? fallback = null;
            while (reader.Read())
            {
                if (reader.NodeType != System.Xml.XmlNodeType.Element)
                    continue;
                if (reader.LocalName == "Executable" && string.Equals(reader.GetAttribute("Name"), exeName, StringComparison.OrdinalIgnoreCase)
                    && Usable(reader.GetAttribute("OverrideDisplayName")) is { } own)
                    return own;
                if (reader.LocalName == "ShellVisuals")
                    fallback ??= Usable(reader.GetAttribute("DefaultDisplayName"));
            }

            return fallback;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        static string? Usable(string? v) =>
            string.IsNullOrWhiteSpace(v) || v.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || v.Length > 80 ? null : v.Trim();
    }

    /// <summary>Название игры: описание из exe, иначе папка игры, иначе имя exe без расширения.</summary>
    public static string GuessName(string exePath, string? fileDescription, string? productName)
    {
        foreach (var candidate in new[] { productName, fileDescription })
        {
            var c = candidate?.Trim();
            if (!string.IsNullOrEmpty(c) && c.Length <= 80 && !c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return c;
        }

        var lower = exePath.Replace('/', '\\');
        foreach (var marker in new[] { @"\steamapps\common\", @"\Epic Games\", @"\XboxGames\", @"\Riot Games\", @"\GOG Games\", @"\EA Games\" })
        {
            var at = lower.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var rest = lower[(at + marker.Length)..];
                var folder = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(folder) && !folder.Contains('.', StringComparison.Ordinal))
                    return folder;
            }
        }

        return System.IO.Path.GetFileNameWithoutExtension(exePath);
    }

    /// <summary>Совпадает ли exe игры с именем процесса (без учёта регистра, как в правилах).</summary>
    public static bool SameExe(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
