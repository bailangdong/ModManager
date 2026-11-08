using System.Text;
using System.Text.RegularExpressions;

namespace ModManager.Core.Services;

/// <summary>What should happen to one piece of a description.</summary>
public enum TranslationSegmentKind
{
    /// <summary>Ordinary prose: send it to the engine.</summary>
    Text,

    /// <summary>Structure or code: reproduce it exactly and never translate it.</summary>
    Verbatim,
}

/// <summary>
/// One translatable unit, positioned in the source so the document can be rebuilt exactly.
/// </summary>
/// <param name="LineIndex">Zero-based source line this belongs to.</param>
/// <param name="PartIndex">Position within that line, for lines split into several units.</param>
/// <param name="Kind">Whether this is prose or must be preserved.</param>
/// <param name="Prefix">Markup that stays in front of the text (heading marks, bullets, table pipes).</param>
/// <param name="Text">The text itself; for <see cref="TranslationSegmentKind.Verbatim"/>, the raw line.</param>
public sealed record TranslationSegment(
    int LineIndex,
    int PartIndex,
    TranslationSegmentKind Kind,
    string Prefix,
    string Text)
{
    /// <summary>True when this unit should be sent to the engine.</summary>
    public bool NeedsTranslation =>
        Kind == TranslationSegmentKind.Text && !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Splits a mod description into translatable units and puts the document back together.
/// </summary>
/// <remarks>
/// <para>
/// WHY STRUCTURE IS SPLIT RATHER THAN MASKED: the alternative would be to send Markdown to the engine with
/// every <c>##</c>, bullet and table pipe replaced by a placeholder. That inflates the placeholder count,
/// invites the model to reorder them, and still risks a heading losing its marker. Rebuilding the document
/// from positioned units means the structure is never the engine's decision at all — and it is the same
/// machinery that keeps paragraph order and stops a repeated paragraph from being translated twice.
/// </para>
/// <para>
/// A unit is one line, or one table cell. Longer prose is left whole here because the worker splits anything
/// that exceeds the model's comfortable segment length on sentence boundaries, where it can also measure the
/// real token count.
/// </para>
/// <para>
/// KNOWN LIMIT, STATED PLAINLY: this is a Markdown-aware line segmenter, not a CommonMark parser. It handles
/// headings, bullets, ordered lists, block quotes, fenced code, indented code and tables, which is what mod
/// descriptions actually contain. A nested construct it does not recognise degrades to "translate this line
/// as prose" rather than corrupting the output, because <see cref="Verbatim"/> is the conservative default
/// only for code fences and dividers.
/// </para>
/// </remarks>
public static class TranslationSegmenter
{
    private static readonly Regex HeadingPattern =
        new(@"^(?<prefix>\s{0,3}#{1,6}\s+)(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex BulletPattern =
        new(@"^(?<prefix>\s*(?:[-*+]|\d{1,3}[.)])\s+)(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex QuotePattern =
        new(@"^(?<prefix>\s*>+\s?)(?<text>.*)$", RegexOptions.Compiled);

    private static readonly Regex FencePattern =
        new(@"^\s{0,3}(?:```|~~~)", RegexOptions.Compiled);

    private static readonly Regex DividerPattern =
        new(@"^\s{0,3}(?:(?:[-*_])\s*){3,}$", RegexOptions.Compiled);

    /// <summary>A table's separator row: pipes, dashes and colons only.</summary>
    private static readonly Regex TableDividerPattern =
        new(@"^\s*\|?[\s:|-]*-[\s:|-]*\|?\s*$", RegexOptions.Compiled);

    /// <summary>Four-space (or tab) indented block, which Markdown treats as literal code.</summary>
    private static readonly Regex IndentedCodePattern =
        new(@"^(?:\t| {4,})\S", RegexOptions.Compiled);

    /// <summary>Splits a description into positioned units.</summary>
    /// <param name="text">Description to split.</param>
    public static IReadOnlyList<TranslationSegment> Segment(string? text)
    {
        var segments = new List<TranslationSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var insideFence = false;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];

            if (FencePattern.IsMatch(line))
            {
                // The fence line itself and everything up to its partner are reproduced untouched.
                insideFence = !insideFence;
                segments.Add(new TranslationSegment(lineIndex, 0, TranslationSegmentKind.Verbatim, string.Empty, line));
                continue;
            }

            if (insideFence)
            {
                segments.Add(new TranslationSegment(lineIndex, 0, TranslationSegmentKind.Verbatim, string.Empty, line));
                continue;
            }

            if (line.Length == 0)
            {
                segments.Add(new TranslationSegment(lineIndex, 0, TranslationSegmentKind.Verbatim, string.Empty, line));
                continue;
            }

            if (DividerPattern.IsMatch(line) || IndentedCodePattern.IsMatch(line))
            {
                segments.Add(new TranslationSegment(lineIndex, 0, TranslationSegmentKind.Verbatim, string.Empty, line));
                continue;
            }

            if (TableDividerPattern.IsMatch(line) && line.Contains('|'))
            {
                segments.Add(new TranslationSegment(lineIndex, 0, TranslationSegmentKind.Verbatim, string.Empty, line));
                continue;
            }

            if (line.Contains('|') && LooksLikeTableRow(line))
            {
                AddTableRow(segments, lineIndex, line);
                continue;
            }

            AddMarkedLine(segments, lineIndex, line);
        }

        return segments;
    }

    /// <summary>
    /// Rebuilds the description from the units, using <paramref name="resolve"/> for every unit's final text.
    /// </summary>
    /// <param name="segments">Units produced by <see cref="Segment"/>.</param>
    /// <param name="resolve">Returns the text to emit for one unit (translation, or the original on failure).</param>
    public static string Compose(
        IReadOnlyList<TranslationSegment> segments,
        Func<TranslationSegment, string> resolve)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(resolve);

        var builder = new StringBuilder();
        var lineIndex = -1;
        var first = true;

        foreach (var segment in segments.OrderBy(s => s.LineIndex).ThenBy(s => s.PartIndex))
        {
            if (segment.LineIndex != lineIndex)
            {
                if (!first)
                {
                    builder.Append('\n');
                }

                lineIndex = segment.LineIndex;
                first = false;
            }

            builder.Append(segment.Prefix).Append(resolve(segment));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Collects the distinct texts that need translating, in document order.
    /// </summary>
    /// <param name="segments">Units produced by <see cref="Segment"/>.</param>
    /// <remarks>
    /// A description that repeats a paragraph — or a page of twenty mods that share a boilerplate line —
    /// then costs one translation, not one per occurrence.
    /// </remarks>
    public static IReadOnlyList<string> DistinctTexts(IReadOnlyList<TranslationSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        foreach (var segment in segments)
        {
            if (segment.NeedsTranslation && seen.Add(segment.Text))
            {
                ordered.Add(segment.Text);
            }
        }

        return ordered;
    }

    /// <summary>Splits a heading, bullet or quote line into markup plus prose.</summary>
    /// <param name="segments">Output list.</param>
    /// <param name="lineIndex">Source line.</param>
    /// <param name="line">Line text.</param>
    private static void AddMarkedLine(List<TranslationSegment> segments, int lineIndex, string line)
    {
        foreach (var pattern in new[] { HeadingPattern, QuotePattern, BulletPattern })
        {
            var match = pattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            segments.Add(new TranslationSegment(
                lineIndex,
                0,
                TranslationSegmentKind.Text,
                match.Groups["prefix"].Value,
                match.Groups["text"].Value));

            return;
        }

        segments.Add(new TranslationSegment(lineIndex, 0, TranslationSegmentKind.Text, string.Empty, line));
    }

    /// <summary>Splits a table row so each cell is translated but the pipes are not.</summary>
    /// <param name="segments">Output list.</param>
    /// <param name="lineIndex">Source line.</param>
    /// <param name="line">Line text.</param>
    /// <remarks>
    /// A neural translator will happily move or drop the <c>|</c> characters that make a Markdown table a
    /// table, which is why each cell travels separately. A segment carries only a prefix and a text, so the
    /// whitespace that trails one cell is carried forward as the next cell's prefix — otherwise "Name " would
    /// be sent to the engine with a trailing space, and a leading or trailing space is not part of a word.
    /// </remarks>
    private static void AddTableRow(List<TranslationSegment> segments, int lineIndex, string line)
    {
        var cells = line.Split('|');
        var carry = string.Empty;

        for (var index = 0; index < cells.Length; index++)
        {
            var cell = cells[index];

            var leading = 0;
            while (leading < cell.Length && char.IsWhiteSpace(cell[leading]))
            {
                leading++;
            }

            var trailing = cell.Length;
            while (trailing > leading && char.IsWhiteSpace(cell[trailing - 1]))
            {
                trailing--;
            }

            var prefix = carry + (index > 0 ? "|" : string.Empty) + cell[..leading];
            var content = cell[leading..trailing];
            carry = cell[trailing..];

            segments.Add(new TranslationSegment(
                lineIndex,
                index,
                TranslationSegmentKind.Text,
                prefix,
                content));
        }
    }

    /// <summary>True when a line with pipes actually looks like a table row.</summary>
    /// <param name="line">Line to test.</param>
    private static bool LooksLikeTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 2)
        {
            return false;
        }

        // Require both edges to be pipes, or at least two pipes with content between them: prose that
        // merely contains a pipe must still be translated as prose.
        var pipes = trimmed.Count(character => character == '|');
        return (trimmed.StartsWith('|') && trimmed.EndsWith('|')) || pipes >= 2;
    }
}
