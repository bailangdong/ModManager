using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Media;
using ModManager.Core;
using ModManager.Core.Safety;
using ModManager.Core.Services;

namespace ModManager.App.ViewModels;

/// <summary>
/// The online Mod browser: search Nexus Mods for the active game, show what is installed, and install
/// or update through the existing pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This page adds no network code of its own. Every request goes through <see cref="INexusProvider"/>,
/// which already enforces the API's rules (documented endpoints only, rate limiting, no bypass of premium
/// or permission gates), and every install goes through <see cref="IModInstallService"/>, which owns the
/// validate → conflict-check → journal → backup → deploy → verify pipeline.
/// </para>
/// <para>
/// The honesty rule the page is built around: when Nexus or the mod's author does not permit a
/// manager-driven download, the page says so and offers the real page instead of a button that would
/// fail. There is no code path here that reports success without having installed anything.
/// </para>
/// </remarks>
public sealed class OnlineModsViewModel : ViewModelBase, IPageViewModel
{
    private readonly ApplicationHost _host;
    private readonly CoverImageLoader _covers;
    private readonly ShellViewModel? _shell;
    private readonly DependencyResolver _resolver = new();
    private readonly INexusLibraryLinker _linker;

    private string _searchText = string.Empty;
    private string _status = "选择游戏后即可浏览在线 Mod。";
    private string _sortOption = "按推荐度";
    private bool _isBusy;
    private bool _includeAdult;
    private bool _hasSearched;
    private bool _hasError;
    private string _errorText = string.Empty;
    private bool _needsSetup;
    private string _setupText = string.Empty;
    private int _page = 1;
    private int _totalCount;
    private bool _hasNextPage;
    private OnlineModRow? _selectedMod;
    private string _detailDescription = string.Empty;
    private string _translatedTitleText = string.Empty;
    private string _translatedSummaryText = string.Empty;
    private string _translatedDescriptionText = string.Empty;
    private string _translationStatus = string.Empty;

    /// <summary>
    /// True while the detail pane shows the original English instead of the translation. The original is
    /// always kept, so this is only a view switch and never destroys the translation.
    /// </summary>
    private bool _showSource;

    private string _detailHtmlNote = string.Empty;
    private string _downloadStatusText = string.Empty;
    private double _downloadProgress;
    private bool _isDownloading;

    /// <summary>Creates the page.</summary>
    /// <param name="host">Composition root.</param>
    /// <param name="shell">Shell, for the active game.</param>
    public OnlineModsViewModel(ApplicationHost host, ShellViewModel? shell = null)
    {
        _host = host;
        _shell = shell;
        _linker = new NexusLibraryLinker(host.Nexus);
        _covers = new CoverImageLoader(host.Images);

        SortOptions.Add("按推荐度");
        SortOptions.Add("按下载量");
        SortOptions.Add("按最近更新");
        SortOptions.Add("按名称");

        SearchCommand = new RelayCommand(() => SearchAsync(1), () => CanSearch);
        RefreshCommand = new RelayCommand(() => SearchAsync(_page), () => CanSearch);
        NextPageCommand = new RelayCommand(() => SearchAsync(_page + 1), () => CanSearch && HasNextPage);
        PreviousPageCommand = new RelayCommand(() => SearchAsync(_page - 1), () => CanSearch && _page > 1);
        OpenPageCommand = new RelayCommand<OnlineModRow>(OpenPage, row => row?.PageUrl is { Length: > 0 });
        OpenSettingsCommand = new RelayCommand(() =>
        {
            _shell?.NavigateTo("设置");
            return Task.CompletedTask;
        });
        OpenDomainPageCommand = new RelayCommand(() => OpenUrl(DomainPageUrl));
        TranslateCommand = new RelayCommand(() => TranslateSelectedAsync());
        TranslateTitleCommand = new RelayCommand(() => TranslateTitleAsync(), () => CanTranslate && HasSelection);
        TranslateDescriptionCommand = new RelayCommand(
            () => TranslateDescriptionAsync(),
            () => CanTranslate && HasSelection);
        ToggleSourceCommand = new RelayCommand(() => ToggleSource(), () => HasTranslation);
        TranslatePageCommand = new RelayCommand(() => TranslatePageTitlesAsync());
        InstallCommand = new RelayCommand<OnlineModRow>(row => InstallAsync(row), row => row is not null && row.CanDownloadViaManager);
        UninstallCommand = new RelayCommand<OnlineModRow>(row => UninstallAsync(row), row => row?.LocalModId is { Length: > 0 });
        InstallDependenciesCommand = new RelayCommand(() => InstallMissingDependenciesAsync(), () => HasMissingDependencies);

        _ = RefreshAsync();
    }

    /// <summary>Search results.</summary>
    public BulkObservableCollection<OnlineModRow> Results { get; } = [];

    /// <summary>Files of the selected mod.</summary>
    public BulkObservableCollection<OnlineModFileRow> DetailFiles { get; } = [];

    /// <summary>Dependency tree of the selected mod.</summary>
    public BulkObservableCollection<DependencyNodeRow> DetailDependencies { get; } = [];

    /// <summary>Available sort orders.</summary>
    public BulkObservableCollection<string> SortOptions { get; } = [];

    /// <summary>Search the current game's domain.</summary>
    public RelayCommand SearchCommand { get; }

    /// <summary>Re-run the current page.</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>Next page of results.</summary>
    public RelayCommand NextPageCommand { get; }

    /// <summary>Previous page of results.</summary>
    public RelayCommand PreviousPageCommand { get; }

    /// <summary>Open the mod's page on nexusmods.com.</summary>
    public RelayCommand<OnlineModRow> OpenPageCommand { get; }

    /// <summary>Jump to Settings, where the API key is configured.</summary>
    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>Open the active game's Nexus domain page in the browser.</summary>
    public RelayCommand OpenDomainPageCommand { get; }

    /// <summary>Translate the selected mod into Chinese.</summary>
    public RelayCommand TranslateCommand { get; }

    /// <summary>Translate the titles of the listed mods into Chinese.</summary>
    public RelayCommand TranslatePageCommand { get; }

    /// <summary>Download and install the selected online mod through the normal pipeline.</summary>
    public RelayCommand<OnlineModRow> InstallCommand { get; }

    /// <summary>Uninstall the local mod associated with the selection.</summary>
    public RelayCommand<OnlineModRow> UninstallCommand { get; }

    /// <summary>Install every missing dependency of the selected mod, after confirmation.</summary>
    public RelayCommand InstallDependenciesCommand { get; }

    /// <summary>Search text.</summary>
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    /// <summary>Selected sort order.</summary>
    public string SortOption
    {
        get => _sortOption;
        set
        {
            if (SetProperty(ref _sortOption, value))
            {
                _ = SearchAsync(1);
            }
        }
    }

