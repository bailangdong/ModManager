using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ModManager.Core;

namespace ModManager.App.Views;

/// <summary>Converts a boolean into a <see cref="Visibility"/>, optionally inverted via the parameter.</summary>
/// <remarks>
/// An integer is accepted as well as a boolean and reads as "non-empty": empty-state panels bind to a
/// collection's <c>Count</c>, and treating a count as a truthy flag is what keeps such a panel hidden
/// when the list has rows in it.
/// </remarks>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value switch
        {
            bool typed => typed,
            int count => count > 0,
            string nonEmpty => nonEmpty.Length > 0,
            null => false,
            _ => true,
        };

        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>Inverts a boolean.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool typed || !typed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool typed || !typed;
}

/// <summary>Returns a brush when the bound value is non-empty, otherwise transparent.</summary>
/// <remarks>
/// Used for tinted badges where the colour is chosen by the row and the template must not hardcode one.
/// </remarks>
public sealed class BrushPassThroughConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value as Brush ?? Brushes.Transparent;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way conversion only.");
}

/// <summary>Formats a <see cref="TimeSpan"/> for display.</summary>
public sealed class DurationConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TimeSpan span
            ? span.TotalHours >= 1 ? $"{span.TotalHours:0.#} 小时" : $"{span.TotalMinutes:0.#} 分钟"
            : "--";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way conversion only.");
}

/// <summary>Renders a capability set as a one-line summary.</summary>
public sealed class CapabilitySummaryConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not GameCapability capabilities || capabilities == GameCapability.None)
        {
            return "无额外能力";
        }

        var names = new List<string>();
        foreach (var flag in Enum.GetValues<GameCapability>())
        {
            if (flag != GameCapability.None && capabilities.Has(flag))
            {
                names.Add(flag.Describe());
            }
        }

        return string.Join(" · ", names);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way conversion only.");
}

/// <summary>
/// Renders an engine enumeration with its Chinese display name.
/// </summary>
/// <remarks>
/// Used where a control is bound straight to an enum (the log-level filter, for example) and would
/// otherwise show the raw member name. The mapping itself lives in <c>Zh</c> so there is one place to
/// change a translation.
/// </remarks>
public sealed class ChineseEnumConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel level => ViewModels.Zh.LogLevel(level),
        IssueSeverity severity => ViewModels.Zh.Severity(severity),
        DownloadState state => ViewModels.Zh.DownloadState(state),
        ModState state => ViewModels.Zh.ModState(state),
        DeploymentMethod method => ViewModels.Zh.Method(method),
        PathSafetyVerdict verdict => ViewModels.Zh.Verdict(verdict),
        null => string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One-way conversion only.");
}
