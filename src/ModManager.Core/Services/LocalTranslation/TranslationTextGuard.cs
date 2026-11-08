using System.Text;
using System.Text.RegularExpressions;

namespace ModManager.Core.Services;

/// <summary>Text with the parts that must not be translated replaced by numbered placeholders.</summary>
/// <param name="Masked">Text to send to the engine.</param>
/// <param name="Values">Original value for each placeholder index.</param>
public sealed record GuardedText(string Masked, IReadOnlyList<string> Values)
{
    /// <summary>True when nothing had to be protected.</summary>
    public bool IsUnchanged => Values.Count == 0;
}

/// <summary>Outcome of putting the protected values back.</summary>
/// <param name="Text">Restored text.</param>
/// <param name="MissingIndices">Placeholders the engine dropped; empty means nothing was lost.</param>
/// <param name="DuplicatedIndices">Placeholders the engine repeated, which would duplicate a path or URL.</param>
public sealed record GuardRestoreResult(
    string Text,
    IReadOnlyList<int> MissingIndices,
    IReadOnlyList<int> DuplicatedIndices)
{
    /// <summary>True when every protected value came back exactly once.</summary>
    public bool IsIntact => MissingIndices.Count == 0 && DuplicatedIndices.Count == 0;

    /// <summary>Indices that came back wrong, for a message the user can act on.</summary>
    public IReadOnlyList<int> DamagedIndices => [.. MissingIndices.Concat(DuplicatedIndices).Distinct().Order()];
}

/// <summary>
/// Protects content that must survive translation byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS, MEASURED RATHER THAN ASSUMED: the bundled English → Chinese model is a neural
/// translator, and it rewrites anything it does not recognise. A real run over this engine turned
/// <c>https://www.nexusmods.com/cyberpunk2077/mods/107</c> into
/// <c>https://www.nexusmods.com/cyberpunk 2077/mods/107</c> — a URL that no longer resolves — and
/// <c>Cyber Engine Tweaks</c> into <c>网络引擎微调</c>. A path a user is told to install into is not
/// decoration; corrupting it is a real failure, so those strings never reach the model at all.
/// </para>
/// <para>
/// WHY <c>{n}</c>, AND WHY THAT IS NOT A GUESS: the placeholder has to survive the round trip, so every
/// candidate was measured against this exact model. Private-use characters (<c>\uE000</c>), control
/// characters, zero-width joiners and <c>«n»</c> came back as garbage — the model mapped the unknown token
/// onto a real one and emitted <c>QQ7</c>. <c>[[n]]</c> and <c>&lt;xn&gt;</c> survived a single marker in a
/// short sentence and then broke with four markers in a paragraph, which is the shape a mod description
/// actually has: <c>[[0]]</c> came back as <c>[ [0]</c>, losing the path it stood for. A plain <c>{n}</c>
/// kept all four indices, in order, in five out of five test sentences including a long one, so that is the
/// format used here. The model has evidently seen enough template syntax to leave <c>{0}</c> alone.
/// </para>
/// <para>
/// A <c>{n}</c> that already appears in the source is itself protected by this class before any placeholder
/// is issued, so a document that contains <c>{0}</c> cannot collide with a generated placeholder.
/// </para>
/// <para>
/// WHAT IS DELIBERATELY NOT HERE: Markdown structure. Headings, list markers and code fences are kept
/// intact by <see cref="TranslationSegmenter"/>, which never sends them to the engine in the first place —
/// masking them would have doubled the placeholder count in every description for no benefit.
/// </para>
/// <para>
/// A round trip that loses a placeholder is <b>reported</b> (<see cref="GuardRestoreResult.MissingIndices"/>)
/// rather than silently patched, because quietly emitting a translation with a missing path is exactly the
/// kind of plausible-looking wrong answer this codebase refuses to produce.
/// </para>
/// </remarks>
public sealed class TranslationTextGuard
{
    /// <summary>Format of one placeholder, chosen from the formats that were measured to survive.</summary>
    private const string PlaceholderOpen = "{";
    private const string PlaceholderClose = "}";

    /// <summary>Matches a placeholder, tolerating the spacing a model sometimes introduces.</summary>
    private static readonly Regex PlaceholderPattern =
        new(@"\{\s*(\d{1,4})\s*\}", RegexOptions.Compiled);

