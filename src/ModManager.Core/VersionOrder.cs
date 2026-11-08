using System.Globalization;

namespace ModManager.Core;

/// <summary>
/// Orders version strings the way mod authors mean them, and parses a declared requirement.
/// </summary>
/// <remarks>
/// Every mod source writes versions differently ("1.2", "v1.2.3", "1.2.3b", "2024.10", "1.0-beta").
/// A plain string comparison is wrong in the cases that matter: it puts "1.10" before "1.9" and treats
/// "1.2" and "1.2.0" as different. This comparer splits a version into numeric runs and text runs, so
/// numbers compare numerically and the trailing label only breaks ties.
/// <para>
/// This is deliberately not a semantic-versioning parser: mod versions are not required to be semver,
/// and pretending they are would reject versions that are perfectly valid in practice.
/// </para>
/// </remarks>
public static class VersionOrder
{
    /// <summary>Compares two version strings. A missing version sorts before any present version.</summary>
    /// <param name="left">Left version.</param>
    /// <param name="right">Right version.</param>
    /// <returns>Negative when left is older, positive when newer, zero when equivalent.</returns>
    public static int Compare(string? left, string? right)
    {
        var a = Tokenize(left);
        var b = Tokenize(right);
        var length = Math.Max(a.Count, b.Count);

        for (var index = 0; index < length; index++)
        {
            var hasA = index < a.Count;
            var hasB = index < b.Count;

            if (!hasA && !hasB)
            {
                return 0;
            }

            // "1.2" equals "1.2.0": a missing trailing segment counts as zero, not as "older than anything".
            // A missing segment that the other side fills with a *label* means the shorter version is the
            // release and the longer one is the pre-release, so "1.2" is newer than "1.2-beta".
            if (!hasA)
            {
                if (b[index].IsNumber && b[index].Number == 0)
                {
                    continue;
                }

                return b[index].IsNumber ? -1 : 1;
            }

            if (!hasB)
            {
                if (a[index].IsNumber && a[index].Number == 0)
                {
                    continue;
                }

                return a[index].IsNumber ? 1 : -1;
            }

            var itemA = a[index];
            var itemB = b[index];

            if (itemA.IsNumber && itemB.IsNumber)
            {
                var byNumber = itemA.Number.CompareTo(itemB.Number);
                if (byNumber != 0)
                {
                    return byNumber;
                }

                continue;
            }

            // A numeric segment beats a label at the same position ("1.2" > "1.2-beta").
            if (itemA.IsNumber != itemB.IsNumber)
            {
                return itemA.IsNumber ? 1 : -1;
            }

            var byText = string.Compare(itemA.Text, itemB.Text, StringComparison.OrdinalIgnoreCase);
            if (byText != 0)
            {
                return byText;
            }
        }

        return 0;
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="baseline"/>.</summary>
    /// <param name="candidate">Version to test.</param>
    /// <param name="baseline">Version to test against.</param>
    public static bool IsNewer(string? candidate, string? baseline) =>
        !string.IsNullOrWhiteSpace(candidate) && Compare(candidate, baseline) > 0;

    /// <summary>
    /// True when an installed version satisfies a declared minimum version.
    /// </summary>
    /// <param name="installedVersion">Version we have, if any.</param>
    /// <param name="minimumVersion">Declared minimum, if any.</param>
    public static bool Satisfies(string? installedVersion, string? minimumVersion) =>
        string.IsNullOrWhiteSpace(minimumVersion) || Compare(installedVersion, minimumVersion) >= 0;

    /// <summary>
    /// Parses a declared dependency into a name and an optional minimum version.
    /// </summary>
    /// <param name="requirement">Dependency string as a mod declares it.</param>
    /// <remarks>
    /// Recognised forms, all of which appear in real mod pages:
    /// <list type="bullet">
    /// <item><c>Some Mod</c> — name only.</item>
    /// <item><c>Some Mod &gt;= 1.2</c>, <c>Some Mod@1.2</c>, <c>Some Mod 1.2+</c> — name plus minimum.</item>
    /// <item><c>?Some Mod</c> — optional; a missing optional dependency is not a problem.</item>
    /// </list>
    /// Anything else (prose such as "loads after X, requires SKSE 2.0.20 for game 1.6.640") is returned
    /// with <see cref="VersionRequirement.UnsupportedReason"/> set, so the interface can say it cannot be
    /// resolved automatically instead of silently guessing.
    /// </remarks>
    public static VersionRequirement Parse(string requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        var text = requirement.Trim();
        if (text.Length == 0)
        {
            return new VersionRequirement(string.Empty, null, false, "空依赖名");
        }

        var optional = false;
        if (text.StartsWith('?'))
        {
            optional = true;
            text = text[1..].Trim();
        }

        if (text.EndsWith("(optional)", StringComparison.OrdinalIgnoreCase))
        {
            optional = true;
            text = text[..^"(optional)".Length].Trim();
        }

        // Prose with sentence punctuation is not a resolvable requirement.
        if (text.Contains(',') || text.Contains(';'))
        {
            return new VersionRequirement(text, null, optional, "包含多个条件，无法自动解析");
        }

        var separators = new[] { "<=", "=<", ">=", "=>", "==", "=", "@" };
        foreach (var separator in separators)
        {
            var position = text.IndexOf(separator, StringComparison.Ordinal);
            if (position <= 0)
            {
                continue;
            }

            var name = text[..position].Trim();
            var version = text[(position + separator.Length)..].Trim().TrimStart('v', 'V');
            if (name.Length == 0 || version.Length == 0)
            {
                continue;
            }

            if (!LooksLikeVersion(version))
            {
                return new VersionRequirement(text, null, optional, $"版本号 “{version}” 无法解析");
            }

            return separator switch
            {
                "<=" or "=<" => new VersionRequirement(name, null, optional, null, MaximumVersion: version),
                "=" or "==" => new VersionRequirement(name, null, optional, null, ExactVersion: version),
                _ => new VersionRequirement(name, version, optional, null),
            };
        }

        // "Some Mod 1.2 - 1.5" is a range. Pre-release versions also contain '-', so only a
        // delimiter surrounded by spaces is treated as a range.
        var rangeSeparator = text.LastIndexOf(" - ", StringComparison.Ordinal);
        if (rangeSeparator > 0)
        {
            var beforeRange = text[..rangeSeparator].Trim();
            var nameSeparator = beforeRange.LastIndexOf(' ');
            if (nameSeparator > 0)
            {
                var name = beforeRange[..nameSeparator].Trim();
                var lower = beforeRange[(nameSeparator + 1)..].Trim();
                var upper = text[(rangeSeparator + 3)..].Trim();
                if (name.Length > 0 && LooksLikeVersion(lower) && LooksLikeVersion(upper))
                {
                    return new VersionRequirement(
                        name,
                        lower.TrimStart('v', 'V'),
                        optional,
                        null,
                        MaximumVersion: upper.TrimStart('v', 'V'));
                }
            }
        }

        // "Some Mod 1.2+" — trailing plus sign.
        if (text.EndsWith('+'))
        {
            var withoutPlus = text[..^1].Trim();
            var split = withoutPlus.LastIndexOf(' ');
            if (split > 0)
            {
                var name = withoutPlus[..split].Trim();
                var version = withoutPlus[(split + 1)..].Trim();
                if (LooksLikeVersion(version))
                {
                    return new VersionRequirement(name, version, optional, null);
                }
            }
        }

        return new VersionRequirement(text, null, optional, null);
    }

    /// <summary>True when a string looks like a version rather than a word.</summary>
    /// <param name="value">Candidate.</param>
    private static bool LooksLikeVersion(string value)
    {
        var digits = 0;
        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                digits++;
            }
            else if (!char.IsLetter(character) && character is not ('.' or '-' or '_' or '+'))
            {
                return false;
            }
        }

