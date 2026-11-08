using ModManager.Core;
using ModManager.Core.Safety;
using ModManager.Core.Services;

namespace ModManager.App.ViewModels;

/// <summary>
/// Load order: the enabled mods of the active game, in resolution order.
/// </summary>
/// <remarks>
/// The order is the profile's <see cref="Profile.ModOrder"/> — the same list the deployment planner
/// resolves conflicts with. Moving a row here and saving therefore changes which mod really wins a
/// contested file, which is exactly what a load-order page must do. The conflict summary at the
/// bottom is computed from the order as currently edited, before saving, so the user sees the effect
/// of a reorder before deploying it.
/// </remarks>
public sealed class LoadOrderViewModel : ViewModelBase, IPageViewModel
{
    private readonly ApplicationHost _host;
    private readonly ShellViewModel? _shell;
    private string _status = "选择一个游戏查看其加载顺序。";
    private string? _activeProfileId;
    private LoadOrderRow? _selectedRow;
    private bool _isBusy;

    /// <summary>Creates the page.</summary>
    /// <param name="host">Composition root.</param>
    /// <param name="shell">Shell, for the active game.</param>
    public LoadOrderViewModel(ApplicationHost host, ShellViewModel? shell = null)
    {
        _host = host;
        _shell = shell;

        MoveUpCommand = new RelayCommand<LoadOrderRow>(row =>
        {
            Move(row, -1);
            return Task.CompletedTask;
        }, row => CanMove(row, -1));

        MoveDownCommand = new RelayCommand<LoadOrderRow>(row =>
        {
            Move(row, +1);
            return Task.CompletedTask;
        }, row => CanMove(row, +1));

        SaveCommand = new RelayCommand(() => SaveAsync(), () => !_isBusy && HasChanges);
    }

    /// <summary>Enabled mods in the current (possibly edited) order.</summary>
    public BulkObservableCollection<LoadOrderRow> Items { get; } = [];

    /// <summary>Conflict preview rows, computed from the current order.</summary>
    public BulkObservableCollection<ConflictRow> Conflicts { get; } = [];

    /// <summary>Moves the selected mod up.</summary>
    public RelayCommand<LoadOrderRow> MoveUpCommand { get; }

    /// <summary>Moves the selected mod down.</summary>
    public RelayCommand<LoadOrderRow> MoveDownCommand { get; }

    /// <summary>Persists the order and redeploys.</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>True when the order differs from the persisted one.</summary>
    public bool HasChanges { get; private set; }

    /// <summary>Selected row.</summary>
    public LoadOrderRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                MoveUpCommand.RaiseCanExecuteChanged();
                MoveDownCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Conflict summary line for the current order.</summary>
    public string ConflictSummary => Conflicts.Count == 0
        ? "当前顺序下没有文件冲突。"
        : $"当前顺序下有 {Conflicts.Count} 个文件冲突；最后列出的 Mod 生效。";

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        var game = _shell?.ActiveGame;
        if (game is null)
        {
            Items.ReplaceAll([]);
            Conflicts.ReplaceAll([]);
            Status = "未选择游戏。请先在游戏页添加。";
            await Task.CompletedTask.ConfigureAwait(true);
            return;
        }

        var profile = _host.Install.EnsureProfile(game.Instance.GameId);
        _activeProfileId = profile.Id;

        RebuildRows(profile.ModOrder);
        HasChanges = false;
        OnPropertyChanged(nameof(HasChanges));
        SaveCommand.RaiseCanExecuteChanged();
        RebuildConflicts();

        Status = $"加载顺序：{Items.Count} 个已启用 Mod。上移/下移调整后点击“保存并部署”。";
        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>Rebuilds the visible rows from an order.</summary>
    /// <param name="order">Mod ids in resolution order.</param>
    private void RebuildRows(IReadOnlyList<string> order)
    {
        var rows = new List<LoadOrderRow>();

        foreach (var modId in order)
        {
            var mod = _host.Library.Mod(modId);
            if (mod is not null)
            {
                rows.Add(new LoadOrderRow(mod));
            }
        }

        // Mods enabled in the profile but missing from the order (should not normally happen) are
        // appended so nothing enabled is silently invisible.
        foreach (var mod in _host.Library.ModsForGame(_shell?.ActiveGame?.Instance.GameId ?? string.Empty))
        {
            if (!rows.Any(row => row.Mod.Id == mod.Id) && _shell?.ActiveGame is not null)
            {
                var profile = _host.Install.EnsureProfile(_shell.ActiveGame.Instance.GameId);
                if (profile.ModOrder.Contains(mod.Id, StringComparer.Ordinal)
                    && !profile.DisabledModIds.Contains(mod.Id, StringComparer.Ordinal))
                {
                    rows.Add(new LoadOrderRow(mod));
                }
            }
        }

        Items.ReplaceAll(rows);
        SelectedRow = null;
    }

