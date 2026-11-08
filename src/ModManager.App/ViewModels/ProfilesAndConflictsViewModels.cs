using System.Windows.Media;
using ModManager.Core;

namespace ModManager.App.ViewModels;

/// <summary>
/// Profiles: create, switch, delete.
/// </summary>
/// <remarks>
/// The user's spec §14 requires profiles per game (Vanilla / test / big pack) that store enabled mods,
/// load order, priority, settings and deployment state, and that switching profiles redeploys
/// automatically.
/// <para>
/// Redeploying on switch is the honest implementation. The alternative — snapshotting game files per profile
/// — would double disk usage and make a profile switch destructive, whereas replaying from manifests is
/// exactly the reverse of a deploy (Modrix's reversibility invariant) and therefore already correct.
/// </para>
/// </remarks>
public sealed class ProfilesViewModel : ViewModelBase, IPageViewModel
{
    private readonly ApplicationHost _host;
    private readonly ShellViewModel? _shell;
    private string _newProfileName = string.Empty;
    private string _status = "请先选择一个游戏，再管理它的配置方案。";
    private bool _isBusy;
    private ProfileRow? _selectedProfile;

    /// <summary>Creates the page.</summary>
    /// <param name="host">Composition root.</param>
    /// <param name="shell">Shell, for the active game.</param>
    public ProfilesViewModel(ApplicationHost host, ShellViewModel? shell = null)
    {
        _host = host;
        _shell = shell;

        CreateCommand = new RelayCommand(() => CreateAsync(), () => _newProfileName.Trim().Length > 0 && _shell?.ActiveGame is not null);
        ActivateCommand = new RelayCommand<ProfileRow>(row => ActivateAsync(row));
        DeleteCommand = new RelayCommand<ProfileRow>(row => DeleteAsync(row));
        CopyCommand = new RelayCommand<ProfileRow>(row => CopyAsync(row));
    }

    /// <summary>Profiles for the active game.</summary>
    public BulkObservableCollection<ProfileRow> Profiles { get; } = [];

    /// <summary>Creates a profile.</summary>
    public RelayCommand CreateCommand { get; }

    /// <summary>Switches to a profile, redeploying.</summary>
    public RelayCommand<ProfileRow> ActivateCommand { get; }

    /// <summary>Deletes a profile.</summary>
    public RelayCommand<ProfileRow> DeleteCommand { get; }

    /// <summary>Duplicates a profile.</summary>
    public RelayCommand<ProfileRow> CopyCommand { get; }