    /// <summary>Include adult content. Off by default and never remembered as on.</summary>
    public bool IncludeAdult
    {
        get => _includeAdult;
        set
        {
            if (SetProperty(ref _includeAdult, value))
            {
                _ = SearchAsync(1);
            }
        }
    }

    /// <summary>True while a request is in flight.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanInteract));
            }
        }
    }

    /// <summary>Status line under the page title.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>True when the last request failed.</summary>
    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    /// <summary>Failure text, already phrased for the user.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>True when the page cannot work until the user configures something.</summary>
    public bool NeedsSetup
    {
        get => _needsSetup;
        private set => SetProperty(ref _needsSetup, value);
    }

    /// <summary>True when the fix is in this application's Nexus settings.</summary>
    public bool ErrorNeedsSettings { get; private set; }

    /// <summary>True when the action has to be completed on the Nexus website.</summary>
    public bool ErrorNeedsNexusPage { get; private set; }

    /// <summary>Domain page to open when the user must finish on the website.</summary>
    public string DomainPageUrl => HasDomain
        ? $"https://www.nexusmods.com/{GameDomain}"
        : "https://www.nexusmods.com";

    /// <summary>What to configure, in Chinese.</summary>
    public string SetupText
    {
        get => _setupText;
        private set => SetProperty(ref _setupText, value);
    }

    /// <summary>True when a search has been attempted, so "no results" can be distinguished from "nothing yet".</summary>
    public bool HasSearched
    {
        get => _hasSearched;
        private set => SetProperty(ref _hasSearched, value);
    }

    /// <summary>True when the current page returned nothing.</summary>
    public bool IsEmpty => _hasSearched && Results.Count == 0 && !HasError;

    /// <summary>True when a search can be attempted.</summary>
    public bool CanSearch =>
        !IsBusy
        && _shell?.ActiveGame is not null
        && !string.IsNullOrWhiteSpace(GameDomain);

    /// <summary>True when the user may interact with the result list.</summary>
    public bool CanInteract => !IsBusy;

    /// <summary>Result count summary.</summary>
    public string ResultSummary => _totalCount > 0
        ? $"共 {_totalCount} 个结果 · 第 {_page} 页"
        : Results.Count > 0 ? $"第 {_page} 页 · {Results.Count} 个结果" : string.Empty;

    /// <summary>Current page number.</summary>
    public int Page => _page;

    /// <summary>True when more results exist.</summary>
    public bool HasNextPage
    {
        get => _hasNextPage;
        private set => SetProperty(ref _hasNextPage, value);
    }

    /// <summary>Nexus domain of the active game.</summary>
    public string GameDomain => _shell?.ActiveGame?.Definition.NexusDomain ?? string.Empty;

    /// <summary>Name of the active game.</summary>
    public string GameName => _shell?.ActiveGame?.Name ?? "未选择游戏";

    /// <summary>True when browsing is possible at all for this game.</summary>
    public bool HasDomain => !string.IsNullOrWhiteSpace(GameDomain);

    /// <summary>The selected result.</summary>
    public OnlineModRow? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetProperty(ref _selectedMod, value) && value is not null)
            {
                _ = LoadDetailAsync(value);
            }
        }
    }

    /// <summary>True when a result is selected.</summary>
    public bool HasSelection => _selectedMod is not null;

    /// <summary>Description of the selected mod, converted from the API's HTML.</summary>
    public string DetailDescription
    {
        get => _detailDescription;
        private set => SetProperty(ref _detailDescription, value);
    }

    /// <summary>Translated title of the selected mod.</summary>
    public string TranslatedTitleText => _translatedTitleText;

    /// <summary>Translated summary of the selected mod.</summary>
    public string TranslatedSummaryText => _translatedSummaryText;

    /// <summary>Translated description of the selected mod.</summary>
    public string TranslatedDescriptionText => _translatedDescriptionText;

    /// <summary>True when a translation is available for the selected mod.</summary>
    public bool HasTranslation => _translatedTitleText.Length > 0 || _translatedDescriptionText.Length > 0;

    /// <summary>True when the selected mod has a Chinese title.</summary>
    public bool HasTranslatedTitle => _translatedTitleText.Length > 0;

    /// <summary>True when the selected mod has a Chinese description.</summary>
    public bool HasTranslatedDescription => _translatedDescriptionText.Length > 0;

    /// <summary>
    /// True while the detail pane shows the original English.
    /// </summary>
    /// <remarks>
    /// The requirement is to keep the original and let the user compare, so switching views never discards
    /// anything: both texts stay in memory and the switch only decides which one is rendered.
    /// </remarks>
    public bool ShowSource
    {
        get => _showSource;
        private set
        {
            if (SetProperty(ref _showSource, value))
            {
                OnPropertyChanged(nameof(SourceToggleText));
                OnPropertyChanged(nameof(DetailTitleText));
                OnPropertyChanged(nameof(DetailBodyText));
            }
        }
    }

    /// <summary>Label for the original/translation switch.</summary>
    public string SourceToggleText => _showSource ? "查看中文" : "查看原文";

    /// <summary>Title rendered in the detail pane, honouring the original/translation switch.</summary>
    /// <remarks>
    /// Reads the selected row rather than the pane's own copy, so a page-wide translation reaches the detail
    /// header too. The pane copy is only filled when this mod's own translate command runs; without this the
    /// user could translate the whole page and still see an English title in the pane.
    /// </remarks>
    public string DetailTitleText =>
        !_showSource && _selectedMod is { HasTranslatedTitle: true } mod
            ? mod.DisplayTitle
            : (_selectedMod?.Name ?? string.Empty);

    /// <summary>Body rendered in the detail pane, honouring the original/translation switch.</summary>
    public string DetailBodyText =>
        !_showSource && HasTranslatedDescription ? _translatedDescriptionText : _detailDescription;

    /// <summary>True when either a local engine or an online key can translate.</summary>
    public bool CanTranslate => _host.Translation.IsAvailable;

    /// <summary>Translates only the selected mod's title.</summary>
    public RelayCommand TranslateTitleCommand { get; }

    /// <summary>Translates only the selected mod's summary and description.</summary>
    public RelayCommand TranslateDescriptionCommand { get; }

    /// <summary>Switches the detail pane between the original text and the Chinese translation.</summary>
    public RelayCommand ToggleSourceCommand { get; }

    /// <summary>Progress, cache notice or error for translation.</summary>
    public string TranslationStatus
    {
        get => _translationStatus;
        private set => SetProperty(ref _translationStatus, value);
    }

    /// <summary>Notes about information the API did not provide.</summary>
    public string DetailNote
    {
        get => _detailHtmlNote;
        private set => SetProperty(ref _detailHtmlNote, value);
    }

    /// <summary>Progress text while a download runs.</summary>
    public string DownloadStatusText
    {
        get => _downloadStatusText;
        private set => SetProperty(ref _downloadStatusText, value);
    }

    /// <summary>Download completion 0..100.</summary>
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    /// <summary>True while a download is running.</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                OnPropertyChanged(nameof(CanInteract));
            }
        }
    }

    /// <summary>True when the selected mod has missing dependencies that could be installed.</summary>
    public bool HasMissingDependencies =>
        _selectedMod is not null && DetailDependencies.Any(row => row.IsMissing);

    /// <summary>Summary of the dependency tree.</summary>
    public string DependencySummary => _selectedMod is null
        ? string.Empty
        : _resolver.ResolveNexusReferences(_selectedMod.Name, _selectedMod.Requirements, InstalledMods()).Summary;

    private IReadOnlyList<ModMetadata> InstalledMods() =>
        _shell?.ActiveGame is { } game ? _host.Library.ModsForGame(game.Instance.GameId) : [];

    /// <inheritdoc />
    public Task RefreshAsync()
    {
        OnPropertyChanged(nameof(GameName));
        OnPropertyChanged(nameof(GameDomain));
        OnPropertyChanged(nameof(HasDomain));

        if (_shell?.ActiveGame is null)
        {
            Status = "还没有选择游戏。请先在“我的游戏”页添加并选择一个游戏。";
            NeedsSetup = false;
            Results.ReplaceAll([]);
            return Task.CompletedTask;
        }

        if (!HasDomain)
        {
            // A game with no Nexus domain simply has nothing to browse. Saying so is better than an
            // empty list that looks like a failed request.
            Status = $"“{GameName}” 没有配置 Nexus 域名，无法浏览在线 Mod。";
            NeedsSetup = false;
            Results.ReplaceAll([]);
            return Task.CompletedTask;
        }

        if (!_host.Nexus.IsAuthenticated)
        {
            NeedsSetup = true;
            SetupText = "尚未配置 Nexus API 密钥。请在“设置 → Nexus”中填入你自己的 API 密钥；"
                + "配置后即可搜索、查看详情并通过管理器下载。未配置时也可以在浏览器中打开 Nexus 页面手动下载。";
            Status = "未配置 Nexus API 密钥。";
            return Task.CompletedTask;
        }

        NeedsSetup = false;
        return SearchAsync(1);
    }

    /// <summary>Runs a search against the active game's domain.</summary>
    /// <param name="page">Page to request, 1-based.</param>
    private async Task SearchAsync(int page)
    {
        if (page < 1 || _shell?.ActiveGame is null || !HasDomain)
        {
            return;
        }

        IsBusy = true;
        HasError = false;
        ErrorText = string.Empty;
        Status = page == 1 ? "正在搜索 Mod…" : $"正在加载第 {page} 页…";

        try
        {
            var query = new NexusSearchQuery
            {
                GameDomain = GameDomain,
                SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                SortField = MapSortField(SortOption),
                SortDirection = SortOption == "按名称" ? "asc" : "desc",
                Page = page,
                PageSize = 20,
                IncludeAdult = IncludeAdult,
            };

            var result = await _host.Nexus.SearchModsAsync(query).ConfigureAwait(true);
            HasSearched = true;

            if (!result.Succeeded || result.Value is null)
            {
                Results.ReplaceAll([]);
                ApplyFailure(result.Error);
                Status = "搜索失败。";
                return;
            }

            var installed = InstalledMods();
            var rows = new List<OnlineModRow>();
            foreach (var summary in result.Value.Items)
            {
                var local = _linker.FindInstalled(installed, summary.ModId)
                    ?? installed.FirstOrDefault(mod =>
                        string.Equals(mod.Name, summary.Name, StringComparison.OrdinalIgnoreCase));

                rows.Add(new OnlineModRow(
                    summary,
                    local,
                    OnlineModActionPlan.Evaluate(summary, local, _host.Nexus.IsAuthenticated)));
            }

            Results.ReplaceAll(rows);
            _page = result.Value.Page;
            _totalCount = result.Value.TotalCount ?? 0;
            HasNextPage = result.Value.HasNextPage;

            // Covers load after the rows are visible, so the list appears immediately.
            _ = LoadCoversAsync(rows);

            Status = rows.Count == 0
                ? "没有找到匹配的 Mod。"
                : $"找到 {(_totalCount > 0 ? _totalCount.ToString() : rows.Count.ToString())} 个 Mod，"
                    + $"其中 {rows.Count(row => row.IsInstalled)} 个已在本地。"
                    + (result.Value.KeywordFilteredLocally
                        ? "（关键词由本程序在 Nexus 返回的列表中本地筛选：v1 接口没有搜索端点）"
                        : string.Empty);

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(Page));
            NextPageCommand.RaiseCanExecuteChanged();
            PreviousPageCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            ApplyFailure(ex.Message);
            Status = "搜索失败。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Translates the selected mod's title, summary and description into Chinese.
    /// </summary>
    /// <remarks>
    /// The translation is cached by source text, so opening the same mod again is instant. When no key is
    /// configured the answer says exactly where to put one instead of failing silently.
    /// </remarks>
    private async Task TranslateSelectedAsync()
    {
        if (SelectedMod is not { } row)
        {
            return;
        }

        if (!CanTranslate)
        {
            TranslationStatus = "没有可用的翻译引擎：请在“设置 → 翻译”初始化本地引擎，或填入在线 API 密钥。";
            return;
        }

        TranslationStatus = "正在翻译…";

        var result = await _host.Translation
            .TranslateAsync(
                $"{GameDomain}-{row.ModId}",
                row.Name,
                row.Summary.Summary,
                DetailDescription)
            .ConfigureAwait(true);

        if (!result.Succeeded || result.Value is null)
        {
            TranslationStatus = result.Error ?? "翻译失败。";
            return;
        }

        SetTranslated(
            result.Value.Title,
            result.Value.Summary,
            result.Value.Description);

        row.TranslatedTitle = result.Value.Title;
        ShowSource = false;
        TranslationStatus = result.Value.FromCache
            ? $"已使用本地翻译缓存（{result.Value.Model}）。"
            : $"翻译完成（{result.Value.Model}）。";

        _host.Logger.Info("Translate", $"已翻译 Mod {row.ModId}。");
    }

    /// <summary>Translates only the selected mod's title.</summary>
    /// <remarks>
    /// A separate action because a title is the one thing a user scans a list for, and translating twenty
    /// titles must not drag twenty descriptions through the engine. The cache key is per field, so a later
    /// description translation cannot invalidate the title.
    /// </remarks>
    private async Task TranslateTitleAsync()
    {
        if (SelectedMod is not { } row)
        {
            return;
        }

        if (!CanTranslate)
        {
            TranslationStatus = "没有可用的翻译引擎：请在“设置 → 翻译”初始化本地引擎，或填入在线 API 密钥。";
            return;
        }

        TranslationStatus = "正在翻译标题…";

        var result = await _host.Translation
            .TranslateAsync($"{GameDomain}-{row.ModId}-title", row.Name, null, null)
            .ConfigureAwait(true);

        if (!result.Succeeded || result.Value is null)
        {
            TranslationStatus = result.Error ?? "标题翻译失败。";
            return;
        }

        _translatedTitleText = result.Value.Title;
        row.TranslatedTitle = result.Value.Title;

        OnPropertyChanged(nameof(TranslatedTitleText));
        OnPropertyChanged(nameof(HasTranslatedTitle));
        OnPropertyChanged(nameof(HasTranslation));
        OnPropertyChanged(nameof(DetailTitleText));
        ToggleSourceCommand.RaiseCanExecuteChanged();

        ShowSource = false;
        TranslationStatus = result.Value.FromCache
            ? $"标题来自缓存（{result.Value.Model}）。"
            : $"标题翻译完成（{result.Value.Model}）。";
    }

    /// <summary>Translates the selected mod's summary and description, keeping the Markdown structure.</summary>
    private async Task TranslateDescriptionAsync()
    {
        if (SelectedMod is not { } row)
        {
            return;
        }

        if (!CanTranslate)
        {
            TranslationStatus = "没有可用的翻译引擎：请在“设置 → 翻译”初始化本地引擎，或填入在线 API 密钥。";
            return;
        }

        if (DetailDescription.Length == 0)
        {
            TranslationStatus = "这个 Mod 没有可翻译的描述。";
            return;
        }

        TranslationStatus = "正在翻译描述…";

        var result = await _host.Translation
            .TranslateAsync(
                $"{GameDomain}-{row.ModId}-description",
                row.Name,
                row.Summary.Summary,
                DetailDescription)
            .ConfigureAwait(true);

        if (!result.Succeeded || result.Value is null)
        {
            TranslationStatus = result.Error ?? "描述翻译失败。";
            return;
        }

        _translatedSummaryText = result.Value.Summary ?? string.Empty;
        _translatedDescriptionText = result.Value.Description ?? string.Empty;

        // Push the summary back onto the row so the list turns Chinese as well. Translating a description used
        // to improve only the detail pane's own copy, which is precisely why the list stayed English after a
        // successful translation.
        row.TranslatedSummary = result.Value.Summary;

        // The description request carries the title too; taking it here saves a second round trip when the
        // user asks for the description first.
        if (!HasTranslatedTitle && result.Value.Title.Length > 0)
        {
            _translatedTitleText = result.Value.Title;
            row.TranslatedTitle = result.Value.Title;
            OnPropertyChanged(nameof(TranslatedTitleText));
            OnPropertyChanged(nameof(HasTranslatedTitle));
        }

        OnPropertyChanged(nameof(TranslatedSummaryText));
        OnPropertyChanged(nameof(TranslatedDescriptionText));
        OnPropertyChanged(nameof(HasTranslatedDescription));
        OnPropertyChanged(nameof(HasTranslation));
        ToggleSourceCommand.RaiseCanExecuteChanged();

        ShowSource = false;
        TranslationStatus = result.Value.FromCache
            ? $"描述来自缓存（{result.Value.Model}）。"
            : $"描述翻译完成（{result.Value.Model}）。";
    }

    /// <summary>Flips the detail pane between the original English and the Chinese translation.</summary>
    private void ToggleSource()
    {
        ShowSource = !ShowSource;
        TranslationStatus = ShowSource ? "正在显示原文。" : "正在显示中文译文。";
    }

    /// <summary>
    /// Translates the titles of the mods currently listed, one at a time and from cache when possible.
    /// </summary>
    private async Task TranslatePageTitlesAsync()
    {
        if (!CanTranslate)
        {
            TranslationStatus = "没有可用的翻译引擎：请在“设置 → 翻译”初始化本地引擎，或填入在线 API 密钥。";
            return;
        }

        var rows = Results.Where(row => !row.HasTranslatedTitle).ToList();
        if (rows.Count == 0)
        {
            TranslationStatus = "本页内容都已有中文翻译。";
            return;
        }

        var done = 0;
        foreach (var row in rows)
        {
            TranslationStatus = $"正在翻译 {done + 1}/{rows.Count}…";

            // Title and summary in one request. The row shows both lines, and a page of Chinese names above
            // English blurbs was exactly the half-translated state this pass exists to remove.
            var result = await _host.Translation
                .TranslateAsync(
                    $"{GameDomain}-{row.ModId}-full",
                    row.Name,
                    row.Summary.Summary,
                    null)
                .ConfigureAwait(true);

            if (result.Succeeded && result.Value is not null)
            {
                row.TranslatedTitle = result.Value.Title;

                if (!string.IsNullOrWhiteSpace(result.Value.Summary))
                {
                    row.TranslatedSummary = result.Value.Summary;
                }

                done++;
            }
            else
            {
                TranslationStatus = result.Error ?? "翻译失败。";
                return;
            }
        }

        TranslationStatus = $"已翻译 {done} 个条目的标题与简介（结果会缓存，下次直接读取）。";

        // The detail header reads the selected row, so a page-wide translation has to re-announce it.
        OnPropertyChanged(nameof(DetailTitleText));
    }

    /// <summary>Applies a translation to the detail pane.</summary>
    /// <param name="title">Translated title.</param>
    /// <param name="summary">Translated summary.</param>
    /// <param name="description">Translated description.</param>
    private void SetTranslated(string title, string? summary, string? description)
    {
        _translatedTitleText = title;
        _translatedSummaryText = summary ?? string.Empty;
        _translatedDescriptionText = description ?? string.Empty;

        OnPropertyChanged(nameof(TranslatedTitleText));
        OnPropertyChanged(nameof(TranslatedSummaryText));
        OnPropertyChanged(nameof(TranslatedDescriptionText));
        OnPropertyChanged(nameof(HasTranslation));
        OnPropertyChanged(nameof(HasTranslatedTitle));
        OnPropertyChanged(nameof(HasTranslatedDescription));

        // A fresh translation is what the user asked to see, so the pane switches to it. The original is
        // still one click away.
        ShowSource = false;
        ToggleSourceCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Loads detail, files and the dependency tree for a result.</summary>
    /// <param name="row">Selected row.</param>
    private async Task LoadDetailAsync(OnlineModRow row)
    {
        DetailDescription = row.SummaryText;
        DetailNote = string.Empty;
        DetailFiles.ReplaceAll([]);
        DetailDependencies.ReplaceAll([]);
        _translatedTitleText = string.Empty;
        _translatedSummaryText = string.Empty;
        _translatedDescriptionText = string.Empty;
        TranslationStatus = CanTranslate
            ? string.Empty
            : "没有可用的翻译引擎：请在“设置 → 翻译”初始化本地引擎（免费，无需密钥），或填入在线 API 密钥。";
        OnPropertyChanged(nameof(TranslatedTitleText));
        OnPropertyChanged(nameof(TranslatedSummaryText));
        OnPropertyChanged(nameof(TranslatedDescriptionText));
        OnPropertyChanged(nameof(HasTranslation));
        OnPropertyChanged(nameof(HasTranslatedTitle));
        OnPropertyChanged(nameof(HasTranslatedDescription));
        OnPropertyChanged(nameof(DetailTitleText));
        OnPropertyChanged(nameof(DetailBodyText));
        ShowSource = false;
        ToggleSourceCommand.RaiseCanExecuteChanged();
        TranslateTitleCommand.RaiseCanExecuteChanged();
        TranslateDescriptionCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasMissingDependencies));
        OnPropertyChanged(nameof(DependencySummary));
        InstallDependenciesCommand.RaiseCanExecuteChanged();

        var preview = _resolver.ResolveNexusReferences(row.Name, row.Requirements, InstalledMods());
        DetailDependencies.ReplaceAll(Flatten(preview.Roots, 0));

        IsBusy = true;
        try
        {
            var detail = await _host.Nexus.GetModDetailAsync(GameDomain, row.ModId).ConfigureAwait(true);
            if (detail.Succeeded && detail.Value is not null)
            {
                DetailDescription = PlainText(detail.Value.Description) is { Length: > 0 } text
                    ? text
                    : (detail.Value.Summary.Summary ?? row.SummaryText);

                DetailNote = detail.Value.Tags.Count > 0
                    ? "标签：" + string.Join(" · ", detail.Value.Tags)
                    : string.Empty;

                DetailFiles.ReplaceAll(detail.Value.Files
                    .OrderByDescending(file => file.IsPrimary)
                    .ThenByDescending(file => file.UploadedAt ?? DateTimeOffset.MinValue)
                    .Select(file => new OnlineModFileRow(file)));

                DetailDependencies.ReplaceAll(Flatten(
                    _resolver.ResolveNexusReferences(row.Name, detail.Value.Requirements, InstalledMods()).Roots,
                    0));
            }
            else
            {
                DetailNote = DescribeFailure(detail.Error ?? "无法加载详情。");
            }

            var files = await _host.Nexus.GetModFilesAsync(GameDomain, row.ModId).ConfigureAwait(true);
            if (files.Succeeded && files.Value is not null && files.Value.Count > 0 && DetailFiles.Count == 0)
            {
                DetailFiles.ReplaceAll(files.Value.Select(file => new OnlineModFileRow(file)));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            DetailNote = DescribeFailure(ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasMissingDependencies));
            OnPropertyChanged(nameof(DependencySummary));
            InstallDependenciesCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Opens the mod's Nexus page in the default browser.</summary>
    /// <param name="row">Row to open.</param>
    private Task OpenPage(OnlineModRow? row)
    {
        if (row?.PageUrl is not { Length: > 0 } url)
        {
            return Task.CompletedTask;
        }

        OpenUrl(url);
        return Task.CompletedTask;
    }

    /// <summary>Opens a URL in the default browser, reporting failure instead of throwing.</summary>
    /// <param name="url">Absolute URL.</param>
    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            Status = "已在浏览器中打开 Nexus 页面。";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = "无法打开浏览器： " + ex.Message;
        }
    }

    /// <summary>
    /// Downloads one online mod into the local package library. It deliberately does not stage or
    /// deploy: installation is a separate, explicit action in the library page.
    /// </summary>
    /// <param name="row">Row to download.</param>
    private async Task InstallAsync(OnlineModRow? row)
    {
        if (row is null || _shell?.ActiveGame is not { } game)
        {
            return;
        }

        if (!row.CanDownloadViaManager)
        {
            // The plan already explained why; repeating it in the status line keeps the reason visible
            // after the click instead of leaving the button looking broken.
            Status = row.BlockingReason ?? "该 Mod 需要前往 Nexus 页面手动下载。";
            return;
        }

        IsBusy = true;
        HasError = false;
        try
        {
            var file = await PickFileAsync(row).ConfigureAwait(true);
            if (file is null)
            {
                Status = "该 Mod 没有可下载的文件。";
                return;
            }

            Status = $"正在向 Nexus 申请下载链接：{row.Name}…";
            var link = await _host.Nexus
                .GetDownloadLinkAsync(GameDomain, row.ModId, file.FileId)
                .ConfigureAwait(true);

            if (!link.Succeeded || link.Value is null)
            {
                ApplyFailure(link.Error);
                Status = "无法下载。";
                return;
            }

            if (link.Value.RequiresPremium)
            {
                HasError = true;
                ErrorText = "该文件需要 Nexus 高级会员。请前往 Nexus 页面下载后，用“从压缩包安装…”导入。";
                Status = "需要高级会员，已停止。";
                return;
            }

            var record = await _host.Downloads
                .EnqueueAsync(new DownloadRequest
                {
                    Url = link.Value.Url,
                    FileName = link.Value.FileName,
                    GameId = game.Instance.GameId,
                    NexusModId = row.ModId,
                    NexusFileId = file.FileId,
                    ModNameHint = row.Name,
                    CoverImageUrl = row.PictureUrl,
                    Headers = _host.Nexus.GetDownloadHeaders(),
                    PackageSource = new PackageSourceRef
                    {
                        Platform = PackagePlatforms.NexusMods,
                        GameKey = game.Instance.GameId,
                        ModKey = row.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        FileKey = file.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        PageUrl = row.PageUrl,
                    },
                    PackageName = row.Name,
                    PackageVersion = row.Summary.Version,
                    PackageAuthor = row.Summary.Author,
                })
                .ConfigureAwait(true);

            IsDownloading = true;
            var completed = await WaitForDownloadAsync(record.Id).ConfigureAwait(true);
            IsDownloading = false;
            DownloadStatusText = string.Empty;
            DownloadProgress = 0;

            if (completed is null || completed.State != DownloadState.Completed)
            {
                HasError = true;
                ErrorText = completed?.Error ?? StatusTextFor(completed?.State);
                Status = "下载失败。";
                return;
            }

            // The download engine only owns bytes. Promotion into the library is a separate step, and
            // the UI must not claim the mod is installable until that step has a package id.
            var packaged = await WaitForPackageAsync(completed.Id).ConfigureAwait(true);
            if (packaged is null || string.IsNullOrWhiteSpace(packaged.PackageId))
            {
                HasError = true;
                ErrorText = "文件已下载，但还没有完成入库。请稍后在“本地仓库”重试，或查看日志。";
                Status = "下载完成，入库未完成。";
                return;
            }

            _host.Logger.Info("Library", $"已从 Nexus 入库 “{row.Name}”（Mod {row.ModId}）。", packaged.PackageId);
            Status = $"{row.Name} 已进入本地仓库，可以在“本地仓库”中安装。";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            ApplyFailure(ex.Message);
            Status = "下载失败。";
        }
        finally
        {
            IsBusy = false;
            IsDownloading = false;
        }
    }

    /// <summary>Uninstalls the local mod associated with a result.</summary>
    /// <param name="row">Row whose local mod should be removed.</param>
    private async Task UninstallAsync(OnlineModRow? row)
    {
        if (row?.LocalModId is not { Length: > 0 } modId || _shell?.ActiveGame is not { } game)
        {
            return;
        }

        var adapter = _host.Adapters.Get(game.Instance.GameId);
        if (adapter is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _host.Install
                .UninstallAsync(game.Instance.GameId, modId, adapter, game.Instance.InstallPath, false, _host.Logger)
                .ConfigureAwait(true);

            Status = result.Succeeded ? $"{row.Name} 已卸载。" : result.Error ?? "卸载失败。";
            HasError = !result.Succeeded;
            ErrorText = result.Succeeded ? string.Empty : Status;

            await SearchAsync(_page).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Explains what installing the missing dependencies would do, and does nothing unless the user
    /// confirms. Nothing is ever installed silently on a mod's behalf.
    /// </summary>
    private Task InstallMissingDependenciesAsync()
    {
        var missing = DetailDependencies.Where(row => row.IsMissing).ToList();
        if (missing.Count == 0)
        {
            Status = "没有缺失的依赖。";
            return Task.CompletedTask;
        }

        var names = string.Join("、", missing.Select(row => row.Name));
        var answer = MessageBox.Show(
            $"该 Mod 声明了以下缺失前置：{names}。\n\n"
            + "当前不会自动下载它们；关闭后会在搜索框中定位第一个，由你逐个确认下载。"
            + "下载完成后仍需在“本地仓库”中单独安装。是否继续？",
            "缺失依赖",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.OK)
        {
            Status = "已取消安装依赖。";
            return Task.CompletedTask;
        }

        Status = $"已定位第一个缺失前置：{names}。请逐个搜索并下载到本地仓库。";
        SearchText = missing[0].Name;
        return SearchAsync(1);
    }

    /// <summary>Chooses the file to download: the primary file, or the newest one.</summary>
    /// <param name="row">Row being installed.</param>
    private async Task<NexusModFile?> PickFileAsync(OnlineModRow row)
    {
        if (DetailFiles.Count > 0)
        {
            return DetailFiles
                .OrderByDescending(entry => entry.File.IsPrimary)
                .ThenByDescending(entry => entry.File.UploadedAt ?? DateTimeOffset.MinValue)
                .Select(entry => entry.File)
                .FirstOrDefault();
        }

        var files = await _host.Nexus.GetModFilesAsync(GameDomain, row.ModId).ConfigureAwait(true);
        if (!files.Succeeded || files.Value is null || files.Value.Count == 0)
        {
            return null;
        }

        return files.Value
            .OrderByDescending(file => file.IsPrimary)
            .ThenByDescending(file => file.UploadedAt ?? DateTimeOffset.MinValue)
            .First();
    }

    /// <summary>Waits for a queued download to reach a terminal state, reporting progress.</summary>
    /// <param name="downloadId">Job id.</param>
    private async Task<DownloadRecord?> WaitForDownloadAsync(string downloadId)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var record = _host.Downloads.Get(downloadId);
            if (record is null)
            {
                return null;
            }

            if (record.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)
            {
                return record;
            }

            DownloadProgress = record.Fraction is { } fraction ? fraction * 100d : 0d;
            var size = record.TotalBytes is { } total
                ? $"{FileSystemOps.FormatBytes(record.BytesDownloaded)} / {FileSystemOps.FormatBytes(total)}"
                : FileSystemOps.FormatBytes(record.BytesDownloaded);
            DownloadStatusText = $"正在下载 {record.FileName}：{size}";
            Status = DownloadStatusText;

            await Task.Delay(400).ConfigureAwait(true);
        }

        return _host.Downloads.Get(downloadId);
    }

    /// <summary>
    /// Waits a bounded time for the ingest service to attach a package id. Ingest is local
    /// (hash + copy), so the wait cannot be unbounded; a real failure still surfaces.
    /// </summary>
    private async Task<DownloadRecord?> WaitForPackageAsync(string downloadId)
    {
        // A large archive can take a while to hash and copy. The wait is bounded, so a real failure
        // still surfaces instead of leaving the page spinning forever.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var record = _host.Downloads.Get(downloadId);
            if (record is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(record.PackageId))
            {
                return record;
            }

            await Task.Delay(200).ConfigureAwait(true);
        }

        return _host.Downloads.Get(downloadId);
    }

    /// <summary>Maps a display sort option to the API's field name.</summary>
    /// <param name="option">Selected option.</param>
    private static string MapSortField(string option) => option switch
    {
        "按下载量" => "downloads",
        "按最近更新" => "lastupdated",
        "按名称" => "name",
        _ => "endorsements",
    };

    /// <summary>Flattens a dependency tree into indented rows for display.</summary>
    /// <param name="nodes">Nodes to flatten.</param>
    /// <param name="depth">Current depth.</param>
    private static List<DependencyNodeRow> Flatten(IReadOnlyList<DependencyNode> nodes, int depth)
    {
        var rows = new List<DependencyNodeRow>();
        foreach (var node in nodes)
        {
            rows.Add(new DependencyNodeRow(node, depth));
            rows.AddRange(Flatten(node.Children, depth + 1));
        }

        return rows;
    }

    /// <summary>
    /// Records a failure with its classification, so the page can offer the right next step.
    /// </summary>
    /// <param name="message">Raw provider message, used when nothing was classified.</param>
    /// <remarks>
    /// A missing or rejected key is presented as the setup card (the fix lives in Settings); a refusal is
    /// presented as an error with a link to the Nexus page (the fix lives on the website). Collapsing the two
    /// into one message is what leaves a user re-typing a valid key, or hunting for a network problem that
    /// does not exist.
    /// </remarks>
    private void ApplyFailure(string? message)
    {
        var error = _host.Nexus.LastError;
        HasError = true;
        ErrorText = NexusUi.Describe(error, message);
        ErrorNeedsSettings = NexusUi.NeedsSettings(error, message);
        ErrorNeedsNexusPage = NexusUi.NeedsNexusPage(error, message);

        if (ErrorNeedsSettings)
        {
            NeedsSetup = true;
            SetupText = ErrorText;
        }
    }

    /// <summary>
    /// Loads the covers for a page of rows, a few at a time.
    /// </summary>
    /// <param name="rows">Rows to fill in.</param>
    /// <remarks>
    /// Bounded concurrency keeps a page of twenty cards from opening twenty connections at once, and every
    /// failure resolves to "no cover" rather than an exception: an image problem must not take the page down.
    /// </remarks>
    private async Task LoadCoversAsync(IReadOnlyList<OnlineModRow> rows)
    {
        var targets = rows.Where(row => !string.IsNullOrWhiteSpace(row.PictureUrl)).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(4, 4);

        var tasks = targets.Select(async row =>
        {
            await gate.WaitAsync().ConfigureAwait(true);

            try
            {
                row.CoverImage = await _covers.LoadAsync(row.PictureUrl).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
            {
                _host.Logger.Warn("Images", "封面加载失败，已保留无图状态。", ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(true);
    }

    /// <summary>Turns a provider failure into an explanation a user can act on.</summary>
    /// <param name="message">Provider message.</param>
    private static string DescribeFailure(string message) => NexusUi.Describe(null, message);

    /// <summary>Text for a terminal-but-not-successful download state.</summary>
    /// <param name="state">Terminal state.</param>
    private static string StatusTextFor(DownloadState? state) => state switch
    {
        DownloadState.Cancelled => "下载已取消。",
        DownloadState.Failed => "下载失败。可在“下载”页重试。",
        null => "下载记录已不存在。",
        _ => "下载未完成。",
    };

    /// <summary>Converts the API's HTML description into readable plain text.</summary>
    /// <param name="html">HTML from the API.</param>
    /// <remarks>
    /// Deliberately a tiny converter rather than an HTML parser: the WPF text controls cannot render HTML
    /// anyway, and pulling in a parser to display a description is not worth a dependency. Anything that is
    /// not text or a line break is dropped.
    /// </remarks>
    private static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(html.Length);
        var inTag = false;

        foreach (var character in html)
        {
            switch (character)
            {
                case '<':
                    inTag = true;
                    break;
                case '>':
                    inTag = false;
                    builder.Append(' ');
                    break;
                case '\n':
                case '\r':
                    builder.AppendLine();
                    break;
                default:
                    if (!inTag)
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        var text = builder.ToString();
        foreach (var entity in new[] { "&amp;", "&lt;", "&gt;", "&quot;", "&#39;", "&nbsp;" })
        {
            text = text.Replace(entity, entity switch
            {
                "&amp;" => "&",
                "&lt;" => "<",
                "&gt;" => ">",
                "&quot;" => "\"",
                "&#39;" => "'",
                _ => " ",
            }, StringComparison.Ordinal);
        }

        return string.Join(
            Environment.NewLine,
            text.Split(Environment.NewLine)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Take(200));
    }
}

/// <summary>One row of the online result list.</summary>
public sealed class OnlineModRow : System.ComponentModel.INotifyPropertyChanged
{
    private ImageSource? _coverImage;
    private string? _translatedTitle;
    private string? _translatedSummary;

    /// <summary>Creates a row.</summary>
    /// <param name="summary">API summary.</param>
    /// <param name="localMod">Installed mod associated with it, when any.</param>
    /// <param name="plan">Decided action plan.</param>
    public OnlineModRow(NexusModSummary summary, ModMetadata? localMod, OnlineModActionPlan plan)
    {
        Summary = summary;
        LocalMod = localMod;
        Plan = plan;
    }

    /// <inheritdoc />
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Decoded cover, loaded from the local image cache after the row appears.
    /// </summary>
    /// <remarks>
    /// Set asynchronously so a slow or missing cover cannot delay the result list. Null means "no cover", and
    /// the row then shows a plain muted tile — never a letter.
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

    /// <summary>True when a cover has been decoded.</summary>
    public bool HasCover => _coverImage is not null;

    /// <summary>API summary.</summary>
    public NexusModSummary Summary { get; }

    /// <summary>Installed mod, when the library has one for this Nexus id.</summary>
    public ModMetadata? LocalMod { get; }

    /// <summary>Action plan decided by <see cref="OnlineModActionPlan.Evaluate"/>.</summary>
    public OnlineModActionPlan Plan { get; }

    /// <summary>Nexus mod id.</summary>
    public int ModId => Summary.ModId;

    /// <summary>Display name.</summary>
    public string Name => Summary.Name;

    /// <summary>Author, or a dash.</summary>
    public string Author => string.IsNullOrWhiteSpace(Summary.Author) ? "未知作者" : Summary.Author;

    /// <summary>Short summary.</summary>
    public string SummaryText => string.IsNullOrWhiteSpace(Summary.Summary) ? "作者没有提供简介。" : Summary.Summary;

    /// <summary>Published version.</summary>
    public string VersionText => string.IsNullOrWhiteSpace(Summary.Version) ? "版本未知" : $"v{Summary.Version}";

    /// <summary>Download count, formatted.</summary>
    public string DownloadsText => Summary.DownloadCount switch
    {
        >= 1_000_000 => $"{Summary.DownloadCount / 1_000_000d:0.#}M 次下载",
        >= 1_000 => $"{Summary.DownloadCount / 1_000d:0.#}k 次下载",
        _ => $"{Summary.DownloadCount} 次下载",
    };

    /// <summary>Endorsement count.</summary>
    public string EndorsementsText => $"{Summary.EndorsementCount} 推荐";

    /// <summary>Last update, formatted.</summary>
    public string UpdatedText => Summary.UpdatedAt is { } updated
        ? $"更新于 {updated.ToLocalTime():yyyy-MM-dd}"
        : "更新时间未知";

    /// <summary>Category, when the API reported one.</summary>
    public string CategoryText => string.IsNullOrWhiteSpace(Summary.Category) ? "未分类" : Summary.Category;

    /// <summary>Translated title shown under the original name.</summary>
    public string? TranslatedTitle
    {
        get => _translatedTitle;
        set
        {
            if (_translatedTitle == value)
            {
                return;
            }

            _translatedTitle = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TranslatedTitle)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasTranslatedTitle)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayTitle)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowOriginalTitle)));
        }
    }

    /// <summary>True when a Chinese title is available.</summary>
    public bool HasTranslatedTitle => !string.IsNullOrWhiteSpace(_translatedTitle);

    /// <summary>Chinese summary for this row, once one has been produced.</summary>
    /// <remarks>
    /// The row carried a translated title but no translated summary, so a page of results showed Chinese
    /// names above English blurbs — the half-translated state the task called out. Translating a mod's
    /// description used to fill only the detail pane's own copy, which is why the list never improved.
    /// </remarks>
    public string? TranslatedSummary
    {
        get => _translatedSummary;
        set
        {
            if (_translatedSummary == value)
            {
                return;
            }

            _translatedSummary = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TranslatedSummary)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasTranslatedSummary)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplaySummary)));
        }
    }

    /// <summary>True when a Chinese summary is available.</summary>
    public bool HasTranslatedSummary => !string.IsNullOrWhiteSpace(_translatedSummary);

    /// <summary>
    /// The title the row shows: Chinese once translated, the original otherwise.
    /// </summary>
    /// <remarks>
    /// This is the correction that matters most in this pass. The list bound its primary line to
    /// <see cref="Name"/> — the English title — and rendered the Chinese as a small accent-coloured note
    /// underneath, so translating a page produced a wall of English with Chinese footnotes. The interface now
    /// does what the task asks: Chinese first, English kept but demoted to a secondary line.
    /// </remarks>
    public string DisplayTitle => HasTranslatedTitle ? _translatedTitle! : Name;

    /// <summary>Original title, shown only as a secondary line once a translation exists.</summary>
    public string OriginalTitle => Name;

    /// <summary>True when the original title should appear as a secondary line.</summary>
    public bool ShowOriginalTitle =>
        HasTranslatedTitle && !string.Equals(_translatedTitle, Name, StringComparison.Ordinal);

    /// <summary>The summary the row shows: Chinese once translated, the original otherwise.</summary>
    public string DisplaySummary => HasTranslatedSummary ? _translatedSummary! : SummaryText;

    /// <summary>Cover image URL, when the API provided one.</summary>
    public string? PictureUrl => Summary.PictureUrl;

    /// <summary>Nexus page URL.</summary>
    public string? PageUrl => Summary.PageUrl;

    /// <summary>Dependency references, filled in once the detail request has run.</summary>
    public IReadOnlyList<NexusModReference> Requirements { get; set; } = [];

    /// <summary>Local state text.</summary>
    public string StateText => Plan.StateText;

    /// <summary>Primary action label.</summary>
    public string PrimaryActionText => Plan.PrimaryActionText;

    /// <summary>True when an install is possible now.</summary>
    public bool CanInstall => Plan.CanInstall;

    /// <summary>True when a manager download is allowed.</summary>
    public bool CanDownloadViaManager => Plan.CanDownloadViaManager;

    /// <summary>Why the primary action is unavailable.</summary>
    public string BlockingReason => Plan.BlockingReason ?? string.Empty;

    /// <summary>True when there is a local mod to uninstall.</summary>
    public bool IsInstalled => LocalMod is not null;

    /// <summary>Local mod id, when installed.</summary>
    public string? LocalModId => LocalMod?.Id;

    /// <summary>Local version, when installed.</summary>
    public string LocalVersionText => LocalMod?.Version is { Length: > 0 } version ? $"本地 {version}" : string.Empty;

    /// <summary>True when an update is available.</summary>
    public bool HasUpdate => Plan.LocalState == OnlineModLocalState.UpdateAvailable;

    /// <summary>Badge colour for the local state.</summary>
    public Brush StateTone => Plan.LocalState switch
    {
        OnlineModLocalState.UpdateAvailable => Palette.Warning(),
        OnlineModLocalState.InstalledLatest => Palette.Success(),
        OnlineModLocalState.InstalledUnknown => Palette.Info(),
        _ => Palette.Secondary(),
    };

    /// <summary>Badge fill for the local state.</summary>
    public Brush StateFill => Plan.LocalState switch
    {
        OnlineModLocalState.UpdateAvailable => Palette.WarningFill(),
        OnlineModLocalState.InstalledLatest => Palette.SuccessFill(),
        OnlineModLocalState.InstalledUnknown => Palette.InfoFill(),
        _ => Palette.NeutralFill(),
    };

    /// <summary>First character of the name, used when there is no cover image.</summary>
    public string IconText => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
}