        return digits > 0;
    }

    /// <summary>Splits a version into numeric and textual runs.</summary>
    /// <param name="version">Version to split.</param>
    private static List<VersionSegment> Tokenize(string? version)
    {
        var segments = new List<VersionSegment>();
        if (string.IsNullOrWhiteSpace(version))
        {
            return segments;
        }

        var text = version.Trim().TrimStart('v', 'V');
        var current = new System.Text.StringBuilder();
        var numeric = false;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            var token = current.ToString();
            if (numeric && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                segments.Add(new VersionSegment(true, value, null));
            }
            else if (token.Length > 0)
            {
                segments.Add(new VersionSegment(false, 0, token));
            }

            current.Clear();
        }

        foreach (var character in text)
        {
            if (character is '.' or '-' or '_' or '+' or ' ')
            {
                Flush();
                continue;
            }

            var isDigit = char.IsDigit(character);
            if (current.Length > 0 && isDigit != numeric)
            {
                Flush();
            }

            numeric = isDigit;
            current.Append(character);
        }

        Flush();
        return segments;
    }

    /// <summary>One run of digits or letters inside a version string.</summary>
    /// <param name="IsNumber">True for a numeric run.</param>
    /// <param name="Number">Numeric value when <paramref name="IsNumber"/>.</param>
    /// <param name="Text">Textual value otherwise.</param>
    private readonly record struct VersionSegment(bool IsNumber, long Number, string? Text);
}

/// <summary>
/// A dependency as declared, split into a name plus an optional minimum version.
/// </summary>
/// <param name="Name">Mod name or id the requirement names.</param>
/// <param name="MinimumVersion">Minimum acceptable version, when the declaration stated one.</param>
/// <param name="Optional">True when the dependency is optional.</param>
/// <param name="UnsupportedReason">Set when the declaration cannot be evaluated automatically.</param>
public sealed record VersionRequirement(
    string Name,
    string? MinimumVersion,
    bool Optional,
    string? UnsupportedReason,
    string? MaximumVersion = null,
    string? ExactVersion = null)
{
    /// <summary>True when the declaration could be understood.</summary>
    public bool IsResolvable => UnsupportedReason is null && Name.Length > 0;

    /// <summary>True when an installed version satisfies this requirement.</summary>
    /// <param name="installedVersion">Version that is installed, if any.</param>
    public bool IsSatisfiedBy(string? installedVersion)
    {
        if (ExactVersion is not null)
        {
            return VersionOrder.Compare(installedVersion, ExactVersion) == 0;
        }

        if (MinimumVersion is not null && VersionOrder.Compare(installedVersion, MinimumVersion) < 0)
        {
            return false;
        }

        return MaximumVersion is null || VersionOrder.Compare(installedVersion, MaximumVersion) <= 0;
    }
}
