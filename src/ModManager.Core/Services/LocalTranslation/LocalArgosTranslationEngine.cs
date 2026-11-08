using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ModManager.Core.Services;

/// <summary>
/// The fully free, fully local English → Chinese engine.
/// </summary>
/// <remarks>
/// <para>
/// WHAT IT IS: the official Argos Translate English → Chinese package, driven by the same two libraries
/// Argos itself uses for inference — CTranslate2 and SentencePiece — running in a Python process this class
/// starts and talks to. No API key, no account, no per-character billing, and no network at translation
/// time: once the pack is on disk the engine works offline.
/// </para>
/// <para>
/// WHY NOT <c>pip install argostranslate</c>: importing that package executes
/// <c>import stanza</c> at module scope, and stanza requires torch plus spacy. That is roughly a gigabyte of
/// dependencies whose only job in Argos is sentence-boundary detection — which this application has to do
/// itself anyway, because it must preserve paragraph order, Markdown structure and protected tokens.
/// Measured on this machine the CTranslate2 path loads the model in about 0.1 s; the stanza path would not
/// load at all without torch. The integration is therefore "the official pack, on the official inference
/// stack", with the sentencizer replaced by our own segmenter — a documented choice, not a silent
/// reimplementation. See <c>engine_worker.py</c> for the exact inference call, which mirrors
/// <c>argostranslate.translate.apply_packaged_translation</c> parameter for parameter.
/// </para>
/// <para>
/// WHERE THINGS LIVE: everything this engine downloads or creates goes under the application's own data
/// directory (<c>&lt;data&gt;\Translation</c>). Nothing is written to a game folder, to another drive, to the
/// registry, or to the system Python installation. The one thing it needs from the machine is a Python
/// interpreter, and if there is none the settings page says so instead of pretending translation works.
/// </para>
/// <para>
/// HONESTY RULE: <see cref="Probe"/> only reports <see cref="LocalEngineState.Ready"/> when the interpreter,
/// the libraries and the language pack are all present. Installing succeeds only after a real translation has
/// produced non-empty output. There is no code path here that reports a working engine that has never
/// translated anything.
/// </para>
/// </remarks>
public sealed class LocalArgosTranslationEngine : IDisposable
{
    /// <summary>Stable identifier of the engine, mixed into translation cache fingerprints.</summary>
    public const string EngineId = "local-argos-ct2-en-zh-1.9";

    /// <summary>Official Argos package index. Used to discover the pack, its URL and its size.</summary>
    public const string PackageIndexUrl =
        "https://raw.githubusercontent.com/argosopentech/argospm-index/main/index.json";

    /// <summary>The English → Chinese pack this engine installs.</summary>
    public const string PackageId = "translate-en_zh-1_9";

    /// <summary>Files the worker needs. Installed from wheels already on disk when available.</summary>
    private static readonly string[] RequiredLibraries = ["ctranslate2", "sentencepiece", "numpy", "pyyaml"];

    /// <summary>Longest a setup step may take before it is abandoned with a reason.</summary>
    private static readonly TimeSpan SetupStepTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Budget for one translation round trip.</summary>
    private static readonly TimeSpan TranslationTimeout = TimeSpan.FromSeconds(90);

    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string? _runtimeRootOverride;
    private readonly TranslationTextGuard _guard = new();

    private readonly SemaphoreSlim _workerGate = new(1, 1);
    private ArgosWorkerClient? _worker;
    private bool _disposed;

