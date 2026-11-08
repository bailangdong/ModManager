using System.IO;
using System.Windows.Media;
using ModManager.Core;
using ModManager.Core.Services;

namespace ModManager.App.ViewModels;

/// <summary>
/// Games: detect, add, remove, and launch.
/// </summary>
/// <remarks>
/// The user's spec opens the experience with "选择游戏 → 自动识别游戏目录", so detection runs across every
/// known definition and the results are shown as candidates the user confirms, rather than asking for a
/// folder path first. Manual addition stays available for the cases detection cannot reach — a portable
/// install on a removable drive, or a game with no store metadata.
/// </remarks>
public sealed class GamesViewModel : ViewModelBase, IPageViewModel
{
    private readonly ApplicationHost _host;
    private readonly ShellViewModel? _shell;
    private readonly CoverImageLoader _covers;
    private readonly NexusGameImageCache _gameImages;
    private string _manualPath = string.Empty;
    private GameDefinition? _manualDefinition;
    private string _status = "正在扫描已安装的游戏…";
    private string _appIdInput = string.Empty;
    private string _detectionSummaryText = string.Empty;

    /// <summary>Creates the page.</summary>
    /// <param name="host">Composition root.</param>
    /// <param name="shell">Shell, when available, so the header's game list can be refreshed after a change.</param>
    public GamesViewModel(ApplicationHost host, ShellViewModel? shell = null)
    {
        _host = host;
        _shell = shell;
        _covers = new CoverImageLoader(host.Images);
        _gameImages = new NexusGameImageCache(host.Paths, host.Logger);

        ScanCommand = new RelayCommand(() => ScanAsync());
        AddCommand = new RelayCommand<DetectedGameRow>(row => AddAsync(row));
        AddDetectedCommand = new RelayCommand<SteamGameRow>(row => AddDetectedAsync(row), row => row?.CanAdd == true);
        SetModRootCommand = new RelayCommand<RegisteredGameRow>(row => SetModRootAsync(row), row => row?.IsGeneric == true);
        AddByAppIdCommand = new RelayCommand(() => AddByAppIdAsync(), () => _appIdInput.Trim().Length > 0);
        OpenDetectedFolderCommand = new RelayCommand<SteamGameRow>(row => OpenDetectedFolderAsync(row));
        RemoveCommand = new RelayCommand<RegisteredGameRow>(row => RemoveAsync(row));
        LaunchCommand = new RelayCommand<RegisteredGameRow>(row => LaunchAsync(row));
        AddManualCommand = new RelayCommand(() => AddManualAsync(), () => _manualDefinition is not null && _manualPath.Length > 0);
        EditPathCommand = new RelayCommand<RegisteredGameRow>(row => EditPathAsync(row));
        SetDefaultCommand = new RelayCommand<RegisteredGameRow>(row => SetDefaultAsync(row), row => row is not null && !row.IsDefault);
        OpenGameFolderCommand = new RelayCommand<RegisteredGameRow>(row =>
        {
            OpenGameFolder(row);
            return Task.CompletedTask;
        });
        LaunchSettingsCommand = new RelayCommand<RegisteredGameRow>(row =>
        {
            EditLaunchSettings(row);
            return Task.CompletedTask;
        });

        // "Enter management" is the primary action on a game row. It makes the game active — which is what
        // every other page operates on — and lands the user where they would go next anyway.
        ManageCommand = new RelayCommand<RegisteredGameRow>(row =>
        {
            if (row is not null && _shell is not null)
            {
                var choice = _shell.Games.FirstOrDefault(candidate =>
                    string.Equals(candidate.Instance.GameId, row.GameId, StringComparison.OrdinalIgnoreCase));

                if (choice is not null)
                {
                    _shell.SelectGame(choice);
                }
            }

            _shell?.NavigateTo("本地 Mod");
            return Task.CompletedTask;
        });
    }

    /// <summary>Games found in the Steam libraries, supported or not.</summary>
    public BulkObservableCollection<SteamGameRow> DetectedGames { get; } = [];

    /// <summary>Known games found outside Steam (registry, well-known directories).</summary>
    public BulkObservableCollection<DetectedGameRow> OtherMatches { get; } = [];

    /// <summary>Registered games.</summary>
    public BulkObservableCollection<RegisteredGameRow> Registered { get; } = [];

    /// <summary>Definitions available for manual addition.</summary>
    public BulkObservableCollection<GameDefinition> AvailableDefinitions { get; } = [];

    /// <summary>Rescans for installed games.</summary>
    public RelayCommand ScanCommand { get; }

    /// <summary>Registers a detected game, including generic Steam games.</summary>
    public RelayCommand<DetectedGameRow> AddCommand { get; }

    /// <summary>Adds a Steam game that was either officially supported or generic.</summary>
    public RelayCommand<SteamGameRow> AddDetectedCommand { get; }

    /// <summary>Changes the mod directory of a generic game.</summary>
    public RelayCommand<RegisteredGameRow> SetModRootCommand { get; }

    /// <summary>Adds a Steam game by typing its app id.</summary>
    public RelayCommand AddByAppIdCommand { get; }

    /// <summary>Opens a detected game's install directory.</summary>
    public RelayCommand<SteamGameRow> OpenDetectedFolderCommand { get; }

    /// <summary>Forgets a registered game. Deletes no files.</summary>
    public RelayCommand<RegisteredGameRow> RemoveCommand { get; }

    /// <summary>Launches a registered game.</summary>
    public RelayCommand<RegisteredGameRow> LaunchCommand { get; }

    /// <summary>Adds a game from a hand-picked directory.</summary>
    public RelayCommand AddManualCommand { get; }