    /// <summary>
    /// Short all-caps tokens that are ordinary English words, not identifiers. Without this list a
    /// sentence like "AND THEN IT IS DONE" would be masked into placeholders and lose its translation.
    /// </summary>
    private static readonly HashSet<string> CommonUppercaseWords = new(StringComparer.Ordinal)
    {
        "AND", "THE", "FOR", "NOT", "YOU", "ALL", "CAN", "NEW", "USE", "SEE", "ONE", "TWO", "MAY",
        "ANY", "BUT", "OUT", "ARE", "WAS", "HAS", "ITS", "GET", "WHO", "HOW", "WHY", "NOW", "DAY",
        "DID", "YES", "OFF", "OWN", "TOO", "WAY", "BOY", "MAN", "OLD", "PUT", "SAY", "SHE", "HER",
        "HIS", "HIM", "OUR", "TEN", "SIX", "RUN", "BIG", "TOP", "END", "FAR", "LOW", "SET", "TRY",
        "WAR", "WON", "YET", "ALSO", "THEN", "THIS", "THAT", "WITH", "FROM", "HAVE", "WILL", "MORE",
        "MOST", "ONLY", "OVER", "SUCH", "THAN", "THEM", "THEY", "VERY", "WELL", "WHAT", "WHEN",
        "YOUR", "JUST", "LIKE", "MADE", "MAKE", "MANY", "MUST", "NAME", "NEXT", "ONCE", "OPEN",
        "PART", "SAME", "SOME", "TAKE", "TEXT", "THAN", "TIME", "TURN", "USED", "WAYS", "WEEK",
        "WERE", "WHAT", "WHEN", "WHOM", "WIDE", "WORD", "WORK", "YEAR", "ZERO", "EACH", "EVEN",
        "EVER", "FIND", "FIRST", "GIVE", "GOOD", "HAND", "HARD", "HEAD", "HELP", "HERE", "HIGH",
        "HOME", "HOUR", "INTO", "KEEP", "KIND", "KNOW", "LAST", "LATE", "LEFT", "LESS", "LIFE",
        "LINE", "LINK", "LIST", "LIVE", "LONG", "LOOK", "LOSE", "LOVE", "MAIN", "MEAN", "MEET",
        "MIND", "MOVE", "MUCH", "NEAR", "NEED", "NINE", "NOTE", "OKAY", "ONTO", "PAGE", "PLAY",
        "READ", "REAL", "REST", "ROOM", "SAID", "SAVE", "SEEN", "SEND", "SIDE", "SIGN", "SITE",
        "SIZE", "SLOW", "SOFT", "SOON", "STAY", "STOP", "SURE", "TALK", "TELL", "TERM", "TEST",
        "THIN", "TOLD", "TOOK", "TREE", "TRUE", "TYPE", "UPON", "USER", "VIEW", "WAIT", "WALK",
        "WANT", "WARM", "WEAK", "WEAR", "WENT", "WIDE", "WIFE", "WIND", "WISH", "WORD", "YOUR",
    };