    /// <summary>Computes conflicts under the order currently being edited.</summary>
    private void RebuildConflicts()
    {
        var game = _shell?.ActiveGame;
        if (game is null)
        {
            Conflicts.ReplaceAll([]);
            return;
        }

        var adapter = _host.Adapters.Get(game.Instance.GameId);
        if (adapter is null)
        {
            Conflicts.ReplaceAll([]);
            return;
        }

        var ordered = Items.Select(row => row.Mod).ToList();
        var modSet = ordered
            .Select(mod => new ModSetEntry(mod, _host.Library.GetManifest(game.Instance.GameId, mod.Id), mod.StagingPath))
            .ToList();

        var profile = _host.Library.GetProfile(_activeProfileId ?? $"{game.Instance.GameId}-default");
        var rules = new List<ConflictResolution>(_host.Library.ConflictRulesForGame(game.Instance.GameId));
        if (profile is not null)
        {
            rules.AddRange(profile.ConflictOverrides.Values);
        }

        Conflicts.ReplaceAll(
            _host.Conflicts
                .Analyze(game.Instance.GameId, modSet, rules)
                .Select(conflict => new ConflictRow(conflict))
                .ToList());

        OnPropertyChanged(nameof(ConflictSummary));
    }

    /// <summary>Moves a row by a signed offset and re-evaluates the conflict preview.</summary>
    /// <param name="row">Row to move.</param>
    /// <param name="delta">-1 up, +1 down.</param>
    private void Move(LoadOrderRow row, int delta)
    {
        if (row is null)
        {
            return;
        }

        var index = Items.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Items.Count)
        {
            return;
        }

        Items.Move(index, target);
        SelectedRow = row;

        HasChanges = true;
        OnPropertyChanged(nameof(HasChanges));
        SaveCommand.RaiseCanExecuteChanged();

        // The conflict preview follows the edited order immediately, so the user sees who would win.
        RebuildConflicts();
    }

    /// <summary>Whether a row can move in a direction.</summary>
    /// <param name="row">Row to test.</param>
    /// <param name="delta">-1 up, +1 down.</param>
    private bool CanMove(LoadOrderRow? row, int delta)
    {
        if (row is null)
        {
            return false;
        }

        var index = Items.IndexOf(row);
        var target = index + delta;
        return index >= 0 && target >= 0 && target < Items.Count;
    }

    /// <summary>Persists the edited order and redeploys the game folder to match it.</summary>
    private async Task SaveAsync()
    {
        var game = _shell?.ActiveGame;
        if (game is null)
        {
            return;
        }

        IsBusy = true;
        Status = "正在保存加载顺序并重新部署…";

        try
        {
            var profile = _host.Library.GetProfile(_activeProfileId ?? $"{game.Instance.GameId}-default")
                ?? _host.Install.EnsureProfile(game.Instance.GameId);

            var order = Items.Select(row => row.Mod.Id).ToList();
            _host.Library.SaveProfile(profile with { ModOrder = order });

            var adapter = _host.Adapters.Get(game.Instance.GameId);
            if (adapter is null)
            {
                Status = "该游戏定义已不可用。";
                return;
            }

            var modSet = new List<ModSetEntry>();
            foreach (var mod in _host.Library.ModsForGame(game.Instance.GameId))
            {
                modSet.Add(new ModSetEntry(mod, _host.Library.GetManifest(game.Instance.GameId, mod.Id), mod.StagingPath));
            }

            var plan = _host.Planner.BuildPlan(
                adapter.Definition,
                game.Instance.InstallPath,
                profile with { ModOrder = order },
                modSet,
                _host.Library.ConflictRulesForGame(game.Instance.GameId));

            var result = await _host.Applier
                .ApplyAsync(plan, adapter.Definition, modSet, _host.Settings.Deployment, _host.Logger)
                .ConfigureAwait(true);

            HasChanges = false;
            OnPropertyChanged(nameof(HasChanges));
            SaveCommand.RaiseCanExecuteChanged();

            Status = result.Succeeded
                ? $"已保存加载顺序并部署（{result.DeployedFileCount} 个文件）。"
                : "部署失败：" + (result.Error ?? "未知错误");

            _host.Logger.Info("LoadOrder", $"已保存 “{game.Instance.GameId}” 的加载顺序：{string.Join(" → ", order)}");

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = "保存失败：" + ex.Message;
            _host.Logger.Error("LoadOrder", "保存加载顺序失败。", ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }
}

/// <summary>One mod in the load-order list.</summary>
public sealed class LoadOrderRow
{
    /// <summary>Creates a row.</summary>
    /// <param name="mod">Library record.</param>
    public LoadOrderRow(ModMetadata mod) => Mod = mod;

    /// <summary>Library record.</summary>
    public ModMetadata Mod { get; }

    /// <summary>Display name.</summary>
    public string Name => Mod.Name;

    /// <summary>Version, or a dash.</summary>
    public string Version => string.IsNullOrWhiteSpace(Mod.Version) ? "-" : Mod.Version;

    /// <summary>File count.</summary>
    public string FileCountText => $"{Mod.StagedFileCount} 个文件";
}
