using System.Diagnostics;
using Tropa.Core.Games;
using Tropa.Core.Model;
using Tropa.Core.Telemetry;
using Tropa.Infrastructure.Net;
using Tropa.Infrastructure.Service;
using Tropa.Infrastructure.SystemIntegration;
using Tropa.Ipc;

namespace Tropa.Infrastructure;

/// <summary>Состояние Teredo для вкладки «Игры» и диагностики (ADR-065). Error — почему не удалось узнать или сделать.</summary>
public sealed record TeredoView(TeredoStatus? Status, string? Error, bool CanManage);

/// <summary>Вкладка «Игры» (ADR-065): поиск игр, обучение доменам, Teredo.</summary>
public sealed partial class TropaEngine
{
    private Timer? _gamesTimer;
    private int _gamesBusy;
    private readonly Dictionary<string, (GameLauncher? Launcher, string Name)> _classified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Как часто обновлять «видна сейчас» у уже известной игры (чтобы не сохранять настройки каждые 30 с).</summary>
    private static readonly TimeSpan LastSeenStep = TimeSpan.FromMinutes(10);

    /// <summary>Игра в списке изменилась сама (найдена, выучены домены) — вкладке перерисоваться.</summary>
    public event EventHandler? GamesChanged;

    /// <summary>Поиск игр раз в 30 с, пока Тропа открыта (ADR-065).</summary>
    public void StartGameWatch() =>
        _gamesTimer ??= new Timer(_ => ScanGamesTick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));

    private void ScanGamesTick()
    {
        if (Interlocked.Exchange(ref _gamesBusy, 1) == 1)
            return;
        try
        {
            ScanGames();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Поиск игр не критичен.
        }
        finally
        {
            Volatile.Write(ref _gamesBusy, 0);
        }
    }

    /// <summary>Найти запущенные игры и добавить новые в список (режим «как было»). Возвращает число новых.</summary>
    public int ScanGames()
    {
        var detected = DetectRunningGames();
        if (detected.Count == 0)
            return 0;
        var now = _now();
        var games = State.Settings.Games;
        // Известные игры, у которых «видна сейчас» свежее 10 минут, не трогаем — иначе сохранение каждые 30 с.
        var stale = detected.Where(d => games.List.FirstOrDefault(g => GameClassifier.SameExe(g.Exe, d.Exe)) is not { } g
            || g.LastSeen is not { } seen || now - seen >= LastSeenStep).ToList();
        if (stale.Count == 0)
            return 0;
        var (merged, added) = GameList.MergeDetected(games, stale, now, AdminGameTemplates());
        UpdateSettings(s => s with { Games = merged });
        foreach (var g in added)
        {
            Log?.Invoke(this, $"Найдена игра: {g.Title} ({g.Exe}). Настроить её можно на вкладке «Игры».");
            Rec(Ev.GameFound, ("exe", GameName(g)), ("launcher", g.Launcher.ToString()), ("admin", Flag(AdminGameTemplates().Any(t => GameClassifier.SameExe(t.Exe, g.Exe)))));
        }

        GamesChanged?.Invoke(this, EventArgs.Empty);
        return added.Count;
    }

    /// <summary>Процессы текущего сеанса Windows, похожие на игры.</summary>
    private List<DetectedGame> DetectRunningGames()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var found = new List<DetectedGame>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.SessionId != session)
                    continue;
                string? path;
                try
                {
                    path = p.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    continue; // процесс с правами выше наших или уже завершился
                }

                if (path is null)
                    continue;
                (GameLauncher? Launcher, string Name) known;
                lock (_classified)
                {
                    if (!_classified.TryGetValue(path, out known))
                    {
                        var launcher = GameClassifier.Classify(path, File.Exists);
                        var name = "";
                        if (launcher is not null)
                            name = NameOf(path, launcher.Value);

                        _classified[path] = known = (launcher, name);
                    }
                }

                if (known.Launcher is { } l)
                    found.Add(new DetectedGame(Path.GetFileName(path), path, l, known.Name));
            }
        }

        return found;
    }

    /// <summary>Добавить игру вручную: имя exe или путь (из «Обзор…» или запущенных программ).</summary>
    public string? AddGameManually(string? exeOrPath)
    {
        var raw = exeOrPath?.Trim().Trim('"');
        if (string.IsNullOrEmpty(raw))
            return "Укажите программу игры, например game.exe.";
        var exe = Core.Routing.RuleInput.NormalizeProcess(Path.GetFileName(raw));
        if (exe is null)
            return "Это не похоже на программу: нужно имя .exe.";
        if (State.Settings.Games.List.Any(g => GameClassifier.SameExe(g.Exe, exe)))
            return $"{exe} уже в списке.";
        var hasPath = Path.IsPathFullyQualified(raw) && File.Exists(raw);
        var launcher = hasPath ? GameClassifier.Classify(raw, File.Exists) ?? GameLauncher.Unknown : GameLauncher.Unknown;
        var name = hasPath ? NameOf(raw, launcher) : Path.GetFileNameWithoutExtension(exe);
        var game = new GameProfile
        {
            Exe = exe,
            Name = name,
            Path = hasPath ? raw : null,
            Launcher = launcher,
            Source = GameSource.Manual,
            NeedsTeredo = launcher == GameLauncher.Xbox,
        };
        if (AdminGameTemplates().FirstOrDefault(t => GameClassifier.SameExe(t.Exe, exe)) is { } t)
            game = GameList.ApplyTemplate(game, t);
        UpdateSettings(s => s with
        {
            Games = s.Games with
            {
                List = [.. s.Games.List, game],
                Ignored = s.Games.Ignored.Where(x => !GameClassifier.SameExe(x, exe)).ToList(),
            },
        });
        Rec(Ev.GameFound, ("exe", GameName(game)), ("launcher", launcher.ToString()), ("manual", "1"));
        return null;
    }

    /// <summary>Переключатель «Автонастройка Тропы / Как было» (решение владельца: только по кнопке).</summary>
    public void SetGameMode(Guid id, GameMode mode)
    {
        if (State.Settings.Games.List.FirstOrDefault(g => g.Id == id) is not { } game || game.Mode == mode)
            return;
        UpdateSettings(s => s with { Games = GameList.Update(s.Games, id, g => g with { Mode = mode, FakeIpSeen = mode == GameMode.Auto ? false : g.FakeIpSeen }) });
        Rec(Ev.GameModeChanged, ("exe", GameName(game)), ("mode", mode.ToString()), ("launcher", game.Launcher.ToString()),
            ("teredo", Flag(game.NeedsTeredo)), ("domains", game.Domains.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public void UpdateGame(Guid id, Func<GameProfile, GameProfile> change) =>
        UpdateSettings(s => s with { Games = GameList.Update(s.Games, id, change) });

    /// <summary>Убрать игру: найденная сама больше не вернётся при поиске (её можно добавить вручную).</summary>
    public void RemoveGame(Guid id) =>
        UpdateSettings(s => s with { Games = GameList.Remove(s.Games, id) });

    /// <summary>Название: у игр Xbox — из MicrosoftGame.config, иначе из описания exe или папки.</summary>
    private static string NameOf(string path, GameLauncher launcher)
    {
        if (launcher == GameLauncher.Xbox && GameClassifier.XboxConfigPath(path, File.Exists) is { } config)
        {
            try
            {
                if (new FileInfo(config).Length < 1_000_000
                    && GameClassifier.XboxDisplayName(File.ReadAllText(config), Path.GetFileName(path)) is { } xboxName)
                    return xboxName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var info = FileVersionInfo.GetVersionInfo(path);
        return GameClassifier.GuessName(path, info.FileDescription, info.ProductName);
    }

    /// <summary>Параметры администратора для этой игры, если есть.</summary>
    public GameTemplate? AdminTemplateFor(GameProfile game) =>
        AdminGameTemplates().FirstOrDefault(t => GameClassifier.SameExe(t.Exe, game.Exe));

    /// <summary>Имя игры для журнала: exe или «игра», если пользователь выключил названия.</summary>
    private string GameName(GameProfile g) => State.Settings.Telemetry.HideGameNames ? "игра" : g.Exe;

    // ---------------- Обучение доменам ----------------

    /// <summary>
    /// Раз в 15 с, пока подключено: домены соединений игр из списка и признак «FakeIP мешает» (ADR-065).
    /// Домены остаются в settings.json и (у игр на автонастройке) уходят друзьям, если администратор
    /// публикует правила; в журнал разработчика — нет.
    /// </summary>
    private async Task LearnGamesAsync(ClashApi api, CancellationToken ct)
    {
        using (api)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                    var games = State.Settings.Games;
                    if (games.List.Count == 0)
                        continue;
                    IReadOnlyList<CoreConnection> connections;
                    try
                    {
                        connections = await api.ConnectionsAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException
                        or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        continue;
                    }

                    var learned = GameList.Learn(games, connections);
                    if (ReferenceEquals(learned, games) || !ReferenceEquals(games, State.Settings.Games))
                        continue;
                    UpdateSettings(s => ReferenceEquals(s.Games, games) ? s with { Games = learned } : s);
                    GamesChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    // ---------------- Teredo ----------------

    /// <summary>Teredo: состояние (без службы — само, netsh не требует прав), включить или перезапустить (только служба).</summary>
    public async Task<TeredoView> TeredoAsync(TeredoAction action, CancellationToken ct = default)
    {
        var service = ServiceAvailable ? _service : null;
        if (service is null)
        {
            var status = await Task.Run(TeredoProbeSafe, ct).ConfigureAwait(false);
            return new TeredoView(status, action == TeredoAction.Status ? null
                : "Включать и перезапускать Teredo может только служба Тропы. Выполните в PowerShell от имени администратора: "
                    + (action == TeredoAction.Enable ? "netsh interface teredo set state type=enterpriseclient servername=" + GameDefaults.TeredoServer : "Restart-Service iphlpsvc -Force"),
                CanManage: false);
        }

        try
        {
            var reply = await service.SendAsync(new TeredoRequest(action), ct).ConfigureAwait(false);
            if (reply is TeredoReply t)
            {
                var status = new TeredoStatus(t.TeredoType, t.Server, t.State, t.Nat, t.TeredoError);
                if (action != TeredoAction.Status)
                    Rec(Ev.Teredo, ("action", action.ToString()), ("state", t.State), ("type", t.TeredoType), ("nat", t.Nat));
                return new TeredoView(status, null, CanManage: true);
            }

            return new TeredoView(await Task.Run(TeredoProbeSafe, ct).ConfigureAwait(false), reply.Error ?? "Служба не ответила.", CanManage: true);
        }
        catch (Exception ex) when (ex is ServiceException or IOException or TimeoutException or InvalidOperationException)
        {
            return new TeredoView(await Task.Run(TeredoProbeSafe, ct).ConfigureAwait(false), ex.Message, CanManage: false);
        }
    }

    private static TeredoStatus? TeredoProbeSafe()
    {
        try
        {
            return TeredoProbe.Read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// После подключения TUN (ADR-065): если игре на автонастройке нужен Teredo — проверить его через 20 с;
    /// выключен — включить, не работает через 40 с — один раз перезапустить службу IP Helper.
    /// </summary>
    private async Task EnsureTeredoAsync(CancellationToken ct)
    {
        var games = State.Settings.Games;
        if (!games.AnyAutoNeedsTeredo || games.TeredoHandsOff || State.Settings.Connection.Mode != CaptureMode.Tun || !ServiceAvailable)
            return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            var view = await TeredoAsync(TeredoAction.Status, ct).ConfigureAwait(false);
            if (view.Status is not { } s)
                return;
            if (s.Disabled)
            {
                Log?.Invoke(this, "Teredo выключен, а он нужен играм Xbox для связи с другими игроками. Включаю.");
                view = await TeredoAsync(TeredoAction.Enable, ct).ConfigureAwait(false);
                s = view.Status ?? s;
            }

            // Квалификация занимает до ~30 с; после пересоздания адаптера TUN Teredo сам часто не поднимается.
            for (var i = 0; i < 4 && !s.Working; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                s = (await TeredoAsync(TeredoAction.Status, ct).ConfigureAwait(false)).Status ?? s;
            }

            if (!s.Working)
            {
                Log?.Invoke(this, "Teredo не поднялся после подключения — перезапускаю службу IP Helper.");
                await TeredoAsync(TeredoAction.Restart, ct).ConfigureAwait(false);
            }
            else
            {
                Rec(Ev.Teredo, ("action", "check"), ("state", s.State), ("type", s.Type), ("nat", s.Nat));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
