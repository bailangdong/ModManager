using System.Diagnostics;
using System.IO;
using System.Text;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Acceptance tests for the free local English → Chinese engine.
/// </summary>
/// <remarks>
/// <para>
/// The rule these tests are written to: a test that proves nothing must not look like a test that passed.
/// Cases that need an installed engine assert that the engine <i>is</i> installed and then translate real
/// text through it, rather than swapping in a stub that would pass on any machine. The suite therefore fails
/// loudly, and truthfully, on a machine where the language pack was never downloaded.
/// </para>
/// <para>
/// Scratch files live under <c>artifacts/tests/local-translation</c> inside the repository, per the task's
/// data-safety rules, and are never written anywhere else.
/// </para>
/// </remarks>
public sealed class LocalTranslationTests
{
    // ---------------------------------------------------------------- state reporting

    /// <summary>
    /// With nothing installed the engine says so, names what is missing, and refuses to translate.
    /// </summary>
    [Fact]
    public void AnUninitialisedEngineExplainsWhatIsMissing()
    {
        var paths = new AppPaths(Scratch("uninitialised"));
        using var engine = new LocalArgosTranslationEngine(paths);

        var status = engine.Probe();

        Assert.Equal(LocalEngineState.NotInitialized, status.State);
        Assert.False(status.CanTranslate);
        Assert.False(status.RuntimeReady);
        Assert.False(status.PackageReady);

        // The message has to tell the user where to go, not merely that something is wrong.
        Assert.Contains("初始化", status.Detail, StringComparison.Ordinal);
    }

    /// <summary>A missing language pack is reported separately from a missing runtime.</summary>
    [Fact]
    public void AMissingLanguagePackIsNamedInTheStatus()
    {
        var root = Scratch("missing-pack");
        var paths = new AppPaths(root);
        using var engine = new LocalArgosTranslationEngine(paths);

        Directory.CreateDirectory(engine.PackagesDirectory);

        var status = engine.Probe();

        Assert.False(status.PackageReady);
        Assert.Contains("语言包", status.Detail, StringComparison.Ordinal);
    }

