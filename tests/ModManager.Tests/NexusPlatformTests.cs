using ModManager.Core;
using ModManager.Core.Services;
using ModManager.App.ViewModels;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// The online-platform layer: version ordering, dependency resolution, Nexus association and the
/// decisions the online page is allowed to offer.
/// </summary>
/// <remarks>
/// Every test here runs against in-memory doubles. The live Nexus API needs a credential and changes
/// underneath a test run, so nothing in this file pretends to have talked to it — the logic under test is
/// what the application does with an answer, including the answer "we could not check".
/// </remarks>
public sealed class NexusPlatformTests
{
    // ---------------------------------------------------------------------------------------------
    // Version ordering
    // ---------------------------------------------------------------------------------------------

    /// <summary>Numeric segments compare as numbers, not as text.</summary>
    [Theory]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("1.9", "1.10", -1)]
    [InlineData("2.0", "1.99", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2", "1.2-beta", 1)]
    public void VersionsCompareTheWayAuthorsMeanThem(string left, string right, int expectedSign)
    {
        var actual = Math.Sign(VersionOrder.Compare(left, right));
        Assert.Equal(expectedSign, actual);
        Assert.Equal(-expectedSign, Math.Sign(VersionOrder.Compare(right, left)));
    }

    /// <summary>A minimum version is satisfied by that version or anything newer.</summary>
    [Theory]
    [InlineData("1.2.0", "1.2", true)]
    [InlineData("1.10.0", "1.9", true)]
    [InlineData("1.1.0", "1.2", false)]
    [InlineData(null, "1.2", false)]
    [InlineData("1.0", null, true)]
    public void VersionRequirementsAreEvaluated(string? installed, string? required, bool expected) =>
        Assert.Equal(expected, VersionOrder.Satisfies(installed, required));

    /// <summary>Declarations are parsed into a name and an optional minimum.</summary>
    [Fact]
    public void RequirementDeclarationsAreParsed()
    {
        var minimum = VersionOrder.Parse("Some Mod >= 1.2");
        Assert.Equal("Some Mod", minimum.Name);
        Assert.Equal("1.2", minimum.MinimumVersion);
        Assert.False(minimum.Optional);

        var at = VersionOrder.Parse("Other Mod@2.0.1");
        Assert.Equal("Other Mod", at.Name);
        Assert.Equal("2.0.1", at.MinimumVersion);

        var plus = VersionOrder.Parse("Third Mod 3.1+");
        Assert.Equal("Third Mod", plus.Name);
        Assert.Equal("3.1", plus.MinimumVersion);

        var optional = VersionOrder.Parse("?Optional Mod");
        Assert.True(optional.Optional);
        Assert.Equal("Optional Mod", optional.Name);

        var plain = VersionOrder.Parse("Plain Mod");
        Assert.Equal("Plain Mod", plain.Name);
        Assert.Null(plain.MinimumVersion);
        Assert.True(plain.IsResolvable);
    }

    [Fact]
    public void RequirementDeclarationsSupportExactAndMaximumVersions()
    {
        var exact = VersionOrder.Parse("Pinned Mod = 2.0");
        Assert.Equal("2.0", exact.ExactVersion);
        Assert.True(exact.IsSatisfiedBy("2.0"));
        Assert.False(exact.IsSatisfiedBy("2.0.1"));

        var maximum = VersionOrder.Parse("Legacy Mod <= 1.4");
        Assert.Equal("1.4", maximum.MaximumVersion);
        Assert.True(maximum.IsSatisfiedBy("1.3"));
        Assert.False(maximum.IsSatisfiedBy("1.5"));

        var range = VersionOrder.Parse("Ranged Mod 1.2 - 1.5");
        Assert.Equal("1.2", range.MinimumVersion);
        Assert.Equal("1.5", range.MaximumVersion);
        Assert.True(range.IsSatisfiedBy("1.4"));
        Assert.False(range.IsSatisfiedBy("1.6"));
    }

    /// <summary>Prose that cannot be evaluated is reported as such rather than guessed at.</summary>
    [Fact]
    public void UnparsableDeclarationsAreFlagged()
    {
        var prose = VersionOrder.Parse("loads after X, requires SKSE 2.0.20");
        Assert.False(prose.IsResolvable);
        Assert.NotNull(prose.UnsupportedReason);
    }

    // ---------------------------------------------------------------------------------------------
    // Dependency resolution
    // ---------------------------------------------------------------------------------------------

    /// <summary>Direct and indirect dependencies are both resolved and marked installed.</summary>
    [Fact]
    public void DependenciesResolveRecursively()
    {
        var mods = new[]
        {
            TestMods.Create("c", "Base Framework", "1.0"),
            TestMods.Create("b", "Library Mod", "2.0", ["Base Framework"]),
            TestMods.Create("a", "Main Mod", "1.0", ["Library Mod"]),
        };

        var report = new DependencyResolver().ResolveLocal(mods[2], mods);

        var library = Assert.Single(report.Roots);
        Assert.Equal("Library Mod", library.Name);
        Assert.Equal(DependencyNodeState.Satisfied, library.State);

        var framework = Assert.Single(library.Children);
        Assert.Equal("Base Framework", framework.Name);
        Assert.Equal(DependencyNodeState.Satisfied, framework.State);
        Assert.DoesNotContain(report.Roots, node => node.IsBlocking);
    }

    /// <summary>A missing dependency is reported as missing, with the branch that needs it.</summary>
    [Fact]
    public void MissingDependencyIsReported()
    {
        var mods = new[] { TestMods.Create("a", "Main Mod", "1.0", ["Absent Framework"]) };

        var report = new DependencyResolver().ResolveLocal(mods[0], mods);

        var node = Assert.Single(report.Roots);
        Assert.Equal(DependencyNodeState.Missing, node.State);
        Assert.True(node.IsBlocking);
        Assert.True(report.HasBlocking);
        Assert.Equal(1, report.MissingCount);
    }

    /// <summary>A dependency that is installed but too old is distinguished from a missing one.</summary>
    [Fact]
    public void OutdatedDependencyIsDistinguishedFromMissing()
    {
        var mods = new[]
        {
            TestMods.Create("b", "Library Mod", "1.0"),
            TestMods.Create("a", "Main Mod", "1.0", ["Library Mod >= 2.0"]),
        };

        var report = new DependencyResolver().ResolveLocal(mods[1], mods);

        var node = Assert.Single(report.Roots);
        Assert.Equal(DependencyNodeState.InstalledOutdated, node.State);
        Assert.Equal("2.0", node.RequiredVersion);
        Assert.Equal("1.0", node.InstalledVersion);
        Assert.Equal(1, report.OutdatedCount);
    }

    /// <summary>A cycle stops the walk instead of recursing forever.</summary>
    [Fact]
    public void CyclicDependenciesAreDetected()
    {
        var mods = new[]
        {
            TestMods.Create("a", "Mod A", "1.0", ["Mod B"]),
            TestMods.Create("b", "Mod B", "1.0", ["Mod A"]),
        };

        var report = new DependencyResolver().ResolveLocal(mods[0], mods);

        var b = Assert.Single(report.Roots);
        Assert.Equal(DependencyNodeState.Satisfied, b.State);

        var cycle = Assert.Single(b.Children);
        Assert.Equal(DependencyNodeState.Cyclic, cycle.State);
        Assert.Equal("Mod A", cycle.Name);
    }

    /// <summary>A mod depending on itself is reported, not silently accepted.</summary>
    [Fact]
    public void SelfReferenceIsReported()
    {
        var mods = new[] { TestMods.Create("a", "Mod A", "1.0", ["Mod A"]) };

        var report = new DependencyResolver().ResolveLocal(mods[0], mods);

        Assert.Equal(DependencyNodeState.SelfReference, Assert.Single(report.Roots).State);
    }

    /// <summary>An optional dependency that is absent is shown but is not a blocking problem.</summary>
    [Fact]
    public void OptionalDependencyDoesNotBlock()
    {
        var mods = new[] { TestMods.Create("a", "Mod A", "1.0", ["?Nice To Have"]) };

        var report = new DependencyResolver().ResolveLocal(mods[0], mods);

        var node = Assert.Single(report.Roots);
        Assert.Equal(DependencyNodeState.OptionalMissing, node.State);
        Assert.False(node.IsBlocking);
        Assert.False(report.HasBlocking);
    }

    /// <summary>Unsupported declarations are surfaced so the interface can say it cannot resolve them.</summary>
    [Fact]
    public void UnsupportedDeclarationIsSurfaced()
    {
        var mods = new[] { TestMods.Create("a", "Mod A", "1.0", ["needs X, Y and Z"]) };

        var report = new DependencyResolver().ResolveLocal(mods[0], mods);

        Assert.Equal(DependencyNodeState.Unsupported, Assert.Single(report.Roots).State);
        Assert.Equal(1, report.UnsupportedCount);
    }

    /// <summary>Nexus requirement references match installed mods by stable id, then by name.</summary>
    [Fact]
    public void NexusReferencesMatchInstalledModsByIdThenName()
    {
        var mods = new[]
        {
            TestMods.Create("installed", "Renamed Locally", "1.0", nexusModId: 4242),
        };

        var references = new List<NexusModReference>
        {
            new(4242, "Upstream Name", "testgame", null, "https://example.invalid/4242"),
            new(9999, "Not Installed", "testgame", "作者说明", "https://example.invalid/9999"),
        };

        var report = new DependencyResolver().ResolveNexusReferences("Root Mod", references, mods);

        Assert.Equal(2, report.Roots.Count);
        Assert.Equal(DependencyNodeState.Installed, report.Roots[0].State);
        Assert.Equal("installed", report.Roots[0].LocalModId);
        Assert.Equal(DependencyNodeState.Missing, report.Roots[1].State);
        Assert.Contains("作者说明", report.Roots[1].Note!, StringComparison.Ordinal);
        Assert.Equal(1, report.MissingCount);
    }

    /// <summary>The browser can follow the requirement graph one level deeper when it asks for it.</summary>
    [Fact]
    public void NexusGraphCanBeExpanded()
    {
        var mods = new[] { TestMods.Create("deep", "Deep Dependency", "1.0", nexusModId: 77) };

        var top = new List<NexusModReference> { new(10, "Middle", "testgame", null, null) };
        var report = new DependencyResolver().ResolveNexusReferences(
            "Root",
            top,
            mods,
            expand: id => id == 10 ? [new NexusModReference(77, "Deep Dependency", "testgame", null, null)] : []);

        var middle = Assert.Single(report.Roots);
        Assert.Equal(DependencyNodeState.Missing, middle.State);
        var deep = Assert.Single(middle.Children);
        Assert.Equal("Deep Dependency", deep.Name);
        Assert.Equal(DependencyNodeState.Installed, deep.State);
    }

    // ---------------------------------------------------------------------------------------------
    // Local ↔ Nexus association
    // ---------------------------------------------------------------------------------------------

    /// <summary>Only mods carrying a Nexus id are linked; nothing is matched by name.</summary>
    [Fact]
    public void OnlyModsWithANexusIdAreLinked()
    {
        var mods = new[]
        {
            TestMods.Create("linked", "Linked Mod", "1.0", nexusModId: 100),
            TestMods.Create("local", "Locally Imported", "1.0"),
        };

        var links = new NexusLibraryLinker(new FakeNexusProvider()).LinksFor(mods);

        var link = Assert.Single(links);
        Assert.Equal("linked", link.LocalModId);
        Assert.Equal(100, link.NexusModId);
    }

    /// <summary>An update is reported when the published version is newer.</summary>
    [Fact]
    public async Task UpdateCheckReportsNewerVersions()
    {
        var provider = new FakeNexusProvider();
        provider.Updates["a"] = new ModUpdateInfo("a", 1, "1.0", "1.1");
        provider.Updates["b"] = new ModUpdateInfo("b", 2, "2.0", "2.0");

        var mods = new[]
        {
            TestMods.Create("a", "Mod A", "1.0", nexusModId: 1),
            TestMods.Create("b", "Mod B", "2.0", nexusModId: 2),
        };

        var result = await new NexusLibraryLinker(provider).CheckForUpdatesAsync("testgame", mods);

        Assert.True(result.Succeeded);
        var links = result.Value!;
        Assert.Equal(NexusLinkState.UpdateAvailable, links.Single(link => link.LocalModId == "a").State);
        Assert.Equal(NexusLinkState.UpToDate, links.Single(link => link.LocalModId == "b").State);
        Assert.True(links.Single(link => link.LocalModId == "a").CanUpdate);
    }

    /// <summary>
    /// A failed check is never reported as "up to date": claiming a mod is current when the question could
    /// not be asked is exactly the kind of quiet falsehood this application must not tell.
    /// </summary>
    [Fact]
    public async Task FailedUpdateCheckIsNotReportedAsUpToDate()
    {
        var provider = new FakeNexusProvider { Error = "401 Unauthorized" };
        var mods = new[] { TestMods.Create("a", "Mod A", "1.0", nexusModId: 1) };

        var result = await new NexusLibraryLinker(provider).CheckForUpdatesAsync("testgame", mods);

        Assert.False(result.Succeeded);
        Assert.All(result.Value!, link => Assert.Equal(NexusLinkState.CheckFailed, link.State));
        Assert.DoesNotContain(result.Value!, link => link.State == NexusLinkState.UpToDate);
    }

    /// <summary>Unlinked mods make the update check a no-op rather than an error.</summary>
    [Fact]
    public async Task UpdateCheckWithoutLinksIsEmpty()
    {
        var result = await new NexusLibraryLinker(new FakeNexusProvider())
            .CheckForUpdatesAsync("testgame", [TestMods.Create("local", "Local Mod")]);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!);
    }

    // ---------------------------------------------------------------------------------------------
    // What the online page may offer
    // ---------------------------------------------------------------------------------------------

    /// <summary>A mod that is not installed can be installed through the manager.</summary>
    [Fact]
    public void NotInstalledModOffersInstall()
    {
        var plan = OnlineModActionPlan.Evaluate(Summary(1, "1.0"), null, authenticated: true);

        Assert.Equal(OnlineModLocalState.NotInstalled, plan.LocalState);
        Assert.True(plan.CanInstall);
        Assert.True(plan.CanDownloadViaManager);
        Assert.Equal("安装", plan.PrimaryActionText);
        Assert.Null(plan.BlockingReason);
    }

    /// <summary>An installed older version offers an update.</summary>
    [Fact]
    public void OlderInstalledVersionOffersUpdate()
    {
        var installed = TestMods.Create("mod", "Mod", "1.0", nexusModId: 1);
        var plan = OnlineModActionPlan.Evaluate(Summary(1, "1.1"), installed, authenticated: true);

        Assert.Equal(OnlineModLocalState.UpdateAvailable, plan.LocalState);
        Assert.True(plan.CanInstall);
        Assert.True(plan.CanUninstall);
        Assert.Equal("更新", plan.PrimaryActionText);
    }

    /// <summary>An installed current version offers nothing to install.</summary>
    [Fact]
    public void CurrentInstalledVersionOffersNothing()
    {
        var installed = TestMods.Create("mod", "Mod", "1.0", nexusModId: 1);
        var plan = OnlineModActionPlan.Evaluate(Summary(1, "1.0"), installed, authenticated: true);

        Assert.Equal(OnlineModLocalState.InstalledLatest, plan.LocalState);
        Assert.False(plan.CanInstall);
        Assert.Equal("已是最新", plan.PrimaryActionText);
    }

    /// <summary>Without a credential the page points at the real page instead of pretending.</summary>
    [Fact]
    public void UnauthenticatedSessionPointsAtTheNexusPage()
    {
        var plan = OnlineModActionPlan.Evaluate(Summary(1, "1.0"), null, authenticated: false);

        Assert.False(plan.CanDownloadViaManager);
        Assert.False(plan.CanInstall);
        Assert.True(plan.NeedsConfiguration);
        Assert.Equal("前往 Nexus 页面", plan.PrimaryActionText);
        Assert.Contains("API 密钥", plan.BlockingReason!, StringComparison.Ordinal);
    }

    /// <summary>An author who disabled manager downloads is respected, not worked around.</summary>
    [Fact]
    public void ManagerDownloadDisabledByAuthorIsRespected()
    {
        var summary = Summary(1, "1.0") with { ModManagerDownloadAllowed = false };
        var plan = OnlineModActionPlan.Evaluate(summary, null, authenticated: true);

        Assert.False(plan.CanDownloadViaManager);
        Assert.False(plan.CanInstall);
        Assert.False(plan.NeedsConfiguration);
        Assert.Contains("未授权管理器下载", plan.BlockingReason!, StringComparison.Ordinal);
    }

    /// <summary>An unrelated local mod with the same name is not mistaken for the online one.</summary>
    [Fact]
    public void SameNameDifferentNexusIdIsNotTreatedAsInstalled()
    {
        var other = TestMods.Create("other", "Mod", "9.9", nexusModId: 555);
        var plan = OnlineModActionPlan.Evaluate(Summary(1, "1.0"), other, authenticated: true);

        Assert.Equal("已安装同名 Mod", plan.StateText);
        Assert.True(plan.CanInstall);
    }

    /// <summary>Builds an online summary.</summary>
    /// <param name="modId">Nexus mod id.</param>
    /// <param name="version">Published version.</param>
    private static NexusModSummary Summary(int modId, string version) => new()
    {
        ModId = modId,
        Name = "Mod",
        Author = "Author",
        Version = version,
        Summary = "A mod.",
        DownloadCount = 1234,
        PageUrl = $"https://www.nexusmods.com/testgame/mods/{modId}",
        GameDomain = "testgame",
    };
}
