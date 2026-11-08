using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ModManager.App.Theme;

/// <summary>
/// Applies the light or dark palette to the running application.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS MUTATES BRUSHES INSTEAD OF SWAPPING DICTIONARIES: every view in this application refers to its
/// colours with <c>{StaticResource SurfaceBrush}</c> and friends. A <c>StaticResource</c> is resolved once,
/// when the XAML is loaded, and never looks again — so replacing the theme dictionary at run time changes
/// nothing that is already on screen, and the only ways out would be to rewrite every reference as
/// <c>DynamicResource</c> or to tear down and rebuild every view.
/// </para>
/// <para>
/// The brushes themselves, however, are shared instances. Mutating the <see cref="SolidColorBrush.Color"/>
/// of the one instance every view already holds updates all of them at once, immediately, with no
/// re-templating and no visual tree rebuild. That is why the palette below is expressed as "which resource
/// key gets which colour" rather than as a second XAML dictionary.
/// </para>
/// <para>
/// A brush that WPF has frozen cannot be mutated. The palette resources are declared without
/// <c>po:Freeze</c>, so in practice none are frozen, but each assignment is guarded anyway: a theme that
/// silently fails to apply would be far harder to diagnose than one that skips a single frozen brush.
/// </para>
/// </remarks>
public static class AppearanceService
{
    /// <summary>True when the dark palette is currently applied.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>
    /// Every themed brush: resource key, light value, dark value, and the tint opacity used for state fills.
    /// </summary>
    private static readonly (string Key, string Light, string Dark, double LightOpacity, double DarkOpacity)[] Palette =
    [
        // Surfaces. The dark palette is layered on purpose — page, content, raised, muted — because a dark
        // theme built from one flat grey has no depth and its cards disappear into the background.
        ("BackgroundBrush",     "#FFF5F7FA", "#FF111318", 1, 1),
        ("SurfaceBrush",        "#FFFFFFFF", "#FF191D25", 1, 1),
        ("SurfaceRaisedBrush",  "#FFFAFBFC", "#FF222733", 1, 1),
        ("SurfaceMutedBrush",   "#FFF1F4F8", "#FF1E232D", 1, 1),
        ("BorderBrush",         "#FFC6D2E1", "#FF343B49", 1, 1),
        ("BorderStrongBrush",   "#FFA9B8CB", "#FF454E60", 1, 1),

        // Text. Three deliberate steps, and the light values are darker than a typical "web grey" on purpose:
        // the previous light palette used #9AA0B0 for captions, which is about 2.6:1 on white — below the
        // readable threshold, and the reason helper text looked washed out. #64748B measures about 4.8:1.
        ("TextBrush",           "#FF172033", "#FFF1F5F9", 1, 1),
        ("TextSecondaryBrush",  "#FF475569", "#FFCBD5E1", 1, 1),
        ("TextDisabledBrush",   "#FF64748B", "#FF94A3B8", 1, 1),

        // Accent
        ("AccentBrush",         "#FF2563EB", "#FF7AA7FF", 1, 1),
        ("AccentHoverBrush",    "#FF1D4FD7", "#FF9DBEFF", 1, 1),
        ("AccentPressedBrush",  "#FF1A44B8", "#FF5B8DEF", 1, 1),
        ("AccentSoftBrush",     "#FFE8F0FF", "#FF263653", 1, 1),

        // Text drawn on top of the accent colour. White is correct on the light palette's #2563EB, but the
        // dark palette's accent is deliberately light (#7AA7FF) so it stands out from a near-black surface —
        // and white on that measures about 2:1, which is not readable. This pair is what keeps the primary
        // button and the brand mark legible in both themes instead of hardcoding "White" and hoping.
        ("OnAccentBrush",       "#FFFFFFFF", "#FF0B1220", 1, 1),

        // State colours. The light values are darkened so badge text stays readable on its own 16% tint
        // (Success 4.05→5.56, Warning 3.99→5.50, Info 4.13→5.22); the dark values are lightened because a
        // saturated colour that reads as "strong" on white reads as "muddy" against a near-black surface.
        // Dark Danger moved from #F87171 to #FCA5A5 for the same reason: on its 22% tint it measured 4.34.
        ("SuccessBrush",        "#FF166534", "#FF4ADE80", 1, 1),
        ("WarningBrush",        "#FF92400E", "#FFFBBF24", 1, 1),
        ("DangerBrush",         "#FFB91C1C", "#FFFCA5A5", 1, 1),
        ("InfoBrush",           "#FF1D4ED8", "#FF7AA7FF", 1, 1),

        // Tinted badge and callout fills. More alpha in the dark palette: a 16% tint over white is a visible
        // wash, and the same 16% over #191D25 is nearly invisible.
        ("SuccessFillBrush",    "#FF166534", "#FF4ADE80", 0.16, 0.22),
        ("WarningFillBrush",    "#FF92400E", "#FFFBBF24", 0.16, 0.22),
        ("DangerFillBrush",     "#FFB91C1C", "#FFFCA5A5", 0.16, 0.22),
        ("InfoFillBrush",       "#FF1D4ED8", "#FF7AA7FF", 0.16, 0.22),
    ];

