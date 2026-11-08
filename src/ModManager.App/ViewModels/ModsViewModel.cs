using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ModManager.Core;

namespace ModManager.App.ViewModels;

/// <summary>
/// Mods: install, enable, disable, uninstall, and review the installation preview.
/// </summary>
/// <remarks>
/// <para>
/// The user's spec §16 asks for cover, name, author, version, source, state, dependencies and conflicts per
/// mod, with install / enable / disable / update / uninstall actions, and an installation preview that shows
/// each source-to-target mapping with a safe / conflict / unsafe badge.
/// </para>
/// <para>
/// The preview is the interesting part. Because the engine's planner is pure, the preview shown here is
/// produced by <i>the same</i> planner that will perform the install — so it cannot drift from what actually
/// happens. Every design decision that made the planner pure pays off at exactly this point.
/// </para>
/// </remarks>
public sealed class ModsViewModel : ViewModelBase, IPageViewModel
{
    private readonly ApplicationHost _host;
    private readonly ShellViewModel? _shell;
    private ModRow? _selectedMod;
    private string _status = "选择一个游戏查看其 Mod。";
    private bool _isBusy;
    private InstallPreview? _preview;
    private string _searchText = string.Empty;
    private string _stateFilter = "全部";
    private string _sortKey = "按名称";
    private StagedMod? _pendingStaged;
    private IGameAdapter? _pendingAdapter;
    private GameChoice? _pendingGame;
    private IReadOnlyList<string> _rootOptions = [];
    private string _selectedRootOption = string.Empty;

    /// <summary>Creates the page.</summary>
    /// <param name="host">Composition root.</param>
    /// <param name="shell">Shell, when available, so the game list can be refreshed.</param>
    public ModsViewModel(ApplicationHost host, ShellViewModel? shell = null)
    {
        _host = host;
        _shell = shell;

        InstallFromArchiveCommand = new RelayCommand(() => InstallFromArchiveAsync(), () => CanOperate);
        EnableCommand = new RelayCommand<ModRow>(row => SetEnabledAsync(row, true), row => CanOperate && row is not null);
        DisableCommand = new RelayCommand<ModRow>(row => SetEnabledAsync(row, false), row => CanOperate && row is not null);
        UninstallCommand = new RelayCommand<ModRow>(row => UninstallAsync(row), row => CanOperate && row is not null);
        DeleteLibraryCommand = new RelayCommand<ModRow>(row => DeleteFromLibraryAsync(row), row => CanOperate && row is not null && row.Manifest is null);
        ConfirmInstallCommand = new RelayCommand(() => ConfirmInstallAsync(), () => _preview is not null);
        CancelInstallCommand = new RelayCommand(() => CancelInstall());
        ReanalyzeCommand = new RelayCommand(() => ReanalyzeWithRootAsync(), () => _preview is not null && RootOptions.Count > 1);
        NavigateToConflictsCommand = new RelayCommand(() =>
        {
            _shell?.NavigateTo("冲突");
            return Task.CompletedTask;
        });
        ReinstallCommand = new RelayCommand<ModRow>(row => ReinstallAsync(row), row => CanOperate && row is not null && row.Manifest is not null);
        PreviewBatchRowCommand = new RelayCommand<BatchImportRow>(row => PreviewBatchAsync(row), row => row is not null && row.Staged is not null && !_isBusy);
        RemoveBatchRowCommand = new RelayCommand<BatchImportRow>(row => RemoveBatchAsync(row));
        // A block body, not an expression body: `OpenFolder` returns void, and between the Action and
        // Func<Task> overloads an expression-bodied lambda gives the compiler no way to prefer one.
        OpenModFolderCommand = new RelayCommand<ModRow>(row =>
        {
            OpenFolder(row);
            return Task.CompletedTask;
        });
    }

    /// <summary>Mods for the active game.</summary>
    public BulkObservableCollection<ModRow> Mods { get; } = [];

    /// <summary>Mods after the search/filter pass; this is what the list renders.</summary>
    public BulkObservableCollection<ModRow> FilteredMods { get; } = [];

    /// <summary>Installs an archive the user picks.</summary>
    public RelayCommand InstallFromArchiveCommand { get; }

    /// <summary>Enables a mod.</summary>
    public RelayCommand<ModRow> EnableCommand { get; }

    /// <summary>Disables a mod.</summary>
    public RelayCommand<ModRow> DisableCommand { get; }

    /// <summary>Uninstalls a mod, honouring the modification-protection rule.</summary>
    public RelayCommand<ModRow> UninstallCommand { get; }

    /// <summary>Deletes a staged (never deployed) mod from the library entirely.</summary>
    public RelayCommand<ModRow> DeleteLibraryCommand { get; }

