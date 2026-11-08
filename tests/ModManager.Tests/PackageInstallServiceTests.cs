using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

public sealed class PackageInstallServiceTests
{
    [Fact]
    public async Task StageAsync_FomodPackage_IsRejectedBeforeExtraction()
    {
        using var host = TestHost.Create();
        var packages = new PackageLibrary(host.Paths, host.Logger);
        var adapters = new GameAdapterRegistry(host.Logger);
        adapters.RegisterDefinition(new GameDefinition
        {
            Id = "testgame",
            Name = "Test Game",
            ModRoot = "Mods",
        });
        var gameId = "testgame";
        var archive = TestArchives.CreateZip(
            host.Tree,
            "fomod.zip",
            ("fomod/ModuleConfig.xml", """
                <config>
                  <moduleName>Test FOMOD</moduleName>
                  <installSteps>
                    <installStep name="Step">
                      <optionalFileGroups>
                        <group name="Main" type="SelectExactlyOne">
                          <plugins>
                            <plugin name="Core">
                              <files>
                                <file source="plugins/example.dll" destination="plugins/example.dll" />
                              </files>
                            </plugin>
                          </plugins>
                        </group>
                      </optionalFileGroups>
                    </installStep>
                  </installSteps>
                </config>
                """),
            ("plugins/example.dll", "binary"));

        var committed = await packages.CommitAsync(
            new PackageCommitRequest
            {
                ArchivePath = archive,
                Source = new PackageSourceRef
                {
                    Platform = "local",
                    GameKey = gameId,
                    ModKey = "fomod",
                    FileKey = "1",
                },
                Name = "Fomod Example",
                Version = "1.0",
            },
            CancellationToken.None);

        Assert.True(committed.Succeeded, committed.Error);
        var package = committed.Value!;

        var service = new PackageInstallService(
            packages,
            host.Library,
            host.Install,
            adapters,
            host.Archives,
            new InstallMethodDetector(),
            new LoaderInstaller(host.Paths, host.Archives),
            host.Logger);

        var preview = await service.PreviewFomodAsync(
            package.PackageId,
            [new FomodSelection(0, 0, [0])],
            CancellationToken.None);
        Assert.True(preview.Succeeded, preview.Error);

        var staged = await service.StageAsync(
            package.PackageId,
            gameId,
            acceptLowConfidence: false,
            rootOverride: null,
            progress: null,
            CancellationToken.None);

        Assert.False(staged.Succeeded);
        Assert.Contains("向导", staged.Error);
        Assert.False(Directory.Exists(Path.Combine(host.Paths.Mods, package.PackageId)));
    }