    /// <summary>Applies a palette to the application's resources.</summary>
    /// <param name="dark">True for the dark palette.</param>
    public static void Apply(bool dark)
    {
        var application = Application.Current;
        if (application is null)
        {
            // Nothing to theme yet. The saved choice is re-applied from OnStartup once resources exist.
            IsDark = dark;
            return;
        }

        foreach (var (key, light, darkValue, lightOpacity, darkOpacity) in Palette)
        {
            var brush = new SolidColorBrush(ParseColor(dark ? darkValue : light))
            {
                Opacity = dark ? darkOpacity : lightOpacity,
            };

            // Frozen on purpose: this instance is published as a resource and never mutated again, so freezing
            // lets WPF share it and skip change notifications.
            brush.Freeze();

            // Replacing the instance at the application dictionary's OWN top level is what makes the switch
            // work, and two separate facts force exactly this shape:
            //   * mutating the original brush is impossible — WPF freezes a brush that a sealed Style
            //     references, which is why the first version of this method silently changed nothing;
            //   * writing to the top level rather than into the merged theme dictionary is what lets the
            //     DynamicResource references every view now uses resolve to the new instance immediately,
            //     because the application dictionary is searched before its merged dictionaries.
            application.Resources[key] = brush;
        }

        ApplyShadow(application, dark);

        IsDark = dark;
    }

    /// <summary>Parses a <c>#AARRGGBB</c> literal into a colour.</summary>
    /// <param name="value">Literal to parse.</param>
    private static Color ParseColor(string value) => (Color)ColorConverter.ConvertFromString(value);

    /// <summary>
    /// Describes the live background brush, for the startup trace.
    /// </summary>
    /// <remarks>
    /// Exists because this switch failed silently once already: the palette was applied but nothing changed
    /// on screen, and there was no way to tell a frozen brush from a missing resource without a line of
    /// evidence. A theme that does nothing must be diagnosable from the log alone.
    /// </remarks>
    public static string DescribeBackground()
    {
        if (Application.Current?.TryFindResource("BackgroundBrush") is not SolidColorBrush brush)
        {
            return "BackgroundBrush not found";
        }

        return $"{brush.Color} frozen={brush.IsFrozen}";
    }

    /// <summary>
    /// Republishes the shared card shadow for the active theme.
    /// </summary>
    /// <param name="application">The running application.</param>
    /// <param name="dark">True for the dark palette.</param>
    /// <remarks>
    /// Replaced rather than mutated, for the same reason the brushes are: a shadow that a sealed Style
    /// references is frozen. The dark palette leans on a slightly stronger, wider shadow rather than removing
    /// it — a card still needs a visible edge, it just cannot use darkness to get one on a near-black surface.
    /// </remarks>
    private static void ApplyShadow(Application application, bool dark)
    {
        application.Resources["CardShadow"] = CreateShadow(
            blur: dark ? 26 : 20,
            depth: 2,
            opacity: dark ? 0.30 : 0.08);

        application.Resources["SoftShadow"] = CreateShadow(
            blur: dark ? 13 : 10,
            depth: 1,
            opacity: dark ? 0.22 : 0.06);
    }

    /// <summary>Builds one shadow effect.</summary>
    /// <param name="blur">Blur radius.</param>
    /// <param name="depth">Offset of the shadow.</param>
    /// <param name="opacity">Shadow opacity.</param>
    private static DropShadowEffect CreateShadow(double blur, double depth, double opacity) => new()
    {
        Color = Color.FromRgb(0x10, 0x18, 0x28),
        BlurRadius = blur,
        ShadowDepth = depth,
        Direction = 270,
        Opacity = opacity,
        RenderingBias = RenderingBias.Performance,
    };
}