    /// <summary>
    /// Protection rules, in priority order. Earlier rules win when two overlap, so a whole fenced code
    /// block beats the URL inside it and a Markdown link beats the bare URL in its target.
    /// </summary>
    private static readonly ProtectionRule[] Rules =
    [
        // Fenced code blocks: never translated, never touched.
        new(@"```[\s\S]*?```", "代码块"),
        new(@"~~~[\s\S]*?~~~", "代码块"),

        // Inline code.
        new(@"`[^`\r\n]+`", "行内代码"),

        // Markdown link and image targets, so a URL cannot be broken by the surrounding text handling.
        new(@"\]\(\s*<?([^)\s>]+)>?", "链接地址", matchGroup: 1),

        // Bare URLs, including the trailing punctuation that is usually sentence punctuation.
        new(@"\b(?:https?|ftp)://[^\s<>\[\]()""']+", "URL", trimTrailingPunctuation: true),
        new(@"\bwww\.[^\s<>\[\]()""']+", "网址", trimTrailingPunctuation: true),

        // Extended-length and UNC paths first: they are unambiguous, and they may contain spaces.
        new(@"\\\\\?\\[^\r\n<>|""*?]+", "文件路径", trimTrailingPunctuation: true),
        new(@"\\\\[A-Za-z0-9._$-]+\\[^\r\n<>|""*?,;]*", "网络路径", trimTrailingPunctuation: true),

        // A drive path may contain spaces — "C:\Games\Cyberpunk 2077\bin\x64" is exactly the shape a mod's
        // install instructions use, and the space-free version of this rule used to cut it in half. Allowing
        // spaces means the pattern needs a stop condition, or it would swallow the sentence around the path:
        // it refuses to cross a common English connective, so "C:\Games\x64 with RED4ext 1.2.3" yields the
        // path and leaves the version number to the rule that protects version numbers.
        new(
            @"[A-Za-z]:[\\/](?:(?!\b(?:and|or|with|to|the|for|in|of|is|are|see|from|at|on|by|its|this|that|"
            + @"then|but|not|you|can|will|must|should|if|as|be|has|have|use|run|copy|place|install|requires|"
            + @"needs|make|all|any|into|onto)\b)[^\r\n<>|""*?,;])*",
            "文件路径",
            trimTrailingPunctuation: true),

        // Unix-style paths, which appear in mod install instructions.
        new(@"(?<![\w:/])/(?:[\w.@+-]+/)+[\w.@+-]+", "文件路径", trimTrailingPunctuation: true),
        new(@"(?<![\w])~/[\w./@+-]+", "文件路径", trimTrailingPunctuation: true),

        // Command lines the user is told to run.
        new(@"(?m)^\s*(?:sudo\s+)?(?:\./)?[\w.-]+\.(?:sh|bat|cmd|ps1|exe)\b[^\r\n]*", "命令行"),

        // Version numbers, including a leading v and a pre-release suffix.
        new(@"\bv?\d+\.\d+(?:\.\d+)*(?:[-_][A-Za-z0-9.]+)?\b", "版本号"),

        // File names with an extension that matters to an installation.
        new(
            @"\b[\w][\w .\-]*\.(?:dll|exe|ini|json|xml|txt|cfg|conf|toml|yaml|yml|pak|esp|esm|esl|bsa|ba2|"
            + @"zip|7z|rar|tar|gz|lua|py|cs|js|ts|bat|cmd|ps1|sh|so|dylib|log|md|png|jpg|jpeg|dds|wav|ogg)\b",
            "文件名",
            ignoreCase: true),

        // Format strings and template placeholders.
        new(@"\{[A-Za-z_][A-Za-z0-9_]*\}", "占位符"),
        new(@"\{\d+\}", "占位符"),
        new(@"\$\{[A-Za-z_][A-Za-z0-9_]*\}", "占位符"),
        new(@"%\d+\$?[sdfiu]", "占位符"),
        new(@"(?<![\w%])%[sdfiu](?![\w])", "占位符"),

        // Identifiers that mix letters and digits: x64, RED4ext, ModEngine2, ScriptExtender3.
        new(@"\b[A-Za-z]+\d+[A-Za-z0-9]*\b", "标识符"),

        // Short all-caps acronyms (SKSE, CET, ENB, NPC), minus ordinary English words in caps.
        new(@"\b[A-Z][A-Z0-9]{1,5}\b", "缩写", filter: value => !CommonUppercaseWords.Contains(value)),
    ];

    /// <summary>Replaces every protected span with a placeholder.</summary>
    /// <param name="text">Text to protect.</param>
    public GuardedText Protect(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new GuardedText(text ?? string.Empty, []);
        }

        var spans = new List<ProtectedSpan>();

        foreach (var rule in Rules)
        {
            foreach (Match match in rule.Pattern.Matches(text))
            {
                if (!match.Success || match.Length == 0)
                {
                    continue;
                }

                var group = rule.MatchGroup is { } index && index < match.Groups.Count
                    ? match.Groups[index]
                    : match;

                var start = group.Index;
                var value = group.Value;

                if (rule.TrimTrailingPunctuation)
                {
                    // Whitespace is trimmed too: a path that stops before a connective ends up with a trailing
                    // space that is punctuation for our purposes, and a path is not allowed to carry one.
                    value = value.TrimEnd(' ', '\t', '.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');
                }

                if (value.Length == 0)
                {
                    continue;
                }

                if (rule.Filter is not null && !rule.Filter(value))
                {
                    continue;
                }

                // An earlier rule already claimed this text; nested protection would corrupt the
                // outer value when it is restored.
                if (spans.Any(span => span.Overlaps(start, value.Length)))
                {
                    continue;
                }

                spans.Add(new ProtectedSpan(start, value.Length, value));
            }
        }

