using System.Text;
using ModManager.Core;
using ModManager.Core.Services;

// TranslationSmoke — real end-to-end verification of the free local English → Chinese engine.
//
// It drives the exact engine the WPF application uses and prints the Chinese it actually gets back, because
// the one claim that matters here — "this machine can translate English mod text without an API key" — cannot
// be established by a mock. Every check below asserts on real output; the exit code is 0 only when all pass.

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // A redirected console can refuse the encoding change; the output is still UTF-8 on the wire.
}

var repoRoot = FindRepoRoot();
var runtimeRoot = Path.Combine(repoRoot, "tools", "LocalTranslationEngine");
var appData = Path.Combine(repoRoot, "artifacts", "smoke", "translation-appdata");

var failures = 0;
void Check(bool condition, string step)
{
    Console.WriteLine((condition ? "PASS  " : "FAIL  ") + step);
    if (!condition)
    {
        failures++;
    }
}

static bool HasChinese(string text) =>
    text.Any(character => character >= '\u4e00' && character <= '\u9fff');

Directory.CreateDirectory(appData);
var paths = new AppPaths(appData);

// ---- optional: drive the real setup path ---------------------------------------------------------
// --initialize runs the user-facing install into the application's own data directory, so the install flow
// itself is exercised rather than assumed. Without it the smoke run reuses the prepared runtime under
// tools/ and only verifies that translation works.
if (args.Contains("--initialize"))
{
    var applicationData = Path.Combine(repoRoot, "src", "ModManager.App", "bin", "Debug", "net8.0-windows", "Data");
    var installPaths = new AppPaths(applicationData);
    installPaths.EnsureCreated();

    using var installEngine = new LocalArgosTranslationEngine(installPaths);

    Console.WriteLine("== 真实初始化：应用数据目录 ==");
    Console.WriteLine($"  目标: {installEngine.RuntimeRoot}");
    Console.WriteLine();

    var reporter = new Progress<LocalEngineProgress>(report =>
        Console.WriteLine($"  [{report.Stage,-18}] {report.Message}"));

    var initialized = await installEngine.InitializeAsync(reporter);

    Console.WriteLine();
    Console.WriteLine("  结果: " + (initialized.Succeeded ? "成功" : "失败：" + initialized.Error));
    if (initialized.Value is not null)
    {
        Console.WriteLine("  " + initialized.Value.Detail);
    }

    return initialized.Succeeded ? 0 : 1;
}

// Two runtimes can legitimately exist: the prepared one under tools/ (a developer convenience) and the one
// the application installs into its own data directory. --appdata verifies the latter, which is the one a
// user actually ends up with.
var applicationDataRoot = Path.Combine(repoRoot, "src", "ModManager.App", "bin", "Debug", "net8.0-windows", "Data");

using var engine = args.Contains("--appdata")
    ? new LocalArgosTranslationEngine(new AppPaths(applicationDataRoot))
    : new LocalArgosTranslationEngine(paths, runtimeRootOverride: runtimeRoot);

// ---- status ---------------------------------------------------------------------------------------
Console.WriteLine("== 引擎状态 ==");
var status = engine.Probe();
Console.WriteLine($"  状态          : {status.State}");
Console.WriteLine($"  Python        : {status.PythonPath} ({status.PythonVersion})");
Console.WriteLine($"  运行时        : {(status.RuntimeReady ? "已安装" : "缺失")}");
Console.WriteLine($"  语言包        : {(status.PackageReady ? $"{status.PackageId} {status.PackageVersion}，{status.PackageBytes / 1048576d:N1} MB" : "缺失")}");
Console.WriteLine($"  说明          : {status.Detail}");
Console.WriteLine();

Check(status.CanTranslate, "引擎报告可用（不需要任何 API 密钥）");
Check(status.PackageReady, "英语 → 简体中文语言包已真实安装");

// ---- a plain sentence -----------------------------------------------------------------------------
Console.WriteLine("== 真实翻译：普通英文句子 ==");
const string sentence = "This mod adds a new weapon and a small quest to the game.";
var sentenceResult = await engine.TranslateTextAsync(sentence);
Console.WriteLine($"  EN: {sentence}");
Console.WriteLine($"  ZH: {(sentenceResult.Succeeded ? sentenceResult.Value : sentenceResult.Error)}");
Check(sentenceResult.Succeeded && HasChinese(sentenceResult.Value!), "普通英文句子翻译成中文");
Console.WriteLine();

// ---- real mod titles ------------------------------------------------------------------------------
Console.WriteLine("== 真实翻译：Nexus Mod 标题 ==");
string[] titles =
[
    "Cyber Engine Tweaks",
    "Mod Engine 2",
    "Script Extender",
];

var titleResult = await engine.TranslateManyAsync(titles);
if (titleResult.Succeeded)
{
    for (var index = 0; index < titles.Length; index++)
    {
        Console.WriteLine($"  EN: {titles[index]}");
        Console.WriteLine($"  ZH: {titleResult.Value![index]}");
    }
}