    /// <summary>Search text; filters by name, author and version.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilters();
            }
        }
    }

    /// <summary>State filter selection.</summary>
    public string StateFilter
    {
        get => _stateFilter;
        set
        {
            if (SetProperty(ref _stateFilter, value))
            {
                ApplyFilters();
            }
        }
    }

    /// <summary>Sort key selection.</summary>
    public string SortKey
    {
        get => _sortKey;
        set
        {
            if (SetProperty(ref _sortKey, value))
            {
                ApplyFilters();
            }
        }
    }

    /// <summary>Applies the reviewed installation.</summary>
    public RelayCommand ConfirmInstallCommand { get; }

    /// <summary>Discards the pending installation.</summary>
    public RelayCommand CancelInstallCommand { get; }

    /// <summary>Re-analyses the pending archive with the user-chosen mod root.</summary>
    public RelayCommand ReanalyzeCommand { get; }

    /// <summary>Opens the Conflicts page.</summary>
    public RelayCommand NavigateToConflictsCommand { get; }

    /// <summary>Re-runs the deployment for an installed mod (reinstall).</summary>
    public RelayCommand<ModRow> ReinstallCommand { get; }

    /// <summary>Shows the preview for one batch-imported archive.</summary>
    public RelayCommand<BatchImportRow> PreviewBatchRowCommand { get; }

    /// <summary>Discards one batch-imported archive.</summary>
    public RelayCommand<BatchImportRow> RemoveBatchRowCommand { get; }

    /// <summary>Results of the most recent batch import.</summary>
    public BulkObservableCollection<BatchImportRow> BatchResults { get; } = [];

    /// <summary>True when the batch results panel is the right pane's content.</summary>
    public bool ShowBatchPanel => BatchResults.Count > 0 && _preview is null;

    /// <summary>Selectable mod-root candidates for the pending archive.</summary>
    public IReadOnlyList<string> RootOptions
    {
        get => _rootOptions;
        private set
        {
            if (SetProperty(ref _rootOptions, value))
            {
                OnPropertyChanged(nameof(HasRootOptions));
                ReanalyzeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when more than one root is selectable, i.e. the choice is meaningful.</summary>
    public bool HasRootOptions => _rootOptions.Count > 1;

    /// <summary>The currently selected root option (empty = archive root).</summary>
    public string SelectedRootOption
    {
        get => _selectedRootOption;
        set => SetProperty(ref _selectedRootOption, value);
    }

    /// <summary>Opens a mod's staging directory in Explorer.</summary>
    public RelayCommand<ModRow> OpenModFolderCommand { get; }

    /// <summary>Selected mod, driving the detail pane.</summary>
    public ModRow? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetProperty(ref _selectedMod, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    /// <summary>True when a mod is selected.</summary>
    public bool HasSelection => _selectedMod is not null;

    /// <summary>Status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>True while a long operation runs.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanOperate));
                InstallFromArchiveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when an operation can start.</summary>
    public bool CanOperate => !_isBusy && _shell?.ActiveGame is not null;

    /// <summary>Pending installation preview, or null.</summary>
    public InstallPreview? Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(PreviewEntries));
                OnPropertyChanged(nameof(PreviewConflicts));
                OnPropertyChanged(nameof(PreviewRootSummary));
                OnPropertyChanged(nameof(PreviewSignals));
                OnPropertyChanged(nameof(PreviewHasRejections));
                OnPropertyChanged(nameof(PreviewRejections));
                OnPropertyChanged(nameof(PreviewModName));
                OnPropertyChanged(nameof(PreviewVersionText));
                OnPropertyChanged(nameof(PreviewVersionLine));
                OnPropertyChanged(nameof(PreviewTargetText));
                OnPropertyChanged(nameof(PreviewSizeText));
                OnPropertyChanged(nameof(PreviewFileCountText));
                OnPropertyChanged(nameof(PreviewConflictCountText));
                OnPropertyChanged(nameof(PreviewFileCountLine));
                OnPropertyChanged(nameof(PreviewSizeLine));
                OnPropertyChanged(nameof(PreviewConflictCountLine));
                OnPropertyChanged(nameof(PreviewHasConflicts));
                OnPropertyChanged(nameof(PreviewNeedsRootText));
                OnPropertyChanged(nameof(PreviewMissingDepsText));
                OnPropertyChanged(nameof(PreviewHasMissingDeps));
                ConfirmInstallCommand.RaiseCanExecuteChanged();
                ReanalyzeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>True when a preview is pending confirmation.</summary>
    public bool HasPreview => _preview is not null;

    /// <summary>Per-file preview rows.</summary>
    public IReadOnlyList<PreviewEntryRow> PreviewEntries =>
        _preview is null
            ? []
            : _preview.Entries
                .Select(entry => new PreviewEntryRow(entry))
                .OrderByDescending(row => row.SeverityRank)
                .ThenBy(row => row.TargetPath, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .ToList();

    /// <summary>Conflicts the pending install would create.</summary>
    public IReadOnlyList<ConflictRow> PreviewConflicts =>
        _preview is null
            ? []
            : _preview.Conflicts
                .Select(conflict => new ConflictRow(conflict))
                .ToList();

    /// <summary>Whether any path was refused outright.</summary>
    public bool PreviewHasRejections => _preview?.HasUnsafePaths ?? false;

    /// <summary>Rejected path explanations.</summary>
    public IReadOnlyList<string> PreviewRejections => _preview?.Rejections ?? [];

    /// <summary>Human-readable summary of the detected mod root and the confidence behind it.</summary>
    public string PreviewRootSummary
    {
        get
        {
            if (_preview is null)
            {
                return string.Empty;
            }

            var root = _preview.DetectedRoot.Length == 0 ? "(archive root)" : _preview.DetectedRoot;
            return $"Mod root: {root}   Confidence: {_preview.Confidence * 100:0}%";
        }
    }

    /// <summary>Display name of the mod being installed.</summary>
    public string PreviewModName => _preview?.ModName ?? string.Empty;

    /// <summary>Version of the mod being installed, or a dash.</summary>
    public string PreviewVersionText
    {
        get
        {
            if (_preview is null)
            {
                return string.Empty;
            }

            var version = _host.Library.Mod(_preview.ModId)?.Version;
            return string.IsNullOrWhiteSpace(version) ? "-" : version;
        }
    }

    /// <summary>Version line for the preview header.</summary>
    public string PreviewVersionLine => "版本 " + PreviewVersionText;

    /// <summary>File-count line for the preview summary.</summary>
    public string PreviewFileCountLine => "文件 " + PreviewFileCountText;

    /// <summary>Size line for the preview summary.</summary>
    public string PreviewSizeLine => "大小 " + PreviewSizeText;

    /// <summary>Conflict-count line for the preview summary.</summary>
    public string PreviewConflictCountLine => "冲突 " + PreviewConflictCountText;

    /// <summary>Absolute deployment root the files will land in.</summary>
    public string PreviewTargetText => _preview?.DeploymentRoot ?? string.Empty;

    /// <summary>Total bytes in human units.</summary>
    public string PreviewSizeText => _preview is null
        ? string.Empty
        : ModManager.Core.Safety.FileSystemOps.FormatBytes(_preview.TotalBytes);

    /// <summary>File count.</summary>
    public string PreviewFileCountText => _preview is null ? string.Empty : $"{_preview.Entries.Count}";

    /// <summary>Conflict count for the pending install.</summary>
    public string PreviewConflictCountText => _preview is null ? string.Empty : $"{_preview.Conflicts.Count}";

    /// <summary>True when the pending install would create conflicts.</summary>
    public bool PreviewHasConflicts => _preview is not null && _preview.Conflicts.Count > 0;

    /// <summary>Shown when the root must be chosen by hand.</summary>
    public string PreviewNeedsRootText => _preview is { NeedsConfirmation: true }
        ? "需要选择 Mod 安装目录"
        : string.Empty;

    /// <summary>Missing dependencies of the pending install, joined for display.</summary>
    public string PreviewMissingDepsText
    {
        get
        {
            if (_preview is null || _preview.MissingDependencies.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("、", _preview.MissingDependencies.Select(dep => dep.Requirement));
        }
    }

    /// <summary>True when the pending install has missing dependencies to show.</summary>
    public bool PreviewHasMissingDeps => _preview is not null && _preview.MissingDependencies.Count > 0;

    /// <summary>Why the analyzer chose that root.</summary>
    public IReadOnlyList<string> PreviewSignals =>
        _preview is null
            ? []
            : _preview.Signals
                .Where(signal => signal.Weight > 0)
                .Select(signal => signal.Reason)
                .ToList();

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(CanOperate));
        InstallFromArchiveCommand.RaiseCanExecuteChanged();

        var game = _shell?.ActiveGame;
        if (game is null)
        {
            Mods.ReplaceAll([]);
            ApplyFilters();
            Status = "未选择游戏。请先在游戏页添加。";
            await Task.CompletedTask.ConfigureAwait(true);
            return;
        }

        var profile = _host.Install.EnsureProfile(game.Instance.GameId);
        var rows = new List<ModRow>();

        foreach (var mod in _host.Library.ModsForGame(game.Instance.GameId))
        {
            var manifest = _host.Library.GetManifest(game.Instance.GameId, mod.Id);
            var enabled = profile.ModOrder.Contains(mod.Id, StringComparer.Ordinal)
                && !profile.DisabledModIds.Contains(mod.Id, StringComparer.Ordinal);

            rows.Add(new ModRow(mod, manifest, enabled, game.Definition.Name));
        }

        Mods.ReplaceAll(rows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase));
        ApplyFilters();

        Status = rows.Count == 0
            ? "该游戏还没有安装 Mod。使用“从压缩包安装”添加。"
            : $"{rows.Count} 个 Mod，{rows.Count(row => row.IsEnabled)} 个已启用。";

        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>Applies the search text, state filter and sort key to the visible list.</summary>
    private void ApplyFilters()
    {
        var query = _searchText?.Trim() ?? string.Empty;
        IEnumerable<ModRow> filtered = Mods;

        if (query.Length > 0)
        {
            filtered = filtered.Where(row =>
                row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Version.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.GameName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        filtered = _stateFilter switch
        {
            "已启用" => filtered.Where(row => row.IsEnabled),
            "已禁用" => filtered.Where(row => !row.IsEnabled),
            "未部署" => filtered.Where(row => row.Manifest is null),
            "有冲突" => filtered.Where(row => row.Manifest is not null && row.Manifest.Conflicts.Count > 0),
            "有更新" => filtered.Where(row => row.HasUpdate),
            _ => filtered,
        };

        filtered = _sortKey switch
        {
            "按安装时间" => filtered.OrderByDescending(row => row.Mod.InstalledAt),
            "按大小" => filtered.OrderByDescending(row => row.Mod.StagedSizeBytes),
            "按状态" => filtered.OrderByDescending(row => row.IsEnabled).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
        };

        FilteredMods.ReplaceAll(filtered.ToList());
        OnPropertyChanged(nameof(FilterSummary));
    }

    /// <summary>One-line description of the visible set, for the filter bar.</summary>
    public string FilterSummary =>
        FilteredMods.Count == Mods.Count
            ? $"{Mods.Count} 个"
            : $"显示 {FilteredMods.Count} / {Mods.Count} 个";

    /// <summary>Picks an archive, analyses it, and shows the preview.</summary>
    private async Task InstallFromArchiveAsync()
    {
        var game = _shell?.ActiveGame;
        if (game is null)
        {
            Status = "请先添加游戏。";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 Mod 压缩包",
            Filter = "Mod archives (*.zip;*.7z;*.rar;*.tar.gz;*.tgz)|*.zip;*.7z;*.rar;*.tar.gz;*.tgz|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var adapter = _host.Adapters.Get(game.Instance.GameId);
        if (adapter is null)
        {
            Status = "该游戏定义已不可用。";
            return;
        }

        IsBusy = true;
        Status = "正在导入本地仓库并分析…";

        try
        {
            // Manual import goes through the same library seam as a download: the archive is copied
            // into Data/Library first, then staged from there. Nothing is read straight from the
            // user's Downloads folder.
            var staged = await _host.PackageInstall
                .ImportAndStageAsync(
                    dialog.FileName,
                    game.Instance.GameId,
                    acceptLowConfidence: true)
                .ConfigureAwait(true);

            if (!staged.Succeeded || staged.Value is null)
            {
                Status = staged.Error ?? "压缩包无法暂存。";
                return;
            }

            // Update detection: an installed mod with the same name but different content is offered
            // as an update. The old version is fully uninstalled (with backup restore) before the new
            // one takes its name, so the library never holds two generations of the same mod.
            var stagedValue = staged.Value;
            var sameName = _host.Library.ModsForGame(game.Instance.GameId).FirstOrDefault(mod =>
                string.Equals(mod.Name, stagedValue.Metadata.Name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mod.Id, stagedValue.Metadata.Id, StringComparison.Ordinal));

            if (sameName is not null)
            {
                var answer = MessageBox.Show(
                    $"检测到已安装的同名 Mod：{sameName.Name}（{sameName.Version ?? "未知版本"}）。\n\n"
                    + "是否将其作为更新导入？\n是：卸载旧版本并用新压缩包替换；\n否：作为新的 Mod 导入。",
                    "更新检测",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer == MessageBoxResult.Yes)
                {
                    Status = $"正在更新 {sameName.Name}：卸载旧版本…";
                    var uninstall = await _host.Install
                        .UninstallAsync(game.Instance.GameId, sameName.Id, adapter, game.Instance.InstallPath, false, _host.Logger)
                        .ConfigureAwait(true);

                    if (!uninstall.Succeeded)
                    {
                        Status = "更新失败：旧版本无法卸载。" + (uninstall.Error ?? string.Empty);
                        return;
                    }

                    // The new generation keeps the old display name, so the library reads continuously.
                    var renamed = stagedValue.Metadata with { Name = sameName.Name };
                    _host.Library.Upsert(renamed);
                    stagedValue = stagedValue with { Metadata = renamed };

                    _host.Logger.Info(
                        "Install",
                        $"Updating '{sameName.Name}': {sameName.Version ?? "?"} -> {renamed.Version ?? "?"}");
                }
            }

            _pendingStaged = stagedValue;
            _pendingAdapter = adapter;
            _pendingGame = game;

            await ShowPreviewAsync(stagedValue, adapter, game).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Status = "安装失败：" + ex.Message;
            _host.Logger.Error("Install", "暂存失败。", ex.Message);
            ShowErrorDialog("导入 Mod", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Builds and shows the installation preview for a staged mod.</summary>
    /// <param name="staged">Staged mod.</param>
    /// <param name="adapter">Game adapter.</param>
    /// <param name="game">Target game.</param>
    private async Task ShowPreviewAsync(StagedMod staged, IGameAdapter adapter, GameChoice game)
    {
        var modSet = BuildModSet(game.Instance.GameId, staged.Metadata.Id);

        var preview = await _host.Install
            .PreviewAsync(staged, adapter, game.Instance.InstallPath, modSet)
            .ConfigureAwait(true);

        if (!preview.Succeeded || preview.Value is null)
        {
            Status = preview.Error ?? "无法生成安装预览。";
            return;
        }

        Preview = preview.Value;
        RootOptions = staged.Analysis.Candidates
            .Where(candidate => candidate.FileCount > 0)
            .Select(candidate => candidate.RelativePath)
            .ToList();
        SelectedRootOption = staged.RootRelativePath;

        Status = preview.Value.NeedsConfirmation
            ? "该压缩包的目录结构不确定，请检查预览并选择正确的 Mod 根目录。"
            : "请检查安装预览并确认。";
    }

    /// <summary>Re-analyses the pending archive with the user-chosen mod root.</summary>
    private async Task ReanalyzeWithRootAsync()
    {
        if (_pendingStaged is null || _pendingAdapter is null || _pendingGame is null)
        {
            return;
        }

        IsBusy = true;
        Status = "正在按所选根目录重新分析…";

        try
        {
            // Re-stage from the package library, not from the old staging path: the package id keeps
            // the installed instance tied to the archive that produced it.
            OperationResult<StagedMod> staged;
            if (!string.IsNullOrWhiteSpace(_pendingStaged.Metadata.PackageId))
            {
                staged = await _host.PackageInstall
                    .StageAsync(
                        _pendingStaged.Metadata.PackageId,
                        _pendingGame.Instance.GameId,
                        acceptLowConfidence: true,
                        rootOverride: SelectedRootOption)
                    .ConfigureAwait(true);
            }
            else
            {
                var request = new InstallRequest
                {
                    ArchivePath = _pendingStaged.Metadata.ArchivePath!,
                    GameId = _pendingGame.Instance.GameId,
                    AcceptLowConfidence = true,
                    RootOverride = SelectedRootOption,
                };

                staged = await _host.Install
                    .StageAsync(request, _pendingAdapter, _host.Logger)
                    .ConfigureAwait(true);
            }

            if (!staged.Succeeded || staged.Value is null)
            {
                Status = staged.Error ?? "压缩包无法暂存。";
                return;
            }

            _pendingStaged = staged.Value;
            await ShowPreviewAsync(staged.Value, _pendingAdapter, _pendingGame).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            Status = "重新分析失败：" + ex.Message;
            ShowErrorDialog("重新分析 Mod", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Batch-imports dropped archives: every file is analysed independently, and a failure in one
    /// never affects the others. Each success lands in the results panel for per-mod preview.
    /// </summary>
    /// <param name="archivePaths">Archive files to import.</param>
    public async Task BatchImportAsync(IReadOnlyList<string> archivePaths)
    {
        var game = _shell?.ActiveGame;
        if (game is null)
        {
            Status = "请先添加游戏。";
            return;
        }

        var adapter = _host.Adapters.Get(game.Instance.GameId);
        if (adapter is null)
        {
            Status = "该游戏定义已不可用。";
            return;
        }

        IsBusy = true;
        var results = new List<BatchImportRow>();
        var succeeded = 0;

        for (var index = 0; index < archivePaths.Count; index++)
        {
            var path = archivePaths[index];
            Status = $"正在导入 {index + 1}/{archivePaths.Count}：{Path.GetFileName(path)}…";

            try
            {
                var staged = await _host.PackageInstall
                    .ImportAndStageAsync(
                        path,
                        game.Instance.GameId,
                        acceptLowConfidence: true)
                    .ConfigureAwait(true);

                if (staged.Succeeded && staged.Value is not null)
                {
                    succeeded++;
                    results.Add(BatchImportRow.Success(path, staged.Value));
                    _host.Logger.Info("Import", $"批导入成功：{Path.GetFileName(path)}");
                }
                else
                {
                    results.Add(BatchImportRow.Failed(path, staged.Error ?? "无法暂存。"));
                    _host.Logger.Warn("Import", $"批导入失败：{Path.GetFileName(path)}", staged.Error ?? string.Empty);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                results.Add(BatchImportRow.Failed(path, ex.Message));
                _host.Logger.Warn("Import", $"批导入失败：{Path.GetFileName(path)}", ex.Message);
            }
        }

        BatchResults.ReplaceAll(results);
        OnPropertyChanged(nameof(ShowBatchPanel));

        Status = $"导入完成：{succeeded}/{archivePaths.Count} 个成功。逐个点击“预览”确认安装。";
        await RefreshAsync().ConfigureAwait(true);

        IsBusy = false;
    }

    /// <summary>Opens the preview for one batch-imported archive.</summary>
    /// <param name="row">Batch row.</param>
    private async Task PreviewBatchAsync(BatchImportRow row)
    {
        var game = _shell?.ActiveGame;
        var adapter = game is null ? null : _host.Adapters.Get(game.Instance.GameId);

        if (row?.Staged is null || game is null || adapter is null)
        {
            return;
        }

        BatchResults.ReplaceAll([]);
        OnPropertyChanged(nameof(ShowBatchPanel));

        _pendingStaged = row.Staged;
        _pendingAdapter = adapter;
        _pendingGame = game;

        await ShowPreviewAsync(row.Staged, adapter, game).ConfigureAwait(true);
    }

    /// <summary>Discards one batch-imported archive.</summary>
    /// <param name="row">Batch row.</param>
    private Task RemoveBatchAsync(BatchImportRow? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        if (row.Staged is not null)
        {
            _host.Install.PurgeStaging(row.Staged.Metadata.Id);
            IModRepository repository = _host.Library;
            repository.Remove(row.Staged.Metadata.Id);
        }

        BatchResults.Remove(row);
        OnPropertyChanged(nameof(ShowBatchPanel));
        return Task.CompletedTask;
    }

    /// <summary>Re-runs the deployment for an installed mod, using its existing staging files.</summary>
    /// <param name="row">Mod to reinstall.</param>
    private async Task ReinstallAsync(ModRow row)
    {
        var game = _shell?.ActiveGame;
        if (game is null || row is null || row.Manifest is null)
        {
            return;
        }

        var adapter = _host.Adapters.Get(game.Instance.GameId);
        if (adapter is null)
        {
            Status = "该游戏定义已不可用。";
            return;
        }

        IsBusy = true;
        Status = $"正在重新安装 {row.Name}…";

        try
        {
            var staged = new StagedMod(
                row.Mod,
                new RootAnalysis
                {
                    SelectedRoot = row.Mod.AnalyzedRootPath ?? string.Empty,
                    Confidence = 1d,
                    Candidates = [],
                    Signals = [],
                    TotalSize = row.Mod.StagedSizeBytes,
                    FileCount = row.Mod.StagedFileCount,
                    DeployableFileCount = row.Mod.StagedFileCount,
                },
                row.StagingPath,
                row.Mod.AnalyzedRootPath ?? string.Empty);

            var result = await _host.Install
                .InstallAsync(staged, adapter, game.Instance.InstallPath, _host.Logger)
                .ConfigureAwait(true);

            Status = result.Succeeded
                ? $"已重新安装 {row.Name}（{result.DeployedFileCount} 个文件）。"
                : "重新安装失败：" + (result.Error ?? "未知错误");

            _host.Logger.Info("Install", $"已重新安装 “{row.Name}”（{row.Mod.Id}）。");

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Install", "重新安装异常。", ex.ToString());
            ShowErrorDialog("重新安装 Mod", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Applies the reviewed installation.</summary>
    private async Task ConfirmInstallAsync()
    {
        var game = _shell?.ActiveGame;
        var preview = _preview;

        if (game is null || preview is null)
        {
            return;
        }

        if (preview.HasUnsafePaths)
        {
            // A refused path means the archive tried to write outside the mod root. That is not a warning to
            // click past, so the install does not proceed.
            Status = "安装被拒绝：部分路径不安全。请查看下方被拒绝的文件。";
            return;
        }

        // Dependency validation before anything is written. A missing mod loader is a hard blocker;
        // other missing requirements are surfaced for an explicit decision, never silently ignored.
        if (preview.MissingDependencies.Count > 0)
        {
            var blockers = preview.MissingDependencies.Where(dep => dep.IsLoader).ToList();
            var others = preview.MissingDependencies.Where(dep => !dep.IsLoader).ToList();

            if (blockers.Count > 0)
            {
                MessageBox.Show(
                    "无法安装\n\n缺少必要的 Mod Loader：\n" + string.Join("\n", blockers.Select(dep => dep.Requirement)),
                    "ModManager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Status = "安装已阻止：缺少必要的 Mod Loader。";
                return;
            }

            if (others.Count > 0)
            {
                var answer = MessageBox.Show(
                    "检测到缺失依赖：\n" + string.Join("\n", others.Select(dep => dep.Requirement))
                    + "\n\n仍然继续安装？",
                    "ModManager",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes)
                {
                    Status = "已取消安装（存在缺失依赖）。";
                    return;
                }
            }
        }

        var adapter = _host.Adapters.Get(game.Instance.GameId);
        if (adapter is null)
        {
            Status = "该游戏定义已不可用。";
            return;
        }

        IsBusy = true;

        try
        {
            var mod = _host.Library.Mod(preview.ModId);
            if (mod is null)
            {
                Status = "找不到已暂存的 Mod。";
                return;
            }

            var staged = new StagedMod(mod, BuildAnalysisStub(mod, preview), mod.StagingPath, preview.DetectedRoot);

            var progress = new Progress<DeployProgress>(report =>
            {
                OnPropertyChanged(nameof(DeployFraction));
                Status = report.CurrentFile is null
                    ? "正在部署…"
                    : $"正在部署 {report.CompletedFiles}/{report.TotalFiles}：{report.CurrentFile}";
            });

            var result = await _host.Install
                .InstallAsync(staged, adapter, game.Instance.InstallPath, _host.Logger, progress)
                .ConfigureAwait(true);

            if (result.Succeeded)
            {
                var kept = result.Warnings.Count;
                Status = $"安装完成。已部署 {result.DeployedFileCount} 个文件"
                    + (result.BackupCount > 0 ? $"，已备份 {result.BackupCount} 个原文件" : string.Empty)
                    + (kept > 0 ? $"，{kept} 条备注见日志。" : "。");

                Preview = null;
                await RefreshAsync().ConfigureAwait(true);
            }
            else
            {
                Status = "安装失败：" + (result.Error ?? "未知错误");

                foreach (var warning in result.Warnings)
                {
                    _host.Logger.Warn("Install", warning);
                }

                // A failed install has already been rolled back by the engine; the dialog says so
                // explicitly instead of leaving the user guessing about the game folder's state.
                MessageBox.Show(
                    "安装失败\n\n已自动恢复安装前状态。\n\n具体原因：\n" + (result.Error ?? "未知错误") + "\n\n详细信息已写入日志。",
                    "ModManager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Install", "安装异常。", ex.ToString());
            ShowErrorDialog("安装 Mod", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Discards the pending installation, removing its staged files.</summary>
    private void CancelInstall()
    {
        if (_preview is null)
        {
            return;
        }

        // The staged files are removed as well as the pending decision: leaving an orphan staging directory
        // behind for a mod the user declined would accumulate silently.
        var purged = _host.Install.PurgeStaging(_preview.ModId);
        Status = purged.Succeeded
            ? "已取消安装；暂存文件已删除。"
            : "已取消安装，但暂存文件无法删除：" + purged.Error;

        Preview = null;
        _pendingStaged = null;
        _pendingAdapter = null;
        _pendingGame = null;
        RootOptions = [];
        SelectedRootOption = string.Empty;
    }

    /// <summary>Enables or disables a mod and redeploys.</summary>
    /// <param name="row">Mod to change.</param>
    /// <param name="enabled">Whether it should be enabled.</param>
    private async Task SetEnabledAsync(ModRow row, bool enabled)
    {
        var game = _shell?.ActiveGame;
        if (game is null || row is null)
        {
            return;
        }

        IsBusy = true;
        Status = enabled ? $"正在启用 {row.Name}…" : $"正在禁用 {row.Name}…";

        try
        {
            var profile = _host.Install.EnsureProfile(game.Instance.GameId);

            var disabled = new List<string>(profile.DisabledModIds);
            var order = new List<string>(profile.ModOrder);

            if (enabled)
            {
                disabled.RemoveAll(id => string.Equals(id, row.Mod.Id, StringComparison.Ordinal));
                if (!order.Contains(row.Mod.Id, StringComparer.Ordinal))
                {
                    // Appended, so a newly enabled mod wins conflicts by default. That matches the engine's
                    // "later mod wins" rule and is the behaviour a user expects from "enable it now".
                    order.Add(row.Mod.Id);
                }
            }
            else if (!disabled.Contains(row.Mod.Id, StringComparer.Ordinal))
            {
                disabled.Add(row.Mod.Id);
            }

            _host.Library.SaveProfile(profile with { ModOrder = order, DisabledModIds = disabled });

            var result = await RedeployAsync(game, profile.Id).ConfigureAwait(true);
            Status = result;
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Uninstalls a mod, keeping files the user changed.</summary>
    /// <param name="row">Mod to uninstall.</param>
    private async Task UninstallAsync(ModRow row)
    {
        var game = _shell?.ActiveGame;
        if (game is null || row is null)
        {
            return;
        }

        // Explicit confirmation, showing exactly what the operation is allowed to remove.
        if (_host.Settings.Settings.Ui.ConfirmUninstall)
        {
            var fileCount = row.Manifest?.Files.Count ?? 0;
            var answer = MessageBox.Show(
                $"确定卸载 {row.Name}？\n\n将移除该 Mod 管理的 {fileCount} 个文件。\n不会删除其他 Mod 的文件。",
                "卸载确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                Status = "已取消卸载。";
                return;
            }
        }

        IsBusy = true;
        Status = $"正在卸载 {row.Name}…";

        try
        {
            var adapter = _host.Adapters.Get(game.Instance.GameId);
            if (adapter is null)
            {
                Status = "该游戏定义已不可用。";
                return;
            }

            // deleteModifiedFiles is false: files whose contents changed since installation are kept and
            // reported. That is the user-modification protection the spec requires as the default.
            var result = await _host.Install
                .UninstallAsync(
                    game.Instance.GameId,
                    row.Mod.Id,
                    adapter,
                    game.Instance.InstallPath,
                    deleteModifiedFiles: false,
                    _host.Logger)
                .ConfigureAwait(true);

            if (result.Succeeded)
            {
                Status = result.Warnings.Count > 0
                    ? $"已卸载 {row.Name}。" + string.Join(" ", result.Warnings)
                    : $"已卸载 {row.Name}。";

                SelectedMod = null;
                await RefreshAsync().ConfigureAwait(true);
            }
            else
            {
                Status = "卸载失败：" + (result.Error ?? "未知错误");
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Install", "卸载异常。", ex.ToString());
            ShowErrorDialog("卸载 Mod", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Deletes a staged (never deployed) mod from the library, removing its staging files.</summary>
    /// <param name="row">Mod to delete.</param>
    /// <remarks>
    /// Only offered for mods with no manifest: a deployed mod must be uninstalled first, so this can
    /// never be a shortcut around the undeploy machinery.
    /// </remarks>
    private async Task DeleteFromLibraryAsync(ModRow row)
    {
        if (row is null || row.Manifest is not null)
        {
            return;
        }

        IsBusy = true;
        Status = $"正在从库中删除 {row.Name}…";

        try
        {
            var purged = _host.Install.PurgeStaging(row.Mod.Id);
            IModRepository repository = _host.Library;
            repository.Remove(row.Mod.Id);

            _host.Logger.Info("Library", $"已从库中删除未部署的 Mod “{row.Name}”（{row.Mod.Id}）。");
            Status = purged.Succeeded
                ? $"已从库中删除 {row.Name}。"
                : $"已从库中删除 {row.Name}，但暂存文件未能完全清除：" + purged.Error;

            SelectedMod = null;
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Rebuilds and applies the plan for the active profile.</summary>
    /// <param name="game">Active game.</param>
    /// <param name="profileId">Profile to deploy.</param>
    private async Task<string> RedeployAsync(GameChoice game, string profileId)
    {
        var adapter = _host.Adapters.Get(game.Instance.GameId);
        if (adapter is null)
        {
            return "该游戏定义已不可用。";
        }

        var profile = _host.Library.GetProfile(profileId) ?? _host.Install.EnsureProfile(game.Instance.GameId);
        var modSet = BuildModSet(game.Instance.GameId, null);

        var plan = _host.Planner.BuildPlan(
            adapter.Definition,
            game.Instance.InstallPath,
            profile,
            modSet,
            _host.Library.ConflictRulesForGame(game.Instance.GameId));

        if (plan.IsEmpty)
        {
            return "无需部署：游戏目录已与配置方案一致。";
        }

        var result = await _host.Applier
            .ApplyAsync(plan, adapter.Definition, modSet, _host.Settings.Deployment, _host.Logger)
            .ConfigureAwait(true);

        if (result.Succeeded)
        {
            return $"已部署 {result.DeployedFileCount} 个文件。"
                + (result.BackupCount > 0 ? $" 已备份 {result.BackupCount} 个原文件。" : string.Empty);
        }

        return "部署失败：" + (result.Error ?? "未知错误");
    }

    /// <summary>Builds the mod set for a game, optionally including a freshly staged mod.</summary>
    /// <param name="gameId">Game id.</param>
    /// <param name="includeStagedModId">Mod id to include even though it is already in the library.</param>
    private IReadOnlyList<ModSetEntry> BuildModSet(string gameId, string? includeStagedModId)
    {
        _ = includeStagedModId;

        var entries = new List<ModSetEntry>();
        foreach (var mod in _host.Library.ModsForGame(gameId))
        {
            entries.Add(new ModSetEntry(mod, _host.Library.GetManifest(gameId, mod.Id), mod.StagingPath));
        }

        return entries;
    }

    /// <summary>Reconstructs the analysis summary a staged mod needs for install.</summary>
    /// <param name="mod">Staged mod.</param>
    /// <param name="preview">Preview carrying the analysis.</param>
    private static RootAnalysis BuildAnalysisStub(ModMetadata mod, InstallPreview preview) => new()
    {
        SelectedRoot = preview.DetectedRoot,
        Confidence = preview.Confidence,
        Candidates = [],
        Signals = preview.Signals,
        TotalSize = preview.TotalBytes,
        FileCount = preview.Entries.Count,
        DeployableFileCount = preview.Entries.Count,
    };

    /// <summary>Opens a mod's staging directory.</summary>
    /// <param name="row">Mod whose folder to open.</param>
    private void OpenFolder(ModRow row)
    {
        if (row is null || !Directory.Exists(row.StagingPath))
        {
            Status = "该目录已不存在。";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = row.StagingPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Status = "无法打开目录：" + ex.Message;
        }
    }

    /// <summary>Deploy progress fraction, for a progress bar.</summary>
    public double DeployFraction { get; private set; }

    /// <summary>Shows the unified error dialog for unexpected operation failures.</summary>
    /// <param name="operation">Operation that failed.</param>
    /// <param name="exception">The failure.</param>
    private void ShowErrorDialog(string operation, Exception exception)
    {
        MessageBox.Show(
            "操作失败\n\n具体原因：\n" + exception.Message
            + "\n\n已采取措施：\n本次操作未产生任何文件修改（失败的操作已由引擎自动回滚）。\n\n详细信息已写入日志。",
            operation + " · ModManager",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}

/// <summary>A mod row on the Mods page.</summary>
public sealed class ModRow
{
    /// <summary>Creates a row.</summary>
    /// <param name="mod">Library record.</param>
    /// <param name="manifest">Deployment record, when deployed.</param>
    /// <param name="isEnabled">Whether the active profile enables it.</param>
    /// <param name="gameName">Game display name.</param>
    public ModRow(ModMetadata mod, ModManifest? manifest, bool isEnabled, string gameName)
    {
        Mod = mod;
        Manifest = manifest;
        IsEnabled = isEnabled;
        GameName = gameName;
    }

    /// <summary>Library record.</summary>
    public ModMetadata Mod { get; }

    /// <summary>Deployment record, when deployed.</summary>
    public ModManifest? Manifest { get; }

    /// <summary>Whether the active profile enables it.</summary>
    public bool IsEnabled { get; }

    /// <summary>Game display name.</summary>
    public string GameName { get; }

    /// <summary>Display name.</summary>
    public string Name => Mod.Name;

    /// <summary>Author, or a dash.</summary>
    public string Author => string.IsNullOrWhiteSpace(Mod.Author) ? "-" : Mod.Author;

    /// <summary>Version, or a dash.</summary>
    public string Version => string.IsNullOrWhiteSpace(Mod.Version) ? "-" : Mod.Version;

    /// <summary>Source label.</summary>
    public string Source => Mod.Source switch
    {
        ModSource.NexusMods => "Nexus Mods",
        ModSource.Thunderstore => "Thunderstore",
        ModSource.GitHub => "GitHub",
        ModSource.CurseForge => "CurseForge",
        ModSource.External => "手动安装",
        ModSource.Other => "其他来源",
        _ => "本地文件",
    };

    /// <summary>Staging directory.</summary>
    public string StagingPath => Mod.StagingPath;

    /// <summary>Category or a dash.</summary>
    public string Category => string.IsNullOrWhiteSpace(Mod.Category) ? "-" : Mod.Category;

    /// <summary>Summary text.</summary>
    public string Summary => string.IsNullOrWhiteSpace(Mod.Summary) ? "无描述。" : Mod.Summary;

    /// <summary>Short description of what was deployed.</summary>
    public string DeploymentText => Manifest is null
        ? "未部署"
        : $"{Manifest.Files.Count} 个文件 · {Zh.MethodList(Manifest.DeploymentMethod)}";

    /// <summary>True when nothing has ever been deployed, so the library-delete action is safe.</summary>
    public bool IsStaged => Manifest is null;

    /// <summary>Staged size in human units.</summary>
    public string SizeText => Core.Safety.FileSystemOps.FormatBytes(Mod.StagedSizeBytes);

    /// <summary>File count.</summary>
    public string FileCountText => $"{Mod.StagedFileCount} 个文件";

    /// <summary>
    /// A one-character glyph for the list row's icon tile.
    /// </summary>
    /// <remarks>
    /// Derived from the mod's own name rather than drawn from an icon set: it costs nothing, is stable
    /// across sessions, and gives each row a visual anchor instead of an identical placeholder square.
    /// </remarks>
    public string IconText => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    /// <summary>Conflicts recorded at deploy time, as a short badge line.</summary>
    public string ConflictSummary => Manifest is null || Manifest.Conflicts.Count == 0
        ? "无冲突"
        : $"{Manifest.Conflicts.Count} 个冲突";

    /// <summary>True when this mod has a recorded conflict, so the row can highlight it.</summary>
    public bool HasConflict => Manifest is { Conflicts.Count: > 0 };

    /// <summary>Colour for the conflict badge line.</summary>
    public Brush ConflictTone => HasConflict ? Palette.Warning() : Palette.Secondary();

    /// <summary>When it was installed.</summary>
    public string InstalledText => Mod.InstalledAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>Declared dependencies, joined.</summary>
    public string DependenciesText => Mod.Dependencies.Count == 0 ? "未声明" : string.Join(", ", Mod.Dependencies);

    /// <summary>Requirements from the source page, joined.</summary>
    public string RequirementsText => Mod.Requirements.Count == 0 ? "未声明" : string.Join(", ", Mod.Requirements);

    /// <summary>Declared incompatibilities, joined.</summary>
    public string IncompatibilitiesText => Mod.Incompatibilities.Count == 0
        ? "未声明"
        : string.Join(", ", Mod.Incompatibilities);

    /// <summary>Conflicts recorded at deploy time.</summary>
    public string ConflictsText => Manifest is null || Manifest.Conflicts.Count == 0
        ? "无记录"
        : string.Join("; ", Manifest.Conflicts);

    /// <summary>State badge text.</summary>
    public string StateText
    {
        get
        {
            if (!IsEnabled)
            {
                return "已禁用";
            }

            if (Manifest is null)
            {
                return Mod.State == ModState.Staged ? "已暂存" : Zh.ModState(Mod.State);
            }

            return Mod.AvailableVersion is { Length: > 0 } ? "有更新" : "已启用";
        }
    }

    /// <summary>Colour for the state badge.</summary>
    public Brush StateTone
    {
        get
        {
            if (!IsEnabled)
            {
                return Palette.Secondary();
            }

            if (Manifest is null)
            {
                return Palette.Warning();
            }

            return Mod.AvailableVersion is { Length: > 0 } ? Palette.Info() : Palette.Success();
        }
    }

    /// <summary>Fill for the state badge.</summary>
    public Brush StateFill
    {
        get
        {
            if (!IsEnabled)
            {
                return Palette.NeutralFill();
            }

            if (Manifest is null)
            {
                return Palette.WarningFill();
            }

            return Mod.AvailableVersion is { Length: > 0 } ? Palette.InfoFill() : Palette.SuccessFill();
        }
    }

    /// <summary>True when an upstream update is known.</summary>
    public bool HasUpdate => Mod.AvailableVersion is { Length: > 0 };

    /// <summary>Update description.</summary>
    public string UpdateText => HasUpdate
        ? $"{Version} → {Mod.AvailableVersion}"
        : "已是最新";
}

/// <summary>One source-to-target row of the installation preview.</summary>
public sealed class PreviewEntryRow
{
    /// <summary>Creates a row.</summary>
    /// <param name="entry">Engine preview entry.</param>
    public PreviewEntryRow(PreviewEntry entry)
    {
        SourceRelative = entry.SourceRelative;
        TargetPath = entry.TargetPath;
        Verdict = entry.Safety;
        Note = entry.Note;
    }

    /// <summary>Archive-relative source path.</summary>
    public string SourceRelative { get; }

    /// <summary>Absolute destination path.</summary>
    public string TargetPath { get; }

    /// <summary>Safety verdict.</summary>
    public PathSafetyVerdict Verdict { get; }

    /// <summary>Why that verdict.</summary>
    public string Note { get; }

    /// <summary>Badge label.</summary>
    public string Badge => Palette.DescribeVerdict(Verdict);

    /// <summary>Badge colour.</summary>
    public Brush Tone => Palette.ForVerdict(Verdict);

    /// <summary>Badge fill.</summary>
    public Brush Fill => Palette.FillForVerdict(Verdict);

    /// <summary>Sort key putting the riskiest paths first.</summary>
    public int SeverityRank => Verdict switch
    {
        PathSafetyVerdict.Unsafe => 3,
        PathSafetyVerdict.Conflict => 2,
        PathSafetyVerdict.Overwrite => 1,
        _ => 0,
    };
}

/// <summary>One row of the batch-import results panel.</summary>
public sealed class BatchImportRow
{
    private BatchImportRow(string filePath, bool succeeded, StagedMod? staged, string message)
    {
        FilePath = filePath;
        Succeeded = succeeded;
        Staged = staged;
        Message = message;
    }

    /// <summary>Creates a success row.</summary>
    public static BatchImportRow Success(string filePath, StagedMod staged) =>
        new(filePath, true, staged, $"已识别 {staged.Metadata.StagedFileCount} 个文件");

    /// <summary>Creates a failure row.</summary>
    public static BatchImportRow Failed(string filePath, string message) =>
        new(filePath, false, null, message);

    /// <summary>Absolute archive path.</summary>
    public string FilePath { get; }

    /// <summary>True when the archive staged successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>Staged mod, for success rows.</summary>
    public StagedMod? Staged { get; }

    /// <summary>Result message.</summary>
    public string Message { get; }

    /// <summary>File name for display.</summary>
    public string FileName => System.IO.Path.GetFileName(FilePath);

    /// <summary>Badge text.</summary>
    public string Badge => Succeeded ? "✓ 成功" : "✕ 失败";

    /// <summary>Badge tone.</summary>
    public System.Windows.Media.Brush Tone => Succeeded ? Palette.Success() : Palette.Danger();

    /// <summary>Badge fill.</summary>
    public System.Windows.Media.Brush Fill => Succeeded ? Palette.SuccessFill() : Palette.DangerFill();

    /// <summary>Display name of the staged mod, for success rows.</summary>
    public string DisplayName => Staged?.Metadata.Name ?? FileName;
}
public sealed class ConflictRow
{
    /// <summary>Creates a row.</summary>
    /// <param name="conflict">Engine conflict.</param>
    public ConflictRow(FileConflict conflict)
    {
        Conflict = conflict;
        RelativeTarget = conflict.RelativeTarget;
        WinnerModId = conflict.WinnerModId;
        OverriddenText = string.Join(", ", conflict.OverriddenModIds);
        Reason = conflict.Reason;
        IsResolved = conflict.Resolution != ConflictResolutionKind.Undecided;
    }

    /// <summary>Engine conflict.</summary>
    public FileConflict Conflict { get; }

    /// <summary>Contested path.</summary>
    public string RelativeTarget { get; }

    /// <summary>Winning mod id.</summary>
    public string WinnerModId { get; }

    /// <summary>Overridden mod ids, joined.</summary>
    public string OverriddenText { get; }

    /// <summary>Why the winner won.</summary>
    public string Reason { get; }

    /// <summary>True when a rule already decides this.</summary>
    public bool IsResolved { get; }

    /// <summary>Status badge text.</summary>
    public string StatusText => IsResolved ? "已设规则" : "按加载顺序";

    /// <summary>Status badge colour.</summary>
    public Brush Tone => IsResolved ? Palette.Success() : Palette.Warning();

    /// <summary>Status badge fill.</summary>
    public Brush Fill => IsResolved ? Palette.SuccessFill() : Palette.WarningFill();

    /// <summary>The chain the spec asks for: Mod A provides it, Mod B overrides.</summary>
    public string Chain => $"{OverriddenText}  →  {RelativeTarget}  →  {WinnerModId} 生效";
}