        if (spans.Count == 0)
        {
            return new GuardedText(text, []);
        }

        spans.Sort((left, right) => left.Start.CompareTo(right.Start));

        var builder = new StringBuilder(text.Length + (spans.Count * 6));
        var values = new List<string>(spans.Count);
        var cursor = 0;

        foreach (var span in spans)
        {
            builder.Append(text, cursor, span.Start - cursor);
            builder.Append(PlaceholderOpen).Append(values.Count).Append(PlaceholderClose);
            values.Add(span.Value);
            cursor = span.Start + span.Length;
        }

        builder.Append(text, cursor, text.Length - cursor);

        return new GuardedText(builder.ToString(), values);
    }

    /// <summary>
    /// Puts the protected values back and reports any placeholder the engine lost.
    /// </summary>
    /// <param name="translated">Text returned by the engine.</param>
    /// <param name="guarded">The result of <see cref="Protect"/>.</param>
    public GuardRestoreResult Restore(string translated, GuardedText guarded)
    {
        ArgumentNullException.ThrowIfNull(guarded);

        var text = translated ?? string.Empty;
        if (guarded.Values.Count == 0)
        {
            return new GuardRestoreResult(text, [], []);
        }

        var counts = new Dictionary<int, int>();

        var restored = PlaceholderPattern.Replace(text, match =>
        {
            if (!int.TryParse(match.Groups[1].Value, out var index)
                || index < 0
                || index >= guarded.Values.Count)
            {
                // An index we never issued: leave the text alone rather than invent a value.
                return match.Value;
            }

            counts[index] = counts.GetValueOrDefault(index) + 1;
            return guarded.Values[index];
        });

        var missing = new List<int>();
        var duplicated = new List<int>();

        for (var index = 0; index < guarded.Values.Count; index++)
        {
            var count = counts.GetValueOrDefault(index);
            if (count == 0)
            {
                missing.Add(index);
            }
            else if (count > 1)
            {
                // A repeated placeholder would paste the same URL or path in twice.
                duplicated.Add(index);
            }
        }

        return new GuardRestoreResult(restored, missing, duplicated);
    }

    /// <summary>One protected region of the source text.</summary>
    /// <param name="Start">Offset in the source.</param>
    /// <param name="Length">Length in the source.</param>
    /// <param name="Value">Original text.</param>
    private readonly record struct ProtectedSpan(int Start, int Length, string Value)
    {
        /// <summary>True when this span and the candidate overlap.</summary>
        /// <param name="start">Candidate start.</param>
        /// <param name="length">Candidate length.</param>
        public bool Overlaps(int start, int length) =>
            start < Start + Length && Start < start + length;
    }

    /// <summary>One protection rule.</summary>
    /// <remarks>
    /// A plain class rather than a record: the properties are named after the constructor parameters, and a
    /// positional record would generate those same names, which the compiler rejects.
    /// </remarks>
    private sealed class ProtectionRule
    {
        /// <summary>Creates a rule.</summary>
        /// <param name="expression">What to match.</param>
        /// <param name="label">Human-readable name, kept for diagnostics and tests.</param>
        /// <param name="matchGroup">Capture group to protect, when the whole match is not the value.</param>
        /// <param name="ignoreCase">Case-insensitive matching.</param>
        /// <param name="trimTrailingPunctuation">Drop trailing sentence punctuation from the captured value.</param>
        /// <param name="filter">Extra test a candidate must pass.</param>
        public ProtectionRule(
            string expression,
            string label,
            int? matchGroup = null,
            bool ignoreCase = false,
            bool trimTrailingPunctuation = false,
            Func<string, bool>? filter = null)
        {
            Pattern = new Regex(
                expression,
                RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));

            Label = label;
            MatchGroup = matchGroup;
            TrimTrailingPunctuation = trimTrailingPunctuation;
            Filter = filter;
        }

        /// <summary>Compiled matcher.</summary>
        public Regex Pattern { get; }

        /// <summary>Name of this rule, for diagnostics and tests.</summary>
        public string Label { get; }

        /// <summary>Capture group to protect, when the whole match is not the value.</summary>
        public int? MatchGroup { get; }

        /// <summary>True when trailing sentence punctuation should be left outside the protection.</summary>
        public bool TrimTrailingPunctuation { get; }

        /// <summary>Extra test a candidate must pass.</summary>
        public Func<string, bool>? Filter { get; }
    }
}