Check(titleResult.Succeeded && titleResult.Value!.All(HasChinese), "真实 Mod 标题翻译成中文");
Console.WriteLine();

// ---- a real mod description -----------------------------------------------------------------------
Console.WriteLine("== 真实翻译：带 Markdown、路径、URL、版本号的 Mod 描述 ==");

const string description =
    "## Requirements\n"
    + "\n"
    + "- RED4ext 1.2.3 or newer\n"
    + "- Cyberpunk 2077 patch 2.0\n"
    + "\n"
    + "## Installation\n"
    + "\n"
    + "1. Copy the files to C:\\Games\\Cyberpunk 2077\\bin\\x64\n"
    + "2. Edit `config.ini` and change the hotkey\n"
    + "\n"
    + "```\n"
    + "load_order = 10\n"
    + "```\n"
    + "\n"
    + "Full documentation: https://wiki.redmodding.org/cyber-engine-tweaks\n";

var descriptionResult = await engine.TranslateDescriptionAsync(description);

if (descriptionResult.Succeeded)
{
    Console.WriteLine("  --- 中文译文 ---");
    foreach (var line in descriptionResult.Value!.Split('\n'))
    {
        Console.WriteLine("  " + line);
    }

    Console.WriteLine("  --- 原文（始终保留）---");
    foreach (var line in description.Split('\n'))
    {
        Console.WriteLine("  " + line);
    }
}
else
{
    Console.WriteLine("  ERROR: " + descriptionResult.Error);
}

Console.WriteLine();

if (descriptionResult.Succeeded)
{
    var translated = descriptionResult.Value!;

    Check(HasChinese(translated), "描述被真实翻译成中文");
    Check(translated.StartsWith("## ", StringComparison.Ordinal), "Markdown 标题结构保留");
    Check(translated.Contains("- ", StringComparison.Ordinal), "Markdown 列表结构保留");
    Check(translated.Contains("```", StringComparison.Ordinal), "代码块围栏保留");
    Check(translated.Contains("load_order = 10", StringComparison.Ordinal), "代码块内容未被翻译");
    Check(translated.Contains("C:\\Games\\Cyberpunk 2077\\bin\\x64", StringComparison.Ordinal), "Windows 安装路径逐字保留");
    Check(translated.Contains("RED4ext", StringComparison.Ordinal), "技术标识符 RED4ext 保留");
    Check(translated.Contains("1.2.3", StringComparison.Ordinal), "版本号 1.2.3 保留");
    Check(translated.Contains("config.ini", StringComparison.Ordinal), "文件名 config.ini 保留");
    Check(
        translated.Contains("https://wiki.redmodding.org/cyber-engine-tweaks", StringComparison.Ordinal),
        "URL 逐字保留");
    Check(translated.Split('\n').Length == description.Split('\n').Length, "段落数量与原文一致（顺序未被打乱）");
}
else
{
    Check(false, "描述翻译未成功：" + descriptionResult.Error);
}

// ---- cache ----------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("== 缓存与原文指纹 ==");

var cacheRoot = Path.Combine(appData, "Cache", "Translations");
if (Directory.Exists(cacheRoot))
{
    Directory.Delete(cacheRoot, recursive: true);
}

var secrets = new FakeSecretStore();
var settings = new TranslationSettings { Engine = TranslationEngineKind.Local };
var service = new TranslationService(secrets, paths, settings, logger: null, httpClient: null, localEngine: engine);

var first = await service.TranslateAsync("smoke-1", "Cyber Engine Tweaks", "A summary.", description);
Check(first.Succeeded, "通过 TranslationService 翻译成功");
Check(first.Succeeded && !first.Value!.FromCache, "首次翻译未命中缓存");
Check(first.Succeeded && HasChinese(first.Value!.Title), "中文标题已生成");
Check(first.Succeeded && HasChinese(first.Value!.Description ?? string.Empty), "中文描述已生成");
Check(first.Succeeded && first.Value!.Model.Contains("本地", StringComparison.Ordinal), $"结果记录了本地引擎（{first.Value?.Model}）");

var second = await service.TranslateAsync("smoke-1", "Cyber Engine Tweaks", "A summary.", description);
Check(second.Succeeded && second.Value!.FromCache, "相同原文命中缓存");

var third = await service.TranslateAsync("smoke-1", "Cyber Engine Tweaks", "A summary.", description + "\n\nAn extra paragraph.");
Check(third.Succeeded && !third.Value!.FromCache, "原文变化后重新翻译（缓存失效）");

// ---- result ---------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine(failures == 0 ? "TRANSLATION SMOKE: ALL PASSED" : $"TRANSLATION SMOKE: {failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

static string FindRepoRoot()
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

/// <summary>A secret store for the smoke run: the local engine must need no credential at all.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    /// <inheritdoc />
    public void Set(string name, string value) => _values[name] = value;

    /// <inheritdoc />
    public void Remove(string name) => _values.Remove(name);

    /// <inheritdoc />
    public bool Contains(string name) => _values.ContainsKey(name);
}
