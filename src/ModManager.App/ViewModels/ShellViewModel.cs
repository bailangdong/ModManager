using System.Windows.Media;
using ModManager.Core;
using ModManager.Core.Services;

namespace ModManager.App.ViewModels;

/// <summary>
/// The shell: page list, navigation, and the active game every page operates on.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — the user's spec §16 asks for a left-hand navigation of Dashboard, Games, Mods,
/// Downloads, Profiles, Conflicts, Settings and Logs, and §4 requires that the UI show only what a game
/// actually supports: "如果游戏没有 Load Order：UI 不应该显示 Load Order 页面".
/// </para>
/// <para>
/// That requirement is honoured literally here. Each page declares the capability it needs, and a page
/// whose capability is absent from the active game's definition is <i>removed from the navigation list</i>
/// rather than merely disabled. A greyed-out Load Order entry for a BepInEx game would imply the feature
/// exists and is broken; hiding it says the truth, which is that the concept does not apply.
/// </para>
/// </remarks>
public sealed class ShellViewModel : ViewModelBase
{
    private readonly ApplicationHost _host;
    private readonly CoverImageLoader _covers;
    private readonly NexusGameImageCache _gameImages;
    private readonly List<PageDescriptor> _allPages;
    private object? _activePage;
    private GameChoice? _activeGame;
    private ImageSource? _activeGameCover;
    private PageDescriptor? _selectedPage;
    private string _statusMessage = "就绪。";
    private bool _isBusy;
    private string _searchText = string.Empty;

    /// <summary>Creates the shell.</summary>
    /// <param name="host">Composition root.</param>
    public ShellViewModel(ApplicationHost host)
    {
        _host = host;
        _covers = new CoverImageLoader(host.Images);
        _gameImages = new NexusGameImageCache(host.Paths, host.Logger);

        _allPages =
        [
            new PageDescriptor("总览", string.Empty, "所有游戏的概况与需要关注的事项。", GameCapability.None, () => new DashboardViewModel(_host, this)),
            new PageDescriptor("游戏", "游戏管理", "添加游戏并选择要管理的游戏。", GameCapability.None, () => new GamesViewModel(_host, this)),
            new PageDescriptor("在线 Mod", "Mod 管理", "浏览 Nexus Mods 上的内容，并直接安装或更新。", GameCapability.None, () => new OnlineModsViewModel(_host, this)),
            new PageDescriptor("本地 Mod", "Mod 管理", "已安装的 Mod：启用、禁用、更新、卸载。", GameCapability.None, () => new ModsViewModel(_host, this)),
            new PageDescriptor("加载顺序", "Mod 管理", "已启用 Mod 的生效顺序：靠后的优先。", GameCapability.FileConflicts, () => new LoadOrderViewModel(_host, this)),
            new PageDescriptor("冲突", "Mod 管理", "多个已启用 Mod 同时提供的文件。", GameCapability.FileConflicts, () => new ConflictsViewModel(_host)),
            new PageDescriptor("配置方案", "Mod 管理", "可以切换的命名 Mod 集合。", GameCapability.Profiles, () => new ProfilesViewModel(_host, this)),
            new PageDescriptor("下载", "管理", "进行中与已完成的下载。", GameCapability.None, () => new DownloadsViewModel(_host)),
            new PageDescriptor("日志", "管理", "结构化日志与事务历史。", GameCapability.None, () => new LogsViewModel(_host)),
            new PageDescriptor("设置", "设置", "凭据、部署与安全选项。", GameCapability.None, () => new SettingsViewModel(_host)),
        ];

        NavigateCommand = new RelayCommand<PageDescriptor>(descriptor =>
        {
            Select(descriptor);
            return Task.CompletedTask;
        });

        SelectGameCommand = new RelayCommand<GameChoice>(choice =>
        {
            SelectGame(choice);
            return Task.CompletedTask;
        });

        OpenSettingsCommand = new RelayCommand(() =>
        {
            NavigateTo("设置");
            return Task.CompletedTask;
        });

        RefreshCommand = new RelayCommand(RefreshAsync);
    }

    /// <summary>Pages applicable to the active game.</summary>
    public BulkObservableCollection<PageDescriptor> Pages { get; } = [];

    /// <summary>
    /// The same pages, grouped under the sidebar's section captions.
    /// </summary>
    /// <remarks>
    /// A flat list is harder to scan once it passes about five entries. Grouping by purpose ("Mod 管理",
    /// "管理") keeps the navigation scannable without hiding anything behind a click.
    /// </remarks>
    public BulkObservableCollection<PageGroup> PageGroups { get; } = [];

    /// <summary>Every registered game, for the header selector.</summary>
    public BulkObservableCollection<GameChoice> Games { get; } = [];

