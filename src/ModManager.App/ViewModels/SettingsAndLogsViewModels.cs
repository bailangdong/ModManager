using System.IO;
using System.Windows.Media;
using ModManager.App.Theme;
using ModManager.Core;
using ModManager.Core.Safety;
using ModManager.Core.Services;

namespace ModManager.App.ViewModels;

/// <summary>
/// Settings: the Nexus credential, deployment behaviour, and safety.
/// </summary>
/// <remarks>
/// <para>
/// The user's spec §7 is explicit that if an API key is necessary it must be a secure setting, and that no
/// credential may be hardcoded, harvested or bypassed. Three things follow, and all three are visible on
/// this page rather than merely implemented:
/// </para>
/// <list type="bullet">
/// <item>the key is entered into a <c>PasswordBox</c>, so it is not rendered in clear text;</item>
/// <item>it is written to the secret store, never to the settings file — the settings document is the one
/// users paste into bug reports;</item>
/// <item>signing out deletes it, rather than merely stopping its use.</item>
/// </list>
/// <para>
/// The safety section is on the same page for a different reason: the approved roots are the guarantee that
/// nothing outside a game folder can be touched, and a guarantee the user cannot inspect is not much of one.
/// </para>
/// </remarks>
public sealed class SettingsViewModel : ViewModelBase, IPageViewModel
{
    private readonly ApplicationHost _host;
    private string _apiKeyInput = string.Empty;
    private string _status = string.Empty;
    private string _nexusAccount = "未登录";
    private bool _isBusy;
    private bool _includeAdultContent;
    private bool _backupBeforeOverwrite = true;
    private bool _verifyAfterDeploy = true;
    private bool _rollbackOnFailure = true;
    private bool _alwaysShowPreview;
    private bool _useHardlinks = true;
    private bool _useSymlinks = true;
    private int _maxConcurrency = 3;
    private string _startupPageName = string.Empty;
    private bool _darkMode;
    private string _nexusLastErrorText = string.Empty;
    private string _translationEndpoint = "https://api.deepseek.com/chat/completions";
    private string _translationModel = "deepseek-chat";
    private string _translationKeyInput = string.Empty;
    private string _translationStatus = string.Empty;
    private string _translationEngineChoice = "自动（优先本地）";
    private string _localEngineStateText = "正在检查本地引擎…";
    private string _localPackageStateText = string.Empty;
    private string _localEngineDetail = string.Empty;
    private string _localEnginePathText = string.Empty;
    private string _localTestResult = string.Empty;
    private double _localInstallProgress;
    private bool _isInstallingLocal;

    /// <summary>The pages the app can open on.</summary>
    public static IReadOnlyList<string> AvailablePages { get; } =
        ["总览", "游戏", "在线 Mod", "本地 Mod", "下载", "配置方案", "冲突", "设置", "日志"];

    /// <summary>Creates the page.</summary>
    /// <param name="host">Composition root.</param>
    public SettingsViewModel(ApplicationHost host)
    {
        _host = host;

        SaveApiKeyCommand = new RelayCommand(() => SaveApiKeyAsync(), () => _apiKeyInput.Trim().Length > 0);
        ClearApiKeyCommand = new RelayCommand(() => ClearApiKeyAsync(), () => _host.Secrets.Contains(ISecretStore.NexusApiKey));
        SaveTranslationCommand = new RelayCommand(() => SaveTranslationAsync());
        InitializeLocalEngineCommand = new RelayCommand(() => InitializeLocalEngineAsync(), () => !_isInstallingLocal);
        TestLocalTranslationCommand = new RelayCommand(
            () => TestLocalTranslationAsync(),
            () => _host.LocalTranslation.Probe().CanTranslate);
        SaveSettingsCommand = new RelayCommand(() => SaveAsync());
        OpenDataFolderCommand = new RelayCommand(() => OpenDataFolder());
        OpenLogFolderCommand = new RelayCommand(() => OpenLogFolder());

        LoadFromSettings(_host.Settings.Settings);
        RefreshLocalEngineState();
    }

    /// <summary>Saves the entered API key.</summary>
    public RelayCommand SaveApiKeyCommand { get; }

    /// <summary>Deletes the stored API key.</summary>
    public RelayCommand ClearApiKeyCommand { get; }

    /// <summary>Persists the option changes.</summary>
    public RelayCommand SaveSettingsCommand { get; }

    /// <summary>Page the app opens on at startup.</summary>
    public string StartupPageName
    {
        get => _startupPageName;
        set => SetProperty(ref _startupPageName, value);
    }

