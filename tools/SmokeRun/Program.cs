using System.IO;
using System.IO.Compression;
using ModManager.Core;
using ModManager.Core.Persistence;
using ModManager.Core.Safety;
using ModManager.Core.Services;

// SmokeRun — headless end-to-end verification of the real engine.
//
// It drives the exact pipeline the WPF app uses, against artifacts\test-game (a synthetic game
// install) and artifacts\test-mods (a locally generated zip). Every step asserts on real file
// changes and prints PASS/FAIL; the exit code is 0 only when every step passed.

var repoRoot = FindRepoRoot();
var gameRoot = Path.Combine(repoRoot, "artifacts", "test-game");
var modsDir = Path.Combine(repoRoot, "artifacts", "test-mods");
var appData = Path.Combine(repoRoot, "artifacts", "smoke", "appdata");

var failures = 0;
void Check(bool condition, string step)
{
    Console.WriteLine((condition ? "PASS  " : "FAIL  ") + step);
    if (!condition)
    {
        failures++;
    }
}

try
{
    // A clean, self-contained fixture every run: the test game folder is rebuilt from scratch so a
    // previous run's leftovers can never leak into the next one. Only artifacts\test-game is
    // touched, never any real game directory.
    var modsRoot = Path.Combine(gameRoot, "Mods");
    if (Directory.Exists(modsRoot))
    {
        Directory.Delete(modsRoot, recursive: true);
    }

    Directory.CreateDirectory(modsRoot);
    File.WriteAllText(Path.Combine(modsRoot, "vanilla.txt"), "original game bytes");
    Directory.CreateDirectory(modsDir);

    var archivePath = Path.Combine(modsDir, "BetterPal-1.0.zip");
    using (var stream = new FileStream(archivePath, FileMode.Create))
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("BetterPal/Mods/plugins/mod.dll");
        using (var writer = new StreamWriter(entry.Open()))
        {
            writer.Write("smoke-dll-bytes");
        }

        var vanilla = zip.CreateEntry("BetterPal/Mods/vanilla.txt");
        using (var vanillaWriter = new StreamWriter(vanilla.Open()))
        {
            vanillaWriter.Write("mod replacement");
        }
    }

    // ---- session 1 ---------------------------------------------------------
    Console.WriteLine("== 会话 1：添加游戏 → 导入 → 分析 → 计划 → 安装 ==");

    var paths = new AppPaths(appData);
    paths.EnsureCreated();
    var logger = new AppLogger();
    var library = new LibraryRepository(new JsonDocumentStore(paths), paths);
    var settings = new SettingsCarrier { Settings = new AppSettings() };
    var pathSafety = new PathSafetyService(paths, settings.Settings.Safety, logger);
    var journals = new JournalService(paths, logger);
    var planner = new DeploymentPlanner(pathSafety, logger);
    var applier = new DeploymentApplier(pathSafety, journals, paths, library);
    var install = new ModInstallService(
        paths,
        new ArchiveReader(),
        new ModAnalyzer(),
        planner,
        applier,
        new VerifyService(),
        new DependencyService(),
        library,
        library,
        library,
        pathSafety,
        settings);

    var definition = new GameDefinition
    {
        Id = "testgame",
        Name = "Test Game",
        ModRoot = "Mods",
        Capabilities = GameCapability.HardlinkDeployment | GameCapability.SymlinkDeployment
            | GameCapability.Profiles | GameCapability.FileConflicts | GameCapability.Dependencies,
        DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
        ContentDirectories = ["mods", "plugins", "data", "config"],
        ModFileExtensions = ["dll", "ini", "json", "txt", "pak", "lua"],
        StripPrefixes = ["Mods"],
    };
    var adapter = new GenericGameAdapter(definition);

    pathSafety.ApproveGameRoot("testgame", gameRoot, Path.Combine(gameRoot, "Mods"));
    library.Upsert(new GameInstance
    {
        GameId = "testgame",
        Name = "Test Game",
        InstallPath = gameRoot,
        ModRootPath = Path.Combine(gameRoot, "Mods"),
    });
    Check(library.AllGames().Any(game => game.GameId == "testgame"), "添加游戏（Library 持久化）");

    var stage = await install.StageAsync(
        new InstallRequest { ArchivePath = archivePath, GameId = "testgame", AcceptLowConfidence = true },
        adapter,
        logger);
    Check(stage.Succeeded, "导入并分析 Mod：" + (stage.Error ?? $"root '{stage.Value?.RootRelativePath}'"));

    var preview = await install.PreviewAsync(stage.Value!, adapter, gameRoot, []);
    Check(preview.Succeeded && !preview.Value!.HasUnsafePaths, "生成安装计划（预览无不安全路径）");

    var deploy = await install.InstallAsync(stage.Value!, adapter, gameRoot, logger);
    Check(deploy.Succeeded, "安装（Journal + 备份 + 部署）");
    Check(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")), "文件实际落盘：Mods/plugins/mod.dll");
    Check(File.ReadAllText(Path.Combine(gameRoot, "Mods", "vanilla.txt")) == "mod replacement", "覆盖 vanilla.txt（已备份原文件）");

    var modId = stage.Value!.Metadata.Id;
    var profile = library.GetProfile("testgame-default");
    Check(profile is not null && profile.ModOrder.Contains(modId), "默认 Profile 已启用该 Mod");

    // ---- disable -----------------------------------------------------------
    Console.WriteLine("== 禁用 / 启用 ==");
    var disabledProfile = profile! with { DisabledModIds = [modId] };
    library.SaveProfile(disabledProfile);

    // The mod set is rebuilt after every profile change, exactly like the UI does — a stale
    // manifest from before the change must never influence the next plan.
    static List<ModSetEntry> BuildModSet(LibraryRepository lib, string id) =>
    [
        new(lib.Mod(id)!, lib.GetManifest("testgame", id), lib.Mod(id)!.StagingPath),
    ];

    var disablePlan = planner.BuildPlan(definition, gameRoot, disabledProfile, BuildModSet(library, modId), []);
    var disableResult = await applier.ApplyAsync(disablePlan, definition, BuildModSet(library, modId), settings.Deployment, logger);
    Check(disableResult.Succeeded, "禁用（重新部署移除文件）");
    Check(!File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")), "禁用后文件已消失");
    Check(File.ReadAllText(Path.Combine(gameRoot, "Mods", "vanilla.txt")) == "original game bytes", "禁用后 vanilla.txt 已恢复原文件");

    var enabledProfile = disabledProfile with { DisabledModIds = [] };
    library.SaveProfile(enabledProfile);
    var enablePlan = planner.BuildPlan(definition, gameRoot, enabledProfile, BuildModSet(library, modId), []);
    var enableResult = await applier.ApplyAsync(enablePlan, definition, BuildModSet(library, modId), settings.Deployment, logger);
    Check(enableResult.Succeeded && File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")), "启用后文件恢复");

    Console.WriteLine("--- 启用后：Mod A 的 Manifest 行 ---");
    foreach (var file in library.GetManifest("testgame", modId)?.Files ?? [])
    {
        Console.WriteLine($"  {file.RelativeTarget} backup={file.BackupPath ?? "(none)"}");
    }

    // ---- restart simulation ------------------------------------------------
    Console.WriteLine("== 重启模拟：全新对象图读取同一份持久化状态 ==");
    var restartedLibrary = new LibraryRepository(new JsonDocumentStore(paths), paths);
    Check(restartedLibrary.ModsForGame("testgame").Any(mod => mod.Id == modId), "重启后 Mod 仍在库中");
    Check(restartedLibrary.GetManifest("testgame", modId) is not null, "重启后 Manifest 仍在");
    Check(restartedLibrary.GetProfile("testgame-default")?.ModOrder.Contains(modId) == true, "重启后启用状态仍在");
    Check(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")), "重启后部署文件仍在");

    // ---- conflict (流程 C) -------------------------------------------------
    Console.WriteLine("== 冲突检测：安装 Mod B 覆盖同一文件 ==");
    var archiveB = Path.Combine(modsDir, "BetterPal-UI-1.0.zip");
    using (var streamB = new FileStream(archiveB, FileMode.Create))
    using (var zipB = new ZipArchive(streamB, ZipArchiveMode.Create))
    {
        var entryB = zipB.CreateEntry("BetterPal-UI/Mods/plugins/mod.dll");
        using var writerB = new StreamWriter(entryB.Open());
        writerB.Write("smoke-dll-bytes-B");
    }

    var stageB = await install.StageAsync(
        new InstallRequest { ArchivePath = archiveB, GameId = "testgame", AcceptLowConfidence = true },
        adapter,
        logger);
    Check(stageB.Succeeded, "导入并分析 Mod B");

    var existingForB = new List<ModSetEntry>
    {
        new(library.Mod(modId)!, library.GetManifest("testgame", modId), library.Mod(modId)!.StagingPath),
    };
    var previewB = await install.PreviewAsync(stageB.Value!, adapter, gameRoot, existingForB);
    Check(
        previewB.Succeeded && previewB.Value!.Conflicts.Count == 1
        && previewB.Value.Conflicts[0].RelativeTarget == "plugins/mod.dll",
        "安装前检测到 1 个文件冲突（plugins/mod.dll）");

    var deployB = await install.InstallAsync(stageB.Value!, adapter, gameRoot, logger);
    Check(deployB.Succeeded, "安装 Mod B");
    Check(
        File.ReadAllText(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")) == "smoke-dll-bytes-B",
        "冲突文件现在由 Mod B 提供");

    Console.WriteLine("--- 安装 Mod B 后：Mod A 的 Manifest 行 ---");
    foreach (var file in library.GetManifest("testgame", modId)?.Files ?? [])
    {
        Console.WriteLine($"  {file.RelativeTarget} backup={file.BackupPath ?? "(none)"}");
    }

    var allMods = library.ModsForGame("testgame")
        .Select(mod => new ModSetEntry(mod, library.GetManifest("testgame", mod.Id), mod.StagingPath))
        .ToList();
    var conflictList = new ConflictService().Analyze("testgame", allMods, []);
    Check(
        conflictList.Any(c => c.RelativeTarget == "plugins/mod.dll" && c.WinnerModId == stageB.Value!.Metadata.Id),
        "冲突服务报告：Mod B 为当前生效者");
    Check(library.GetManifest("testgame", modId) is not null, "Mod A 的 Manifest 在安装 Mod B 后仍完好");

    // ---- load order (流程 G) ------------------------------------------------
    Console.WriteLine("== 加载顺序：调整顺序改变生效者 ==");

    static List<ModSetEntry> ModSetFor(LibraryRepository lib) =>
        lib.ModsForGame("testgame")
            .Select(mod => new ModSetEntry(mod, lib.GetManifest("testgame", mod.Id), mod.StagingPath))
            .ToList();

    var modIdB = stageB.Value!.Metadata.Id;

    async Task ReorderAndDeploy(IReadOnlyList<string> order, string label)
    {
        var current = library.GetProfile("testgame-default")!;
        var updated = current with { ModOrder = order };
        library.SaveProfile(updated);

        var plan = planner.BuildPlan(definition, gameRoot, updated, ModSetFor(library), []);
        var result = await applier.ApplyAsync(plan, definition, ModSetFor(library), settings.Deployment, logger);
        Check(result.Succeeded, $"按顺序 [{label}] 重新部署");
    }

    await ReorderAndDeploy([modId, modIdB], "A, B");
    Check(
        File.ReadAllText(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")) == "smoke-dll-bytes-B",
        "顺序 A→B：由 Mod B 生效");

    await ReorderAndDeploy([modIdB, modId], "B, A");
    Check(
        File.ReadAllText(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")) == "smoke-dll-bytes",
        "顺序 B→A：改由 Mod A 生效（加载顺序真实影响文件）");

    await ReorderAndDeploy([modId, modIdB], "A, B（还原）");
    Check(
        File.ReadAllText(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")) == "smoke-dll-bytes-B",
        "还原顺序后由 Mod B 生效");

    // ---- reinstall (流程 H) -------------------------------------------------
    Console.WriteLine("== 重新安装 ==");
    var reinstall = await install.InstallAsync(stage.Value!, adapter, gameRoot, logger);
    Check(reinstall.Succeeded, "重新安装 Mod A");
    Check(library.GetManifest("testgame", modId)?.Files.Count == 2, "重装后 Mod A 的 Manifest 仍记录 2 个文件");

    // ---- uninstall ---------------------------------------------------------
    // SMOKE_KEEP=1 stops before the teardown so the resulting data directory can be used to review
    // the interface with a populated library. The assertions above have already run at that point.
    if (Environment.GetEnvironmentVariable("SMOKE_KEEP") == "1")
    {
        Console.WriteLine("== SMOKE_KEEP=1：保留已安装状态，跳过卸载 ==");
        Console.WriteLine($"   Mods A/B 已安装于 {gameRoot}，数据目录 {appData}");
    }
    else
    {
        var uninstallB = await install.UninstallAsync("testgame", stageB.Value!.Metadata.Id, adapter, gameRoot, false, logger);
        Check(uninstallB.Succeeded, "卸载 Mod B");
        Check(
            File.ReadAllText(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")) == "smoke-dll-bytes",
            "卸载 Mod B 后，Mod A 的文件内容恢复");

        Console.WriteLine("== 卸载 ==");
        var uninstall = await install.UninstallAsync("testgame", modId, adapter, gameRoot, false, logger);
        Check(uninstall.Succeeded, "卸载");
        Check(!File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")), "卸载后 Mod 文件已移除");
        Check(File.ReadAllText(Path.Combine(gameRoot, "Mods", "vanilla.txt")) == "original game bytes", "卸载后 vanilla.txt 已从备份恢复");
        Check(!library.ModsForGame("testgame").Any(), "卸载后 Library 已清空");
    }

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "SMOKE RUN: ALL PASSED" : $"SMOKE RUN: {failures} FAILURE(S)");
    Environment.ExitCode = failures == 0 ? 0 : 1;
}
catch (Exception ex)
{
    Console.WriteLine("SMOKE RUN CRASHED: " + ex);
    Environment.ExitCode = 2;
}

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
