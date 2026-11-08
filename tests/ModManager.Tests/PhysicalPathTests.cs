using System.Diagnostics;
using System.IO;
using ModManager.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace ModManager.Tests;

/// <summary>
/// Proves that the deployment lock uses the physical directory, not the spelling of its path.
/// Junctions and symbolic links are created only inside a throwaway test tree.
/// </summary>
public sealed class PhysicalPathTests
{
    private readonly ITestOutputHelper _output;

    public PhysicalPathTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CaseAndTrailingSeparatorVariantsResolveToTheSameKey()
    {
        using var tree = new TemporaryTree();
        var real = tree.Dir("real");

        Assert.True(PhysicalPathResolver.TryResolve(real, out var first, out var error), error);
        Assert.True(PhysicalPathResolver.TryResolve(real + Path.DirectorySeparatorChar, out var second, out error), error);
        Assert.True(PhysicalPathResolver.TryResolve(real.ToUpperInvariant(), out var third, out error), error);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public async Task JunctionAliasSharesTheInProcessLock()
    {
        using var tree = new TemporaryTree();
        var target = tree.Dir("target");
        var link = Path.Combine(tree.Root, "link");

        if (!TryCreateJunction(link, target))
        {
            _output.WriteLine("Junction creation is not available in this environment; alias lock test was not exercised.");
            return;
        }

        var gate = new InstallationGate(crossProcess: false);
        using var first = await gate.AcquireAsync(target, "test");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.AcquireAsync(link, "test", cancellation.Token));
    }

    [Fact]
    public async Task SymbolicLinkAliasSharesTheInProcessLockWhenSupported()
    {
        using var tree = new TemporaryTree();
        var target = tree.Dir("target");
        var link = Path.Combine(tree.Root, "symlink");

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _output.WriteLine($"Symbolic-link creation is not available: {ex.Message}");
            return;
        }

        var gate = new InstallationGate(crossProcess: false);
        using var first = await gate.AcquireAsync(target, "test");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.AcquireAsync(link, "test", cancellation.Token));
    }

    [Fact]
    public async Task DifferentPhysicalDirectoriesStillRunConcurrently()
    {
        using var tree = new TemporaryTree();
        var first = tree.Dir("first");
        var second = tree.Dir("second");
        var gate = new InstallationGate(crossProcess: false);

        using var firstLease = await gate.AcquireAsync(first, "test");
        using var secondLease = await gate.AcquireAsync(second, "test").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(secondLease);
    }

    [Fact]
    public async Task MissingDirectoryFailsClosedInsteadOfUsingALexicalKey()
    {
        using var tree = new TemporaryTree();
        var missing = Path.Combine(tree.Root, "does-not-exist");
        var gate = new InstallationGate(crossProcess: false);

        await Assert.ThrowsAsync<IOException>(() => gate.AcquireAsync(missing, "test"));
    }

    [Fact]
    public async Task JunctionAliasBlocksAcrossProcesses()
    {
        using var tree = new TemporaryTree();
        var target = tree.Dir("target");
        var link = Path.Combine(tree.Root, "link");

        if (!TryCreateJunction(link, target))
        {
            _output.WriteLine("Junction creation is not available; cross-process alias test was not exercised.");
            return;
        }

        var probeDll = Path.Combine(AppContext.BaseDirectory, "ModManager.LockProbe.dll");
        Assert.True(File.Exists(probeDll), $"The lock probe was not copied to '{probeDll}'.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(probeDll);
        startInfo.ArgumentList.Add("hold");
        startInfo.ArgumentList.Add(target);
        startInfo.ArgumentList.Add("2500");

        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the lock probe.");

        try
        {
            Assert.Equal("locked", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));

            var gate = new InstallationGate(
                crossProcess: true,
                crossProcessTimeout: TimeSpan.FromMilliseconds(250));

            await Assert.ThrowsAsync<TimeoutException>(() => gate.AcquireAsync(link, "cross-process-alias"));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