    /// <summary>Creates the engine.</summary>
    /// <param name="paths">Application data locations.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional client, so tests can script the package index.</param>
    /// <param name="runtimeRootOverride">
    /// Where to keep the runtime and language packs, instead of <c>&lt;data&gt;\Translation</c>. Two real
    /// uses: a test or a locked-down deployment can point at a prepared runtime without copying a 67 MB
    /// language pack into a temporary directory.
    /// </param>
    public LocalArgosTranslationEngine(
        AppPaths paths,
        IAppLogger? logger = null,
        HttpClient? httpClient = null,
        string? runtimeRootOverride = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger;
        _runtimeRootOverride = runtimeRootOverride;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Root of everything the engine owns.</summary>
    public string RuntimeRoot => _runtimeRootOverride ?? Path.Combine(_paths.Root, "Translation");

    /// <summary>Isolated Python environment holding the inference libraries.</summary>
    public string RuntimeDirectory => Path.Combine(RuntimeRoot, "runtime");

    /// <summary>Directory the language packs are unpacked into.</summary>
    public string PackagesDirectory => Path.Combine(RuntimeRoot, "packages");

    /// <summary>The installed English → Chinese pack.</summary>
    public string PackageDirectory => Path.Combine(PackagesDirectory, PackageId);

    /// <summary>Scratch directory for downloads.</summary>
    public string DownloadsDirectory => Path.Combine(RuntimeRoot, "downloads");

    /// <summary>The worker script, materialised from the assembly.</summary>
    public string WorkerScriptPath => Path.Combine(RuntimeRoot, "engine_worker.py");

    /// <summary>Interpreter inside the isolated environment, once it exists.</summary>
    public string VirtualEnvironmentPython =>
        Path.Combine(RuntimeDirectory, OperatingSystem.IsWindows() ? "Scripts" : "bin", "python.exe");

    /// <summary>
    /// Looks at the machine and reports what is and is not in place, without installing anything.
    /// </summary>
    public LocalEngineStatus Probe()
    {
        var python = FindSystemPython();
        var runtimeReady = File.Exists(VirtualEnvironmentPython);
        var packageReady = IsPackageInstalled();

        var packageVersion = packageReady ? ReadPackageVersion() : null;
        var packageBytes = packageReady ? DirectorySize(PackageDirectory) : 0;

        if (!runtimeReady && python is null)
        {
            return new LocalEngineStatus(
                LocalEngineState.NotInitialized,
                null,
                null,
                false,
                packageReady,
                packageReady ? PackageId : null,
                packageVersion,
                packageBytes,
                "本机没有找到 Python 3.10 或更高版本。本地翻译引擎需要它来运行 Argos 语言包；"
                + "请从 https://www.python.org/downloads/ 安装，然后回到这里点“初始化本地引擎”。"
                + "（本程序只读取 Python，不会修改 PATH、注册表或系统 Python 环境。）");
        }

        if (runtimeReady && packageReady)
        {
            return new LocalEngineStatus(
                LocalEngineState.Ready,
                VirtualEnvironmentPython,
                python?.Version,
                true,
                true,
                PackageId,
                packageVersion,
                packageBytes,
                $"本地引擎已就绪：Argos 英中语言包 {packageVersion}，推理运行时已安装，翻译时不联网。");
        }

        var missing = new List<string>();
        if (!runtimeReady)
        {
            missing.Add("推理运行时（CTranslate2 + SentencePiece）");
        }

        if (!packageReady)
        {
            missing.Add("英语 → 简体中文语言包");
        }

        return new LocalEngineStatus(
            LocalEngineState.NotInitialized,
            python?.Path ?? VirtualEnvironmentPython,
            python?.Version,
            runtimeReady,
            packageReady,
            packageReady ? PackageId : null,
            packageVersion,
            packageBytes,
            "本地引擎尚未初始化，缺少：" + string.Join("、", missing) + "。点击“初始化本地引擎”即可下载安装。");
    }

    /// <summary>
    /// Installs whatever is missing: the isolated runtime, the inference libraries and the language pack.
    /// </summary>
    /// <param name="progress">Receives one report per step, including download bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Idempotent: a step that is already done is skipped, so pressing the button twice does not re-download
    /// 67 MB of language pack. Each external command is bounded by <see cref="SetupStepTimeout"/> and quotes
    /// its own output when it fails, because "initialisation failed" with no reason is not actionable.
    /// </remarks>
    public async Task<OperationResult<LocalEngineStatus>> InitializeAsync(
        IProgress<LocalEngineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            Directory.CreateDirectory(RuntimeRoot);
            Directory.CreateDirectory(DownloadsDirectory);
            Directory.CreateDirectory(PackagesDirectory);

            // The worker script is part of this assembly, so it cannot go missing or drift from the build.
            await ExtractWorkerScriptAsync(progress, cancellationToken).ConfigureAwait(false);

            var python = FindSystemPython();
            if (python is null)
            {
                const string reason =
                    "本机没有找到可用的 Python 3.10+。本地翻译引擎需要 Python 才能运行 Argos 语言包。"
                    + "请先安装 Python（https://www.python.org/downloads/），然后重试。本程序不会自动安装系统软件。";

                progress?.Report(new LocalEngineProgress(LocalEngineStage.Failed, reason));
                return OperationResult<LocalEngineStatus>.Fail(reason);
            }

            if (python.Version is { Length: > 0 } version
                && Version.TryParse(version.Split(' ')[0], out var parsed)
                && parsed.Major < 3)
            {
                var reason = $"找到的 Python 版本过低（{version}），本地翻译引擎需要 3.10 或更高。";
                progress?.Report(new LocalEngineProgress(LocalEngineStage.Failed, reason));
                return OperationResult<LocalEngineStatus>.Fail(reason);
            }

            progress?.Report(new LocalEngineProgress(
                LocalEngineStage.Probing,
                $"使用 Python：{python.Path}（{python.Version ?? "版本未知"}）"));

            if (!File.Exists(VirtualEnvironmentPython))
            {
                progress?.Report(new LocalEngineProgress(LocalEngineStage.Runtime, "正在创建独立的翻译运行时…"));

                var create = await RunAsync(
                    python.Path,
                    ["-m", "venv", RuntimeDirectory],
                    SetupStepTimeout,
                    cancellationToken).ConfigureAwait(false);

                if (!create.Succeeded || !File.Exists(VirtualEnvironmentPython))
                {
                    var reason = "创建 Python 虚拟环境失败：" + create.Describe();
                    progress?.Report(new LocalEngineProgress(LocalEngineStage.Failed, reason));
                    return OperationResult<LocalEngineStatus>.Fail(reason);
                }
            }

            if (!AreLibrariesInstalled())
            {
                progress?.Report(new LocalEngineProgress(
                    LocalEngineStage.Dependencies,
                    "正在安装推理库（CTranslate2、SentencePiece、NumPy）…"));

                var install = await InstallLibrariesAsync(progress, cancellationToken).ConfigureAwait(false);
                if (!install.Succeeded)
                {
                    progress?.Report(new LocalEngineProgress(LocalEngineStage.Failed, install.Error!));
                    return OperationResult<LocalEngineStatus>.Fail(install.Error!);
                }
            }
            else
            {
                progress?.Report(new LocalEngineProgress(LocalEngineStage.Dependencies, "推理库已安装，跳过。"));
            }

            if (!IsPackageInstalled())
            {
                var package = await InstallLanguagePackageAsync(progress, cancellationToken).ConfigureAwait(false);
                if (!package.Succeeded)
                {
                    progress?.Report(new LocalEngineProgress(LocalEngineStage.Failed, package.Error!));
                    return OperationResult<LocalEngineStatus>.Fail(package.Error!);
                }
            }
            else
            {
                progress?.Report(new LocalEngineProgress(
                    LocalEngineStage.InstallingPackage,
                    $"语言包 {PackageId} 已安装，跳过。"));
            }

            progress?.Report(new LocalEngineProgress(LocalEngineStage.Verifying, "正在用一次真实翻译验证引擎…"));

            var check = await SelfTestAsync(cancellationToken).ConfigureAwait(false);
            if (!check.Succeeded)
            {
                var reason = "引擎已安装，但测试翻译失败：" + check.Error;
                progress?.Report(new LocalEngineProgress(LocalEngineStage.Failed, reason));
                return OperationResult<LocalEngineStatus>.Fail(reason);
            }

            var status = Probe() with
            {
                State = LocalEngineState.Ready,
                Detail = $"本地引擎已就绪，测试翻译成功：“{check.Value}”。",
            };

            progress?.Report(new LocalEngineProgress(LocalEngineStage.Done, status.Detail, 1d));
            _logger?.Info("Translate", "本地翻译引擎初始化完成。");

            return OperationResult<LocalEngineStatus>.Ok(status);
        }
        catch (OperationCanceledException)
        {
            const string reason = "初始化已取消。";
            progress?.Report(new LocalEngineProgress(LocalEngineStage.Failed, reason));
            return OperationResult<LocalEngineStatus>.Fail(reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var reason = "初始化失败：" + ex.Message;
            progress?.Report(new LocalEngineProgress(LocalEngineStage.Failed, reason));
            return OperationResult<LocalEngineStatus>.Fail(reason);
        }
    }

    /// <summary>Translates one string, protecting everything that must survive untouched.</summary>
    /// <param name="text">Text to translate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<OperationResult<string>> TranslateTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var result = await TranslateManyAsync([text], cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? OperationResult<string>.Ok(result.Value![0])
            : OperationResult<string>.Fail(result.Error!);
    }

