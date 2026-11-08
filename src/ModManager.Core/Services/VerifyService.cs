namespace ModManager.Core.Services;

/// <summary>
/// Verifies a deployed tree against its manifests, and decides what an uninstall may remove.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix <c>deploy/verify.rs</c> plus the user's spec §13. The spec is explicit
/// about the case that matters:
/// </para>
/// <list type="bullet">
/// <item>the game originally had <c>a.dll</c>;</item>
/// <item>installing Mod A replaced it;</item>
/// <item>the user then edited <c>a.dll</c> by hand;</item>
/// <item>uninstalling Mod A must <b>not</b> simply delete it.</item>
/// </list>
/// <para>
/// The rule that answers this is to compare three hashes: the hash recorded when we deployed, the
/// hash on disk now, and the original's hash from the backup. When the current hash differs from
/// what we deployed, the file is no longer ours to remove — the default is to keep it and ask.
/// </para>
/// <para>
/// Modrix applies this rule on the removal path itself ("<c>present &amp;&amp; !file_matches</c> → skip
/// and count <c>skipped_modified</c>"). We surface the same finding earlier, as a per-file verdict,
/// because the user asked for a prompt rather than a silent skip — so the decision is available
/// before anything is deleted, not discovered afterwards.
/// </para>
/// </remarks>
public sealed class VerifyService : IVerifyService
{
    /// <inheritdoc />
    public Task<VerifyReport> VerifyGameAsync(
        string gameId,
        IReadOnlyList<ModManifest> manifests,
        string installPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        _ = installPath;

        var details = new List<FileVerification>();
        var intact = 0;
        var modified = 0;
        var missing = 0;
        var unreadable = 0;

        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var file in manifest.Files)
            {
                var absolute = DeploymentPlanner.SafeCombine(manifest.DeploymentRoot, file.RelativeTarget);
                var verification = Inspect(file, absolute);

                switch (verification.Status)
                {
                    case FileVerifyStatus.Intact:
                        intact++;
                        break;

                    case FileVerifyStatus.Modified:
                    case FileVerifyStatus.Foreign:
                        modified++;
                        break;

                    case FileVerifyStatus.Missing:
                        missing++;
                        break;

                    default:
                        unreadable++;
                        break;
                }

                details.Add(verification);
            }
        }

        var report = new VerifyReport(gameId, details.Count, intact, modified, missing, unreadable, details);
        return Task.FromResult(report);
    }

    /// <inheritdoc />
    public IReadOnlyList<FileVerification> PlanUndeploy(ModManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var verdicts = new List<FileVerification>(manifest.Files.Count);

        foreach (var file in manifest.Files)
        {
            var absolute = DeploymentPlanner.SafeCombine(manifest.DeploymentRoot, file.RelativeTarget);
            verdicts.Add(Inspect(file, absolute));
        }

        return verdicts;
    }

    /// <summary>Inspects one deployed file and classifies it.</summary>
    /// <param name="file">Manifest row.</param>
    /// <param name="absolute">Absolute path on disk.</param>
    private static FileVerification Inspect(DeployedFile file, string absolute)
    {
        if (!Safety.FileSystemOps.ExistsNoFollow(absolute))
        {
            return new FileVerification(
                file.RelativeTarget,
                FileVerifyStatus.Missing,
                file.Sha256,
                null,
                file.ModId);
        }

        string actual;
        try
        {
            actual = Safety.FileSystemOps.HashFile(absolute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileVerification(
                file.RelativeTarget,
                FileVerifyStatus.Unreadable,
                file.Sha256,
                null,
                file.ModId);
        }

        if (string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new FileVerification(
                file.RelativeTarget,
                FileVerifyStatus.Intact,
                file.Sha256,
                actual,
                file.ModId);
        }

        // The bytes are not what we wrote. Distinguish "the user edited our file" from "something
        // replaced it with the original content again", because the second case means the game or
        // another tool already undid our work and removing it would be destructive of nothing.
        var status = file.OriginalSha256 is { Length: > 0 }
            && string.Equals(actual, file.OriginalSha256, StringComparison.OrdinalIgnoreCase)
                ? FileVerifyStatus.Foreign
                : FileVerifyStatus.Modified;

        return new FileVerification(
            file.RelativeTarget,
            status,
            file.Sha256,
            actual,
            file.ModId);
    }
}
