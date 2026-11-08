using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Per-game deployment serialization. These tests deliberately use only the gate: the applier's
/// filesystem work is covered elsewhere, and a timing test on real deployment would be flaky.
/// </summary>
public sealed class InstallationGateTests
{
    [Fact]
    public async Task SameGameSerializesUntilTheFirstLeaseIsDisposed()
    {
        var gate = new InstallationGate();
        var first = await gate.AcquireAsync("game-a", "apply");

        var secondTask = gate.AcquireAsync("game-a", "undeploy");
        await Task.Delay(75);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(second);
    }

    [Fact]
    public async Task DifferentGamesDoNotBlockEachOther()
    {
        var gate = new InstallationGate();
        using var first = await gate.AcquireAsync("game-a", "apply");
        using var second = await gate.AcquireAsync("game-b", "apply").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(second);
    }

    [Fact]
    public async Task CancellingTheWaitReleasesTheWaiter()
    {
        var gate = new InstallationGate();
        using var first = await gate.AcquireAsync("game-a", "apply");
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.AcquireAsync("game-a", "undeploy", cancellation.Token));

        first.Dispose();
        using var next = await gate.AcquireAsync("game-a", "apply").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task ExceptionStillReleasesTheLeaseThroughUsing()
    {
        var gate = new InstallationGate();

        try
        {
            using var lease = await gate.AcquireAsync("game-a", "apply");
            throw new InvalidOperationException("simulated deployment failure");
        }
        catch (InvalidOperationException)
        {
            // The using above must have released the game.
        }

        using var next = await gate.AcquireAsync("game-a", "apply").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(next);
    }
}
