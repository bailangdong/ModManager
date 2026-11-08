using System.Diagnostics;
using System.IO;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Proves the named-mutex layer with a real second process, not by constructing two locks in one
/// process. The child only holds the lock over a scratch directory; it never touches a game folder.
/// </summary>
public sealed class CrossProcessLockTests
{
    [Fact]
    public async Task NamedMutexBlocksAnotherProcessThenReleasesWhenThatProcessExits()
    {
        using var tree = new TemporaryTree();
        var resource = tree.Dir("game");

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
        startInfo.ArgumentList.Add(resource);
        startInfo.ArgumentList.Add("2500");

        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the lock probe.");

        try
        {
            var locked = await child.StandardOutput
                .ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("locked", locked);

            var gate = new InstallationGate(
                crossProcess: true,
                crossProcessTimeout: TimeSpan.FromMilliseconds(250));

            var started = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(
                () => gate.AcquireAsync(resource, "cross-process-probe"));
            Assert.True(
                started.Elapsed >= TimeSpan.FromMilliseconds(200),
                $"The waiter returned after only {started.Elapsed.TotalMilliseconds:0} ms.");

            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, child.ExitCode);

            // The OS released the mutex when the child exited; the parent can now take it.
            using var lease = await gate.AcquireAsync(resource, "after-child-exit")
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(lease);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }
}
