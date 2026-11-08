namespace ModManager.Core;

/// <summary>One group choice in a FOMOD plan. Indices refer to <see cref="FomodStep.Groups"/>.</summary>
public sealed record FomodSelection(
    int StepIndex,
    int GroupIndex,
    IReadOnlyList<int> OptionIndices);

/// <summary>Resolved file set and validation messages for a FOMOD selection.</summary>
public sealed record FomodSelectionResult(
    IReadOnlyList<FomodFile> Files,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates a user's FOMOD choices and flattens them into the files that must be staged.
/// </summary>
/// <remarks>
/// Pure and side-effect free: it never reads the archive or writes a file. The caller can show
/// the errors in the wizard, then pass the resolved list to the extraction layer. A group with
/// <see cref="FomodGroupType.SelectAll"/> ignores the supplied selection and selects every option,
/// because that is what the format means.
/// </remarks>
public static class FomodPlanSelector
{
    /// <summary>Validates selections and returns selected files in stable, priority order.</summary>
    public static FomodSelectionResult Resolve(
        FomodPlan plan,
        IReadOnlyList<FomodSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selections);

        var errors = new List<string>();
        var warnings = new List<string>();
        var byGroup = new Dictionary<(int Step, int Group), FomodSelection>();

        if (plan.UnsupportedFeatures.Count > 0)
        {
            errors.Add(
                "该 FOMOD 使用了当前尚未支持的安装特性："
                + string.Join("、", plan.UnsupportedFeatures)
                + "。为避免漏装或错装，已阻止自动暂存。");
        }

        foreach (var selection in selections)
        {
            if (!byGroup.TryAdd((selection.StepIndex, selection.GroupIndex), selection))
            {
                errors.Add($"步骤 {selection.StepIndex + 1} 的选项组 {selection.GroupIndex + 1} 被重复提交。");
            }
        }

        var files = new List<FomodFile>();
        var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var stepIndex = 0; stepIndex < plan.Steps.Count; stepIndex++)
        {
            var step = plan.Steps[stepIndex];
            for (var groupIndex = 0; groupIndex < step.Groups.Count; groupIndex++)
            {
                var group = step.Groups[groupIndex];
                byGroup.TryGetValue((stepIndex, groupIndex), out var selection);

                var chosen = ResolveGroup(group, selection, stepIndex, groupIndex, errors);
                if (chosen.Count == 0)
                {
                    continue;
                }

                foreach (var optionIndex in chosen)
                {
                    if (optionIndex < 0 || optionIndex >= group.Options.Count)
                    {
                        errors.Add($"步骤 {stepIndex + 1} / 组 {groupIndex + 1} 的选项 {optionIndex + 1} 不存在。");
                        continue;
                    }

                    foreach (var file in group.Options[optionIndex].Files)
                    {
                        files.Add(file);
                        if (destinations.TryGetValue(file.Destination, out var previous)
                            && !string.Equals(previous, file.Source, StringComparison.OrdinalIgnoreCase))
                        {
                            warnings.Add(
                                $"目标「{file.Destination}」被多个选项写入（{previous} 与 {file.Source}），按优先级后者覆盖前者。");
                        }
                        else
                        {
                            destinations[file.Destination] = file.Source;
                        }
                    }
                }
            }
        }

        foreach (var key in byGroup.Keys)
        {
            if (key.Step < 0 || key.Step >= plan.Steps.Count)
            {
                errors.Add($"提交了不存在的步骤 {key.Step + 1}。");
                continue;
            }

            var step = plan.Steps[key.Step];
            if (key.Group < 0 || key.Group >= step.Groups.Count)
            {
                errors.Add($"步骤 {key.Step + 1} 没有选项组 {key.Group + 1}。");
            }
        }

        var ordered = files
            .OrderBy(file => file.Priority)
            .ThenBy(file => file.Destination, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FomodSelectionResult(ordered, errors, warnings);
    }

    private static IReadOnlyList<int> ResolveGroup(
        FomodGroup group,
        FomodSelection? selection,
        int stepIndex,
        int groupIndex,
        List<string> errors)
    {
        if (group.Type == FomodGroupType.SelectAll)
        {
            return Enumerable.Range(0, group.Options.Count).ToList();
        }

        if (selection is null)
        {
            if (group.Type is FomodGroupType.SelectExactlyOne or FomodGroupType.SelectAtLeastOne)
            {
                errors.Add($"步骤 {stepIndex + 1} / 组「{group.Name}」必须至少选择一个选项。");
            }

            return [];
        }

        var chosen = selection.OptionIndices
            .Where(index => index >= 0 && index < group.Options.Count)
            .Distinct()
            .ToList();

        if (chosen.Count != selection.OptionIndices.Count)
        {
            errors.Add($"步骤 {stepIndex + 1} / 组「{group.Name}」包含不存在的选项。");
        }

        switch (group.Type)
        {
            case FomodGroupType.SelectExactlyOne when chosen.Count != 1:
                errors.Add($"步骤 {stepIndex + 1} / 组「{group.Name}」必须且只能选择一个选项。");
                break;
            case FomodGroupType.SelectAtLeastOne when chosen.Count == 0:
                errors.Add($"步骤 {stepIndex + 1} / 组「{group.Name}」必须至少选择一个选项。");
                break;
        }

        return chosen;
    }
}
