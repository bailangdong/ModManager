namespace ModManager.Core.Services;

/// <summary>
/// Rule-based install-method classifier. It deliberately uses several independent signals and
/// returns a confidence; it never extracts or writes anything.
/// </summary>
public sealed class InstallMethodDetector : IInstallMethodDetector
{
    /// <inheritdoc />
    public InstallMethodAnalysis Analyze(IGameAdapter adapter, ArchiveIndex index)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(index);

        var paths = index.Entries
            .Where(entry => !entry.IsDirectory)
            .Select(entry => entry.LowerPath)
            .ToList();

        var evidence = new List<string>();
        var notes = new List<string>();
        var loader = adapter.Definition.Loader?.Kind ?? ModLoaderKind.Unknown;

        if (Has(paths, "fomod/moduleconfig.xml") || Has(paths, "fomod/info.xml"))
        {
            evidence.Add("压缩包包含 FOMOD 安装配置。");
            notes.Add("需要展示组件选择向导，按用户选择生成安装方案。");
            return Result(InstallMethodKind.Fomod, 0.99d, evidence, true, notes);
        }

        if (Has(paths, "bepinex/core/bepinex.dll") || Has(paths, "bepinex/core/bepinex.preloader.dll"))
        {
            evidence.Add("压缩包本身就是 BepInEx 运行环境。");
            notes.Add("这是 Loader 分发包，应先安装/更新 Loader，再安装普通插件。");
            return Result(InstallMethodKind.LoaderDistribution, 0.98d, evidence, false, notes);
        }

        if (Has(paths, "melonloader/net6/melonloader.dll") || Has(paths, "melonloader/dependencies/0harmony.dll"))
        {
            evidence.Add("压缩包本身就是 MelonLoader 运行环境。");
            notes.Add("这是 Loader 分发包，应先安装/更新 Loader。");
            return Result(InstallMethodKind.LoaderDistribution, 0.98d, evidence, false, notes);
        }

        if (Has(paths, "smapi.installer.dll") || Has(paths, "smapi.installer.exe"))
        {
            evidence.Add("压缩包包含 SMAPI 安装器。");
            notes.Add("SMAPI 安装需要专用安装器；不要直接解压到游戏目录。");
            return Result(InstallMethodKind.Smapi, 0.95d, evidence, true, notes);
        }

        if (HasPrefix(paths, "bepinex/"))
        {
            evidence.Add("压缩包包含 BepInEx/ 目录。");
            return Result(InstallMethodKind.BepInEx, 0.95d, evidence, false, notes);
        }

        if (HasPrefix(paths, "melonloader/") || HasPrefix(paths, "userlibs/")
            || (loader == ModLoaderKind.MelonLoader && HasPrefix(paths, "mods/")))
        {
            evidence.Add("压缩包包含 MelonLoader 的 Mods/UserLibs 布局。");
            return Result(InstallMethodKind.MelonLoader, 0.90d, evidence, false, notes);
        }

        if (loader == ModLoaderKind.Smapi && Has(paths, "manifest.json"))
        {
            evidence.Add("Stardew 风格 manifest.json，且游戏使用 SMAPI。");
            return Result(InstallMethodKind.Smapi, 0.92d, evidence, false, notes);
        }

        if (HasSuffix(paths, ".pak") || HasPrefix(paths, "content/paks/") || HasPrefix(paths, "logicmods/"))
        {
            evidence.Add("压缩包包含 UE .pak 或 LogicMods 内容。");
            return Result(InstallMethodKind.UePak, 0.88d, evidence, false, notes);
        }

        if (Has(paths, "info.json") && (HasPrefix(paths, "archives/") || HasPrefix(paths, "r6/") || HasPrefix(paths, "mods/")))
        {
            evidence.Add("压缩包包含 Cyberpunk REDmod/Archive 特征。");
            return Result(InstallMethodKind.Cyberpunk, 0.86d, evidence, false, notes);
        }

        if (Has(paths, "install.exe") || Has(paths, "setup.exe") || Has(paths, "install.bat") || Has(paths, "install.ps1"))
        {
            evidence.Add("压缩包根目录包含安装程序或脚本。");
            notes.Add("安装器可能修改游戏文件；必须展示来源和影响范围，并由用户确认。");
            return Result(InstallMethodKind.DedicatedInstaller, 0.80d, evidence, true, notes);
        }

        if (HasPrefix(paths, "plugins/")
            || (HasSuffix(paths, ".dll")
                && loader is ModLoaderKind.BepInEx or ModLoaderKind.MelonLoader or ModLoaderKind.GenericDll))
        {
            evidence.Add("压缩包包含加载器插件 DLL。");
            return Result(InstallMethodKind.BepInEx, 0.68d, evidence, false, notes);
        }

        if (paths.Count > 0)
        {
            evidence.Add("未发现专用安装器或已知加载器布局，按游戏 Mod 根目录直接部署。");
            return Result(InstallMethodKind.DirectCopy, 0.62d, evidence, false, notes);
        }

        evidence.Add("压缩包为空或没有可部署文件。");
        return Result(InstallMethodKind.Manual, 0.10d, evidence, true, notes);
    }

    private static InstallMethodAnalysis Result(
        InstallMethodKind kind,
        double confidence,
        List<string> evidence,
        bool wizard,
        List<string> notes)
        => new(kind, confidence, evidence, wizard, SuggestedRoot: null, notes);

    private static bool Has(IReadOnlyList<string> paths, string exact)
        => paths.Any(path => string.Equals(path, exact, StringComparison.OrdinalIgnoreCase));

    private static bool HasPrefix(IReadOnlyList<string> paths, string prefix)
        => paths.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool HasSuffix(IReadOnlyList<string> paths, string suffix)
        => paths.Any(path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