    /// <summary>Name for a new profile.</summary>
    public string NewProfileName
    {
        get => _newProfileName;
        set
        {
            if (SetProperty(ref _newProfileName, value))
            {
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>True while a switch is in progress.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Selected profile.</summary>
    public ProfileRow? SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        var game = _shell?.ActiveGame;
        if (game is null)
        {
            Profiles.ReplaceAll([]);
            Status = "还没有选择游戏。请先在“我的游戏”页添加一个。";
            await Task.CompletedTask.ConfigureAwait(true);
            return;
        }

        var profile = _host.Install.EnsureProfile(game.Instance.GameId);
        var rows = new List<ProfileRow>();

        foreach (var candidate in _host.Library.ProfilesForGame(game.Instance.GameId))
        {
            var enabled = candidate.ModOrder.Count(id => !candidate.DisabledModIds.Contains(id, StringComparer.Ordinal));
            rows.Add(new ProfileRow(candidate, enabled));
        }

        Profiles.ReplaceAll(rows.OrderByDescending(row => row.IsActive).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase));
        Status = $"{rows.Count} profile(s). The active profile is '{profile.Name}'.";

        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>Creates a profile seeded from the active one.</summary>
    private async Task CreateAsync()
    {
        var game = _shell?.ActiveGame;
        if (game is null)
        {
            return;
        }

        var name = _newProfileName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        if (_host.Library.ProfilesForGame(game.Instance.GameId)
            .Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "已存在同名的配置方案。";
            return;
        }

        var active = _host.Install.EnsureProfile(game.Instance.GameId);

        // Seeded from the active profile so a new profile starts from a working setup rather than from
        // nothing, which is what "duplicate my current setup and tweak it" means in practice.
        _host.Library.SaveProfile(new Profile
        {
            Id = $"{game.Instance.GameId}-{Guid.NewGuid():N}"[..24],
            Name = name,
            GameId = game.Instance.GameId,
            ModOrder = active.ModOrder,
            DisabledModIds = active.DisabledModIds,
            PluginOrder = active.PluginOrder,
            IsActive = false,
        });

        NewProfileName = string.Empty;
        Status = $"Created profile '{name}' from '{active.Name}'.";
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Switches to a profile and redeploys.</summary>
    /// <param name="row">Profile to activate.</param>
    private async Task ActivateAsync(ProfileRow row)
    {
        var game = _shell?.ActiveGame;
        if (game is null || row is null || row.IsActive)
        {
            return;
        }

        var adapter = _host.Adapters.Get(game.Instance.GameId);
        if (adapter is null)
        {
            return;
        }

        IsBusy = true;
        Status = $"Switching to '{row.Name}'...";

        try
        {
            // The old profile is deactivated first so the "one active profile per game" invariant holds
            // even if the redeploy below fails.
            foreach (var profile in _host.Library.ProfilesForGame(game.Instance.GameId))
            {
                if (profile.IsActive)
                {
                    _host.Library.SaveProfile(profile with { IsActive = false });
                }
            }

            _host.Library.SaveProfile(row.Profile with { IsActive = true });

            var modSet = new List<ModSetEntry>();
            foreach (var mod in _host.Library.ModsForGame(game.Instance.GameId))
            {
                modSet.Add(new ModSetEntry(mod, _host.Library.GetManifest(game.Instance.GameId, mod.Id), mod.StagingPath));
            }

            var plan = _host.Planner.BuildPlan(
                adapter.Definition,
                game.Instance.InstallPath,
                row.Profile with { IsActive = true },
                modSet,
                _host.Library.ConflictRulesForGame(game.Instance.GameId));

            if (plan.IsEmpty)
            {
                Status = $"Switched to '{row.Name}'. The game folder already matches it.";
                return;
            }

            var result = await _host.Applier
                .ApplyAsync(plan, adapter.Definition, modSet, _host.Settings.Deployment, _host.Logger)
                .ConfigureAwait(true);

            Status = result.Succeeded
                ? $"Switched to '{row.Name}': {result.DeployedFileCount} file(s) deployed, "
                    + $"{plan.Files.Count(file => file.Safety == PathSafetyVerdict.Overwrite)} path(s) touched."
                : "切换配置方案失败：" + (result.Error ?? "未知错误");

            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Duplicates a profile.</summary>
    /// <param name="row">Profile to copy.</param>
    private async Task CopyAsync(ProfileRow row)
    {
        if (row is null)
        {
            return;
        }

        NewProfileName = row.Name + " copy";
        await CreateAsync().ConfigureAwait(true);
    }

    /// <summary>Deletes a profile, refusing the last one and the active one.</summary>
    /// <param name="row">Profile to delete.</param>
    private async Task DeleteAsync(ProfileRow row)
    {
        if (row is null || _shell?.ActiveGame is null)
        {
            return;
        }

        var game = _shell.ActiveGame;
        var all = _host.Library.ProfilesForGame(game.Instance.GameId);

        if (all.Count <= 1)
        {
            Status = "每个游戏至少要保留一个配置方案。";
            return;
        }

        if (row.IsActive)
        {
            // Deleting the active profile would leave the game's deployed state attributable to nothing, so
            // the user must switch away first. That is a deliberate refusal, not an oversight.
            Status = "请先切换到其他配置方案，再删除这一个。";
            return;
        }

        _host.Library.RemoveProfile(row.Profile.Id);
        Status = $"Deleted profile '{row.Name}'. No files were removed from the game folder.";
        await RefreshAsync().ConfigureAwait(true);
    }
}

/// <summary>A profile row.</summary>
/// <param name="Profile">Profile record.</param>
/// <param name="EnabledModCount">Enabled mod count.</param>
public sealed record ProfileRow(Profile Profile, int EnabledModCount)
{
    /// <summary>Display name.</summary>
    public string Name => Profile.Name;

    /// <summary>Whether it is the active profile.</summary>
    public bool IsActive => Profile.IsActive;

    /// <summary>Description or a default.</summary>
    public string Description => string.IsNullOrWhiteSpace(Profile.Description)
        ? "No description."
        : Profile.Description;

    /// <summary>Mod summary.</summary>
    public string ModSummary => $"{EnabledModCount} enabled of {Profile.ModOrder.Count} listed";

    /// <summary>Conflict rule count.</summary>
    public string RuleSummary => Profile.ConflictOverrides.Count == 0
        ? "暂无冲突规则"
        : $"{Profile.ConflictOverrides.Count} conflict rule(s)";

    /// <summary>Last deploy time.</summary>
    public string DeployedText => Profile.LastDeployedAt is { } stamp
        ? "Last deployed " + stamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "Never deployed";

    /// <summary>Badge text.</summary>
    public string StateText => IsActive ? "Active" : "Inactive";

    /// <summary>Badge colour.</summary>
    public Brush Tone => IsActive ? Palette.Success() : Palette.Secondary();

    /// <summary>Badge fill.</summary>
    public Brush Fill => IsActive ? Palette.SuccessFill() : Palette.NeutralFill();
}

/// <summary>
/// Conflicts: every contested path, with the rule controls the spec asks for.
/// </summary>
public sealed class ConflictsViewModel : ViewModelBase, IPageViewModel
{
    private readonly ApplicationHost _host;
    private readonly ShellViewModel? _shell;
    private string _status = "请先选择一个游戏，再查看冲突。";
    private ConflictRow? _selectedConflict;

    /// <summary>Creates the page.</summary>
    /// <param name="host">Composition root.</param>
    /// <param name="shell">Shell, for the active game.</param>
    public ConflictsViewModel(ApplicationHost host, ShellViewModel? shell = null)
    {
        _host = host;
        _shell = shell;

        PreferWinnerCommand = new RelayCommand<ConflictRow>(row => SetRuleAsync(row, preferWinner: true));
        PreferLoserCommand = new RelayCommand<ConflictRow>(row => SetRuleAsync(row, preferWinner: false));
        ClearRuleCommand = new RelayCommand<ConflictRow>(row => ClearRuleAsync(row));
    }

    /// <summary>Conflict rows.</summary>
    public BulkObservableCollection<ConflictRow> Conflicts { get; } = [];

    /// <summary>Steps explaining the selected conflict.</summary>
    public BulkObservableCollection<ConflictStepRow> Steps { get; } = [];

    /// <summary>Keeps the current winner.</summary>
    public RelayCommand<ConflictRow> PreferWinnerCommand { get; }

    /// <summary>Makes the losing mod win instead.</summary>
    public RelayCommand<ConflictRow> PreferLoserCommand { get; }

    /// <summary>Removes the rule, returning to load order.</summary>
    public RelayCommand<ConflictRow> ClearRuleCommand { get; }

    /// <summary>Selected conflict, driving the explanation pane.</summary>
    public ConflictRow? SelectedConflict
    {
        get => _selectedConflict;
        set
        {
            if (SetProperty(ref _selectedConflict, value))
            {
                LoadExplanation();
            }
        }
    }

    /// <summary>Status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Explanation of the selected conflict.</summary>
    public string Explanation { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        var game = _shell?.ActiveGame;
        if (game is null)
        {
            Conflicts.ReplaceAll([]);
            Steps.ReplaceAll([]);
            Status = "还没有选择游戏。请先在“我的游戏”页添加一个。";
            await Task.CompletedTask.ConfigureAwait(true);
            return;
        }

        var modSet = BuildModSet(game.Instance.GameId);
        var rules = _host.Library.ConflictRulesForGame(game.Instance.GameId);
        var conflicts = _host.Conflicts.Analyze(game.Instance.GameId, modSet, rules);

        Conflicts.ReplaceAll(conflicts.Select(conflict => new ConflictRow(conflict)));

        Status = conflicts.Count == 0
            ? "没有文件冲突：没有两个已启用 Mod 提供同一个文件。"
            : $"{conflicts.Count} 个文件被多个 Mod 同时提供，其中 "
                + $"{conflicts.Count(conflict => conflict.Resolution == ConflictResolutionKind.Undecided)} 个由加载顺序决定。";

        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>Records a per-path rule naming the winner or the loser.</summary>
    /// <param name="row">Conflict to decide.</param>
    /// <param name="preferWinner">True to keep the current winner, false to invert it.</param>
    private async Task SetRuleAsync(ConflictRow row, bool preferWinner)
    {
        var game = _shell?.ActiveGame;
        if (game is null || row is null)
        {
            return;
        }

        var target = preferWinner
            ? row.WinnerModId
            : row.Conflict.OverriddenModIds.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(target))
        {
            Status = "这个文件没有其他 Mod 可供选择。";
            return;
        }

        _host.Library.Upsert(game.Instance.GameId, new ConflictResolution(
            row.RelativeTarget,
            ConflictResolutionKind.PreferMod,
            target));

        Status = $"'{target}' now provides {row.RelativeTarget}. Redeploy to apply it.";
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Removes a recorded rule.</summary>
    /// <param name="row">Conflict whose rule to clear.</param>
    private async Task ClearRuleAsync(ConflictRow row)
    {
        var game = _shell?.ActiveGame;
        if (game is null || row is null)
        {
            return;
        }

        _host.Library.RemoveConflictRule(game.Instance.GameId, row.RelativeTarget);
        Status = $"已清除 {row.RelativeTarget} 的规则，改由加载顺序决定。";
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Builds the step-by-step explanation of the selected conflict.</summary>
    private void LoadExplanation()
    {
        var game = _shell?.ActiveGame;

        if (game is null || _selectedConflict is null)
        {
            Steps.ReplaceAll([]);
            Explanation = string.Empty;
            OnPropertyChanged(nameof(Explanation));
            return;
        }

        var modSet = BuildModSet(game.Instance.GameId);
        var explanation = _host.Conflicts.Explain(game.Instance.GameId, _selectedConflict.RelativeTarget, modSet);

        Steps.ReplaceAll(explanation.Steps.Select(step => new ConflictStepRow(step)));
        Explanation = explanation.Explanation;
        OnPropertyChanged(nameof(Explanation));
    }

    /// <summary>
    /// Builds the mod set for a game, ordered by the active profile's load order.
    /// </summary>
    /// <param name="gameId">Game id.</param>
    /// <remarks>
    /// The order matters: the conflict service reports the <i>last</i> provider as the winner, so an
    /// unordered set would name a different winner than the planner actually deploys. Mods the profile
    /// does not list are appended in library order so nothing is hidden.
    /// </remarks>
    private IReadOnlyList<ModSetEntry> BuildModSet(string gameId)
    {
        var byId = new Dictionary<string, ModSetEntry>(StringComparer.Ordinal);
        foreach (var mod in _host.Library.ModsForGame(gameId))
        {
            byId[mod.Id] = new ModSetEntry(mod, _host.Library.GetManifest(gameId, mod.Id), mod.StagingPath);
        }

        var profile = _host.Library.ProfilesForGame(gameId).FirstOrDefault(candidate => candidate.IsActive);
        if (profile is null)
        {
            return byId.Values.ToList();
        }

        var ordered = new List<ModSetEntry>(byId.Count);
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

        foreach (var pair in byId)
        {
            if (!profile.DisabledModIds.Contains(pair.Key, StringComparer.Ordinal) && seen.Add(pair.Key))
            {
                ordered.Add(pair.Value);
            }
        }

        return ordered;
    }
}

/// <summary>One mod's claim on a contested path.</summary>
/// <param name="Step">Engine step.</param>
public sealed record ConflictStepRow(ConflictStep Step)
{
    /// <summary>Mod display name.</summary>
    public string ModName => Step.ModName;

    /// <summary>Staged source path.</summary>
    public string SourcePath => Step.SourcePath;

    /// <summary>File size.</summary>
    public string SizeText => Core.Safety.FileSystemOps.FormatBytes(Step.Size);

    /// <summary>Whether this mod provides the bytes.</summary>
    public bool Wins => Step.Wins;

    /// <summary>Badge text.</summary>
    public string StateText => Step.Wins ? "提供该文件" : "被覆盖";

    /// <summary>Badge colour.</summary>
    public Brush Tone => Step.Wins ? Palette.Success() : Palette.Secondary();

    /// <summary>Badge fill.</summary>
    public Brush Fill => Step.Wins ? Palette.SuccessFill() : Palette.NeutralFill();
}
