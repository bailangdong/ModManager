using System.IO;

namespace ModManager.Tests;

/// <summary>
/// A throwaway directory tree under the repository's <c>artifacts</c> folder.
/// </summary>
/// <remarks>
/// <para>
/// The suite runs against real files rather than a fake filesystem on purpose: every invariant this project
/// protects — containment, atomic replacement, hash-checked deletion — is a statement about what happens to
/// real files, and a fake filesystem would be a re-implementation whose bugs would be indistinguishable from
/// the engine's. Each tree is unique per test, so tests can run in parallel without touching each other.
/// </para>
/// <para>
/// WHY NOT THE SYSTEM TEMP DIRECTORY: the task's data-safety rules put test scratch files under the project's
/// own <c>artifacts</c> directory, and in a sandboxed build the test host is often denied write access to
/// <c>%TEMP%</c> even when an interactive shell is allowed it — which turned every file-backed test into an
/// <c>UnauthorizedAccessException</c> that had nothing to do with the code under test. Keeping scratch inside
/// the workspace removes both problems at once.
/// </para>
/// </remarks>
public sealed class TemporaryTree : IDisposable
{
    /// <summary>Creates the tree and its root directory.</summary>
    public TemporaryTree()
    {
        Root = Path.Combine(RepositoryRoot(), "artifacts", "tests", "tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Absolute root of this tree.</summary>
    public string Root { get; }

    /// <summary>Creates a subdirectory and returns its absolute path.</summary>
    /// <param name="relative">Relative path, written with forward slashes.</param>
    public string Dir(string relative)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a text file, creating its parent directory.</summary>
    /// <param name="relative">Relative path, written with forward slashes.</param>
    /// <param name="content">Text to write.</param>
    /// <returns>The absolute path written.</returns>
    public string WriteFile(string relative, string content)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Writes a binary file, creating its parent directory.</summary>
    /// <param name="relative">Relative path, written with forward slashes.</param>
    /// <param name="content">Bytes to write.</param>
    /// <returns>The absolute path written.</returns>
    public string WriteBytes(string relative, byte[] content)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a test run over; it stays under artifacts,
            // where the next run (or the developer) can see and clean it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Repository root, found by walking up to the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ModManager.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("ModManager.sln not found above " + AppContext.BaseDirectory);
    }
}
