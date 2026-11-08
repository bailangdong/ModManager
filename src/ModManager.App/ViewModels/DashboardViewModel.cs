using System.IO;
using System.Windows.Media;
using ModManager.Core;
using ModManager.Core.Safety;
using ModManager.Core.Services;

namespace ModManager.App.ViewModels;

/// <summary>
/// Palette shared by every page.
/// </summary>
/// <remarks>
/// Status colours resolve in one place so a badge means the same thing on every screen. Each tone returns
/// a text brush and a matching tinted fill, which is what makes the pills readable against the dark surface
/// instead of looking like flat blocks of saturated colour.
/// </remarks>
public static class Palette
{
    /// <summary>Foreground for a safety verdict.</summary>
    /// <param name="verdict">Verdict to colour.</param>
    public static Brush ForVerdict(PathSafetyVerdict verdict) => verdict switch
    {
        PathSafetyVerdict.Safe => Success(),
        PathSafetyVerdict.Overwrite => Warning(),
        PathSafetyVerdict.Conflict => Warning(),
        _ => Danger(),
    };

    /// <summary>Fill for a safety verdict.</summary>
    /// <param name="verdict">Verdict to colour.</param>
    public static Brush FillForVerdict(PathSafetyVerdict verdict) => verdict switch
    {
        PathSafetyVerdict.Safe => SuccessFill(),
        PathSafetyVerdict.Overwrite => WarningFill(),
        PathSafetyVerdict.Conflict => WarningFill(),
        _ => DangerFill(),
    };

    /// <summary>Short badge text for a verdict.</summary>
    /// <param name="verdict">Verdict to describe.</param>
    public static string DescribeVerdict(PathSafetyVerdict verdict) => Zh.Verdict(verdict);

    /// <summary>Foreground for an issue severity.</summary>
    /// <param name="severity">Severity to colour.</param>
    public static Brush ForSeverity(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => Danger(),
        IssueSeverity.Warning => Warning(),
        _ => Info(),
    };

    /// <summary>Fill for an issue severity.</summary>
    /// <param name="severity">Severity to colour.</param>
    public static Brush FillForSeverity(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => DangerFill(),
        IssueSeverity.Warning => WarningFill(),
        _ => InfoFill(),
    };

    /// <summary>Foreground for a mod state.</summary>
    /// <param name="state">State to colour.</param>
    public static Brush ForModState(ModState state) => state switch
    {
        ModState.Deployed => Success(),
        ModState.UpdateAvailable => Info(),
        ModState.NeedsAttention => Warning(),
        ModState.Disabled => Secondary(),
        _ => Secondary(),
    };

    /// <summary>Fill for a mod state.</summary>
    /// <param name="state">State to colour.</param>
    public static Brush FillForModState(ModState state) => state switch
    {
        ModState.Deployed => SuccessFill(),
        ModState.UpdateAvailable => InfoFill(),
        ModState.NeedsAttention => WarningFill(),
        _ => NeutralFill(),
    };

    /// <summary>Foreground for a download state.</summary>
    /// <param name="state">State to colour.</param>
    public static Brush ForDownloadState(DownloadState state) => state switch
    {
        DownloadState.Completed => Success(),
        DownloadState.Failed => Danger(),
        DownloadState.Paused => Warning(),
        DownloadState.Cancelled => Secondary(),
        _ => Info(),
    };

    /// <summary>Fill for a download state.</summary>
    /// <param name="state">State to colour.</param>
    public static Brush FillForDownloadState(DownloadState state) => state switch
    {
        DownloadState.Completed => SuccessFill(),
        DownloadState.Failed => DangerFill(),
        DownloadState.Paused => WarningFill(),
        DownloadState.Cancelled => NeutralFill(),
        _ => InfoFill(),
    };

    /// <summary>Foreground for a log level.</summary>
    /// <param name="level">Level to colour.</param>
    public static Brush ForLogLevel(LogLevel level) => level switch
    {
        LogLevel.Error or LogLevel.Critical => Danger(),
        LogLevel.Warning => Warning(),
        LogLevel.Information => Info(),
        _ => Secondary(),
    };

    /// <summary>The success text brush.</summary>
    public static Brush Success() => Brush("SuccessBrush");