    [Fact]
    public async Task StageFomodAsync_RegistersStagedModForDeployment()
    {
        using var host = TestHost.Create();
        var packages = new PackageLibrary(host.Paths, host.Logger);
        var adapters = new GameAdapterRegistry(host.Logger);
        adapters.RegisterDefinition(new GameDefinition
        {
            Id = "testgame",
            Name = "Test Game",
            ModRoot = "Mods",
        });

        const string gameId = "testgame";
        var archive = TestArchives.CreateZip(
            host.Tree,
            "fomod-stage.zip",
            ("fomod/ModuleConfig.xml", """
                <config>
                  <moduleName>Test FOMOD</moduleName>
                  <installSteps>
                    <installStep name="Step">
                      <optionalFileGroups>
                        <group name="Main" type="SelectExactlyOne">
                          <plugins>
                            <plugin name="Core">
                              <files>
                                <file source="plugins/example.dll" destination="plugins/example.dll" />
                              </files>
                            </plugin>
                          </plugins>
                        </group>
                      </optionalFileGroups>
                    </installStep>
                  </installSteps>
                </config>
                """),
            ("plugins/example.dll", "binary"));

        var committed = await packages.CommitAsync(
            new PackageCommitRequest
            {
                ArchivePath = archive,
                Source = new PackageSourceRef
                {
                    Platform = "local",
                    GameKey = gameId,
                    ModKey = "fomod-stage",
                    FileKey = "1",
                },
                Name = "Fomod Stage Example",
                Version = "1.0",
            },
            CancellationToken.None);
        Assert.True(committed.Succeeded, committed.Error);

        var service = new PackageInstallService(
            packages,
            host.Library,
            host.Install,
            adapters,
            host.Archives,
            new InstallMethodDetector(),
            new LoaderInstaller(host.Paths, host.Archives),
            host.Logger);

        var staged = await service.StageFomodAsync(
            committed.Value!.PackageId,
            gameId,
            [new FomodSelection(0, 0, [0])],
            host.Tree.Dir("stage"),
            CancellationToken.None);

        Assert.True(staged.Succeeded, staged.Error);
        var mod = staged.Value!;
        Assert.Equal(committed.Value.PackageId, mod.Metadata.PackageId);
        Assert.Equal(nameof(InstallMethodKind.Fomod), mod.Metadata.InstallMethod);
        Assert.True(mod.Analysis.IsFomod);
        Assert.Equal(ModState.Staged, mod.Metadata.State);
        Assert.True(File.Exists(Path.Combine(mod.StagingRoot, "plugins", "example.dll")));

        var gameRoot = host.Tree.Dir("game");
        var preview = await service.PreviewAsync(
            mod,
            gameRoot,
            []);
        Assert.True(preview.Succeeded, preview.Error);
        Assert.Contains(
            preview.Value!.Entries,
            file => file.SourceRelative.Replace('\\', '/').EndsWith("plugins/example.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InspectDependencies_SeparatesInstalledLibraryAndMissing()
    {
        using var host = TestHost.Create();
        var packages = new PackageLibrary(host.Paths, host.Logger);
        var adapters = new GameAdapterRegistry(host.Logger);
        adapters.RegisterDefinition(new GameDefinition
        {
            Id = "testgame",
            Name = "Test Game",
            ModRoot = "Mods",
        });

        const string gameId = "testgame";
        host.Library.Upsert(new ModMetadata
        {
            Id = "installed-dep",
            Name = "Installed Dep",
            Version = "1.0.0",
            GameId = gameId,
            Source = ModSource.LocalFile,
            ArchivePath = Path.Combine(host.Paths.Mods, "installed.zip"),
            ArchiveHash = "installed",
            StagingPath = Path.Combine(host.Paths.Mods, "installed"),
        });

        var libraryArchive = TestArchives.CreateZip(
            host.Tree,
            "library-dep.zip",
            ("plugins/library.dll", "binary"));
        var libraryCommit = await packages.CommitAsync(
            new PackageCommitRequest
            {
                ArchivePath = libraryArchive,
                Source = new PackageSourceRef
                {
                    Platform = "local",
                    GameKey = gameId,
                    ModKey = "library-dep",
                    FileKey = "1",
                },
                Name = "Library Dep",
                Version = "2.0.0",
            },
            CancellationToken.None);
        Assert.True(libraryCommit.Succeeded, libraryCommit.Error);

        var rootArchive = TestArchives.CreateZip(
            host.Tree,
            "root.zip",
            ("plugins/root.dll", "binary"));
        var rootCommit = await packages.CommitAsync(
            new PackageCommitRequest
            {
                ArchivePath = rootArchive,
                Source = new PackageSourceRef
                {
                    Platform = "local",
                    GameKey = gameId,
                    ModKey = "root",
                    FileKey = "1",
                },
                Name = "Root Mod",
                Version = "1.0.0",
                Dependencies =
                [
                    "Installed Dep",
                    "Library Dep-2.0.0",
                    "Missing Dep",
                    "   ",
                ],
            },
            CancellationToken.None);
        Assert.True(rootCommit.Succeeded, rootCommit.Error);
        var root = rootCommit.Value!;

        var service = new PackageInstallService(
            packages,
            host.Library,
            host.Install,
            adapters,
            host.Archives,
            new InstallMethodDetector(),
            new LoaderInstaller(host.Paths, host.Archives),
            host.Logger);

        var inspected = await service.InspectDependenciesAsync(
            root.PackageId,
            gameId,
            CancellationToken.None);

        Assert.True(inspected.Succeeded, inspected.Error);
        var report = inspected.Value!;
        Assert.Equal(3, report.Dependencies.Count);
        Assert.Equal(PackageDependencyState.Installed, report.Dependencies[0].State);
        Assert.Equal("installed-dep", report.Dependencies[0].MatchedModId);
        Assert.Equal(PackageDependencyState.InLibrary, report.Dependencies[1].State);
        Assert.Equal(libraryCommit.Value!.PackageId, report.Dependencies[1].MatchedPackageId);
        Assert.Equal("2.0.0", report.Dependencies[1].MatchedVersion);
        Assert.Equal(PackageDependencyState.Missing, report.Dependencies[2].State);
        Assert.Equal(1, report.MissingCount);
        Assert.True(report.HasMissing);
    }

    [Fact]
    public async Task StageAsync_LowConfidenceArchiveRequiresExplicitConfirmation()
    {
        using var host = TestHost.Create();
        var packages = new PackageLibrary(host.Paths, host.Logger);
        var adapters = new GameAdapterRegistry(host.Logger);
        adapters.RegisterDefinition(new GameDefinition
        {
            Id = "testgame",
            Name = "Test Game",
            ModRoot = "Mods",
        });

        var archive = TestArchives.CreateZip(host.Tree, "unknown-layout.zip", ("notes.txt", "plain text"));
        var committed = await packages.CommitAsync(
            new PackageCommitRequest
            {
                ArchivePath = archive,
                Source = new PackageSourceRef
                {
                    Platform = "local",
                    GameKey = "testgame",
                    ModKey = "unknown-layout",
                    FileKey = "1",
                },
                Name = "Unknown Layout",
                Version = "1.0.0",
            },
            CancellationToken.None);
        Assert.True(committed.Succeeded, committed.Error);

        var service = new PackageInstallService(
            packages,
            host.Library,
            host.Install,
            adapters,
            host.Archives,
            new InstallMethodDetector(),
            new LoaderInstaller(host.Paths, host.Archives),
            host.Logger);

        var automatic = await service.StageAsync(
            committed.Value!.PackageId,
            "testgame",
            acceptLowConfidence: false,
            rootOverride: null,
            progress: null,
            CancellationToken.None);
        Assert.False(automatic.Succeeded);
        Assert.Contains("置信度", automatic.Error);

        var manual = await service.StageAsync(
            committed.Value.PackageId,
            "testgame",
            acceptLowConfidence: true,
            rootOverride: null,
            progress: null,
            CancellationToken.None);
        Assert.True(manual.Succeeded, manual.Error);
    }

    [Fact]
    public async Task InspectDependencies_ReportsVersionMismatchAsBlocking()
    {
        using var host = TestHost.Create();
        var packages = new PackageLibrary(host.Paths, host.Logger);
        var adapters = new GameAdapterRegistry(host.Logger);
        adapters.RegisterDefinition(new GameDefinition
        {
            Id = "testgame",
            Name = "Test Game",
            ModRoot = "Mods",
        });

        const string gameId = "testgame";
        host.Library.Upsert(new ModMetadata
        {
            Id = "installed-dep",
            Name = "Installed Dep",
            Version = "1.0.0",
            GameId = gameId,
            Source = ModSource.LocalFile,
            ArchivePath = Path.Combine(host.Paths.Mods, "installed.zip"),
            ArchiveHash = "installed",
            StagingPath = Path.Combine(host.Paths.Mods, "installed"),
        });

        var archive = TestArchives.CreateZip(
            host.Tree,
            "root-mismatch.zip",
            ("plugins/root.dll", "binary"));
        var committed = await packages.CommitAsync(
            new PackageCommitRequest
            {
                ArchivePath = archive,
                Source = new PackageSourceRef
                {
                    Platform = "local",
                    GameKey = gameId,
                    ModKey = "root-mismatch",
                    FileKey = "1",
                },
                Name = "Root Mismatch",
                Version = "1.0.0",
                Dependencies = ["Installed Dep >= 2.0.0"],
            },
            CancellationToken.None);
        Assert.True(committed.Succeeded, committed.Error);

        var service = new PackageInstallService(
            packages,
            host.Library,
            host.Install,
            adapters,
            host.Archives,
            new InstallMethodDetector(),
            new LoaderInstaller(host.Paths, host.Archives),
            host.Logger);

        var inspected = await service.InspectDependenciesAsync(
            committed.Value!.PackageId,
            gameId,
            CancellationToken.None);

        Assert.True(inspected.Succeeded, inspected.Error);
        var dependency = Assert.Single(inspected.Value!.Dependencies);
        Assert.Equal(PackageDependencyState.Mismatch, dependency.State);
        Assert.Equal(PackageDependencyKind.Required, dependency.Kind);
        Assert.False(dependency.VersionSatisfied);
        Assert.Equal(1, inspected.Value.RequiredMissingCount);
        Assert.True(inspected.Value.HasBlockingMissing);
    }
}
