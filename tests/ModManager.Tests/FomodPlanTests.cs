using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

public sealed class FomodPlanTests
{
    private const string SimpleConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <config>
          <moduleName>Test FOMOD</moduleName>
          <installSteps>
            <installStep name="Step 1">
              <optionalFileGroups>
                <group name="Main" type="SelectExactlyOne">
                  <plugins>
                    <plugin name="Core">
                      <description>Core files</description>
                      <files>
                        <file source="Core/Plugin.dll" destination="Data/Plugin.dll" priority="10" />
                        <folder source="Core/Textures" destination="Data/Textures" />
                      </files>
                      <conditionFlags>
                        <flag name="core">on</flag>
                      </conditionFlags>
                    </plugin>
                    <plugin name="Optional">
                      <files>
                        <file source="Optional/Extra.dll" destination="Data/Extra.dll" />
                      </files>
                    </plugin>
                  </plugins>
                </group>
              </optionalFileGroups>
            </installStep>
          </installSteps>
        </config>
        """;

    [Fact]
    public async Task ReadAsync_ParsesStepsGroupsOptionsAndFiles()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(
            tree,
            "fomod.zip",
            ("fomod/ModuleConfig.xml", SimpleConfig),
            ("Core/Plugin.dll", "binary"),
            ("Core/Textures/a.dds", "texture"));

        var result = await FomodPlanReader.ReadAsync(archive, new ArchiveReader());

        Assert.True(result.Succeeded, result.Error);
        var plan = result.Value!;
        Assert.Equal("Test FOMOD", plan.ModuleName);
        var step = Assert.Single(plan.Steps);
        Assert.Equal("Step 1", step.Name);

        var group = Assert.Single(step.Groups);
        Assert.Equal("Main", group.Name);
        Assert.Equal(FomodGroupType.SelectExactlyOne, group.Type);
        Assert.Equal(2, group.Options.Count);

        var core = group.Options[0];
        Assert.Equal("Core", core.Name);
        Assert.Equal("Core files", core.Description);
        Assert.Equal(["core"], core.Flags);
        Assert.Equal(2, core.Files.Count);
        Assert.Equal(new FomodFile("Core/Plugin.dll", "Data/Plugin.dll", 10, false), core.Files[0]);
        Assert.Equal(new FomodFile("Core/Textures", "Data/Textures", 0, true), core.Files[1]);

        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public async Task ReadAsync_RejectsTraversalPathsButKeepsPlan()
    {
        const string config = """
            <config>
              <moduleName>Unsafe</moduleName>
              <installSteps>
                <installStep name="Step">
                  <optionalFileGroups>
                    <group name="Main" type="SelectAny">
                      <plugins>
                        <plugin name="Bad">
                          <files>
                            <file source="../evil.dll" destination="Data/evil.dll" />
                            <file source="safe.dll" destination="../../outside.dll" />
                            <file source="ok.dll" destination="Data/ok.dll" />
                          </files>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(
            tree,
            "unsafe.zip",
            ("fomod/ModuleConfig.xml", config),
            ("ok.dll", "ok"));

        var result = await FomodPlanReader.ReadAsync(archive, new ArchiveReader());

        Assert.True(result.Succeeded, result.Error);
        var option = Assert.Single(Assert.Single(Assert.Single(result.Value!.Steps).Groups).Options);
        var file = Assert.Single(option.Files);
        Assert.Equal("Data/ok.dll", file.Destination);
        Assert.Contains(result.Value.Warnings, warning => warning.Contains("不安全的源路径"));
        Assert.Contains(result.Value.Warnings, warning => warning.Contains("不安全的目标路径"));
    }

    [Fact]
    public async Task ReadAsync_WithoutConfig_Fails()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(tree, "plain.zip", ("readme.txt", "no fomod here"));

        var result = await FomodPlanReader.ReadAsync(archive, new ArchiveReader());

        Assert.False(result.Succeeded);
        Assert.Contains("ModuleConfig.xml", result.Error);
    }

    [Fact]
    public async Task ReadAsync_WithMalformedXml_Fails()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(
            tree,
            "bad.zip",
            ("fomod/ModuleConfig.xml", "<config><installSteps>"));

        var result = await FomodPlanReader.ReadAsync(archive, new ArchiveReader());

        Assert.False(result.Succeeded);
        Assert.Contains("XML", result.Error);
    }

    [Fact]
    public async Task ReadAsync_MarksUnsupportedConditionalInstallsAndBlocksSelection()
    {
        const string config = """
            <config>
              <moduleName>Conditional</moduleName>
              <installSteps>
                <installStep name="Step">
                  <optionalFileGroups>
                    <group name="Main" type="SelectAny">
                      <plugins>
                        <plugin name="Core">
                          <files>
                            <file source="Core.dll" destination="Data/Core.dll" />
                          </files>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
              <conditionalFileInstalls>
                <patterns>
                  <pattern>
                    <dependencies>
                      <fileDependency file="Data/Core.dll" state="Active" />
                    </dependencies>
                    <files>
                      <file source="Extra.dll" destination="Data/Extra.dll" />
                    </files>
                  </pattern>
                </patterns>
              </conditionalFileInstalls>
            </config>
            """;

        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(
            tree,
            "conditional.zip",
            ("fomod/ModuleConfig.xml", config),
            ("Core.dll", "core"));

        var read = await FomodPlanReader.ReadAsync(archive, new ArchiveReader());
        Assert.True(read.Succeeded, read.Error);
        Assert.Contains("conditionalFileInstalls", read.Value!.UnsupportedFeatures);

        var selection = FomodPlanSelector.Resolve(
            read.Value,
            [new FomodSelection(0, 0, [0])]);

        Assert.NotEmpty(selection.Errors);
        Assert.Contains(selection.Errors, error => error.Contains("尚未支持"));
    }
}