    /// <summary>The success fill brush.</summary>
    public static Brush SuccessFill() => Brush("SuccessFillBrush");

    /// <summary>The warning text brush.</summary>
    public static Brush Warning() => Brush("WarningBrush");

    /// <summary>The warning fill brush.</summary>
    public static Brush WarningFill() => Brush("WarningFillBrush");

    /// <summary>The danger text brush.</summary>
    public static Brush Danger() => Brush("DangerBrush");

    /// <summary>The danger fill brush.</summary>
    public static Brush DangerFill() => Brush("DangerFillBrush");

    /// <summary>The info text brush.</summary>
    public static Brush Info() => Brush("InfoBrush");

    /// <summary>The info fill brush.</summary>
    public static Brush InfoFill() => Brush("InfoFillBrush");

    /// <summary>The accent brush.</summary>
    public static Brush Accent() => Brush("AccentBrush");

    /// <summary>The secondary text brush.</summary>
    public static Brush Secondary() => Brush("TextSecondaryBrush");

    /// <summary>A neutral fill, used for states with no particular urgency.</summary>
    public static Brush NeutralFill() => Brush("SurfaceRaisedBrush");

    /// <summary>Looks a brush up from application resources, falling back to a transparent brush.</summary>
    /// <param name="key">Resource key.</param>
    /// <remarks>
    /// Falling back rather than throwing keeps a missing key from taking the whole window down; in the
    /// designer or a unit-test host there is no application resource dictionary at all.
    /// </remarks>
    private static Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}