/// <summary>One published file of an online mod.</summary>
/// <param name="File">API file record.</param>
public sealed record OnlineModFileRow(NexusModFile File)
{
    /// <summary>File name.</summary>
    public string Name => File.Name;

    /// <summary>Version, or a dash.</summary>
    public string VersionText => string.IsNullOrWhiteSpace(File.Version) ? "-" : $"v{File.Version}";

    /// <summary>Category as published.</summary>
    public string CategoryText => string.IsNullOrWhiteSpace(File.Category) ? "未分类" : File.Category;

    /// <summary>Human size.</summary>
    public string SizeText => FileSystemOps.FormatBytes(File.SizeBytes);

    /// <summary>True when the uploader marked this the main file.</summary>
    public bool IsPrimary => File.IsPrimary;

    /// <summary>Badge text for the main file.</summary>
    public string PrimaryText => File.IsPrimary ? "主文件" : "附加文件";

    /// <summary>Upload date, formatted.</summary>
    public string UploadedText => File.UploadedAt is { } uploaded
        ? uploaded.ToLocalTime().ToString("yyyy-MM-dd")
        : "日期未知";

    /// <summary>Description, when published.</summary>
    public string DescriptionText => string.IsNullOrWhiteSpace(File.Description) ? string.Empty : File.Description;
}

