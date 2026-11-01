using System.Globalization;
using Tropa.Core.Cores;
using Tropa.Core.Telemetry;
using Tropa.Core.Updates;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Service;
using Tropa.Infrastructure.Updates;

namespace Tropa.Infrastructure;

/// <summary>Версия ядра, которую можно выбрать (ADR-058).</summary>
/// <param name="Source">Откуда: подписанный манифест владельца или GitHub без проверки владельцем.</param>
/// <param name="Installed">Уже скачана — выбирается без загрузки.</param>
public sealed record CoreVersionOption(string Core, string Version, CorePickSource Source, bool Preview, DateTimeOffset? Published, bool Installed, bool Selected)
{
    public string Label =>
        Version + (Preview ? " · preview" : "") + (Source == CorePickSource.GitHub ? " · GitHub" : " · проверено владельцем")
        + (Selected ? " · выбрана" : Installed ? " · скачана" : "");
}

/// <summary>Что сейчас с одним ядром: версия выпуска, выбранная, прежняя и что можно выбрать.</summary>
/// <param name="PreviousVersion">Что было до последнего выбора: версия, "" — версия выпуска, null — выбора ещё не было.</param>
public sealed record CoreVersionsInfo(string Core, string ReleaseVersion, string? SelectedVersion, string? PreviousVersion, IReadOnlyList<CoreVersionOption> Options);

public sealed partial class TropaEngine
{
    private VerifiedManifest? _coreManifest;

    private CorePickStore Picks => new(_paths.CorePicksDirectory);

    /// <summary>Выбранные версии: у службы, если она есть (именно её ядра работают в подключении), иначе свои.</summary>
    private IReadOnlyList<(CorePick? Pick, string Core, string Version, CorePickSource Source, bool Preview, bool Selected, bool Previous)> CurrentPicks()
    {
        // Во время подключения связь со службой не переоткрывается и её список устаревает — тогда свой (он повторяет службу).
        if (_service is { IsConnected: true } service && !_runningViaService)
            return [.. service.CorePicks.Select(p => ((CorePick?)null, p.Core, p.Version, p.Source == Ipc.CoreSourceDto.Manifest ? CorePickSource.Manifest : CorePickSource.GitHub,
                p.Preview, p.Selected, p.Previous))];
        var state = Picks.Load();
        return [.. state.Installed.Select(p => ((CorePick?)p, p.Core, p.Version, p.Source, p.Preview,
            state.Selected.GetValueOrDefault(p.Core) == p.Version, state.Previous.GetValueOrDefault(p.Core) == p.Version))];
    }

    private string ReleaseVersion(string core)
    {
        if (_service is { IsConnected: true, ReleaseCoreVersions: { } text })
        {
            // «sing-box 1.14.2 · Xray 26.9.30»
            foreach (var part in text.Split(" · "))
            {
                var pieces = part.Split(' ');
                if (pieces.Length == 2 && string.Equals(pieces[0], core, StringComparison.OrdinalIgnoreCase))
                    return pieces[1];
            }
        }

        return _locations.ReleaseCores.Get(core).Version;
    }