    /// <summary>Navigates to a page.</summary>
    public RelayCommand<PageDescriptor> NavigateCommand { get; }

    /// <summary>Switches the active game.</summary>
    public RelayCommand<GameChoice> SelectGameCommand { get; }

    /// <summary>Rebuilds the current page from the engine.</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>Jumps to the settings page from the toolbar gear.</summary>
    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>The page currently displayed.</summary>
    public object? ActivePage
    {
        get => _activePage;
        private set => SetProperty(ref _activePage, value);
    }

    /// <summary>The page currently selected in the navigation list.</summary>
    public PageDescriptor? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (SetProperty(ref _selectedPage, value) && value is not null)
            {
                Activate(value);
            }
        }
    }

    /// <summary>
    /// The toolbar's search box. Typing here drives the Mods page's own filter.
    /// </summary>
    /// <remarks>
    /// The shell owns the box because it belongs to the window chrome, but the filtering itself stays in
    /// the page that knows what a Mod is — the shell only forwards the text, and only when that page is
    /// the one on screen.
    /// </remarks>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
            {
                return;
            }

            if (ActivePage is ModsViewModel mods)
            {
                mods.SearchText = value;
            }
        }
    }

    /// <summary>The game every page currently operates on.</summary>
    public GameChoice? ActiveGame
    {
        get => _activeGame;
        private set
        {
            if (SetProperty(ref _activeGame, value))
            {
                OnPropertyChanged(nameof(ActiveGameDescription));
                OnPropertyChanged(nameof(CapabilitySummary));
                OnPropertyChanged(nameof(HasActiveGame));

                // The sidebar shows the same artwork as the Games page, resolved through the same cache.
                _ = LoadActiveGameCoverAsync();
            }
        }
    }

    /// <summary>Artwork for the active game, when its definition declares a Nexus domain.</summary>
    public ImageSource? ActiveGameCover
    {
        get => _activeGameCover;
        private set
        {
            if (SetProperty(ref _activeGameCover, value))
            {
                OnPropertyChanged(nameof(HasActiveGameCover));
            }
        }
    }

    /// <summary>True when the sidebar has a picture to show.</summary>
    public bool HasActiveGameCover => _activeGameCover is not null;

    /// <summary>
    /// Loads the active game's artwork, reusing the shared Nexus artwork cache.
    /// </summary>
    /// <remarks>
    /// A game with no Nexus domain, or one whose image cannot be fetched, simply keeps the placeholder: the
    /// sidebar never invents a picture and never shows a letter.
    /// </remarks>
    private async Task LoadActiveGameCoverAsync()
    {
        var domain = ActiveGame?.Definition.NexusDomain;
        if (string.IsNullOrWhiteSpace(domain))
        {
            ActiveGameCover = null;
            return;
        }

        try
        {
            var artwork = await _gameImages.GetAsync(_host.Nexus, [domain]).ConfigureAwait(true);
            ActiveGameCover = artwork.TryGetValue(domain, out var game) && game.CardUrl is { Length: > 0 } url
                ? await _covers.LoadAsync(url).ConfigureAwait(true)
                : null;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Http.HttpRequestException or InvalidOperationException)
        {
            ActiveGameCover = null;
            _host.Logger.Warn("Nexus", "当前游戏封面加载失败，已保留占位。", ex.Message);
        }
    }

    /// <summary>One-line description of the active game, shown in the toolbar breadcrumb.</summary>
    public string ActiveGameDescription =>
        _activeGame is null
            ? "尚未选择游戏"
            : $"{_activeGame.Name}";

    /// <summary>Human-readable capability list, so the gating is inspectable rather than magic.</summary>
    public string CapabilitySummary =>
        _activeGame is null ? string.Empty : _activeGame.DescribeCapabilities();

    /// <summary>True when a game is selected.</summary>
    public bool HasActiveGame => _activeGame is not null;

    /// <summary>Status bar text.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>True while a long operation is running.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Loads initial state: recovery summary, game list, and the first page.</summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            ReloadGames();

            if (_host.Recovery.HadWork)
            {
                // Surfaced once, prominently: a user whose last session crashed needs to know that
                // something was repaired, not to discover it later in a log file.
                StatusMessage = _host.Recovery.Failed.Count > 0
                    ? $"恢复操作需要处理：{string.Join("；", _host.Recovery.Messages)}"
                    : $"已从上次被中断的操作中恢复。{string.Join(" ", _host.Recovery.Messages)}";
            }

            Select(_allPages[0]);

            // The "open at this page" preference: honoured only when the page is currently
            // applicable (a capability-gated page is skipped rather than forced open).
            var startupPage = _host.Settings.Settings.Ui.StartupPageName;
            var target = Pages.FirstOrDefault(page => page.Name == startupPage) ?? Pages.FirstOrDefault();
            if (target is not null && !string.Equals(target.Name, SelectedPage?.Name, StringComparison.Ordinal))
            {
                SelectedPage = target;
            }

            await Task.CompletedTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "启动失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Stops background work before the window closes.</summary>
    public async Task ShutdownAsync()
    {
        try
        {
            await _host.Downloads.PauseAllAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shutting down; nothing useful can be done with a failure here.
        }

        _host.Downloads.Dispose();
        _host.Nexus.Dispose();
    }

    /// <summary>Reloads the game list and keeps the current selection if it still exists.</summary>
    public void ReloadGames()
    {
        var previousId = _activeGame?.Instance.GameId;

        var choices = new List<GameChoice>();
        foreach (var instance in _host.Library.AllGames())
        {
            var adapter = _host.Adapters.Get(instance.GameId);
            if (adapter is not null)
            {
                choices.Add(new GameChoice(instance, adapter.Definition));
            }
        }

        Games.ReplaceAll(choices);

        var restored = choices.FirstOrDefault(choice =>
            string.Equals(choice.Instance.GameId, previousId, StringComparison.OrdinalIgnoreCase));

        // No in-memory selection yet (fresh start): restore the user's default game from settings,
        // so the app opens on the same game it closed on.
        if (restored is null && previousId is null)
        {
            var defaultId = _host.Settings.Settings.LastSelectedGameId;
            if (!string.IsNullOrWhiteSpace(defaultId))
            {
                restored = choices.FirstOrDefault(choice =>
                    string.Equals(choice.Instance.GameId, defaultId, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (restored is not null)
        {
            SelectGame(restored, force: true);
        }
        else if (choices.Count > 0)
        {
            SelectGame(choices[0], force: true);
        }
        else
        {
            ActiveGame = null;
            RebuildPageList();
        }
    }

    /// <summary>Switches the active game and rebuilds the page list for its capabilities.</summary>
    /// <param name="choice">Game to activate.</param>
    public void SelectGame(GameChoice choice) => SelectGame(choice, force: false);

    /// <summary>Sets the status bar text from a page.</summary>
    /// <param name="message">Message to show.</param>
    public void SetStatus(string message) => StatusMessage = message;

    /// <summary>Reports a failure to the user without an exception dialog.</summary>
    /// <param name="message">Message to show.</param>
    public void ReportError(string message)
    {
        StatusMessage = message;
        _host.Logger.Warn("Ui", message);
    }

    /// <summary>Navigates to a page by its display name, when it is currently applicable.</summary>
    /// <param name="pageName">Display name of the target page.</param>
    public void NavigateTo(string pageName)
    {
        var target = Pages.FirstOrDefault(page => string.Equals(page.Name, pageName, StringComparison.Ordinal));
        if (target is not null)
        {
            SelectedPage = target;
        }
    }

    /// <summary>Routes dropped archive files to the Mods page for batch import.</summary>
    /// <param name="archivePaths">Archive files dropped onto the window.</param>
    public async Task ImportArchivesAsync(IReadOnlyList<string> archivePaths)
    {
        ArgumentNullException.ThrowIfNull(archivePaths);

        if (archivePaths.Count == 0)
        {
            return;
        }

        if (ActiveGame is null)
        {
            StatusMessage = "请先在“游戏”页添加一个游戏，然后再拖入 Mod 压缩包。";
            return;
        }

        NavigateTo("本地 Mod");

        if (ActivePage is ModsViewModel mods)
        {
            await mods.BatchImportAsync(archivePaths).ConfigureAwait(true);
        }
    }

    /// <summary>Switches the active game.</summary>
    /// <param name="choice">Game to activate.</param>
    /// <param name="force">True to re-apply even when the id is unchanged.</param>
    private void SelectGame(GameChoice choice, bool force)
    {
        if (!force && _activeGame is not null
            && string.Equals(_activeGame.Instance.GameId, choice.Instance.GameId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ActiveGame = choice;
        RebuildPageList();

        // The selection is the app's "default game": persisted so the next start opens on the same
        // game instead of silently falling back to the first one in the list.
        if (!string.Equals(_host.Settings.Settings.LastSelectedGameId, choice.Instance.GameId, StringComparison.Ordinal))
        {
            _host.Settings.Settings = _host.Settings.Settings with { LastSelectedGameId = choice.Instance.GameId };
            _host.SaveSettings();
        }

        // Keep the user on the same page across a game switch when the new game supports it, which is the
        // common case while comparing two games' setups.
        var target = Pages.FirstOrDefault(page => page.Name == _selectedPage?.Name) ?? Pages.FirstOrDefault();
        if (target is not null)
        {
            SelectedPage = target;
        }
    }

    /// <summary>Rebuilds the navigation list for the active game's capabilities.</summary>
    private void RebuildPageList()
    {
        var capabilities = _activeGame?.Definition.Capabilities ?? GameCapability.None;

        var applicable = new List<PageDescriptor>();
        foreach (var page in _allPages)
        {
            // Capability gating, applied as a real filter rather than a disabled state. A game with no
            // Profiles capability has no Profiles page at all.
            if (page.Required is GameCapability.None || capabilities.Has(page.Required))
            {
                applicable.Add(page);
            }
        }

        Pages.ReplaceAll(applicable);

        // Grouped view for the sidebar: sections in a fixed reading order, empty ones dropped. An empty
        // caption means "no header" — the overview stands alone at the top rather than under a heading
        // that would just repeat its own name.
        var groups = new List<PageGroup>();
        foreach (var section in new[] { string.Empty, "游戏管理", "Mod 管理", "管理", "设置" })
        {
            var members = applicable.Where(page => string.Equals(page.Section, section, StringComparison.Ordinal)).ToList();
            if (members.Count > 0)
            {
                groups.Add(new PageGroup(section, members));
            }
        }

        PageGroups.ReplaceAll(groups);
    }

    /// <summary>Activates a page, reusing its existing view-model when the instance matches.</summary>
    /// <param name="descriptor">Page to activate.</param>
    private void Activate(PageDescriptor descriptor)
    {
        // Reuse only when the type matches, so switching games rebuilds a page's data but switching pages
        // does not throw away scroll position and selection state.
        if (ActivePage is not null
            && ActivePage.GetType() == descriptor.Factory().GetType()
            && ActivePage is IPageViewModel existing)
        {
            _ = existing.RefreshAsync();
            ActivePage = existing;
            return;
        }

        var viewModel = descriptor.Factory();
        ActivePage = viewModel;

        if (viewModel is IPageViewModel page)
        {
            _ = page.RefreshAsync();
        }
    }

    /// <summary>
    /// Reloads the game list and whichever page is on screen.
    /// </summary>
    /// <remarks>
    /// This is what the toolbar's Refresh button runs, so it has to be safe at any moment: it only re-reads
    /// state from the engine, and never mutates anything.
    /// </remarks>
    /// <returns>A task that completes once the current page has reloaded.</returns>
    private async Task RefreshAsync()
    {
        ReloadGames();

        if (ActivePage is IPageViewModel page)
        {
            await page.RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Selects a page and notifies the navigation list.</summary>
    /// <param name="descriptor">Page to select.</param>
    private void Select(PageDescriptor descriptor)
    {
        SelectedPage = descriptor;
    }
}

/// <summary>
/// A navigable page: its label, why it exists, and the capability it requires.
/// </summary>
/// <param name="Name">Label shown in the sidebar.</param>
/// <param name="Section">Sidebar group the page belongs to.</param>
/// <param name="Description">Tooltip.</param>
/// <param name="Required">Capability the active game must declare, or None.</param>
/// <param name="Factory">Creates the page's view-model.</param>
public sealed record PageDescriptor(
    string Name,
    string Section,
    string Description,
    GameCapability Required,
    Func<object> Factory);

/// <summary>A sidebar section: its caption plus the pages filed under it.</summary>
/// <param name="Title">Section caption.</param>
/// <param name="Pages">Pages in the section.</param>
public sealed record PageGroup(string Title, IReadOnlyList<PageDescriptor> Pages);

/// <summary>A page that can reload itself from the engine.</summary>
public interface IPageViewModel
{
    /// <summary>Reloads the page's data.</summary>
    Task RefreshAsync();
}

/// <summary>A game in the header selector.</summary>
public sealed class GameChoice
{
    /// <summary>Creates a choice.</summary>
    /// <param name="instance">Registered game.</param>
    /// <param name="definition">Its definition.</param>
    public GameChoice(GameInstance instance, GameDefinition definition)
    {
        Instance = instance;
        Definition = definition;
    }

    /// <summary>Registered game.</summary>
    public GameInstance Instance { get; }

    /// <summary>Definition, supplying the capabilities.</summary>
    public GameDefinition Definition { get; }

    /// <summary>Display name.</summary>
    public string Name => Definition.Name;

    /// <summary>Install directory.</summary>
    public string InstallPath => Instance.InstallPath;

    /// <summary>Mod root.</summary>
    public string ModRootPath => Instance.ModRootPath;

    /// <summary>Comma-separated capability names, shown so the gating is explainable.</summary>
    public string DescribeCapabilities()
    {
        if (Definition.Capabilities == GameCapability.None)
        {
            return "无额外能力";
        }

        var names = new List<string>();
        foreach (GameCapability flag in Enum.GetValues<GameCapability>())
        {
            if (flag != GameCapability.None && Definition.Capabilities.Has(flag))
            {
                names.Add(flag.Describe());
            }
        }

        return string.Join(" · ", names);
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}