    /// <summary>Translating before installing fails; it never invents a translation.</summary>
    [Fact]
    public async Task TranslatingWithoutAnInstalledEngineFailsInsteadOfInventingText()
    {
        var paths = new AppPaths(Scratch("not-installed"));
        using var engine = new LocalArgosTranslationEngine(paths);

        var result = await engine.TranslateManyAsync(["This mod adds a sword."]);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Contains("初始化", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>A worker that cannot be started reports the real reason.</summary>
    [Fact]
    public async Task StartingAWorkerWithAMissingInterpreterFailsWithAReason()
    {
        var scratch = Scratch("missing-interpreter");
        var script = Path.Combine(scratch, "engine_worker.py");
        await File.WriteAllTextAsync(script, "print('never runs')");

        var missing = Path.Combine(scratch, "definitely-not-python.exe");

        var result = await ArgosWorkerClient.StartAsync(
            missing,
            script,
            scratch,
            TimeSpan.FromSeconds(5),
            logger: null);

        Assert.False(result.Succeeded);
        Assert.Contains("Python", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>A worker that fails to load its model is reported verbatim, not swallowed.</summary>
    [Fact]
    public async Task AWorkerThatCannotLoadItsModelReportsItsOwnError()
    {
        var python = RequirePython();
        var scratch = Scratch("fatal-worker");
        var script = Path.Combine(scratch, "fatal_worker.py");

        await File.WriteAllTextAsync(
            script,
            "import sys\n"
            + "sys.stdout.reconfigure(encoding='utf-8')\n"
            + "print('{\"id\":0,\"ok\":false,\"fatal\":true,\"error\":\"模拟的语言包损坏\"}', flush=True)\n"
            + "sys.exit(3)\n");

        var result = await ArgosWorkerClient.StartAsync(
            python,
            script,
            scratch,
            TimeSpan.FromSeconds(20),
            logger: null);

        Assert.False(result.Succeeded);
        Assert.Contains("模拟的语言包损坏", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>A worker that dies without answering is reported rather than waited on forever.</summary>
    [Fact]
    public async Task AWorkerThatExitsImmediatelyIsReported()
    {
        var python = RequirePython();
        var scratch = Scratch("silent-worker");
        var script = Path.Combine(scratch, "silent_worker.py");

        await File.WriteAllTextAsync(script, "import sys\nsys.exit(0)\n");

        var result = await ArgosWorkerClient.StartAsync(
            python,
            script,
            scratch,
            TimeSpan.FromSeconds(20),
            logger: null);

        Assert.False(result.Succeeded);
        Assert.Contains("就绪", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>A worker that accepts a request and never answers is killed on the timeout.</summary>
    /// <remarks>
    /// This is the case that matters most in practice: a wedged engine must surface as an error the user can
    /// read, not as an interface that stops responding.
    /// </remarks>
    [Fact]
    public async Task AWorkerThatNeverAnswersIsStoppedByTheTimeout()
    {
        var python = RequirePython();
        var scratch = Scratch("hanging-worker");
        var script = Path.Combine(scratch, "hanging_worker.py");

        await File.WriteAllTextAsync(
            script,
            "import sys, time\n"
            + "sys.stdout.reconfigure(encoding='utf-8')\n"
            + "print('{\"id\":0,\"ok\":true,\"ready\":true,\"package\":\"fake\"}', flush=True)\n"
            + "for line in sys.stdin:\n"
            + "    time.sleep(600)\n");

        var started = await ArgosWorkerClient.StartAsync(
            python,
            script,
            scratch,
            TimeSpan.FromSeconds(5),
            logger: null);

        Assert.True(started.Succeeded, started.Error);
        using var worker = started.Value!;

        var stopwatch = Stopwatch.StartNew();
        var result = await worker.TranslateAsync(["hello"]);
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.Contains("超时", result.Error!, StringComparison.Ordinal);

        // The budget has to be honoured, plus a little room for process startup and teardown.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"超时未被遵守：{stopwatch.Elapsed}");
    }

    /// <summary>Cancelling a translation is reported as a cancellation.</summary>
    [Fact]
    public async Task ACancelledTranslationIsReportedAsCancelled()
    {
        var paths = new AppPaths(Scratch("cancelled"));
        using var engine = new LocalArgosTranslationEngine(
            paths,
            runtimeRootOverride: SharedRuntimeRoot());

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await engine.TranslateManyAsync(["This mod adds a sword."], cancellation.Token);

        Assert.False(result.Succeeded);
    }

    // ---------------------------------------------------------------- text protection

    /// <summary>Paths, URLs, versions, identifiers and code survive a translation untouched.</summary>
    [Fact]
    public void EverythingThatMustNotChangeIsReplacedByAPlaceholder()
    {
        var guard = new TranslationTextGuard();

        const string source =
            "Install to C:\\Games\\Cyberpunk 2077\\bin\\x64 with RED4ext 1.2.3, "
            + "see https://www.nexusmods.com/cyberpunk2077/mods/107 and read `config.ini` first.";

        var guarded = guard.Protect(source);

        Assert.False(guarded.IsUnchanged);
        Assert.Contains("C:\\Games\\Cyberpunk 2077\\bin\\x64", guarded.Values);
        Assert.Contains(guarded.Values, value => value.StartsWith("https://www.nexusmods.com", StringComparison.Ordinal));
        Assert.Contains("1.2.3", guarded.Values);

        // The engine only ever sees placeholders, never the real path.
        Assert.DoesNotContain("Cyberpunk 2077", guarded.Masked, StringComparison.Ordinal);

        var restored = guard.Restore(guarded.Masked, guarded);

        Assert.True(restored.IsIntact);
        Assert.Equal(source, restored.Text);
    }

    /// <summary>A placeholder the engine dropped is detected instead of silently losing the value.</summary>
    [Fact]
    public void ALostPlaceholderIsReportedRatherThanPatchedOver()
    {
        var guard = new TranslationTextGuard();
        var guarded = guard.Protect("Open https://example.com/mod/42 to continue.");

        // Simulate a model that ate the placeholder entirely.
        var restored = guard.Restore("打开 继续。", guarded);

        Assert.False(restored.IsIntact);
        Assert.Contains(0, restored.MissingIndices);
    }

    /// <summary>Ordinary prose is not masked into placeholders.</summary>
    [Fact]
    public void PlainProseIsLeftAlone()
    {
        var guard = new TranslationTextGuard();

        var guarded = guard.Protect("This mod adds a new weapon and a small quest to the game.");

        Assert.True(guarded.IsUnchanged);
        Assert.Equal("This mod adds a new weapon and a small quest to the game.", guarded.Masked);
    }

    /// <summary>Uppercase English words are not mistaken for acronyms.</summary>
    [Fact]
    public void UppercaseEnglishWordsAreNotTreatedAsIdentifiers()
    {
        var guard = new TranslationTextGuard();

        var guarded = guard.Protect("THE MOD IS NOT FOR YOU AND IT HAS NO MENU");

        Assert.DoesNotContain("THE", guarded.Values);
        Assert.DoesNotContain("AND", guarded.Values);
        Assert.DoesNotContain("NOT", guarded.Values);
    }

    // ---------------------------------------------------------------- segmentation

    /// <summary>Markdown structure survives segmentation and recomposition exactly.</summary>
    [Fact]
    public void MarkdownStructureIsRebuiltExactly()
    {
        const string source =
            "# Heading\n"
            + "\n"
            + "- first item\n"
            + "- second item\n"
            + "\n"
            + "```\n"
            + "load order: 1\n"
            + "```\n"
            + "\n"
            + "| Name | Version |\n"
            + "| --- | --- |\n"
            + "| RED4ext | 1.2.3 |\n";

        var segments = TranslationSegmenter.Segment(source);

        // Recomposing without translating must reproduce the input character for character.
        var composed = TranslationSegmenter.Compose(segments, segment => segment.Text);

        Assert.Equal(source, composed);
    }

    /// <summary>Code fences are never sent to the engine.</summary>
    [Fact]
    public void CodeBlocksAreNotTranslated()
    {
        const string source = "```\nrm -rf /tmp/x\n```\n";

        var segments = TranslationSegmenter.Segment(source);

        Assert.All(segments, segment => Assert.False(segment.NeedsTranslation));
    }

    /// <summary>A repeated paragraph is translated once.</summary>
    [Fact]
    public void RepeatedTextIsOnlyTranslatedOnce()
    {
        const string source = "Same line here.\nDifferent line.\nSame line here.\n";

        var distinct = TranslationSegmenter.DistinctTexts(TranslationSegmenter.Segment(source));

        Assert.Equal(2, distinct.Count);
        Assert.Contains("Same line here.", distinct);
        Assert.Contains("Different line.", distinct);
    }

    /// <summary>Paragraph order is preserved when a translation is applied out of order.</summary>
    [Fact]
    public void ParagraphOrderIsPreserved()
    {
        const string source = "First paragraph.\n\nSecond paragraph.\n\nThird paragraph.\n";

        var segments = TranslationSegmenter.Segment(source);
        var composed = TranslationSegmenter.Compose(
            segments,
            segment => segment.NeedsTranslation ? "[" + segment.Text + "]" : segment.Text);

        Assert.Equal("[First paragraph.]\n\n[Second paragraph.]\n\n[Third paragraph.]\n", composed);
    }

    /// <summary>Table pipes are kept out of the engine's reach.</summary>
    [Fact]
    public void TablePipesAreNotSentToTheEngine()
    {
        var segments = TranslationSegmenter.Segment("| Name | Version |\n");

        var texts = segments.Where(segment => segment.NeedsTranslation).Select(segment => segment.Text).ToList();

        Assert.Contains("Name", texts);
        Assert.Contains("Version", texts);
        Assert.DoesNotContain(texts, text => text.Contains('|'));
    }

    // ---------------------------------------------------------------- real engine

    /// <summary>
    /// The installed engine really translates English into Chinese.
    /// </summary>
    /// <remarks>
    /// Deliberately not mocked. A stub here would prove that the plumbing compiles while saying nothing about
    /// whether a user gets Chinese, which is the only thing this feature is for.
    /// </remarks>
    [Fact]
    public async Task TheInstalledEngineReallyTranslatesEnglishIntoChinese()
    {
        Assert.True(
            SharedEngineInstalled(),
            "本地翻译引擎未安装，无法验证真实翻译。运行“设置 → 翻译 → 初始化本地引擎”后重试。");

        var paths = new AppPaths(Scratch("real-engine"));
        using var engine = new LocalArgosTranslationEngine(paths, runtimeRootOverride: SharedRuntimeRoot());

        Assert.True(engine.Probe().CanTranslate, "共享运行时存在，但引擎报告不可用。");

        var result = await engine.TranslateManyAsync(
            ["This mod adds a new weapon and a small quest.", "A lightweight UI overhaul."]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.Value!.Count);

        foreach (var translation in result.Value!)
        {
            Assert.Contains(translation, character => character >= '\u4e00' && character <= '\u9fff');
        }
    }

    /// <summary>A real translation keeps every protected value intact end to end.</summary>
    [Fact]
    public async Task ARealTranslationDoesNotDamagePathsUrlsVersionsOrIdentifiers()
    {
        Assert.True(SharedEngineInstalled(), "本地翻译引擎未安装，无法验证真实翻译。");

        var paths = new AppPaths(Scratch("real-protection"));
        using var engine = new LocalArgosTranslationEngine(paths, runtimeRootOverride: SharedRuntimeRoot());

        const string source =
            "Install to C:\\Games\\Cyberpunk 2077\\bin\\x64 and make sure RED4ext 1.2.3 is present. "
            + "Full instructions: https://www.nexusmods.com/cyberpunk2077/mods/107";

        var result = await engine.TranslateTextAsync(source);

        Assert.True(result.Succeeded, result.Error);

        var translated = result.Value!;
        Assert.Contains("C:\\Games\\Cyberpunk 2077\\bin\\x64", translated, StringComparison.Ordinal);
        Assert.Contains("RED4ext", translated, StringComparison.Ordinal);
        Assert.Contains("1.2.3", translated, StringComparison.Ordinal);
        Assert.Contains("https://www.nexusmods.com/cyberpunk2077/mods/107", translated, StringComparison.Ordinal);
    }

    /// <summary>A long Markdown description keeps its structure through a real translation.</summary>
    [Fact]
    public async Task ARealTranslationKeepsLongMarkdownStructure()
    {
        Assert.True(SharedEngineInstalled(), "本地翻译引擎未安装，无法验证真实翻译。");

        var paths = new AppPaths(Scratch("real-markdown"));
        using var engine = new LocalArgosTranslationEngine(paths, runtimeRootOverride: SharedRuntimeRoot());

        const string source =
            "# Features\n"
            + "\n"
            + "- Adds a new weapon to the game.\n"
            + "- Reworks the inventory screen completely.\n"
            + "\n"
            + "```\n"
            + "install: copy files to Mods/\n"
            + "```\n";

        var result = await engine.TranslateDescriptionAsync(source);

        Assert.True(result.Succeeded, result.Error);

        var translated = result.Value!;
        Assert.StartsWith("# ", translated, StringComparison.Ordinal);
        Assert.Contains("- ", translated, StringComparison.Ordinal);
        Assert.Contains("```", translated, StringComparison.Ordinal);
        Assert.Contains("install: copy files to Mods/", translated, StringComparison.Ordinal);
        Assert.Contains(translated, character => character >= '\u4e00' && character <= '\u9fff');
    }

    /// <summary>The same source text is translated once and served from cache afterwards.</summary>
    [Fact]
    public async Task RepeatingATranslationHitsTheCache()
    {
        Assert.True(SharedEngineInstalled(), "本地翻译引擎未安装，无法验证缓存。");

        var scratch = Scratch("real-cache");
        var paths = new AppPaths(scratch);
        var secrets = new FakeSecretStore(null);

        using var engine = new LocalArgosTranslationEngine(paths, runtimeRootOverride: SharedRuntimeRoot());

        var settings = new TranslationSettings { Engine = TranslationEngineKind.Local };
        var service = new TranslationService(secrets, paths, settings, logger: null, httpClient: null, localEngine: engine);

        var first = await service.TranslateAsync("tyk-1", "Test Mod", "A summary.", "A description.");
        Assert.True(first.Succeeded, first.Error);
        Assert.False(first.Value!.FromCache);

        var second = await service.TranslateAsync("tyk-1", "Test Mod", "A summary.", "A description.");
        Assert.True(second.Succeeded, second.Error);
        Assert.True(second.Value!.FromCache);
        Assert.Equal(first.Value!.Title, second.Value!.Title);

        // Both the original and the translation are available: the cache stores the translation, the caller
        // keeps the source, and the model name records which engine produced it.
        Assert.Contains("本地", first.Value!.Model, StringComparison.Ordinal);
    }

    /// <summary>Changing the source text invalidates the cache and translates again.</summary>
    [Fact]
    public async Task ChangingTheSourceTextForcesARetranslation()
    {
        Assert.True(SharedEngineInstalled(), "本地翻译引擎未安装，无法验证缓存失效。");

        var paths = new AppPaths(Scratch("real-invalidation"));
        var secrets = new FakeSecretStore(null);

        using var engine = new LocalArgosTranslationEngine(paths, runtimeRootOverride: SharedRuntimeRoot());

        var settings = new TranslationSettings { Engine = TranslationEngineKind.Local };
        var service = new TranslationService(secrets, paths, settings, logger: null, httpClient: null, localEngine: engine);

        var first = await service.TranslateAsync("tyk-2", "Test Mod", "A summary.", "First version of the description.");
        Assert.True(first.Succeeded, first.Error);

        var cached = await service.TranslateAsync("tyk-2", "Test Mod", "A summary.", "First version of the description.");
        Assert.True(cached.Value!.FromCache);

        var changed = await service.TranslateAsync("tyk-2", "Test Mod", "A summary.", "Second version of the description.");
        Assert.True(changed.Succeeded, changed.Error);
        Assert.False(changed.Value!.FromCache);
    }

    /// <summary>The engine's own self test reports real Chinese output.</summary>
    [Fact]
    public async Task TheSelfTestReturnsRealChinese()
    {
        Assert.True(SharedEngineInstalled(), "本地翻译引擎未安装，无法运行自检。");

        var paths = new AppPaths(Scratch("real-selftest"));
        using var engine = new LocalArgosTranslationEngine(paths, runtimeRootOverride: SharedRuntimeRoot());

        var result = await engine.SelfTestAsync();

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(result.Value!, character => character >= '\u4e00' && character <= '\u9fff');
    }

    // ---------------------------------------------------------------- settings compatibility

    /// <summary>An existing settings file keeps working: the engine choice defaults to auto.</summary>
    [Fact]
    public void SettingsWithoutAnEngineFieldStillLoad()
    {
        const string legacy = """
            {
              "Version": 1,
              "Theme": "Light",
              "Translation": {
                "Enabled": true,
                "Endpoint": "https://api.deepseek.com/chat/completions",
                "Model": "deepseek-chat",
                "HasKey": true
              }
            }
            """;

        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(legacy);

        Assert.NotNull(settings);
        Assert.Equal(TranslationEngineKind.Auto, settings!.Translation.Engine);
        Assert.Equal("deepseek-chat", settings.Translation.Model);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Repository root, found by walking up to the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ModManager.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("ModManager.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A scratch directory inside the repository, as the task's data rules require.</summary>
    /// <param name="name">Subdirectory name.</param>
    /// <remarks>
    /// Emptied on every call. A leftover translation cache from a previous run would make the cache tests
    /// pass for the wrong reason — "the second call was cached" would be true because the first one was too.
    /// </remarks>
    private static string Scratch(string name)
    {
        var path = Path.Combine(RepositoryRoot(), "artifacts", "tests", "local-translation", name);

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>The prepared runtime under <c>tools/</c>, when it has been built.</summary>
    private static string SharedRuntimeRoot() => Path.Combine(RepositoryRoot(), "tools", "LocalTranslationEngine");

    /// <summary>True when a real English → Chinese engine is available to test against.</summary>
    private static bool SharedEngineInstalled()
    {
        var root = SharedRuntimeRoot();
        return File.Exists(Path.Combine(root, "runtime", "Scripts", "python.exe"))
            && File.Exists(Path.Combine(root, "packages", LocalArgosTranslationEngine.PackageId, "metadata.json"));
    }

    /// <summary>A Python interpreter for the worker-protocol tests, or a clear failure.</summary>
    private static string RequirePython()
    {
        var bundled = Path.Combine(SharedRuntimeRoot(), "runtime", "Scripts", "python.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Python");

        if (Directory.Exists(localPrograms))
        {
            var candidate = Directory
                .EnumerateDirectories(localPrograms, "Python3*")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, "python.exe"))
                .FirstOrDefault(File.Exists);

            if (candidate is not null)
            {
                return candidate;
            }
        }

        Assert.Fail("找不到 Python 解释器，无法运行翻译引擎进程协议测试。");
        return string.Empty;
    }
}