    /// <summary>Re-points a registered game at a different install directory.</summary>
    public RelayCommand<RegisteredGameRow> EditPathCommand { get; }

    /// <summary>Makes a game the default the app opens on.</summary>
    public RelayCommand<RegisteredGameRow> SetDefaultCommand { get; }

    /// <summary>Opens a game's install directory in Explorer.</summary>
    public RelayCommand<RegisteredGameRow> OpenGameFolderCommand { get; }

    /// <summary>Opens the launch-settings editor for a game.</summary>
    public RelayCommand<RegisteredGameRow> LaunchSettingsCommand { get; }

    /// <summary>Makes a game active and opens its Mod list.</summary>
    public RelayCommand<RegisteredGameRow> ManageCommand { get; }

    /// <summary>Directory the user picked for manual addition.</summary>
    public string ManualPath
    {
        get => _manualPath;
        set
        {
            if (SetProperty(ref _manualPath, value))
            {
                AddManualCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Definition to use for manual addition.</summary>
    public GameDefinition? ManualDefinition
    {
        get => _manualDefinition;
        set
        {
            if (SetProperty(ref _manualDefinition, value))
            {
                AddManualCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>App id the user typed for manual addition.</summary>
    public string AppIdInput
    {
        get => _appIdInput;
        set
        {
            if (SetProperty(ref _appIdInput, value))
            {
                AddByAppIdCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Counts of the last scan, shown as the page's statistics.</summary>
    public GameDetectionSummary Detection { get; private set; } = GameDetectionSummary.Empty;

    /// <summary>One-line summary of the last scan.</summary>
    public string DetectionSummaryText
    {
        get => _detectionSummaryText;
        private set => SetProperty(ref _detectionSummaryText, value);
    }

    /// <summary>Games found on disk.</summary>
    public int DetectedCount => Detection.Detected;

    /// <summary>Games with a dedicated definition.</summary>
    public int SupportedCount => Detection.Supported;

    /// <summary>Games that would use the generic adapter.</summary>
    public int GenericCount => Detection.Generic;

    /// <summary>Games whose install cannot be used.</summary>
    public int InvalidCount => Detection.Invalid;

    /// <summary>Games already added to the library.</summary>
    public int ManagedCount => Detection.Managed;

    /// <summary>True when the scan produced anything to show.</summary>
    public bool HasDetectedGames => DetectedGames.Count > 0;

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        AvailableDefinitions.ReplaceAll(_host.Adapters.All().Select(adapter => adapter.Definition));
        ReloadRegistered();
        await ScanAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Fills in game artwork for the rows whose definition declares a Nexus domain.
    /// </summary>
    /// <remarks>
    /// The games list is fetched at most once a week and cached on disk, so this costs nothing on later runs.
    /// A game with no Nexus mapping simply keeps the plain tile: inventing artwork for it is not an option.
    /// </remarks>
    private async Task LoadGameArtworkAsync()
    {
        try
        {
            if (DetectedGames.Count == 0)
            {
                return;
            }

            var artwork = await ResolveNexusArtworkAsync(DetectedGames.Select(row => row.NexusDomain))
                .ConfigureAwait(true);

            var withCover = 0;
            var withoutCover = new List<string>();

            foreach (var row in DetectedGames)
            {
                var candidate = GameArtworkResolver.Resolve(row.AppId, row.NexusDomain, artwork);
                row.ArtworkSource = candidate.Describe;

                if (candidate.HasImage)
                {
                    row.CoverImage = await _covers.LoadAsync(candidate.Url).ConfigureAwait(true);
                }

                if (row.HasCover)
                {
                    withCover++;
                }
                else
                {
                    withoutCover.Add($"{row.Name}（{candidate.Describe}）");
                }
            }

            // One line per scan rather than one per game: the acceptance report needs real coverage, and a
            // silent fallback to a placeholder is exactly the failure that went unnoticed before.
            _host.Logger.Info(
                "Images",
                $"游戏封面：{withCover}/{DetectedGames.Count} 已加载。",
                withoutCover.Count == 0 ? "全部游戏都有封面。" : "使用占位图：" + string.Join("；", withoutCover));
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            _host.Logger.Warn("Nexus", "游戏封面加载失败，已保留无图状态。", ex.Message);
        }
    }

    /// <summary>
    /// Fills in artwork for the managed-game rows.
    /// </summary>
    /// <remarks>
    /// Every row is considered, not only the ones carrying a Nexus domain. A game added through the generic
    /// adapter has no dedicated definition, so no domain, and looping over domain-bearing rows only is exactly
    /// why most of this machine's games had no cover: the second source was never consulted. See
    /// <see cref="GameArtworkResolver"/> for the order of precedence.
    /// </remarks>
    private async Task LoadRegisteredArtworkAsync()
    {
        try
        {
            if (Registered.Count == 0)
            {
                return;
            }

            var artwork = await ResolveNexusArtworkAsync(Registered.Select(row => row.NexusDomain))
                .ConfigureAwait(true);

            foreach (var row in Registered)
            {
                var candidate = GameArtworkResolver.Resolve(row.SteamAppId, row.NexusDomain, artwork);
                row.ArtworkSource = candidate.Describe;

                if (candidate.HasImage)
                {
                    row.CoverImage = await _covers.LoadAsync(candidate.Url).ConfigureAwait(true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            _host.Logger.Warn("Nexus", "游戏封面加载失败，已保留无图状态。", ex.Message);
        }
    }

    /// <summary>
    /// Resolves Nexus artwork for whichever domains are actually in play.
    /// </summary>
    /// <param name="domains">Candidate domains; nulls and blanks are ignored.</param>
    private async Task<IReadOnlyDictionary<string, NexusGameArtwork>> ResolveNexusArtworkAsync(
        IEnumerable<string?> domains)
    {
        var wanted = domains
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (wanted.Count == 0)
        {
            return new Dictionary<string, NexusGameArtwork>(StringComparer.OrdinalIgnoreCase);
        }

        return await _gameImages.GetAsync(_host.Nexus, wanted).ConfigureAwait(true);
    }

    /// <summary>Reloads the registered-game list.</summary>
    private void ReloadRegistered()
    {
        var rows = new List<RegisteredGameRow>();
        var defaultId = _host.Settings.Settings.LastSelectedGameId;

        foreach (var instance in _host.Library.AllGames())
        {
            var adapter = _host.Adapters.Get(instance.GameId);
            if (adapter is null)
            {
                continue;
            }

            var loader = adapter.CheckLoader(instance.InstallPath);

            // Real counts from the library and the active profile — never display-only estimates.
            var gameMods = _host.Library.ModsForGame(instance.GameId);
            var profile = _host.Library.ProfilesForGame(instance.GameId).FirstOrDefault(candidate => candidate.IsActive);
            var enabled = profile is null
                ? 0
                : gameMods.Count(mod => profile.ModOrder.Contains(mod.Id, StringComparer.Ordinal)
                    && !profile.DisabledModIds.Contains(mod.Id, StringComparer.Ordinal));

            // A game added in generic mode can gain official support later. Detecting that here is what lets
            // the list say so without re-creating the record or touching the user's chosen mod root. The
            // generated fallback definition is excluded: it exists precisely because there is no support.
            var supportedId = instance.SteamAppId is { } appId
                ? _host.Adapters.All()
                    .Select(candidate => candidate.Definition)
                    .Where(definition => definition.SteamAppId == appId && !definition.IsGenericFallback)
                    .Select(definition => definition.Id)
                    .FirstOrDefault()
                : null;

            rows.Add(new RegisteredGameRow(
                instance.GameId,
                adapter.Definition.Name,
                instance.InstallPath,
                instance.ModRootPath,
                instance.AddedManually,
                loader,
                string.Equals(instance.GameId, defaultId, StringComparison.OrdinalIgnoreCase),
                gameMods.Count,
                enabled,
                instance.SupportKind,
                instance.SteamAppId,
                supportedId,
                adapter.Definition.NexusDomain));
        }

        Registered.ReplaceAll(rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase));

        // Rebuilding the list throws away the previous rows and their artwork, so it is re-attached here
        // rather than at each of the six call sites that rebuild it. Fire-and-forget for the same reason
        // the detected list does it: a cover must never delay the list appearing.
        _ = LoadRegisteredArtworkAsync();
    }

    /// <summary>
    /// Scans the Steam libraries for every installed game, and the other probes for known games installed
    /// outside Steam.
    /// </summary>
    /// <remarks>
    /// The Steam part reports <i>everything</i> it finds: a game with no dedicated definition is shown as a
    /// generic candidate rather than dropped. That is the whole point of the scan — "detected" and
    /// "officially supported" are different questions, and the interface must answer both.
    /// </remarks>
    private async Task ScanAsync()
    {
        Status = "正在扫描 Steam 游戏库…";

        var known = new HashSet<string>(
            _host.Library.AllGames().Select(instance => instance.GameId),
            StringComparer.OrdinalIgnoreCase);

        SteamScanResult scan = SteamScanResult.Empty;
        var resolved = new List<ResolvedGame>();
        var otherRows = new List<DetectedGameRow>();

        await Task.Run(() =>
        {
            scan = new SteamLibraryScanner().Scan();
            resolved = new GameSupportResolver().ResolveAll(scan.Games, _host.Adapters).ToList();

            // Non-Steam installs of known games (registry, common directories) are still worth surfacing:
            // a portable copy on a removable drive has no Steam manifest at all.
            var detector = new GameDetector();
            foreach (var adapter in _host.Adapters.All())
            {
                if (known.Contains(adapter.GameId)
                    || adapter.Definition.SteamAppId is not null)
                {
                    continue;
                }

                var result = detector.Detect(adapter.Definition);
                if (result is { Found: true, InstallPath: not null })
                {
                    otherRows.Add(new DetectedGameRow(
                        adapter.Definition,
                        result.InstallPath,
                        Zh.ProbeKind(result.Method),
                        result.Evidence,
                        result.Confidence));
                }
            }
        }).ConfigureAwait(true);

        var registered = _host.Library.AllGames().ToList();

        DetectedGames.ReplaceAll(resolved.Select(entry =>
        {
            var existing = registered.FirstOrDefault(instance =>
                string.Equals(instance.GameId, entry.GameId, StringComparison.OrdinalIgnoreCase)
                || instance.SteamAppId == entry.Detected.AppId);

            return new SteamGameRow(
                entry,
                existing,
                registered,
                existing is null ? 0 : _host.Library.ModsForGame(existing.GameId).Count);
        }));

        OtherMatches.ReplaceAll(otherRows
            .OrderByDescending(row => row.Confidence)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase));

        // Game artwork comes from Nexus, and the mapping arrives after the list is already visible.
        _ = LoadGameArtworkAsync();

        var summary = GameSupportResolver.Summarize(
            resolved,
            known);
        DetectionSummaryText = summary.Detected == 0
            ? "未找到 Steam 游戏库。可用下方的方式手动添加。"
            : $"已扫描 {scan.Libraries.Count} 个 Steam 库：检测到 {summary.Detected} 个游戏，"
                + $"其中 {summary.Supported} 个有专用支持、{summary.Generic} 个可使用通用模式"
                + (summary.Invalid > 0 ? $"，{summary.Invalid} 个异常" : string.Empty)
                + $"；已管理 {summary.Managed} 个。";

        Detection = summary;
        Status = DetectionSummaryText;

        OnPropertyChanged(nameof(DetectedCount));
        OnPropertyChanged(nameof(SupportedCount));
        OnPropertyChanged(nameof(GenericCount));
        OnPropertyChanged(nameof(InvalidCount));
        OnPropertyChanged(nameof(ManagedCount));
        OnPropertyChanged(nameof(HasDetectedGames));
        _shell?.ReloadGames();
    }

    /// <summary>
    /// Registers a detected game, asking for a mod directory first when the game has no definition.
    /// </summary>
    /// <param name="row">Detected game to add.</param>
    private async Task AddDetectedAsync(SteamGameRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.Detected.IsValid)
        {
            Status = row.Detected.Problem ?? "该游戏的安装目录不可用，无法添加。";
            return;
        }

        if (row.IsSupported)
        {
            var adapter = _host.Adapters.Get(row.Resolved.GameId);
            if (adapter is null)
            {
                Status = "该游戏定义已不可用。";
                return;
            }

            AddGame(adapter, row.Detected.InstallDirectory, addedManually: false, row.Detected);
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        // Generic: the mod root is the user's decision, never a guess. Ask once, remember it, and store it
        // in the generated definition so a restart resolves to the same place.
        var choice = row.ModRootChoice ?? AskForModRoot(row.Detected, row.ExistingModRootRelative);
        if (choice is null)
        {
            return;
        }

        await RegisterGenericAsync(row, choice).ConfigureAwait(true);
    }

    /// <summary>Builds and registers the generic definition for a Steam game.</summary>
    /// <param name="row">Detected game.</param>
    /// <param name="choice">Mod root chosen by the user.</param>
    private async Task RegisterGenericAsync(SteamGameRow row, ModRootChoice choice)
    {
        var definition = SteamGameDefinitions.Build(
            row.Detected.AppId,
            row.Detected.Name,
            choice.RelativePath,
            choice.InUserProfile);

        try
        {
            var path = _host.Adapters.SaveUserDefinition(definition, _host.Paths.Games);
            var adapter = _host.Adapters.Get(definition.Id);
            if (adapter is null)
            {
                Status = "通用游戏定义保存后未能注册。";
                return;
            }

            AddGame(adapter, row.Detected.InstallDirectory, addedManually: true, row.Detected, GameSupportKind.Generic);

            _host.Logger.Info(
                "Games",
                $"已用通用模式添加 Steam 游戏 “{row.Detected.Name}”（AppID {row.Detected.AppId}），Mod 目录 {choice.Description}。",
                path);

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "无法保存该游戏的通用定义：" + ex.Message;
        }
    }

    /// <summary>
    /// Asks the user where mods should go for a game that has no definition.
    /// </summary>
    /// <param name="game">Detected game.</param>
    /// <param name="existingRelative">Mod root already chosen for this game, when there is one.</param>
    private ModRootChoice? AskForModRoot(InstalledSteamGame game, string? existingRelative)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"选择 {game.Name} 的 Mod 目录（必须位于游戏目录或“文档”目录内）",
            InitialDirectory = existingRelative is { Length: > 0 } && Directory.Exists(
                Path.Combine(game.InstallDirectory, existingRelative.Replace('/', Path.DirectorySeparatorChar)))
                ? Path.Combine(game.InstallDirectory, existingRelative.Replace('/', Path.DirectorySeparatorChar))
                : game.InstallDirectory,
        };

        if (dialog.ShowDialog() != true)
        {
            Status = "已取消添加：通用模式需要先指定 Mod 目录。";
            return null;
        }

        var result = SteamGameDefinitions.ResolveChoice(
            game.InstallDirectory,
            documents,
            dialog.FolderName,
            _host.PathSafety.IsProtectedLocation);
        if (!result.Succeeded || result.Value is null)
        {
            Status = result.Error ?? "该目录不能作为 Mod 目录。";
            return null;
        }

        return result.Value;
    }

    /// <summary>
    /// Changes the mod directory of a game that was added in generic mode.
    /// </summary>
    /// <param name="row">Registered game to re-point.</param>
    private async Task SetModRootAsync(RegisteredGameRow? row)
    {
        if (row is null)
        {
            return;
        }

        var definition = _host.Adapters.Get(row.GameId)?.Definition;
        if (definition is null || !definition.IsUserDefined)
        {
            // A game with official support gets its mod root from that definition; letting the user
            // overwrite it would silently discard the game's loader and layout rules.
            Status = row.IsGeneric
                ? "该游戏的定义已不可用。"
                : "该游戏使用官方定义，Mod 目录由其决定，不能单独修改。";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"选择 {row.Name} 的 Mod 目录",
            InitialDirectory = Directory.Exists(row.ModRootPath) ? row.ModRootPath : row.InstallPath,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var result = SteamGameDefinitions.ResolveChoice(
            row.InstallPath,
            documents,
            dialog.FolderName,
            _host.PathSafety.IsProtectedLocation);
        if (!result.Succeeded || result.Value is null)
        {
            Status = result.Error ?? "该目录不能作为 Mod 目录。";
            return;
        }

        var choice = result.Value;
        var updated = definition with
        {
            ModRoot = choice.RelativePath,
            ModRootInUserProfile = choice.InUserProfile,
        };

        try
        {
            _host.Adapters.SaveUserDefinition(updated, _host.Paths.Games);

            var adapter = _host.Adapters.Get(row.GameId);
            if (adapter is null)
            {
                Status = "更新后未能重新注册该游戏定义。";
                return;
            }

            var modRoot = adapter.ResolveModRoot(row.InstallPath);

            // The previous root's approval is revoked before the new one is granted, so a directory the user
            // moved away from stops being writable.
            _host.PathSafety.RevokeGameRoot(row.GameId);
            _host.PathSafety.ApproveGameRoot(row.GameId, row.InstallPath, modRoot);

            var instance = _host.Library.AllGames().FirstOrDefault(game => game.GameId == row.GameId);
            if (instance is not null)
            {
                _host.Library.Upsert(instance with
                {
                    ModRootPath = _host.PathSafety.Canonicalize(modRoot) ?? modRoot,
                    LastVerifiedAt = DateTimeOffset.UtcNow,
                });
            }

            Status = $"已更新 {row.Name} 的 Mod 目录：{choice.Description}";
            _host.Logger.Info("Games", $"已更新 “{row.GameId}” 的 Mod 目录为 {modRoot}。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "无法更新 Mod 目录：" + ex.Message;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Adds a Steam game the user typed an app id for.</summary>
    private async Task AddByAppIdAsync()
    {
        var text = AppIdInput.Trim();
        if (!int.TryParse(text, out var appId) || appId <= 0)
        {
            Status = "请输入有效的 Steam AppID（纯数字）。";
            return;
        }

        Status = $"正在查找 AppID {appId}…";

        var game = await Task.Run(() => new SteamLibraryScanner().FindByAppId(appId)).ConfigureAwait(true);
        if (game is null)
        {
            Status = $"未在本机 Steam 库中找到 AppID {appId}。请确认该游戏已安装，或改用“按目录添加”。";
            return;
        }

        var resolved = new GameSupportResolver().Resolve(game, _host.Adapters);
        var registered = _host.Library.AllGames().ToList();
        var row = new SteamGameRow(
            resolved,
            registered.FirstOrDefault(instance => instance.SteamAppId == game.AppId),
            registered);

        AppIdInput = string.Empty;
        await AddDetectedAsync(row).ConfigureAwait(true);
    }

    /// <summary>Opens the install directory of a detected game.</summary>
    /// <param name="row">Detected game.</param>
    private Task OpenDetectedFolderAsync(SteamGameRow? row)
    {
        var path = row?.Detected.InstallDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Status = "该目录已不存在。";
            return Task.CompletedTask;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Status = "无法打开目录：" + ex.Message;
        }

        return Task.CompletedTask;
    }

    /// <summary>Registers a detected game.</summary>
    /// <param name="row">Detection result to register.</param>
    private async Task AddAsync(DetectedGameRow row)
    {
        var adapter = _host.Adapters.Get(row.Definition.Id);
        if (adapter is null)
        {
            Status = "该游戏定义已不可用。";
            return;
        }

        AddGame(adapter, row.InstallPath, addedManually: false);
        ReloadRegistered();
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Adds a game from a hand-picked directory.</summary>
    private async Task AddManualAsync()
    {
        if (_manualDefinition is null)
        {
            Status = "请先选择要添加的游戏。";
            return;
        }

        var adapter = _host.Adapters.Get(_manualDefinition.Id);
        if (adapter is null)
        {
            Status = "该游戏定义已不可用。";
            return;
        }

        var detector = new GameDetector();
        var validation = detector.ValidateManualPath(adapter.Definition, _manualPath);

        if (!validation.Found)
        {
            // The declared required files are what make a directory the right game; refusing here is far
            // better than deploying into the wrong folder and discovering it later.
            Status = "该目录不像是 " + adapter.Definition.Name + "。" + string.Join(" ", validation.Evidence);
            return;
        }

        AddGame(adapter, validation.InstallPath!, addedManually: true);
        ManualPath = string.Empty;
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Validates, approves and registers a game.</summary>
    /// <param name="adapter">Adapter to register.</param>
    /// <param name="installPath">Install directory.</param>
    /// <param name="addedManually">Whether the user picked the directory by hand.</param>
    /// <param name="detected">Steam detection this came from, when it came from one.</param>
    /// <param name="support">Support level recorded with the game.</param>
    private void AddGame(
        IGameAdapter adapter,
        string installPath,
        bool addedManually,
        InstalledSteamGame? detected = null,
        GameSupportKind support = GameSupportKind.Supported)
    {
        try
        {
            var modRoot = adapter.ResolveModRoot(installPath);

            // Approving the root is what authorises every later write for this game. Doing it here, once,
            // is why no other code has to reason about whether a path is allowed.
            _host.PathSafety.ApproveGameRoot(adapter.GameId, installPath, modRoot);

            var loader = adapter.CheckLoader(installPath);
            var resolvedModRoot = _host.PathSafety.Canonicalize(modRoot) ?? modRoot;

            _host.Library.Upsert(new GameInstance
            {
                GameId = adapter.GameId,
                Name = adapter.Definition.Name,
                InstallPath = _host.PathSafety.Canonicalize(installPath) ?? installPath,
                ModRootPath = resolvedModRoot,
                DetectedBy = detected is not null ? InstallProbeKind.Steam : addedManually ? InstallProbeKind.PathHint : InstallProbeKind.Steam,
                AddedManually = addedManually,
                LastVerifiedAt = DateTimeOffset.UtcNow,
                LoaderInstalled = loader.Installed,

                // Steam provenance, so a later scan recognises this install instead of offering it again.
                SteamAppId = detected?.AppId,
                SupportKind = support,
                LastDetectedAt = detected is null ? null : DateTimeOffset.UtcNow,
                InstallMissing = false,
            });

            // Every game gets a default profile, so the Mods page has something to enable mods into.
            _host.Install.EnsureProfile(adapter.GameId);

            Status = $"已添加 {adapter.Definition.Name}。Mod 将部署到 {resolvedModRoot}。";
            _host.Logger.Info("Games", $"已添加游戏 “{adapter.GameId}”，路径 {installPath}。");

            _shell?.ReloadGames();
        }
        catch (InvalidOperationException ex)
        {
            // Raised when the directory is a protected location, which is a refusal we want to explain
            // rather than swallow.
            Status = ex.Message;
        }
    }

    /// <summary>Forgets a game registration.</summary>
    /// <param name="row">Game to remove.</param>
    private async Task RemoveAsync(RegisteredGameRow row)
    {
        // Only the registration is removed. The staged mods, manifests and backups stay on disk, because
        // silently deleting a user's library when they tidy a list would be indefensible.
        _host.Library.RemoveGame(row.GameId);
        _host.PathSafety.RevokeGameRoot(row.GameId);

        Status = $"已从列表移除 {row.Name}。其暂存 Mod 与备份均已保留。";
        _host.Logger.Info("Games", $"已移除游戏 “{row.GameId}”。");

        _shell?.ReloadGames();
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Launches a game, honouring a user-configured launch setup first.</summary>
    /// <param name="row">Game to launch.</param>
    private Task LaunchAsync(RegisteredGameRow row)
    {
        var instance = _host.Library.AllGames().FirstOrDefault(game => game.GameId == row.GameId);

        // A user-configured executable takes precedence over the definition's launch information.
        if (instance?.LaunchExecutable is { Length: > 0 } configured)
        {
            var exePath = Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(instance.InstallPath, configured);

            if (!File.Exists(exePath))
            {
                Status = "配置的启动程序不存在：" + exePath;
                return Task.CompletedTask;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = instance.LaunchArguments ?? string.Empty,
                    WorkingDirectory = string.IsNullOrWhiteSpace(instance.LaunchWorkingDirectory)
                        ? instance.InstallPath
                        : instance.LaunchWorkingDirectory,
                    UseShellExecute = false,
                });

                Status = $"正在启动 {row.Name}…";
                _host.Logger.Info("Games", $"已通过配置的 EXE 启动 “{row.GameId}”：{exePath}");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                Status = "无法启动游戏：" + ex.Message;
            }

            return Task.CompletedTask;
        }

        var adapter = _host.Adapters.Get(row.GameId);
        if (adapter is null)
        {
            Status = "该游戏定义已不可用。";
            return Task.CompletedTask;
        }

        var result = adapter.Launch(row.InstallPath);
        Status = result.Succeeded
            ? $"正在启动 {row.Name}…"
            : result.Error ?? "无法启动游戏。";

        return Task.CompletedTask;
    }

    /// <summary>Opens the launch-settings editor and persists the result.</summary>
    /// <param name="row">Game to edit.</param>
    private void EditLaunchSettings(RegisteredGameRow row)
    {
        if (row is null)
        {
            return;
        }

        var instance = _host.Library.AllGames().FirstOrDefault(game => game.GameId == row.GameId);
        if (instance is null)
        {
            Status = "该游戏不在列表中了。";
            return;
        }

        var dialog = new Views.LaunchSettingsWindow(
            row.Name,
            instance.LaunchExecutable,
            instance.LaunchArguments,
            instance.LaunchWorkingDirectory)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _host.Library.Upsert(instance with
        {
            LaunchExecutable = dialog.Executable,
            LaunchArguments = dialog.Arguments,
            LaunchWorkingDirectory = dialog.WorkingDirectory,
        });

        Status = $"已保存 {row.Name} 的启动设置。";
        _host.Logger.Info("Games", $"已更新 “{row.GameId}” 的启动设置。");
        ReloadRegistered();
    }

    /// <summary>Re-points a registered game at a different install directory.</summary>
    /// <param name="row">Game to edit.</param>
    private Task EditPathAsync(RegisteredGameRow row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        var adapter = _host.Adapters.Get(row.GameId);
        if (adapter is null)
        {
            Status = "该游戏定义已不可用。";
            return Task.CompletedTask;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"选择 {row.Name} 的安装目录",
            InitialDirectory = Directory.Exists(row.InstallPath) ? row.InstallPath : string.Empty,
        };

        if (dialog.ShowDialog() != true)
        {
            return Task.CompletedTask;
        }

        var validation = new GameDetector().ValidateManualPath(adapter.Definition, dialog.FolderName);
        if (!validation.Found)
        {
            Status = $"该目录不像是 {adapter.Definition.Name}。" + string.Join(" ", validation.Evidence);
            return Task.CompletedTask;
        }

        try
        {
            var installPath = _host.PathSafety.Canonicalize(validation.InstallPath!) ?? validation.InstallPath!;
            var modRoot = _host.PathSafety.Canonicalize(adapter.ResolveModRoot(installPath)) ?? adapter.ResolveModRoot(installPath);

            // The old root's approval is revoked before the new one is granted, so a stale path never
            // stays writable after the game has moved.
            _host.PathSafety.RevokeGameRoot(row.GameId);
            _host.PathSafety.ApproveGameRoot(adapter.GameId, installPath, modRoot);

            var instance = _host.Library.AllGames().First(game => game.GameId == row.GameId);
            _host.Library.Upsert(instance with
            {
                InstallPath = installPath,
                ModRootPath = modRoot,
                LastVerifiedAt = DateTimeOffset.UtcNow,
            });

            Status = $"已更新 {row.Name} 的安装目录。Mod 将部署到 {modRoot}。";
            _host.Logger.Info("Games", $"已将 “{row.GameId}” 的安装路径更新为 {installPath}。");

            _shell?.ReloadGames();
            ReloadRegistered();
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }

        return Task.CompletedTask;
    }

    /// <summary>Makes a game the default the app opens on.</summary>
    /// <param name="row">Game to make default.</param>
    private Task SetDefaultAsync(RegisteredGameRow row)
    {
        var choice = _shell?.Games.FirstOrDefault(candidate =>
            string.Equals(candidate.Instance.GameId, row.GameId, StringComparison.OrdinalIgnoreCase));

        if (choice is null)
        {
            Status = "该游戏不在列表中了。";
            return Task.CompletedTask;
        }

        _shell!.SelectGame(choice);
        ReloadRegistered();
        Status = $"已将 {row.Name} 设为默认游戏。";
        _host.Logger.Info("Games", $"已将 “{row.GameId}” 设为默认游戏。");
        return Task.CompletedTask;
    }

    /// <summary>Opens a game's install directory in Explorer.</summary>
    /// <param name="row">Game whose folder to open.</param>
    private void OpenGameFolder(RegisteredGameRow row)
    {
        if (row is null || !Directory.Exists(row.InstallPath))
        {
            Status = "该目录已不存在。";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = row.InstallPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Status = "无法打开目录：" + ex.Message;
        }
    }
}

/// <summary>A detected but unregistered game.</summary>
/// <param name="Definition">Matching definition.</param>
/// <param name="InstallPath">Where it was found.</param>
/// <param name="Method">Which probe found it.</param>
/// <param name="Evidence">Why we believe it.</param>
/// <param name="Confidence">Detection confidence.</param>
public sealed record DetectedGameRow(
    GameDefinition Definition,
    string InstallPath,
    string Method,
    IReadOnlyList<string> Evidence,
    double Confidence)
{
    /// <summary>Display name.</summary>
    public string Name => Definition.Name;

    /// <summary>Evidence joined for display.</summary>
    public string EvidenceText => string.Join(" ", Evidence);

    /// <summary>Confidence as a percentage.</summary>
    public string ConfidenceText => $"{Confidence * 100:0}% 匹配";

    /// <summary>True when the definition declares a mod root worth showing.</summary>
    public bool HasModRoot => !string.IsNullOrWhiteSpace(Definition.ModRoot);
}

/// <summary>A registered game.</summary>
/// <param name="GameId">Definition id.</param>
/// <param name="Name">Display name.</param>
/// <param name="InstallPath">Install directory.</param>
/// <param name="ModRootPath">Mod root.</param>
/// <param name="AddedManually">Whether the user picked the directory.</param>
/// <param name="Loader">Loader status.</param>
/// <param name="IsDefault">Whether this is the app's default game.</param>
/// <param name="ModCount">Staged mods for this game.</param>
/// <param name="EnabledModCount">Mods enabled in the active profile.</param>
/// <param name="SupportKind">Support level the game was added with.</param>
/// <param name="SteamAppId">Steam app id, when it came from Steam.</param>
/// <param name="SupportedDefinitionId">Dedicated definition now covering this game, when one exists.</param>
/// <param name="NexusDomain">Nexus domain of the game's definition, used to fetch its artwork.</param>
public sealed record RegisteredGameRow(
    string GameId,
    string Name,
    string InstallPath,
    string ModRootPath,
    bool AddedManually,
    LoaderStatus Loader,
    bool IsDefault,
    int ModCount,
    int EnabledModCount,
    GameSupportKind SupportKind = GameSupportKind.Supported,
    int? SteamAppId = null,
    string? SupportedDefinitionId = null,
    string? NexusDomain = null) : System.ComponentModel.INotifyPropertyChanged
{
    private ImageSource? _coverImage;

    /// <inheritdoc />
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The game's artwork, loaded from the local image cache once the list is visible.
    /// </summary>
    /// <remarks>
    /// Null means "no artwork yet"; the row then keeps a plain muted tile rather than falling back to a
    /// letter, which is the same rule the Nexus browser applies to a mod with no cover.
    /// </remarks>
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

    /// <summary>Where the cover came from, or why there is none. Surfaced as a tooltip on the tile.</summary>
    public string ArtworkSource { get; set; } = "尚未解析";

    /// <summary>Loader badge text.</summary>
    public string LoaderText => Loader.Required
        ? (Loader.Installed ? "Loader 已安装" : "缺少 Loader")
        : "无需 Loader";

    /// <summary>Short mod-count summary.</summary>
    public string ModSummary => $"{EnabledModCount} / {ModCount} 个 Mod 已启用";

    /// <summary>Colour for the loader badge.</summary>
    public Brush LoaderTone => Loader.Required && !Loader.Installed
        ? Palette.Warning()
        : Palette.Success();

    /// <summary>Fill for the loader badge.</summary>
    public Brush LoaderFill => Loader.Required && !Loader.Installed
        ? Palette.WarningFill()
        : Palette.SuccessFill();

    /// <summary>How the game was added.</summary>
    public string OriginText => AddedManually ? "按目录添加" : "自动检测";

    /// <summary>True when the game is managed through the generic adapter.</summary>
    public bool IsGeneric => SupportKind == GameSupportKind.Generic;

    /// <summary>Support badge text.</summary>
    public string SupportText => SupportKind switch
    {
        GameSupportKind.Generic => SupportedDefinitionId is { Length: > 0 } ? "通用模式（已有官方支持）" : "通用模式",
        GameSupportKind.Invalid => "异常",
        _ => "已支持",
    };

    /// <summary>Support badge colour.</summary>
    public Brush SupportTone => SupportKind switch
    {
        GameSupportKind.Generic => Palette.Info(),
        GameSupportKind.Invalid => Palette.Danger(),
        _ => Palette.Success(),
    };

    /// <summary>Support badge fill.</summary>
    public Brush SupportFill => SupportKind switch
    {
        GameSupportKind.Generic => Palette.InfoFill(),
        GameSupportKind.Invalid => Palette.DangerFill(),
        _ => Palette.SuccessFill(),
    };

    /// <summary>Steam app id as text, when known.</summary>
    public string AppIdText => SteamAppId is { } appId ? $"AppID {appId}" : string.Empty;

    /// <summary>Whether the install directory still exists.</summary>
    public bool InstallExists => Directory.Exists(InstallPath);

    /// <summary>Explanation of a missing install directory.</summary>
    public string MissingText => InstallExists ? string.Empty : "安装目录已不存在，可能已被移动或卸载。";
}

/// <summary>
/// One Steam game found on disk, with what the application can do about it.
/// </summary>
/// <param name="Resolved">Detection plus support level.</param>
/// <param name="Existing">Library record for this game, when it is already managed.</param>
/// <param name="AllRegistered">Every registered game, used to detect duplicates by app id.</param>
/// <param name="ManagedModCount">Mods already staged for this game, when it is managed.</param>
public sealed record SteamGameRow(
    ResolvedGame Resolved,
    GameInstance? Existing,
    IReadOnlyList<GameInstance> AllRegistered,
    int ManagedModCount = 0) : System.ComponentModel.INotifyPropertyChanged
{
    private ImageSource? _coverImage;

    /// <inheritdoc />
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The game's artwork, loaded from the local image cache once the list is visible.
    /// </summary>
    /// <remarks>
    /// Null means "no artwork yet"; the row then shows a plain muted tile rather than a letter.
    /// </remarks>
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

    /// <summary>Where the cover came from, or why there is none.</summary>
    public string ArtworkSource { get; set; } = "尚未解析";

    /// <summary>Nexus domain for this game, when its definition declares one.</summary>
    public string? NexusDomain => Resolved.Definition?.NexusDomain;
    /// <summary>Detected game.</summary>
    public InstalledSteamGame Detected => Resolved.Detected;

    /// <summary>Display name.</summary>
    public string Name => Detected.Name;

    /// <summary>Steam app id.</summary>
    public int AppId => Detected.AppId;

    /// <summary>App id as displayed.</summary>
    public string AppIdText => Detected.AppId > 0 ? $"AppID {Detected.AppId}" : "AppID 未知";

    /// <summary>Install directory.</summary>
    public string InstallPath => Detected.InstallDirectory;

    /// <summary>True when the install directory exists.</summary>
    public bool IsValid => Detected.IsValid;

    /// <summary>Problem text when the entry is not usable.</summary>
    public string ProblemText => Detected.Problem ?? string.Empty;

    /// <summary>True when a dedicated definition exists.</summary>
    public bool IsSupported => Resolved.IsSupported;

    /// <summary>True when the game would use the generic adapter.</summary>
    public bool IsGeneric => Resolved.IsGeneric;

    /// <summary>True when the entry cannot be used at all.</summary>
    public bool IsInvalid => Resolved.Support == GameSupportKind.Invalid;

    /// <summary>Support badge text.</summary>
    public string SupportText => Resolved.Support switch
    {
        GameSupportKind.Supported => "已支持",
        GameSupportKind.Generic => "通用模式",
        _ => "异常",
    };

    /// <summary>Support badge colour.</summary>
    public Brush SupportTone => Resolved.Support switch
    {
        GameSupportKind.Supported => Palette.Success(),
        GameSupportKind.Generic => Palette.Info(),
        _ => Palette.Danger(),
    };

    /// <summary>Support badge fill.</summary>
    public Brush SupportFill => Resolved.Support switch
    {
        GameSupportKind.Supported => Palette.SuccessFill(),
        GameSupportKind.Generic => Palette.InfoFill(),
        _ => Palette.DangerFill(),
    };

    /// <summary>True when the game is already managed.</summary>
    public bool IsManaged => Existing is not null
        || AllRegistered.Any(instance => instance.SteamAppId == Detected.AppId);

    /// <summary>Management badge text.</summary>
    public string ManagedText => IsManaged ? "已添加到 ModManager" : "未添加";

    /// <summary>Installed mod count for a managed game, otherwise zero.</summary>
    public int ModCount => ManagedModCount;

    /// <summary>Mod root of the managed record, when there is one.</summary>
    public string ModRootText => Existing?.ModRootPath ?? string.Empty;

    /// <summary>
    /// True when the row offers to add the game.
    /// </summary>
    /// <remarks>
    /// An invalid install cannot be added, and an already-managed game is not offered again — but a generic
    /// game keeps its "set mod directory" action available through the registered list.
    /// </remarks>
    public bool CanAdd => IsValid && !IsManaged;

    /// <summary>Mod root already chosen for this game, when it was added before.</summary>
    public string? ExistingModRootRelative => Resolved.Definition is { IsUserDefined: true } definition
        ? definition.ModRoot
        : null;

    /// <summary>Mod root choice made in this session, so a second click does not ask again.</summary>
    public ModRootChoice? ModRootChoice { get; init; }

    /// <summary>
    /// Nexus domain of the dedicated definition, when it has one.
    /// </summary>
    /// <remarks>
    /// Empty rather than "Nexus: none" when there is no mapping. A generic-mode game has no domain by
    /// definition, so printing the absence on every such row was pure noise — it told the user nothing they
    /// could act on and competed with the facts that matter on that line.
    /// </remarks>
    public string NexusText => Resolved.Definition?.NexusDomain is { Length: > 0 } domain
        ? $"Nexus：{domain}"
        : string.Empty;

    /// <summary>First character of the name, for the icon tile.</summary>
    public string IconText => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
}