/// <summary>
/// Dashboard: what is installed, what needs attention, and what was repaired at startup.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase, IPageViewModel
{
    private readonly ApplicationHost _host;
    private readonly ShellViewModel? _shell;
    private readonly CoverImageLoader _covers;
    private readonly NexusGameImageCache _gameImages;
    private string _headline = "加载中…";
    private string _recoveryMessage = string.Empty;
    private bool _hasRecovery;
    private int _totalGames;
    private int _detectedGames;
    private int _supportedGames;
    private int _genericGames;
    private int _totalMods;
    private int _totalEnabled;
    private int _totalDisabled;
    private int _totalUpdates;
    private int _totalConflicts;
    private int _totalMissingDependencies;
    private int _activeDownloads;
    private int _failedDownloads;

    /// <summary>Creates the dashboard.</summary>
    /// <param name="host">Composition root.</param>
    /// <param name="shell">Shell, for the "view conflicts" navigation.</param>
    public DashboardViewModel(ApplicationHost host, ShellViewModel? shell = null)
    {
        _host = host;
        _shell = shell;
        _covers = new CoverImageLoader(host.Images);
        _gameImages = new NexusGameImageCache(host.Paths, host.Logger);

        NavigateToConflictsCommand = new RelayCommand(() =>
        {
            _shell?.NavigateTo("冲突");
            return Task.CompletedTask;
        });

        OpenOnlineModsCommand = new RelayCommand(() =>
        {
            _shell?.NavigateTo("在线 Mod");
            return Task.CompletedTask;
        });

        OpenLocalModsCommand = new RelayCommand(() =>
        {
            _shell?.NavigateTo("本地 Mod");
            return Task.CompletedTask;
        });

        OpenGamesCommand = new RelayCommand(() =>
        {
            _shell?.NavigateTo("游戏库");
            return Task.CompletedTask;
        });
    }

    /// <summary>Opens the Conflicts page.</summary>
    public RelayCommand NavigateToConflictsCommand { get; }

    /// <summary>Opens the online Mod browser.</summary>
    public RelayCommand OpenOnlineModsCommand { get; }

    /// <summary>Opens the local Mod list.</summary>
    public RelayCommand OpenLocalModsCommand { get; }

    /// <summary>Opens the game library.</summary>
    public RelayCommand OpenGamesCommand { get; }

    /// <summary>Name of the game everything on this page is about.</summary>
    public string ActiveGameName => _shell?.ActiveGame?.Name ?? "尚未选择游戏";

    /// <summary>Cover of the active game, forwarded from the shell so the hero tile can show it.</summary>
    public ImageSource? ActiveGameCover => _shell?.ActiveGameCover;

    /// <summary>True when the active game has a cover to display.</summary>
    public bool HasActiveGameCover => ActiveGameCover is not null;

    /// <summary>Mod counts for the active game, or an overall count when it is not in the summary list.</summary>
    public string ActiveGameModText => Games.FirstOrDefault(game =>
        string.Equals(game.Name, ActiveGameName, StringComparison.OrdinalIgnoreCase)) is { } row
            ? row.ModSummary
            : $"共 {TotalMods} 个 Mod";

    /// <summary>One line about available updates, so the overview can point at the next action.</summary>
    public string UpdateSummaryText => TotalUpdates > 0
        ? $"{TotalUpdates} 个 Mod 有可用更新"
        : "暂无可用更新";

    /// <summary>Per-game summary rows.</summary>
    public BulkObservableCollection<GameSummaryRow> Games { get; } = [];

    /// <summary>Issues that need the user's attention.</summary>
    public BulkObservableCollection<IssueRow> Issues { get; } = [];

    /// <summary>The most recent log entries, newest first — the "recent activity" feed.</summary>
    public BulkObservableCollection<ActivityRow> RecentActivity { get; } = [];

    /// <summary>The most recently staged mods, newest first.</summary>
    public BulkObservableCollection<RecentInstallRow> RecentInstalls { get; } = [];

    /// <summary>One-line summary of the whole library.</summary>
    public string Headline    {
        get => _headline;
        private set => SetProperty(ref _headline, value);
    }

    /// <summary>Text shown when the previous session left work to repair.</summary>
    public string RecoveryMessage
    {
        get => _recoveryMessage;
        private set => SetProperty(ref _recoveryMessage, value);
    }

    /// <summary>True when there is a recovery message to show.</summary>
    public bool HasRecovery
    {
        get => _hasRecovery;
        private set => SetProperty(ref _hasRecovery, value);
    }

    /// <summary>Registered games.</summary>
    public int TotalGames
    {
        get => _totalGames;
        private set => SetProperty(ref _totalGames, value);
    }

    /// <summary>
    /// Games found on this machine, including ones with no dedicated definition.
    /// </summary>
    /// <remarks>
    /// Reported separately from the managed count because "how many games did we find" and "how many can we
    /// manage" are different questions; conflating them made a full Steam library look like two games.
    /// </remarks>
    public int DetectedGames
    {
        get => _detectedGames;
        private set => SetProperty(ref _detectedGames, value);
    }

    /// <summary>Detected games with a dedicated definition.</summary>
    public int SupportedGames
    {
        get => _supportedGames;
        private set => SetProperty(ref _supportedGames, value);
    }

    /// <summary>Detected games that would use the generic adapter.</summary>
    public int GenericGames
    {
        get => _genericGames;
        private set => SetProperty(ref _genericGames, value);
    }

    /// <summary>One line about detection versus management.</summary>
    public string DetectionSummaryText => DetectedGames == 0
        ? $"已管理 {TotalGames} 个游戏"
        : $"检测到 {DetectedGames} 个游戏：专用支持 {SupportedGames} 个、通用模式 {GenericGames} 个；已管理 {TotalGames} 个";

    /// <summary>Staged mods across every game.</summary>
    public int TotalMods
    {
        get => _totalMods;
        private set => SetProperty(ref _totalMods, value);
    }

    /// <summary>Mods enabled in their active profiles.</summary>
    public int TotalEnabled
    {
        get => _totalEnabled;
        private set => SetProperty(ref _totalEnabled, value);
    }

    /// <summary>Mods explicitly disabled in their active profiles.</summary>
    public int TotalDisabled
    {
        get => _totalDisabled;
        private set => SetProperty(ref _totalDisabled, value);
    }

    /// <summary>Mods with a newer upstream version.</summary>
    public int TotalUpdates
    {
        get => _totalUpdates;
        private set => SetProperty(ref _totalUpdates, value);
    }

    /// <summary>Unresolved conflicts.</summary>
    public int TotalConflicts
    {
        get => _totalConflicts;
        private set
        {
            if (SetProperty(ref _totalConflicts, value))
            {
                OnPropertyChanged(nameof(HasConflicts));
            }
        }
    }

    /// <summary>True when at least one unresolved conflict exists, showing the shortcut.</summary>
    public bool HasConflicts => _totalConflicts > 0;

    /// <summary>Unsatisfied dependencies.</summary>
    public int TotalMissingDependencies
    {
        get => _totalMissingDependencies;
        private set => SetProperty(ref _totalMissingDependencies, value);
    }

    /// <summary>One-line summary of the whole install base, shown under the welcome headline.</summary>
    public string HeaderSummary =>
        $"共 {TotalGames} 个游戏 · {TotalMods} 个 Mod · {TotalEnabled} 个已启用";

    /// <summary>Secondary counter shown beside the headline metrics.</summary>
    public string DisabledSummary => $"已禁用 {TotalDisabled}";

    /// <summary>Secondary counter shown beside the headline metrics.</summary>
    public string MissingDependencySummary => $"缺失依赖 {TotalMissingDependencies}";

    /// <summary>How many games are actually managed, shown beside the detected counts.</summary>
    public string ManagedSummary => $"已管理 {TotalGames} 个";

    /// <summary>Downloads currently running.</summary>
    public int ActiveDownloads
    {
        get => _activeDownloads;
        private set => SetProperty(ref _activeDownloads, value);
    }

    /// <summary>Downloads that failed and can be retried.</summary>
    public int FailedDownloads
    {
        get => _failedDownloads;
        private set => SetProperty(ref _failedDownloads, value);
    }

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        var rows = new List<GameSummaryRow>();
        var issues = new List<IssueRow>();
        var recent = new List<RecentInstallRow>();
        var mods = 0;
        var disabledTotal = 0;
        var updates = 0;
        var conflicts = 0;
        var missing = 0;

        foreach (var instance in _host.Library.AllGames())
        {
            var adapter = _host.Adapters.Get(instance.GameId);
            if (adapter is null)
            {
                issues.Add(new IssueRow(
                    IssueSeverity.Warning,
                    $"未找到游戏定义：{instance.GameId}",
                    "该游戏的定义文件已不存在，无法继续管理它的 Mod。",
                    "重新添加该游戏，或恢复它的定义文件。",
                    null));
                continue;
            }

            var gameMods = _host.Library.ModsForGame(instance.GameId);
            var profiles = _host.Library.ProfilesForGame(instance.GameId);
            var profile = profiles.FirstOrDefault(candidate => candidate.IsActive);

            var modSet = new List<ModSetEntry>();
            foreach (var mod in gameMods)
            {
                modSet.Add(new ModSetEntry(mod, _host.Library.GetManifest(instance.GameId, mod.Id), mod.StagingPath));
            }

            // Ordered by the active profile's load order, so the conflict count (and any winner the
            // dashboard would report) matches what the planner actually deploys.
            if (profile is not null)
            {
                var byId = modSet.ToDictionary(entry => entry.Metadata.Id, StringComparer.Ordinal);
                var ordered = new List<ModSetEntry>(modSet.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var modId in profile.ModOrder)
                {
                    if (profile.DisabledModIds.Contains(modId, StringComparer.Ordinal) || !seen.Add(modId))
                    {
                        continue;
                    }

                    if (byId.TryGetValue(modId, out var entry))
                    {
                        ordered.Add(entry);
                    }
                }

                foreach (var entry in modSet)
                {
                    if (!profile.DisabledModIds.Contains(entry.Metadata.Id, StringComparer.Ordinal)
                        && seen.Add(entry.Metadata.Id))
                    {
                        ordered.Add(entry);
                    }
                }

                modSet = ordered;
            }

            // Rules come from three places: the repository, and each profile's own per-path decisions. They
            // are merged here so the conflict count matches what the planner would actually resolve.
            var rules = new List<ConflictResolution>(_host.Library.ConflictRulesForGame(instance.GameId));
            foreach (var candidate in profiles)
            {
                foreach (var rule in candidate.ConflictOverrides.Values)
                {
                    rules.Add(rule);
                }
            }

            var conflictList = _host.Conflicts.Analyze(instance.GameId, modSet, rules);
            var missingList = _host.Dependencies.FindMissing(adapter, instance.InstallPath, gameMods);
            var gameIssues = await _host.Health
                .CheckAsync(adapter, instance, gameMods, conflictList)
                .ConfigureAwait(true);

            var gameUpdates = gameMods.Count(mod => mod.AvailableVersion is { Length: > 0 });

            mods += gameMods.Count;
            updates += gameUpdates;
            conflicts += conflictList.Count(c => c.Resolution == ConflictResolutionKind.Undecided);
            missing += missingList.Count;

            var enabled = profile is null
                ? 0
                : gameMods.Count(mod => profile.ModOrder.Contains(mod.Id, StringComparer.Ordinal)
                    && !profile.DisabledModIds.Contains(mod.Id, StringComparer.Ordinal));

            var disabled = profile is null
                ? 0
                : gameMods.Count(mod => profile.DisabledModIds.Contains(mod.Id, StringComparer.Ordinal));

            disabledTotal += disabled;

            foreach (var mod in gameMods)
            {
                recent.Add(new RecentInstallRow(mod.Name, adapter.Definition.Name, mod.InstalledAt));
            }

            rows.Add(new GameSummaryRow(
                adapter.Definition.Name,
                instance.InstallPath,
                instance.ModRootPath,
                gameMods.Count,
                enabled,
                gameUpdates,
                conflictList.Count,
                missingList.Count,
                instance.LoaderInstalled,
                adapter.Definition.Capabilities,
                instance.SteamAppId,
                adapter.Definition.NexusDomain));

            foreach (var issue in gameIssues)
            {
                issues.Add(new IssueRow(
                    issue.Severity,
                    issue.Title,
                    issue.Detail,
                    issue.FixHint ?? string.Empty,
                    adapter.Definition.Name));
            }
        }

        Games.ReplaceAll(rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase));

        // Covers load after the list is on screen, so artwork never delays the overview rendering.
        _ = LoadGameArtworkAsync();

        // Detection is reported separately from management. Counting only catalogue matches is what made a
        // full Steam library read as two games, so the overview now says how many were found and how many
        // are officially supported.
        try
        {
            var managedIds = _host.Library.AllGames().Select(game => game.GameId).ToList();
            var scan = await Task.Run(() => new SteamLibraryScanner().Scan()).ConfigureAwait(true);
            var summary = GameSupportResolver.Summarize(
                new GameSupportResolver().ResolveAll(scan.Games, _host.Adapters),
                managedIds);

            DetectedGames = summary.Detected;
            SupportedGames = summary.Supported;
            GenericGames = summary.Generic;
            OnPropertyChanged(nameof(DetectionSummaryText));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Detection is best-effort: a locked Steam directory must not break the overview.
            _host.Logger.Warn("Games", "Steam 游戏库扫描失败，概览未包含检测统计。", ex.Message);
        }

        TotalGames = rows.Count;
        TotalMods = mods;
        TotalEnabled = CountEnabled(rows);
        TotalDisabled = disabledTotal;
        TotalUpdates = updates;
        TotalConflicts = conflicts;
        TotalMissingDependencies = missing;

        // The header line and the two secondary counters are derived, so they are re-announced here
        // rather than maintained as separate state that could drift out of step with the numbers.
        OnPropertyChanged(nameof(HeaderSummary));
        OnPropertyChanged(nameof(DisabledSummary));
        OnPropertyChanged(nameof(MissingDependencySummary));
        OnPropertyChanged(nameof(ActiveGameName));
        OnPropertyChanged(nameof(UpdateSummaryText));

        // Most recently staged mods first; the page shows the newest handful.
        RecentInstalls.ReplaceAll(recent
            .OrderByDescending(entry => entry.InstalledAt)
            .Take(6)
            .ToList());

        var downloads = _host.Downloads.All();
        ActiveDownloads = downloads.Count(record => record.IsActive);
        FailedDownloads = downloads.Count(record => record.State == DownloadState.Failed);

        foreach (var download in downloads.Where(record => record.State == DownloadState.Failed))
        {
            issues.Add(new IssueRow(
                IssueSeverity.Warning,
                $"下载失败：{download.FileName}",
                download.Error ?? "下载未完成。",
                "打开“下载”页重试。",
                null));
        }

        // Blocking issues first, then by severity, so the most actionable item is at the top.
        Issues.ReplaceAll(issues
            .OrderByDescending(issue => issue.Severity)
            .ThenBy(issue => issue.Title, StringComparer.Ordinal));

        HasRecovery = _host.Recovery.HadWork;
        RecoveryMessage = string.Join(" ", _host.Recovery.Messages);

        // Recent activity: the structured log is the single source of truth for what the app did, so
        // the dashboard feed is a projection of it rather than a second, separately-maintained list.
        RecentActivity.ReplaceAll(_host.Logger
            .Recent(15)
            .Select(entry => new ActivityRow(entry))
            .ToList());

        Headline = rows.Count == 0
            ? "还没有游戏。请到“游戏”页添加一个。"
            : $"{rows.Count} 个游戏，{mods} 个 Mod，{CountEnabled(rows)} 个已启用。";

        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>Counts enabled mods across the given rows.</summary>
    /// <param name="rows">Summary rows.</param>
    /// <summary>
    /// Loads cover art for the overview's game tiles.
    /// </summary>
    /// <remarks>
    /// The dashboard and the game library deliberately share one resolver, so a game cannot show a cover on
    /// one page and a placeholder on the other. Every game is considered, including the ones with no Nexus
    /// domain — those are the majority, and they are exactly the ones that had no artwork before.
    /// </remarks>
    private async Task LoadGameArtworkAsync()
    {
        try
        {
            if (Games.Count == 0)
            {
                return;
            }

            var domains = Games
                .Select(game => game.NexusDomain)
                .Where(domain => !string.IsNullOrWhiteSpace(domain))
                .Select(domain => domain!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var artwork = domains.Count > 0
                ? await _gameImages.GetAsync(_host.Nexus, domains).ConfigureAwait(true)
                : new Dictionary<string, NexusGameArtwork>(StringComparer.OrdinalIgnoreCase);

            foreach (var game in Games)
            {
                var candidate = GameArtworkResolver.Resolve(game.SteamAppId, game.NexusDomain, artwork);
                game.ArtworkSource = candidate.Describe;

                if (candidate.HasImage)
                {
                    game.CoverImage = await _covers.LoadAsync(candidate.Url).ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException
            or System.Net.Http.HttpRequestException
            or InvalidOperationException)
        {
            _host.Logger.Warn("Nexus", "游戏封面加载失败，已保留无图状态。", ex.Message);
        }
    }

    private static int CountEnabled(IEnumerable<GameSummaryRow> rows) => rows.Sum(row => row.EnabledModCount);
}

/// <summary>One game's row on the Dashboard.</summary>
/// <param name="Name">Display name.</param>
/// <param name="InstallPath">Install directory.</param>
/// <param name="ModRootPath">Mod root.</param>
/// <param name="ModCount">Staged mod count.</param>
/// <param name="EnabledModCount">Enabled mod count.</param>
/// <param name="UpdateCount">Available updates.</param>
/// <param name="ConflictCount">Conflict count.</param>
/// <param name="MissingDependencyCount">Missing dependency count.</param>
/// <param name="LoaderInstalled">Whether the mod loader is present.</param>
/// <param name="Capabilities">Declared capabilities.</param>
public sealed record GameSummaryRow(
    string Name,
    string InstallPath,
    string ModRootPath,
    int ModCount,
    int EnabledModCount,
    int UpdateCount,
    int ConflictCount,
    int MissingDependencyCount,
    bool LoaderInstalled,
    GameCapability Capabilities,
    int? SteamAppId = null,
    string? NexusDomain = null) : System.ComponentModel.INotifyPropertyChanged
{
    private ImageSource? _coverImage;

    /// <inheritdoc />
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Cover art, or null when there is no reliable source for this game.</summary>
    public ImageSource? CoverImage
    {
        get => _coverImage;
        set
        {
            if (ReferenceEquals(_coverImage, value))
            {
                return;
            }

            _coverImage = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CoverImage)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasCover)));
        }
    }

    /// <summary>True when artwork has been loaded.</summary>
    public bool HasCover => _coverImage is not null;

    /// <summary>Where the cover came from, or why there is none. Shown as a tooltip on the tile.</summary>
    public string ArtworkSource { get; set; } = "尚未解析";

    /// <summary>Loader badge text.</summary>
    public string LoaderText => Capabilities.Has(GameCapability.ModLoader)
        ? (LoaderInstalled ? "Loader 已安装" : "缺少 Loader")
        : "无需 Loader";

    /// <summary>Short mod-count summary.</summary>
    public string ModSummary => $"{EnabledModCount} / {ModCount} 已启用";

    /// <summary>Short conflict summary.</summary>
    public string ConflictSummary => ConflictCount == 0 ? "无冲突" : $"{ConflictCount} 个冲突";

    /// <summary>Short dependency summary.</summary>
    public string DependencySummary => MissingDependencyCount == 0
        ? "依赖已满足"
        : $"缺少 {MissingDependencyCount} 项";

    /// <summary>Short update summary.</summary>
    public string UpdateSummary => UpdateCount == 0 ? "已是最新" : $"{UpdateCount} 个更新";
}

