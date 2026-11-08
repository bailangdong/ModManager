using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModManager.Core.Serialization;

/// <summary>
/// Shared serializer settings, so every persisted document uses the same conventions.
/// </summary>
/// <remarks>
/// Centralised for one concrete reason: recovery reads documents written by an earlier version of
/// the application, so the two sides must agree on shape. Scattering <c>JsonSerializerOptions</c>
/// across call sites is how a persisted format silently drifts.
/// <para>
/// The <c>Manifest</c> settings are the important ones — a manifest is the authority for undeploy,
/// so it must round-trip exactly. Modrix calls its manifest table "sacred" for the same reason.
/// </para>
/// </remarks>
public static class ManifestJson
{
    /// <summary>Options used for manifests, journals and other durable documents.</summary>
    public static JsonSerializerOptions Options { get; } = Create(writeIndented: true);

    /// <summary>Options used for caches and settings, where compactness beats readability.</summary>
    public static JsonSerializerOptions Compact { get; } = Create(writeIndented: false);

    /// <summary>Builds one options instance.</summary>
    /// <param name="writeIndented">Whether to pretty-print.</param>
    private static JsonSerializerOptions Create(bool writeIndented) => new()
    {
        WriteIndented = writeIndented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Enums as names, not integers. A journal or manifest is something a support engineer may have
        // to read by hand, and "Hardlink" is worth considerably more than "0" in that situation.
        Converters = { new JsonStringEnumConverter() },

        // Case-insensitive so a hand-edited game definition still loads.
        PropertyNameCaseInsensitive = true,
    };
}