    /// <summary>
    /// Версии ядер, которые можно выбрать: установленные, дополнительные из подписанного манифеста и,
    /// если разрешено в настройках, выпуски с GitHub. <paramref name="notes"/> — что не удалось узнать.
    /// Без <paramref name="online"/> — только скачанные и уже полученный манифест, без запросов в сеть.
    /// </summary>
    public async Task<IReadOnlyList<CoreVersionsInfo>> ListCoreVersionsAsync(List<string> notes, bool online = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var settings = State.Settings.Cores;
        try
        {
            if (online)
                _coreManifest = (await CheckUpdatesAsync(ct).ConfigureAwait(false)).Manifest;
        }
        catch (Exception ex) when (ex is IntegrityException or HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException or IOException)
        {
            notes.Add("Список версий из манифеста не получен: " + Scrubber.Scrub(ex.Message));
        }

        var github = new Dictionary<string, IReadOnlyList<CoreReleases.Release>>();
        if (online && settings.GitHubCores)
        {
            using var http = UpdateHttp();
            var client = new CoreReleaseClient(http);
            foreach (var repo in CoreReleases.Repos)
            {
                try
                {
                    github[repo.Core] = await client.ListAsync(repo.Core, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IntegrityException or HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
                {
                    notes.Add($"Выпуски {repo.Core} с GitHub не получены: " + Scrubber.Scrub(ex.Message));
                }
            }
        }

        var picks = CurrentPicks();
        var result = new List<CoreVersionsInfo>();
        foreach (var repo in CoreReleases.Repos)
        {
            var release = ReleaseVersion(repo.Core);
            var options = new Dictionary<string, CoreVersionOption>(StringComparer.Ordinal);
            void Add(CoreVersionOption o)
            {
                if (o.Version == release || (o.Preview && !settings.PreviewCores && !o.Installed))
                    return;
                // Проверенная владельцем запись важнее такой же версии с GitHub.
                if (!options.TryGetValue(o.Version, out var old) || (old.Source == CorePickSource.GitHub && o.Source == CorePickSource.Manifest && !old.Installed))
                    options[o.Version] = o;
            }

            foreach (var p in picks.Where(p => p.Core == repo.Core))
                Add(new CoreVersionOption(repo.Core, p.Version, p.Source, p.Preview, null, Installed: true, p.Selected));
            if (_coreManifest?.Manifest is { } m)
            {
                foreach (var x in m.ExtraCores.Where(x => x.Entry.Name == repo.Core))
                    Add(new CoreVersionOption(repo.Core, x.Entry.Version, CorePickSource.Manifest, x.Preview, null, Installed: false, Selected: false));
                if (m.Cores.Get(repo.Core) is { } main && main.Version != release)
                    Add(new CoreVersionOption(repo.Core, main.Version, CorePickSource.Manifest, false, null, Installed: false, Selected: false));
            }

            foreach (var r in github.GetValueOrDefault(repo.Core) ?? [])
                Add(new CoreVersionOption(repo.Core, r.Version, CorePickSource.GitHub, r.Preview, r.Published, Installed: false, Selected: false));

            var selected = picks.FirstOrDefault(p => p.Core == repo.Core && p.Selected).Version;
            var previous = picks.FirstOrDefault(p => p.Core == repo.Core && p.Previous).Version ?? (selected is not null ? "" : null);
            result.Add(new CoreVersionsInfo(repo.Core, release, selected, previous,
                [.. options.Values.OrderByDescending(o => o.Selected).ThenByDescending(o => VersionKey(o.Version), StringComparer.Ordinal)]));
        }

        return result;
    }

    /// <summary>
    /// Скачивает выбранную версию ядра, проверяет её и ставит у службы (она проверяет всё сама) и у себя.
    /// Начинает работать со следующего подключения.
    /// </summary>
    public async Task InstallCoreVersionAsync(CoreVersionOption option, IProgress<string>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (option.Installed)
        {
            await SelectCoreVersionAsync(option.Core, option.Version, ct).ConfigureAwait(false);
            return;
        }

        var repo = CoreReleases.For(option.Core);
        var version = CoreReleases.RequireVersion(option.Version);
        if (option.Source == CorePickSource.GitHub && !State.Settings.Cores.GitHubCores)
            throw new InvalidOperationException("Версии с GitHub выключены в настройках.");
        var work = Path.Combine(_paths.Local, "core-download", $"{repo.Core}-{Guid.NewGuid():N}");
        try
        {
            CoreEntry entry;
            bool preview;
            using (var http = UpdateHttp())
            {
                var client = new CoreReleaseClient(http);
                if (option.Source == CorePickSource.Manifest)
                {
                    var m = _coreManifest?.Manifest ?? throw new InvalidOperationException("Сначала обновите список версий.");
                    var extra = m.ExtraCores.FirstOrDefault(x => x.Entry.Name == repo.Core && x.Entry.Version == version);
                    var expected = extra?.Entry ?? (m.Cores.Get(repo.Core) is { } main && main.Version == version ? main
                        : throw new InvalidOperationException($"Версии {repo.Core} {version} нет в манифесте."));
                    preview = extra?.Preview ?? false;
                    progress?.Report($"Скачиваю {repo.Core} {version}…");
                    var zip = await client.DownloadArchiveAsync(repo.Core, version, expected.ArchiveSha256, work, ct).ConfigureAwait(false);
                    var got = CoreReleaseClient.ExtractExe(repo.Core, version, zip, expected.ArchiveSha256, work);
                    if (got.Files[0].Sha256 != expected.Files[0].Sha256)
                        throw new IntegrityException($"Файл {repo.ExeTarget} не совпадает с манифестом. Установка отменена.");
                    entry = expected;
                }
                else
                {
                    progress?.Report($"Узнаю у GitHub хэш {repo.Core} {version}…");
                    var release = await client.GetReleaseAsync(repo.Core, version, ct).ConfigureAwait(false);
                    preview = release.Preview;
                    progress?.Report($"Скачиваю {repo.Core} {version}…");
                    var zip = await client.DownloadArchiveAsync(repo.Core, version, release.ArchiveSha256!, work, ct).ConfigureAwait(false);
                    entry = CoreReleaseClient.ExtractExe(repo.Core, version, zip, release.ArchiveSha256!, work);
                }
            }

            progress?.Report("Устанавливаю…");
            await AttachServiceAsync(ct).ConfigureAwait(false);
            if (_service is { IsConnected: true } service)
            {
                var request = option.Source == CorePickSource.Manifest
                    ? new Ipc.InstallCoreRequest(repo.Core, version, Ipc.CoreSourceDto.Manifest, work,
                        Convert.ToBase64String(_coreManifest!.Bytes), Convert.ToBase64String(_coreManifest.Signature))
                    : new Ipc.InstallCoreRequest(repo.Core, version, Ipc.CoreSourceDto.GitHub, work);
                var reply = await service.SendAsync(request, ct).ConfigureAwait(false);
                if (!reply.Ok)
                    throw new ServiceException(reply.Error ?? "Служба не установила ядро.");
            }

            // Своя копия — для проверок серверов и работы без службы.
            Picks.Install(entry, option.Source, preview, Path.Combine(work, repo.ExeTarget), _now());
            Rec(Ev.CoreVersionPicked, ("core", repo.Core), ("version", version), ("source", option.Source.ToString()), ("preview", preview ? "1" : "0"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        await AfterCorePickAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Выбрать скачанную версию ядра; null — снова версия выпуска.</summary>
    public async Task SelectCoreVersionAsync(string core, string? version, CancellationToken ct = default)
    {
        core = CoreReleases.For(core).Core;
        if (version is not null)
            CoreReleases.RequireVersion(version);
        await AttachServiceAsync(ct).ConfigureAwait(false);
        if (_service is { IsConnected: true } service)
        {
            var reply = await service.SendAsync(new Ipc.SelectCoreRequest(core, version), ct).ConfigureAwait(false);
            if (!reply.Ok)
                throw new ServiceException(reply.Error ?? "Служба не выбрала версию ядра.");
        }

        var own = Picks.Load();
        if (version is null || own.Installed.Any(p => p.Core == core && p.Version == version))
            Picks.Select(core, version);
        else if (_service is not { IsConnected: true })
            throw new InvalidOperationException($"Версия {core} {version} не скачана.");
        Rec(Ev.CoreVersionPicked, ("core", core), ("version", version ?? "release"), ("source", "installed"));
        await AfterCorePickAsync(ct).ConfigureAwait(false);
    }

    private async Task AfterCorePickAsync(CancellationToken ct)
    {
        _locations = Picks.Apply(_locations);
        await ReattachServiceAsync(ct).ConfigureAwait(false);
        Log?.Invoke(this, "Версии ядер: " + CoreVersions + (Status.State == ConnectionState.Connected ? ". Заработают после переподключения." : "."));
        ServiceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ключ сортировки: числа с ведущими нулями, выпуск выше своих preview.</summary>
    private static string VersionKey(string version)
    {
        var dash = version.IndexOf('-', StringComparison.Ordinal);
        var main = dash < 0 ? version : version[..dash];
        var key = string.Join('.', main.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n.ToString("D6", CultureInfo.InvariantCulture) : p));
        return key + (dash < 0 ? "~" : "-" + version[(dash + 1)..]);
    }
}