/// <summary>One row of the dependency tree, indented by depth.</summary>
/// <param name="Node">Dependency node.</param>
/// <param name="Depth">Nesting depth.</param>
public sealed record DependencyNodeRow(DependencyNode Node, int Depth)
{
    /// <summary>Name as displayed.</summary>
    public string Name => Node.Name;

    /// <summary>Indentation for the tree.</summary>
    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    /// <summary>Prefix that draws the tree shape.</summary>
    public string Branch => Depth == 0 ? "├─" : "└─";

    /// <summary>State text in Chinese.</summary>
    public string StateText => Node.State switch
    {
        DependencyNodeState.Installed => "已安装",
        DependencyNodeState.Satisfied => "已安装",
        DependencyNodeState.InstalledOutdated => "版本过低",
        DependencyNodeState.Missing => "缺失",
        DependencyNodeState.OptionalMissing => "可选，未安装",
        DependencyNodeState.Unsupported => "无法自动解析",
        DependencyNodeState.SelfReference => "依赖自身",
        DependencyNodeState.Cyclic => "循环依赖",
        _ => Node.State.ToString(),
    };

    /// <summary>Colour for the state.</summary>
    public Brush Tone => Node.State switch
    {
        DependencyNodeState.Missing => Palette.Danger(),
        DependencyNodeState.InstalledOutdated => Palette.Warning(),
        DependencyNodeState.Unsupported => Palette.Secondary(),
        DependencyNodeState.SelfReference or DependencyNodeState.Cyclic => Palette.Warning(),
        DependencyNodeState.OptionalMissing => Palette.Secondary(),
        _ => Palette.Success(),
    };

    /// <summary>Explanation shown next to the node.</summary>
    public string Note => Node.Note ?? string.Empty;

    /// <summary>True when this node is missing and could be installed.</summary>
    public bool IsMissing => Node.NeedsInstall;

    /// <summary>Version requirement text.</summary>
    public string VersionText => Node.RequiredVersion is { Length: > 0 } required
        ? $"需要 {required}"
        : string.Empty;

    /// <summary>Nexus mod id, when the reference named one.</summary>
    public string NexusText => Node.NexusModId is { } id ? $"Nexus #{id}" : string.Empty;
}