/// <summary>An actionable issue row.</summary>
/// <param name="Severity">How urgent.</param>
/// <param name="Title">Short summary.</param>
/// <param name="Detail">Explanation.</param>
/// <param name="Fix">What to do.</param>
/// <param name="Scope">Game or mod it belongs to.</param>
public sealed record IssueRow(IssueSeverity Severity, string Title, string Detail, string Fix, string? Scope)
{
    /// <summary>Colour for the severity badge.</summary>
    public Brush Tone => Palette.ForSeverity(Severity);

    /// <summary>Fill for the severity badge.</summary>
    public Brush Fill => Palette.FillForSeverity(Severity);

    /// <summary>Badge label.</summary>
    /// <summary>Severity name in Chinese.</summary>
    public string Level => Zh.Severity(Severity);

    /// <summary>True when there is a suggested fix to show.</summary>
    public bool HasFix => !string.IsNullOrWhiteSpace(Fix);

    /// <summary>True when the scope is known.</summary>
    public bool HasScope => !string.IsNullOrWhiteSpace(Scope);
}

/// <summary>One row of the dashboard's recent-activity feed, projected from a log entry.</summary>
public sealed class ActivityRow
{
    /// <summary>Creates a row from a log entry.</summary>
    /// <param name="entry">Log entry.</param>
    public ActivityRow(LogEntry entry)
    {
        Timestamp = entry.Timestamp.ToLocalTime();
        Category = entry.Category;
        Message = entry.Message;
        Level = entry.Level;
    }

