using ModManager.Core;
using Xunit;

namespace ModManager.Tests;

public sealed class FomodSelectionTests
{
    [Fact]
    public void Resolve_SelectExactlyOne_ReturnsChosenFiles()
    {
        var plan = Plan(FomodGroupType.SelectExactlyOne, Option("A", "A/a.dll"), Option("B", "B/b.dll"));

        var result = FomodPlanSelector.Resolve(plan, [new FomodSelection(0, 0, [1])]);

        Assert.Empty(result.Errors);
        var file = Assert.Single(result.Files);
        Assert.Equal("B/b.dll", file.Source);
        Assert.Equal("Data/B/b.dll", file.Destination);
    }

    [Fact]
    public void Resolve_SelectExactlyOne_WithoutChoice_IsError()
    {
        var plan = Plan(FomodGroupType.SelectExactlyOne, Option("A", "A/a.dll"));

        var result = FomodPlanSelector.Resolve(plan, []);

        Assert.Contains(result.Errors, error => error.Contains("必须"));
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Resolve_SelectExactlyOne_WithTwoChoices_IsError()
    {
        var plan = Plan(FomodGroupType.SelectExactlyOne, Option("A", "A/a.dll"), Option("B", "B/b.dll"));

        var result = FomodPlanSelector.Resolve(plan, [new FomodSelection(0, 0, [0, 1])]);

        Assert.Contains(result.Errors, error => error.Contains("只能选择一个"));
    }

    [Fact]
    public void Resolve_SelectAll_SelectsEveryOption()
    {
        var plan = Plan(FomodGroupType.SelectAll, Option("A", "A/a.dll"), Option("B", "B/b.dll"));

        var result = FomodPlanSelector.Resolve(plan, []);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Files.Count);
    }

    [Fact]
    public void Resolve_SelectAny_WithoutChoice_IsAllowed()
    {
        var plan = Plan(FomodGroupType.SelectAny, Option("A", "A/a.dll"));

        var result = FomodPlanSelector.Resolve(plan, []);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Resolve_OutOfRangeChoice_IsError()
    {
        var plan = Plan(FomodGroupType.SelectAny, Option("A", "A/a.dll"));

        var result = FomodPlanSelector.Resolve(plan, [new FomodSelection(0, 0, [3])]);

        Assert.Contains(result.Errors, error => error.Contains("不存在"));
    }

    [Fact]
    public void Resolve_DuplicateDestination_ReportsWarning()
    {
        var plan = Plan(
            FomodGroupType.SelectAny,
            Option("A", "A/a.dll", destination: "Data/shared.dll"),
            Option("B", "B/b.dll", destination: "Data/shared.dll"));

        var result = FomodPlanSelector.Resolve(plan, [new FomodSelection(0, 0, [0, 1])]);

        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, warning => warning.Contains("多个选项"));
        Assert.Equal(2, result.Files.Count);
    }

    private static FomodPlan Plan(FomodGroupType type, params FomodOption[] options) =>
        new(
            "Test",
            [new FomodStep("Step", [new FomodGroup("Group", type, options)])],
            [],
            []);

    private static FomodOption Option(string name, string source, string? destination = null) =>
        new(
            name,
            null,
            [new FomodFile(source, destination ?? "Data/" + source, 0, false)],
            []);
}