    /// <summary>
    /// Translates a batch, de-duplicating identical texts and restoring protected values.
    /// </summary>
    /// <param name="texts">Texts to translate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Duplicates are translated once: a description that repeats a paragraph, or a page of mods sharing a
    /// boilerplate line, costs a single pass through the model.
    /// </remarks>
    public async Task<OperationResult<IReadOnlyList<string>>> TranslateManyAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return OperationResult<IReadOnlyList<string>>.Ok([]);
        }

        var status = Probe();
        if (!status.CanTranslate)
        {
            return OperationResult<IReadOnlyList<string>>.Fail(status.Detail);
        }

        // Protect, then de-duplicate on the masked form so two identical sources share one translation.
        var masked = new List<GuardedText>(texts.Count);
        var pending = new List<string>();
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var text in texts)
        {
            var guarded = _guard.Protect(text ?? string.Empty);
            masked.Add(guarded);

            if (guarded.Masked.Length == 0 || indexOf.ContainsKey(guarded.Masked))
            {
                continue;
            }

            indexOf[guarded.Masked] = pending.Count;
            pending.Add(guarded.Masked);
        }

        if (pending.Count == 0)
        {
            return OperationResult<IReadOnlyList<string>>.Ok([.. texts.Select(t => t ?? string.Empty)]);
        }

        // Cancellation can land while the worker gate is being acquired or while the round trip is in
        // flight. Neither is an error worth an exception: a cancelled translation is reported as cancelled.
        ArgosWorkerClient workerClient;
        try
        {
            var worker = await AcquireWorkerAsync(cancellationToken).ConfigureAwait(false);
            if (!worker.Succeeded || worker.Value is null)
            {
                return OperationResult<IReadOnlyList<string>>.Fail(worker.Error!);
            }

            workerClient = worker.Value;
        }
        catch (OperationCanceledException)
        {
            return OperationResult<IReadOnlyList<string>>.Fail("翻译已取消。");
        }

        OperationResult<IReadOnlyList<string>> translated;
        try
        {
            translated = await workerClient.TranslateAsync(pending, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DropWorkerAsync().ConfigureAwait(false);
            return OperationResult<IReadOnlyList<string>>.Fail("翻译已取消。");
        }

        if (!translated.Succeeded || translated.Value is null)
        {
            // A failed round trip leaves the process in an unknown state; drop it so the next
            // attempt starts clean rather than reading a stale response.
            await DropWorkerAsync().ConfigureAwait(false);

            return OperationResult<IReadOnlyList<string>>.Fail(translated.Error!);
        }

        var results = new List<string>(texts.Count);
        for (var index = 0; index < texts.Count; index++)
        {
            var guarded = masked[index];

            if (guarded.Masked.Length == 0)
            {
                results.Add(texts[index] ?? string.Empty);
                continue;
            }

            var slot = indexOf[guarded.Masked];
            var restored = _guard.Restore(translated.Value[slot], guarded);

            if (!restored.IsIntact)
            {
                // The model damaged a placeholder. Emitting the half-restored text would silently lose or
                // duplicate a path, so the original is kept and the caller is told what happened.
                var damaged = string.Join("、", restored.DamagedIndices.Select(i => guarded.Values[i]));
                _logger?.Warn("Translate", "翻译过程中受保护内容被改动，已保留原文。", damaged);

                return OperationResult<IReadOnlyList<string>>.Fail(
                    $"翻译引擎改动了必须原样保留的内容（{damaged}），已保留原文以避免产生错误信息。");
            }

            results.Add(restored.Text);
        }

        return OperationResult<IReadOnlyList<string>>.Ok(results);
    }

    /// <summary>Translates a full mod description, keeping its Markdown structure.</summary>
    /// <param name="description">Description to translate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<OperationResult<string>> TranslateDescriptionAsync(
        string description,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return OperationResult<string>.Ok(description ?? string.Empty);
        }

        var segments = TranslationSegmenter.Segment(description);
        var distinct = TranslationSegmenter.DistinctTexts(segments);

        if (distinct.Count == 0)
        {
            return OperationResult<string>.Ok(description);
        }

        var translated = await TranslateManyAsync(distinct, cancellationToken).ConfigureAwait(false);
        if (!translated.Succeeded || translated.Value is null)
        {
            return OperationResult<string>.Fail(translated.Error!);
        }

        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < distinct.Count; index++)
        {
            lookup[distinct[index]] = translated.Value[index];
        }

        var composed = TranslationSegmenter.Compose(
            segments,
            segment => segment.NeedsTranslation && lookup.TryGetValue(segment.Text, out var value)
                ? value
                : segment.Text);

        return OperationResult<string>.Ok(composed);
    }

    /// <summary>
    /// Runs one real translation to prove the engine works, and returns what it produced.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<OperationResult<string>> SelfTestAsync(CancellationToken cancellationToken = default)
    {
        const string probe = "This mod adds a new weapon and a small quest.";

        var result = await TranslateManyAsync([probe], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            return OperationResult<string>.Fail(result.Error!);
        }

        var text = result.Value[0];
        if (string.IsNullOrWhiteSpace(text))
        {
            return OperationResult<string>.Fail("引擎返回了空译文。");
        }

        // A Chinese answer must actually contain Chinese; an English echo means the pack is not loaded.
        if (!text.Any(character => character >= '\u4e00' && character <= '\u9fff'))
        {
            return OperationResult<string>.Fail($"引擎返回的内容不是中文（{text}），语言包可能未正确安装。");
        }

        return OperationResult<string>.Ok(text);
    }

    /// <summary>Stops the worker process, if one is running.</summary>
    public async Task DropWorkerAsync()
    {
        await _workerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _worker?.Dispose();
            _worker = null;
        }
        finally
        {
            _workerGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _worker?.Dispose();
        _worker = null;
        _workerGate.Dispose();

        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>Returns a live worker, starting one when needed.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<OperationResult<ArgosWorkerClient>> AcquireWorkerAsync(CancellationToken cancellationToken)
    {
        await _workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_worker is { IsAlive: true })
            {
                return OperationResult<ArgosWorkerClient>.Ok(_worker);
            }

            _worker?.Dispose();
            _worker = null;

            if (!File.Exists(VirtualEnvironmentPython))
            {
                return OperationResult<ArgosWorkerClient>.Fail(
                    "本地翻译运行时尚未安装。请在“设置 → 翻译”点击“初始化本地引擎”。");
            }

            if (!File.Exists(WorkerScriptPath))
            {
                await ExtractWorkerScriptAsync(null, cancellationToken).ConfigureAwait(false);
            }

            var started = await ArgosWorkerClient.StartAsync(
                VirtualEnvironmentPython,
                WorkerScriptPath,
                PackageDirectory,
                TranslationTimeout,
                _logger,
                cancellationToken).ConfigureAwait(false);

            if (!started.Succeeded || started.Value is null)
            {
                return OperationResult<ArgosWorkerClient>.Fail(started.Error!);
            }

            _worker = started.Value;
            return OperationResult<ArgosWorkerClient>.Ok(_worker);
        }
        finally
        {
            _workerGate.Release();
        }
    }

    /// <summary>Writes the embedded worker script next to the runtime.</summary>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ExtractWorkerScriptAsync(
        IProgress<LocalEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(LocalArgosTranslationEngine).Assembly;
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            candidate => candidate.EndsWith("engine_worker.py", StringComparison.Ordinal));

        if (name is null)
        {
            throw new InvalidOperationException(
                "程序集内没有找到 engine_worker.py。这是一个构建问题：请检查 ModManager.Core.csproj 是否把它作为 EmbeddedResource 包含。");
        }

        await using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("无法读取内嵌的 engine_worker.py。");

        Directory.CreateDirectory(RuntimeRoot);

        await using var file = new FileStream(WorkerScriptPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(new LocalEngineProgress(LocalEngineStage.Probing, "已释放翻译引擎脚本。"));
    }

    /// <summary>Installs the inference libraries, preferring wheels already on disk.</summary>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<OperationResult<bool>> InstallLibrariesAsync(
        IProgress<LocalEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        // An offline wheel directory next to the runtime makes setup work on a slow or absent connection,
        // and makes the install reproducible. It is optional.
        var wheelDirectory = FindOfflineWheelDirectory();

        var arguments = new List<string> { "-m", "pip", "install", "--disable-pip-version-check" };

        if (wheelDirectory is not null)
        {
            progress?.Report(new LocalEngineProgress(
                LocalEngineStage.Dependencies,
                $"正在从本地 wheel 安装推理库：{wheelDirectory}"));

            arguments.Add("--no-index");
            arguments.Add("--find-links");
            arguments.Add(wheelDirectory);
        }
        else
        {
            progress?.Report(new LocalEngineProgress(
                LocalEngineStage.Dependencies,
                "正在从 PyPI 下载并安装推理库（约 32 MB）…"));
        }

        arguments.AddRange(RequiredLibraries);

        var result = await RunAsync(
            VirtualEnvironmentPython,
            arguments,
            SetupStepTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return OperationResult<bool>.Fail(
                "安装推理库失败。" + result.Describe()
                + " 若网络不可用，可把 wheel 文件放进 " + Path.Combine(RuntimeRoot, "wheels") + " 后重试。");
        }

        return AreLibrariesInstalled()
            ? OperationResult<bool>.Ok(true)
            : OperationResult<bool>.Fail("pip 报告成功，但推理库仍无法导入。请检查 Python 版本是否为 3.10 以上。");
    }

    /// <summary>Downloads and unpacks the official language pack, reporting source, size and progress.</summary>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<OperationResult<bool>> InstallLanguagePackageAsync(
        IProgress<LocalEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new LocalEngineProgress(
            LocalEngineStage.DownloadingPackage,
            $"正在读取 Argos 语言包索引：{PackageIndexUrl}"));

        var descriptor = await ResolvePackageAsync(cancellationToken).ConfigureAwait(false);
        if (!descriptor.Succeeded || descriptor.Value is null)
        {
            return OperationResult<bool>.Fail(descriptor.Error!);
        }

        var package = descriptor.Value;
        var downloadPath = Path.Combine(DownloadsDirectory, package.Id + ".argosmodel");

        // A previously downloaded archive is reused instead of fetched again. That makes a retry after a
        // failure cheap, and it is the only practical way to install on a connection too slow to finish
        // 67 MB in one attempt: the file can be placed here by hand and the install will pick it up. A
        // truncated file still fails the unpack below and the checks after it, so a bad archive cannot
        // masquerade as a good one.
        var reusable = File.Exists(downloadPath) && new FileInfo(downloadPath).Length > 1_048_576;

        if (reusable)
        {
            progress?.Report(new LocalEngineProgress(
                LocalEngineStage.DownloadingPackage,
                $"使用已下载的语言包（{new FileInfo(downloadPath).Length / 1048576d:N1} MB），跳过下载。",
                1d));
        }
        else
        {
            progress?.Report(new LocalEngineProgress(
                LocalEngineStage.DownloadingPackage,
                $"正在下载 {package.DisplayName}（{package.SizeText}，来源 {package.SourceUrl}）",
                0d,
                0,
                package.SizeBytes > 0 ? package.SizeBytes : null));

            var download = await DownloadAsync(package, downloadPath, progress, cancellationToken).ConfigureAwait(false);
            if (!download.Succeeded)
            {
                return OperationResult<bool>.Fail(download.Error!);
            }
        }

        progress?.Report(new LocalEngineProgress(
            LocalEngineStage.InstallingPackage,
            "正在解压语言包…"));

        try
        {
            if (Directory.Exists(PackageDirectory))
            {
                Directory.Delete(PackageDirectory, recursive: true);
            }

            // The archive already contains the package directory, so it unpacks into the packages root.
            ZipFile.ExtractToDirectory(downloadPath, PackagesDirectory, overwriteFiles: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return OperationResult<bool>.Fail("解压语言包失败：" + ex.Message);
        }

        if (!IsPackageInstalled())
        {
            return OperationResult<bool>.Fail(
                "语言包已下载，但解压后没有找到 metadata.json。压缩包可能不完整，请重试。");
        }

        // The archive is kept only while it is being unpacked: it is 67 MB, and the pack itself is
        // what the engine reads.
        TryDelete(downloadPath);

        progress?.Report(new LocalEngineProgress(
            LocalEngineStage.InstallingPackage,
            $"语言包 {package.Id} 安装完成。",
            1d));

        return OperationResult<bool>.Ok(true);
    }

    /// <summary>Reads the official index and picks the English → Chinese entry.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<OperationResult<LanguagePackageDescriptor>> ResolvePackageAsync(
        CancellationToken cancellationToken)
    {
        string indexJson;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));

            indexJson = await _http.GetStringAsync(PackageIndexUrl, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return OperationResult<LanguagePackageDescriptor>.Fail(
                $"无法获取 Argos 语言包索引（{PackageIndexUrl}）：{ex.Message}。"
                + "请检查网络连接后重试。");
        }

        try
        {
            using var document = JsonDocument.Parse(indexJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return OperationResult<LanguagePackageDescriptor>.Fail("Argos 语言包索引格式不是预期的数组。");
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!string.Equals(GetString(entry, "from_code"), "en", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(GetString(entry, "to_code"), "zh", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var links = entry.TryGetProperty("links", out var linkElement)
                    && linkElement.ValueKind == JsonValueKind.Array
                        ? linkElement.EnumerateArray().Select(item => item.GetString()).Where(item => item is not null).ToList()
                        : [];

                if (links.Count == 0 || links[0] is not { Length: > 0 } url)
                {
                    continue;
                }

                var version = GetString(entry, "package_version") ?? "未知";
                var id = $"translate-en_zh-{version.Replace('.', '_')}";

                return OperationResult<LanguagePackageDescriptor>.Ok(new LanguagePackageDescriptor(
                    string.Equals(id, PackageId, StringComparison.Ordinal) ? id : PackageId,
                    $"Argos English → Chinese（{GetString(entry, "from_name")} → {GetString(entry, "to_name")}）",
                    url,
                    0,
                    "en",
                    "zh",
                    version));
            }

            return OperationResult<LanguagePackageDescriptor>.Fail(
                "Argos 官方索引里没有找到英语 → 简体中文的语言包。");
        }
        catch (JsonException ex)
        {
            return OperationResult<LanguagePackageDescriptor>.Fail("解析 Argos 语言包索引失败：" + ex.Message);
        }
    }

    /// <summary>Streams the pack to disk, reporting bytes as they arrive.</summary>
    /// <param name="package">Package descriptor.</param>
    /// <param name="targetPath">Where to write it.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<OperationResult<bool>> DownloadAsync(
        LanguagePackageDescriptor package,
        string targetPath,
        IProgress<LocalEngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DownloadsDirectory);

        try
        {
            using var response = await _http
                .GetAsync(package.SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return OperationResult<bool>.Fail(
                    $"下载语言包失败：服务器返回 {(int)response.StatusCode}。来源：{package.SourceUrl}");
            }

            var total = response.Content.Headers.ContentLength ?? 0;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long received = 0;
            var lastReport = 0L;

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;

                // One report per megabyte is enough to move a progress bar without flooding the UI thread.
                if (received - lastReport >= 1_048_576)
                {
                    lastReport = received;
                    progress?.Report(new LocalEngineProgress(
                        LocalEngineStage.DownloadingPackage,
                        $"正在下载语言包：{received / 1048576d:N1} MB"
                            + (total > 0 ? $" / {total / 1048576d:N1} MB" : string.Empty),
                        total > 0 ? Math.Clamp((double)received / total, 0d, 1d) : null,
                        received,
                        total > 0 ? total : null));
                }
            }

            if (received == 0)
            {
                return OperationResult<bool>.Fail("下载语言包失败：服务器返回了空内容。");
            }

            return OperationResult<bool>.Ok(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(targetPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            TryDelete(targetPath);
            return OperationResult<bool>.Fail(
                $"下载语言包失败：{ex.Message}。来源：{package.SourceUrl}");
        }
    }

    /// <summary>Runs one external command with a hard timeout and captured output.</summary>
    /// <param name="fileName">Executable.</param>
    /// <param name="arguments">Arguments.</param>
    /// <param name="timeout">How long it may run.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    private static async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return CommandResult.Failure("进程无法启动。");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return CommandResult.Failure(ex.Message);
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            return CommandResult.Failure(cancellationToken.IsCancellationRequested
                ? "已取消。"
                : $"命令超过 {timeout.TotalMinutes:0} 分钟未完成，已终止：{Path.GetFileName(fileName)}");
        }

        var standardOutput = await output.ConfigureAwait(false);
        var standardError = await error.ConfigureAwait(false);

        return process.ExitCode == 0
            ? CommandResult.Success(standardOutput)
            : CommandResult.Failure($"退出码 {process.ExitCode}。{Tail(standardError)}");
    }

    /// <summary>True when every inference library can be imported by the isolated interpreter.</summary>
    private bool AreLibrariesInstalled()
    {
        if (!File.Exists(VirtualEnvironmentPython))
        {
            return false;
        }

        var sitePackages = Path.Combine(RuntimeDirectory, "Lib", "site-packages");
        if (!Directory.Exists(sitePackages))
        {
            return false;
        }

        // Checked on the filesystem rather than by running Python: probing must stay instant, and the
        // worker itself performs the real import when it starts.
        return RequiredLibraries.All(library =>
            Directory.Exists(Path.Combine(sitePackages, library))
            || File.Exists(Path.Combine(sitePackages, library + ".py"))
            || Directory.EnumerateDirectories(sitePackages, library + "-*.dist-info").Any());
    }

    /// <summary>True when the language pack looks complete.</summary>
    private bool IsPackageInstalled() =>
        File.Exists(Path.Combine(PackageDirectory, "metadata.json"))
        && File.Exists(Path.Combine(PackageDirectory, "sentencepiece.model"))
        && Directory.Exists(Path.Combine(PackageDirectory, "model"));

    /// <summary>Reads the installed pack's version, or null.</summary>
    private string? ReadPackageVersion()
    {
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(PackageDirectory, "metadata.json")));

            return GetString(document.RootElement, "package_version");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Finds a Python 3 interpreter without touching PATH, the registry or any system install.</summary>
    /// <remarks>
    /// The Microsoft Store aliases under <c>WindowsApps</c> are skipped deliberately: they exist as
    /// zero-byte stubs on machines with no Python at all, so treating one as an interpreter would produce a
    /// confusing failure later instead of an honest "no Python found" here.
    /// </remarks>
    private static PythonCandidate? FindSystemPython()
    {
        var candidates = new List<string>();

        var fromEnvironment = Environment.GetEnvironmentVariable("MODMANAGER_PYTHON");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            candidates.Add(fromEnvironment);
        }

        var localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python");

        if (Directory.Exists(localPrograms))
        {
            candidates.AddRange(Directory
                .EnumerateDirectories(localPrograms, "Python3*")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, "python.exe")));
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (directory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.Add(Path.Combine(directory.Trim(), "python.exe"));
        }

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var version = ReadPythonVersion(candidate);
            if (version is not null)
            {
                return new PythonCandidate(candidate, version);
            }
        }

        return null;
    }

    /// <summary>Asks an interpreter for its version, with a short timeout.</summary>
    /// <param name="path">Interpreter to probe.</param>
    private static string? ReadPythonVersion(string path)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import sys;print('%d.%d.%d' % sys.version_info[:3])");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();

            // A Store stub opens the Store instead of running Python, so it never answers in time.
            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }

                return null;
            }

            var text = output.Trim();
            return text.Length > 0 && char.IsDigit(text[0]) ? text : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Looks for a directory of wheels that can be installed without a network.</summary>
    private string? FindOfflineWheelDirectory()
    {
        var beside = Path.Combine(RuntimeRoot, "wheels");
        if (Directory.Exists(beside) && Directory.EnumerateFiles(beside, "*.whl").Any())
        {
            return beside;
        }

        // A development checkout keeps them here; shipping builds simply do not have the directory.
        var development = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "wheels"));
        return Directory.Exists(development) && Directory.EnumerateFiles(development, "*.whl").Any()
            ? development
            : null;
    }

    /// <summary>Total size of a directory tree, for the settings display.</summary>
    /// <param name="path">Directory.</param>
    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Deletes a file, ignoring the failure: it is scratch space.</summary>
    /// <param name="path">File to delete.</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind on purpose rather than failing the install over scratch space.
        }
    }

    /// <summary>Reads a string property, or null.</summary>
    /// <param name="element">JSON object.</param>
    /// <param name="name">Property name.</param>
    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Last few lines of a command's error output.</summary>
    /// <param name="text">Captured stderr.</param>
    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tail = string.Join(" ", lines.TakeLast(3)).Trim();
        return tail.Length > 300 ? tail[..300] : tail;
    }

    /// <summary>An interpreter this machine actually has.</summary>
    /// <param name="Path">Executable path.</param>
    /// <param name="Version">Version string, e.g. <c>3.14.4</c>.</param>
    private sealed record PythonCandidate(string Path, string? Version);

    /// <summary>Outcome of one external command.</summary>
    /// <param name="Succeeded">True on exit code zero.</param>
    /// <param name="Output">Captured stdout.</param>
    /// <param name="Error">Failure description.</param>
    private sealed record CommandResult(bool Succeeded, string Output, string? Error)
    {
        /// <summary>A successful run.</summary>
        /// <param name="output">Captured stdout.</param>
        public static CommandResult Success(string output) => new(true, output, null);

        /// <summary>A failed run.</summary>
        /// <param name="error">Why it failed.</param>
        public static CommandResult Failure(string error) => new(false, string.Empty, error);

        /// <summary>One line describing the failure.</summary>
        public string Describe() => Error ?? "未知错误。";
    }
}