    /// <summary>Local timestamp.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Category the entry was logged under, in Chinese for display.</summary>
    public string CategoryText => Zh.Category(Category);

    /// <summary>Raw category the entry was logged under (Deploy, Install, Games…).</summary>
    public string Category { get; }

    /// <summary>Message text.</summary>
    public string Message { get; }

    /// <summary>Log level, driving the badge tone.</summary>
    public LogLevel Level { get; }

    /// <summary>Level name in Chinese.</summary>
    public string LevelText => Zh.LogLevel(Level);

    /// <summary>Time as a short clock string.</summary>
    public string TimeText => Timestamp.ToString("HH:mm");

    /// <summary>Badge tone for the level.</summary>
    public Brush Tone => Palette.ForLogLevel(Level);
}

/// <summary>One row of the dashboard's "recently installed" list.</summary>
public sealed class RecentInstallRow
{
    /// <summary>Creates a row.</summary>
    /// <param name="name">Mod name.</param>
    /// <param name="gameName">Game the mod belongs to.</param>
    /// <param name="installedAt">When it was staged.</param>
    public RecentInstallRow(string name, string gameName, DateTimeOffset installedAt)
    {
        Name = name;
        GameName = gameName;
        InstalledAt = installedAt.ToLocalTime();
    }

    /// <summary>Mod name.</summary>
    public string Name { get; }

    /// <summary>Game name.</summary>
    public string GameName { get; }

    /// <summary>Local install time.</summary>
    public DateTimeOffset InstalledAt { get; }

    /// <summary>Short time string.</summary>
    public string TimeText => InstalledAt.ToString("MM-dd HH:mm");

    /// <summary>Secondary line.</summary>
    public string Detail => $"{GameName} · {TimeText}";
}