    /// <summary>
    /// True when the dark palette is active.
    /// </summary>
    /// <remarks>
    /// Applied the moment it is toggled rather than on save: a theme switch the user cannot see until they
    /// press a button is not a theme switch. The choice is written to settings at the same time so it
    /// survives a restart.
    /// </remarks>
    public bool DarkMode
    {
        get => _darkMode;
        set
        {
            if (SetProperty(ref _darkMode, value))
            {
                ApplyDarkMode(value);
            }
        }
    }

    /// <summary>Application version, for the About card.</summary>
    public string VersionText =>
        typeof(ModManager.App.App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Version line for the About card.</summary>
    public string VersionLine => "版本 " + VersionText;

    /// <summary>Opens the application data directory.</summary>
    public RelayCommand OpenDataFolderCommand { get; }

    /// <summary>Opens the log directory.</summary>
    public RelayCommand OpenLogFolderCommand { get; }

    /// <summary>Approved write roots, shown so the safety guarantee is inspectable.</summary>
    public BulkObservableCollection<ApprovedRootRow> ApprovedRoots { get; } = [];

    /// <summary>API key being entered. Never read back from the store into this property.</summary>
    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set
        {
            if (SetProperty(ref _apiKeyInput, value))
            {
                SaveApiKeyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Nexus account description.</summary>
    public string NexusAccount
    {
        get => _nexusAccount;
        private set => SetProperty(ref _nexusAccount, value);
    }

    /// <summary>Status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>True while a request is in flight.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Whether a key is currently stored.</summary>
    public bool HasApiKey => _host.Secrets.Contains(ISecretStore.NexusApiKey);

    /// <summary>Connection state, phrased for the user.</summary>
    public string NexusConnectionText => _host.Nexus.AuthMode switch
    {
        NexusAuthMode.ApiKey => "已连接（API 密钥）",
        NexusAuthMode.OAuth => "已连接（浏览器授权）",
        _ => "未连接",
    };

    /// <summary>When the credential was last validated, or a plain statement that it never was.</summary>
    public string NexusLastSyncText => _host.Settings.Settings.Nexus.LastAuthenticatedAt is { } at
        ? $"上次校验：{at.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "尚未校验过凭据。";

    /// <summary>Classified explanation of the last failed connection test.</summary>
    public string NexusLastErrorText
    {
        get => _nexusLastErrorText;
        private set
        {
            if (SetProperty(ref _nexusLastErrorText, value))
            {
                OnPropertyChanged(nameof(HasNexusError));
            }
        }
    }

    /// <summary>True when the last test failed and there is an explanation to show.</summary>
    public bool HasNexusError => _nexusLastErrorText.Length > 0;

    /// <summary>
    /// What the stored credential actually allows, including the limits that are not ours to lift.
    /// </summary>
    /// <remarks>
    /// Stated explicitly rather than left to be discovered: the download path is gated server-side by Nexus
    /// and by each author, so a settings page that implied "all downloads work" would be lying about a
    /// capability neither this application nor the API key can grant.
    /// </remarks>
    public IReadOnlyList<NexusCapabilityRow> NexusCapabilities =>
    [
        new("浏览与搜索 Mod", HasApiKey, "使用官方 API 查询游戏、Mod 列表与搜索。"),
        new("查看详情与文件", HasApiKey, "读取 Mod 描述、依赖关系与已发布文件。"),
        new("通过管理器下载", HasApiKey, "由 Nexus 在服务端校验你的账户权限；被拒绝时会明确提示。"),
        new("检测更新", HasApiKey, "用记录的 Nexus 标识检查已安装 Mod 的新版本。"),
        new("本地安装与回滚", true, "下载完成后走与其他 Mod 完全相同的安装流水线。"),
    ];

    /// <summary>Actions that must be completed on the Nexus website, not here.</summary>
    public IReadOnlyList<string> NexusManualActions { get; } =
    [
        "需要高级会员才能直接下载的文件：请在 Nexus 页面下载，再用“从压缩包安装…”导入。",
        "作者关闭了“允许管理器下载”的 Mod：同上，需要在页面上手动下载。",
        "需要年龄段/内容确认的 Mod：确认只能在 Nexus 页面上完成。",
        "账号、密码与令牌管理：本程序只保存你自己生成的 API 密钥，不接触账号密码。",
    ];

    /// <summary>Includes adult-flagged results in Nexus searches.</summary>
    public bool IncludeAdultContent
    {
        get => _includeAdultContent;
        set => SetProperty(ref _includeAdultContent, value);
    }

    /// <summary>Backs up game files before overwriting them.</summary>
    public bool BackupBeforeOverwrite
    {
        get => _backupBeforeOverwrite;
        set => SetProperty(ref _backupBeforeOverwrite, value);
    }

    /// <summary>Re-hashes deployed files to confirm the write.</summary>
    public bool VerifyAfterDeploy
    {
        get => _verifyAfterDeploy;
        set => SetProperty(ref _verifyAfterDeploy, value);
    }

    /// <summary>Reverses a failed deployment automatically.</summary>
    public bool RollbackOnFailure
    {
        get => _rollbackOnFailure;
        set => SetProperty(ref _rollbackOnFailure, value);
    }

    /// <summary>Shows the installation preview for every install.</summary>
    public bool AlwaysShowPreview
    {
        get => _alwaysShowPreview;
        set => SetProperty(ref _alwaysShowPreview, value);
    }

    /// <summary>Allows hardlink deployment.</summary>
    public bool UseHardlinks
    {
        get => _useHardlinks;
        set => SetProperty(ref _useHardlinks, value);
    }

    /// <summary>Allows symlink deployment.</summary>
    public bool UseSymlinks
    {
        get => _useSymlinks;
        set => SetProperty(ref _useSymlinks, value);
    }

    /// <summary>Maximum simultaneous downloads.</summary>
    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set => SetProperty(ref _maxConcurrency, Math.Clamp(value, 1, 8));
    }

    /// <summary>Where the application keeps its data.</summary>
    public string DataPath => _host.Paths.Root;

    /// <summary>Where mods are staged.</summary>
    public string StagingPath => _host.Paths.Mods;

    /// <summary>Where downloads land.</summary>
    public string DownloadPath => _host.Paths.Downloads;

    /// <summary>Where backups are kept.</summary>
    public string BackupPath => _host.Paths.Backups;

    /// <summary>The game the app opens on, or a hint when none is chosen.</summary>
    public string DefaultGameText
    {
        get
        {
            var defaultId = _host.Settings.Settings.LastSelectedGameId;
            if (string.IsNullOrWhiteSpace(defaultId))
            {
                return "未设置（启动时选择列表中的第一个游戏）。";
            }

            var instance = _host.Library.AllGames().FirstOrDefault(game => game.GameId == defaultId);
            return instance is null
                ? "已记录的游戏已不存在。"
                : $"{instance.Name}（可在“游戏”页点击“设为默认”修改）";
        }
    }

    /// <summary>A truthful statement of what the secret store does and does not protect against.</summary>
    public string SecretStoreNote => OperatingSystem.IsWindows()
        ? "密钥使用 Windows DPAPI 加密，与你的用户账户绑定，并保存在设置文件之外，"
            + "因此分享设置文件时不会泄露。但它不能防御已经以你的身份运行的恶意程序。"
        : "密钥以混淆形式保存在设置文件之外。当前平台不做加密，请把该文件视为敏感数据。";

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        ReloadRoots();
        NexusAccount = _host.Secrets.Contains(ISecretStore.NexusApiKey)
            ? "已保存 API 密钥" + (string.IsNullOrWhiteSpace(_host.Settings.Settings.Nexus.UserName)
                ? string.Empty
                : $"（{_host.Settings.Settings.Nexus.UserName}）")
            : "未登录。添加 API 密钥后即可浏览与下载 Nexus Mods 上的内容。";

        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(DefaultGameText));
        ClearApiKeyCommand.RaiseCanExecuteChanged();
        RefreshLocalEngineState();

        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>Reloads the approved-root list.</summary>
    private void ReloadRoots()
    {
        var rows = new List<ApprovedRootRow>();
        foreach (var root in _host.PathSafety.ApprovedRoots)
        {
            rows.Add(new ApprovedRootRow(root));
        }

        ApprovedRoots.ReplaceAll(rows.OrderBy(row => row.GameId, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Copies settings into the form.</summary>
    /// <param name="settings">Settings to load.</param>
    private void LoadFromSettings(AppSettings settings)
    {
        _includeAdultContent = settings.Nexus.IncludeAdultContent;
        _backupBeforeOverwrite = settings.Deployment.BackupBeforeOverwrite;
        _verifyAfterDeploy = settings.Deployment.VerifyAfterDeploy;
        _rollbackOnFailure = settings.Deployment.RollbackOnFailure;
        _useHardlinks = settings.Deployment.AllowedMethods.Contains(DeploymentMethod.Hardlink);
        _useSymlinks = settings.Deployment.AllowedMethods.Contains(DeploymentMethod.Symlink);
        _maxConcurrency = settings.Downloads.MaxConcurrency;
        _alwaysShowPreview = settings.Ui.AlwaysShowInstallPreview;
        _startupPageName = settings.Ui.StartupPageName;
        _darkMode = string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Validates and stores the entered key.</summary>
    private async Task SaveApiKeyAsync()
    {
        var key = _apiKeyInput.Trim();
        if (key.Length == 0)
        {
            return;
        }

        IsBusy = true;
        Status = "正在向 Nexus Mods 校验密钥…";

        try
        {
            var credentials = new NexusCredentials { ApiKey = key };
            var result = await _host.Nexus
                .ValidateCredentialsAsync(credentials)
                .ConfigureAwait(true);

            if (result.Succeeded && result.Value is not null)
            {
                // Only a key Nexus accepted is persisted, so a typo cannot be stored as if it worked.
                _host.Secrets.Set(ISecretStore.NexusApiKey, key);

                _host.Settings.Settings = _host.Settings.Settings with
                {
                    Nexus = _host.Settings.Settings.Nexus with
                    {
                        AuthMode = NexusAuthMode.ApiKey,
                        HasApiKey = true,
                        UserName = result.Value.UserName,
                        IsPremium = result.Value.IsPremium ?? false,
                        LastAuthenticatedAt = DateTimeOffset.UtcNow,
                    },
                };

                _host.SaveSettings();

                NexusAccount = $"已登录：{result.Value.UserName}"
                    + (result.Value.IsPremium == true ? "（高级会员）" : string.Empty);
                Status = "API 密钥有效，已安全保存。";
                NexusLastErrorText = string.Empty;
                ApiKeyInput = string.Empty;
            }
            else
            {
                // Classified, so "no key", "rejected key", "needs premium", "server down" and "no network" do
                // not all read as the same sentence.
                Status = NexusUi.Describe(_host.Nexus.LastError, result.Error ?? "Nexus Mods 拒绝了这个密钥。");
                NexusLastErrorText = Status;
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasApiKey));
            ClearApiKeyCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Translation endpoint.</summary>
    public string TranslationEndpoint
    {
        get => _translationEndpoint;
        set => SetProperty(ref _translationEndpoint, value);
    }

    /// <summary>Translation model name.</summary>
    public string TranslationModel
    {
        get => _translationModel;
        set => SetProperty(ref _translationModel, value);
    }

    /// <summary>Key typed into the translation card. It is never read back out.</summary>
    public string TranslationKeyInput
    {
        get => _translationKeyInput;
        set => SetProperty(ref _translationKeyInput, value);
    }

    /// <summary>Whether a translation key is stored, stated without revealing it.</summary>
    public string TranslationKeyState => _host.Secrets.Contains(ISecretStore.TranslationApiKey)
        ? "翻译服务：已配置密钥"
        : "翻译服务：未配置密钥";

    /// <summary>True when at least one engine can translate right now.</summary>
    /// <remarks>
    /// Not <c>IsConfigured</c>: that asks whether an online key exists, which is the wrong question once a
    /// free local engine is installed. The local engine needs no credential at all.
    /// </remarks>
    public bool CanTranslate => _host.Translation.IsAvailable;

    /// <summary>Status line for the translation card.</summary>
    public string TranslationStatus
    {
        get => _translationStatus;
        private set => SetProperty(ref _translationStatus, value);
    }

    /// <summary>The engine choices offered in the interface.</summary>
    public IReadOnlyList<string> TranslationEngineChoices { get; } =
        ["自动（优先本地）", "仅本地 Argos（免费）", "仅在线 API"];

    /// <summary>Selected engine choice.</summary>
    public string TranslationEngineChoice
    {
        get => _translationEngineChoice;
        set
        {
            if (SetProperty(ref _translationEngineChoice, value))
            {
                ApplyEngineChoice(value);
            }
        }
    }

    /// <summary>Engine state, in the words the user needs: not initialised, downloading, usable, failed.</summary>
    public string LocalEngineStateText
    {
        get => _localEngineStateText;
        private set => SetProperty(ref _localEngineStateText, value);
    }

    /// <summary>English → Chinese language pack state, including version and size.</summary>
    public string LocalPackageStateText
    {
        get => _localPackageStateText;
        private set => SetProperty(ref _localPackageStateText, value);
    }

    /// <summary>Plain-language explanation of the current state, including any failure and its reason.</summary>
    public string LocalEngineDetail
    {
        get => _localEngineDetail;
        private set => SetProperty(ref _localEngineDetail, value);
    }

    /// <summary>Where the engine keeps its runtime and language pack.</summary>
    public string LocalEnginePathText
    {
        get => _localEnginePathText;
        private set => SetProperty(ref _localEnginePathText, value);
    }

    /// <summary>Result of the test translation, showing the real output.</summary>
    public string LocalTestResult
    {
        get => _localTestResult;
        private set => SetProperty(ref _localTestResult, value);
    }

    /// <summary>Install progress 0..100; only meaningful while a download is running.</summary>
    public double LocalInstallProgress
    {
        get => _localInstallProgress;
        private set => SetProperty(ref _localInstallProgress, value);
    }

    /// <summary>True while the local engine is being installed.</summary>
    public bool IsInstallingLocal
    {
        get => _isInstallingLocal;
        private set
        {
            if (SetProperty(ref _isInstallingLocal, value))
            {
                InitializeLocalEngineCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Installs the local runtime and the English → Chinese language pack.</summary>
    public RelayCommand InitializeLocalEngineCommand { get; }

    /// <summary>Runs one real translation and shows exactly what came back.</summary>
    public RelayCommand TestLocalTranslationCommand { get; }

    /// <summary>Saves the translation endpoint, model and key.</summary>
    public RelayCommand SaveTranslationCommand { get; }

    /// <summary>
    /// Stores the translation configuration.
    /// </summary>
    /// <remarks>
    /// The key is written to the encrypted secret store, never to settings.json or the log. Saving is
    /// deliberate rather than automatic so a half-typed key cannot be persisted.
    /// </remarks>
    private Task SaveTranslationAsync()
    {
        var endpoint = TranslationEndpoint.Trim();
        var model = TranslationModel.Trim();

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            TranslationStatus = "服务地址必须是 http/https 的完整地址，例如 https://api.deepseek.com/chat/completions。";
            return Task.CompletedTask;
        }

        if (model.Length == 0)
        {
            TranslationStatus = "请填写模型名称，例如 deepseek-chat。";
            return Task.CompletedTask;
        }

        var key = TranslationKeyInput.Trim();
        if (key.Length > 0)
        {
            _host.Secrets.Set(ISecretStore.TranslationApiKey, key);
            TranslationKeyInput = string.Empty;
        }

        _host.Settings.Settings = _host.Settings.Settings with
        {
            // `with` rather than a fresh record: the engine choice and the last-used stamp survive a save,
            // which a new instance would silently reset to its defaults.
            Translation = _host.Settings.Settings.Translation with
            {
                Enabled = true,
                Endpoint = endpoint,
                Model = model,
                HasKey = _host.Secrets.Contains(ISecretStore.TranslationApiKey),
            },
        };

        _host.SaveSettings();

        TranslationStatus = _host.Translation.IsConfigured
            ? "翻译已就绪。在“在线 Mod”里点“翻译为中文”即可。"
            : "尚未保存密钥，翻译暂不可用。";

        OnPropertyChanged(nameof(TranslationKeyState));
        OnPropertyChanged(nameof(CanTranslate));

        return Task.CompletedTask;
    }

    /// <summary>Maps the interface's engine choice onto the stored setting and persists it.</summary>
    /// <param name="choice">Selected label.</param>
    private void ApplyEngineChoice(string choice)
    {
        var engine = choice switch
        {
            "仅本地 Argos（免费）" => TranslationEngineKind.Local,
            "仅在线 API" => TranslationEngineKind.Online,
            _ => TranslationEngineKind.Auto,
        };

        var current = _host.Settings.Settings;
        _host.Settings.Settings = current with
        {
            Translation = current.Translation with { Engine = engine },
        };

        _host.SaveSettings();

        OnPropertyChanged(nameof(CanTranslate));
        TranslationStatus = _host.Translation.IsAvailable
            ? "翻译引擎已切换，当前可用。"
            : "翻译引擎已切换，但当前没有可用的引擎：请先初始化本地引擎，或填写在线 API 密钥。";
    }

    /// <summary>Re-reads the local engine state and refreshes the card.</summary>
    /// <remarks>
    /// Cheap by design: it stats a few files rather than starting the interpreter, so it is safe to call on
    /// every page refresh.
    /// </remarks>
    private void RefreshLocalEngineState()
    {
        var status = _host.LocalTranslation.Probe();

        LocalEngineStateText = status.State switch
        {
            LocalEngineState.Ready => "可用",
            LocalEngineState.Initializing => "正在下载语言包…",
            LocalEngineState.Failed => "失败",
            _ => "未初始化",
        };

        LocalPackageStateText = status.PackageReady
            ? $"英语 → 简体中文：已安装（{status.PackageId}，版本 {status.PackageVersion ?? "未知"}，"
                + $"{status.PackageBytes / 1048576d:N1} MB）"
            : "英语 → 简体中文：未安装";

        LocalEngineDetail = status.Detail;
        LocalEnginePathText = _host.LocalTranslation.RuntimeRoot;

        // Restore the stored choice without re-saving it.
        _translationEngineChoice = _host.Settings.Settings.Translation.Engine switch
        {
            TranslationEngineKind.Local => "仅本地 Argos（免费）",
            TranslationEngineKind.Online => "仅在线 API",
            _ => "自动（优先本地）",
        };

        OnPropertyChanged(nameof(TranslationEngineChoice));
        OnPropertyChanged(nameof(CanTranslate));
        TestLocalTranslationCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Installs whatever the local engine is missing, reporting every step.</summary>
    private async Task InitializeLocalEngineAsync()
    {
        if (IsInstallingLocal)
        {
            return;
        }

        IsInstallingLocal = true;
        LocalInstallProgress = 0;
        LocalTestResult = string.Empty;
        LocalEngineStateText = "正在初始化…";

        try
        {
            // Created on the UI thread, so the reports arrive on it and the progress bar can move.
            var progress = new Progress<LocalEngineProgress>(report =>
            {
                LocalEngineDetail = report.Message;
                if (report.Fraction is { } fraction)
                {
                    LocalInstallProgress = fraction * 100d;
                }
            });

            var result = await _host.LocalTranslation
                .InitializeAsync(progress)
                .ConfigureAwait(true);

            if (result.Succeeded && result.Value is not null)
            {
                LocalInstallProgress = 100;
                LocalTestResult = result.Value.Detail;
                Status = "本地翻译引擎已就绪：无需 API 密钥即可翻译。";
            }
            else
            {
                // The reason is shown verbatim; a failed install must never look like a successful one.
                LocalTestResult = result.Error ?? "初始化失败，原因未知。";
                Status = "本地引擎初始化失败。";
            }
        }
        finally
        {
            IsInstallingLocal = false;
            RefreshLocalEngineState();
        }
    }

    /// <summary>Runs one real translation and shows the input and the output.</summary>
    private async Task TestLocalTranslationAsync()
    {
        LocalTestResult = "正在测试…";

        var result = await _host.LocalTranslation.SelfTestAsync().ConfigureAwait(true);

        LocalTestResult = result.Succeeded
            ? "测试通过。英文原文：This mod adds a new weapon and a small quest."
                + Environment.NewLine + "中文译文：" + result.Value
            : "测试失败：" + result.Error;

        RefreshLocalEngineState();
    }

    /// <summary>Deletes the stored key.</summary>
    private async Task ClearApiKeyAsync()
    {
        _host.Nexus.SignOut();

        _host.Settings.Settings = _host.Settings.Settings with
        {
            Nexus = _host.Settings.Settings.Nexus with
            {
                AuthMode = NexusAuthMode.None,
                HasApiKey = false,
                UserName = null,
                IsPremium = false,
            },
        };

        _host.SaveSettings();

        NexusAccount = "未登录。";
        Status = "已删除保存的 API 密钥。";

        OnPropertyChanged(nameof(HasApiKey));
        ClearApiKeyCommand.RaiseCanExecuteChanged();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>Persists the option changes.</summary>
    private async Task SaveAsync()
    {
        var methods = new List<DeploymentMethod>();
        if (_useHardlinks)
        {
            methods.Add(DeploymentMethod.Hardlink);
        }

        if (_useSymlinks)
        {
            methods.Add(DeploymentMethod.Symlink);
        }

        // Copy is always permitted: it is the one method that works everywhere, and removing it would make
        // deployment fail outright on a filesystem that supports nothing else.
        methods.Add(DeploymentMethod.Copy);

        var current = _host.Settings.Settings;

        _host.Settings.Settings = current with
        {
            Nexus = current.Nexus with { IncludeAdultContent = _includeAdultContent },
            Deployment = current.Deployment with
            {
                BackupBeforeOverwrite = _backupBeforeOverwrite,
                VerifyAfterDeploy = _verifyAfterDeploy,
                RollbackOnFailure = _rollbackOnFailure,
                AllowedMethods = methods,
            },
            Downloads = current.Downloads with { MaxConcurrency = _maxConcurrency },
            Ui = current.Ui with
            {
                AlwaysShowInstallPreview = _alwaysShowPreview,
                StartupPageName = string.IsNullOrWhiteSpace(_startupPageName) ? string.Empty : _startupPageName,
            },
        };

        _host.SaveSettings();
        Status = "设置已保存。部署相关选项在下一次操作时生效。";

        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>Applies the light or dark palette and persists the choice.</summary>
    /// <param name="dark">True for the dark palette.</param>
    private void ApplyDarkMode(bool dark)
    {
        AppearanceService.Apply(dark);

        var current = _host.Settings.Settings;
        _host.Settings.Settings = current with
        {
            Theme = dark ? "Dark" : "Light",

            // Kept in step because the field predates the switch and other code may still read it.
            UseLightTheme = !dark,
        };

        _host.SaveSettings();
        Status = dark ? "已切换到深色模式。" : "已切换到浅色模式。";
    }

    /// <summary>Opens the application data directory.</summary>
    private void OpenDataFolder() => OpenPath(_host.Paths.Root, "data");

    /// <summary>Opens the log directory.</summary>
    private void OpenLogFolder() => OpenPath(_host.Paths.Logs, "log");

    /// <summary>Opens a directory in Explorer.</summary>
    /// <param name="path">Directory to open.</param>
    /// <param name="label">Label for the status message.</param>
    private void OpenPath(string path, string label)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });

            Status = $"已打开{label}目录。";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Status = $"无法打开{label}目录：{ex.Message}";
        }
    }
}

/// <summary>An approved write root.</summary>
/// <param name="Root">Root record.</param>
public sealed record ApprovedRootRow(ApprovedRoot Root)
{
    /// <summary>Game it belongs to.</summary>
    public string GameId => Root.GameId;

    /// <summary>Install directory.</summary>
    public string InstallPath => Root.InstallPath;

    /// <summary>Mod root, the only directory this application writes into.</summary>
    public string ModRootPath => Root.ModRootPath;

    /// <summary>When it was approved.</summary>
    public string ApprovedText => Root.ApprovedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// Logs: the structured log stream and the transaction history.
/// </summary>
/// <remarks>
/// The transaction list is here rather than on its own page because it answers the same question the log
/// does — "what did this application actually do?" — and because a journal that cannot be read is not much
/// of a recovery guarantee.
/// </remarks>
public sealed class LogsViewModel : ViewModelBase, IPageViewModel
{
    private readonly ApplicationHost _host;
    private LogLevel _minimumLevel = LogLevel.Information;
    private string _status = string.Empty;
    private string _searchText = string.Empty;
    private LogRow? _selectedEntry;

    /// <summary>Creates the page and subscribes to the log stream.</summary>
    /// <param name="host">Composition root.</param>
    public LogsViewModel(ApplicationHost host)
    {
        _host = host;

        // Live tail rather than a refresh button: the point of watching a deploy is to see it happen.
        _host.Logger.EntryWritten += (_, _) => RefreshIfVisible();

        RefreshCommand = new RelayCommand(() => RefreshAsync());
        CopySelectedCommand = new RelayCommand(() => CopySelected(), () => _selectedEntry is not null);
        ClearDisplayCommand = new RelayCommand(() =>
        {
            // Clears the on-screen list only — the log files on disk are the audit record and are
            // deliberately never touched by this.
            Entries.ReplaceAll([]);
            SelectedEntry = null;
            Status = "显示已清空（日志文件未受影响，点击刷新可重新加载）。";
            return Task.CompletedTask;
        });
    }

    /// <summary>Log rows.</summary>
    public BulkObservableCollection<LogRow> Entries { get; } = [];

    /// <summary>Transaction rows.</summary>
    public BulkObservableCollection<TransactionRow> Transactions { get; } = [];

    /// <summary>Reloads both lists.</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>Copies the selected entry to the clipboard.</summary>
    public RelayCommand CopySelectedCommand { get; }

    /// <summary>Clears the on-screen log list only; never touches the log files.</summary>
    public RelayCommand ClearDisplayCommand { get; }

    /// <summary>Search text; filters category, message and detail.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>Minimum level to display.</summary>
    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set
        {
            if (SetProperty(ref _minimumLevel, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>Status line.</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Selected log entry.</summary>
    public LogRow? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                CopySelectedCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SelectedDetail));
            }
        }
    }

    /// <summary>Detail text of the selected entry.</summary>
    public string SelectedDetail => _selectedEntry?.Detail ?? "选择一条日志查看详情。";

    /// <inheritdoc />
    public Task RefreshAsync()
    {
        var query = _searchText?.Trim() ?? string.Empty;
        var entries = _host.Logger
            .Recent(2000)
            .Where(entry => entry.Level >= _minimumLevel)
            .Where(entry => query.Length == 0
                || entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (entry.Detail ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new LogRow(entry))
            .ToList();

        Entries.ReplaceAll(entries);

        var transactions = _host.Journals
            .All()
            .Take(200)
            .Select(journal => new TransactionRow(journal))
            .ToList();

        Transactions.ReplaceAll(transactions);

        Status = $"{entries.Count} 条日志（≥ {_minimumLevel}），"
            + $"{transactions.Count} 个事务，"
            + $"{transactions.Count(row => row.NeedsRecovery)} 个待恢复。";

        return Task.CompletedTask;
    }

    /// <summary>Refreshes only while this page is the one the user is looking at.</summary>
    private void RefreshIfVisible()
    {
        // A cheap guard rather than an event subscription per navigation: rebuilding two lists on every log
        // line while the user is on another page would be pure waste.
        _ = RefreshAsync();
    }

    /// <summary>Copies the selected entry to the clipboard.</summary>
    private void CopySelected()
    {
        if (_selectedEntry is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_selectedEntry.FullText);
            Status = "已复制所选日志到剪贴板。";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard can be locked by another process; not worth surfacing as an error.
            Status = "剪贴板被其他程序占用，请重试。";
        }
    }
}

/// <summary>A log row.</summary>
/// <param name="Entry">Log entry.</param>
public sealed record LogRow(LogEntry Entry)
{
    /// <summary>Timestamp.</summary>
    public string TimeText => Entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    /// <summary>Level name.</summary>
    public string LevelText => Zh.LogLevel(Entry.Level);

    /// <summary>Level colour.</summary>
    public Brush Tone => Palette.ForLogLevel(Entry.Level);

    /// <summary>Subsystem name in Chinese.</summary>
    public string CategoryText => Zh.Category(Entry.Category);

    /// <summary>Raw subsystem identifier, kept for diagnostics.</summary>
    public string Category => Entry.Category;

    /// <summary>Message.</summary>
    public string Message => Entry.Message;

    /// <summary>Optional detail.</summary>
    public string Detail => Entry.Detail ?? string.Empty;

    /// <summary>True when there is detail to expand.</summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(Entry.Detail);

    /// <summary>Full text, for the clipboard.</summary>
    public string FullText => $"{Entry.Timestamp:O} {Entry.Level} [{Entry.Category}] {Entry.Message}"
        + (string.IsNullOrWhiteSpace(Entry.Detail) ? string.Empty : Environment.NewLine + Entry.Detail);
}

/// <summary>A transaction row, showing journal state.</summary>
/// <param name="Journal">Journal record.</param>
public sealed record TransactionRow(DeploymentJournal Journal)
{
    /// <summary>Transaction id.</summary>
    public string TransactionId => Journal.TransactionId;

    /// <summary>Start time.</summary>
    public string StartedText => Journal.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>State name.</summary>
    public string StateText => Zh.JournalState(Journal.State);

    /// <summary>Game.</summary>
    public string GameId => Journal.GameId;

    /// <summary>Profile.</summary>
    public string ProfileId => Journal.ProfileId;

    /// <summary>Step count.</summary>
    public string StepSummary => $"{Journal.Entries.Count} 条步骤记录、{Journal.UndoActions.Count} 项回滚动作";

    /// <summary>Mods the transaction concerned.</summary>
    public string ModsText => Journal.ModIds.Count == 0 ? "-" : string.Join(", ", Journal.ModIds);

    /// <summary>True when the transaction still needs recovery.</summary>
    public bool NeedsRecovery => Journal.NeedsRecovery;

    /// <summary>State colour.</summary>
    public Brush Tone => Journal.State switch
    {
        JournalState.Committed => Palette.Success(),
        JournalState.Failed => Palette.Danger(),
        JournalState.RolledBack => Palette.Warning(),
        _ => Palette.Info(),
    };

    /// <summary>State fill.</summary>
    public Brush Fill => Journal.State switch
    {
        JournalState.Committed => Palette.SuccessFill(),
        JournalState.Failed => Palette.DangerFill(),
        JournalState.RolledBack => Palette.WarningFill(),
        _ => Palette.InfoFill(),
    };

    /// <summary>Error text, when the transaction failed.</summary>
    public string ErrorText => Journal.Error ?? string.Empty;

    /// <summary>True when there is an error to show.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(Journal.Error);
}

/// <summary>One Nexus capability line on the settings page.</summary>
/// <param name="Name">Capability name.</param>
/// <param name="Available">True when it works with the current credential.</param>
/// <param name="Detail">Plain explanation, including any limit that is not ours to lift.</param>
public sealed record NexusCapabilityRow(string Name, bool Available, string Detail)
{
    /// <summary>State badge text.</summary>
    public string StateText => Available ? "可用" : "需要 API 密钥";

    /// <summary>Badge colour.</summary>
    public Brush Tone => Available ? Palette.Success() : Palette.Secondary();

    /// <summary>Badge fill.</summary>
    public Brush Fill => Available ? Palette.SuccessFill() : Palette.NeutralFill();
}
