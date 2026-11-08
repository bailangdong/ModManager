using System.IO;
using System.Text;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Regression cover for <see cref="SecretStore"/>.
/// </summary>
/// <remarks>
/// The store encrypts with DPAPI through <c>DpapiInterop</c>, a hand-written P/Invoke over
/// <c>CryptProtectData</c>/<c>CryptUnprotectData</c> (the engine takes no NuGet packages, so the
/// <c>System.Security.Cryptography.ProtectedData</c> wrapper is not available to it). A P/Invoke is exactly
/// the kind of code that compiles and then fails at runtime, so the round trip is proven here against the
/// real operating system rather than assumed.
/// </remarks>
public sealed class SecretStoreTests
{
    private const string Key = ISecretStore.NexusApiKey;
    private const string Value = "nexus-api-key-round-trip-42";

    /// <summary>The value must survive a save and a fresh load — that is the whole point of the store.</summary>
    [Fact]
    public void RoundTripsAValueAcrossInstances()
    {
        using var tree = new TemporaryTree();
        var directory = tree.Dir("secrets");

        new SecretStore(directory).Set(Key, Value);
        var reloaded = new SecretStore(directory);

        Assert.Equal(Value, reloaded.Get(Key));
    }

    /// <summary>A second instance must replace the value rather than append a duplicate entry.</summary>
    [Fact]
    public void OverwritesAnExistingValue()
    {
        using var tree = new TemporaryTree();
        var directory = tree.Dir("secrets");

        var store = new SecretStore(directory);
        store.Set(Key, Value);
        store.Set(Key, "replacement");

        Assert.Equal("replacement", new SecretStore(directory).Get(Key));
    }

    /// <summary>An unknown key is absent, not an exception.</summary>
    [Fact]
    public void ReturnsNullForAnUnknownKey()
    {
        using var tree = new TemporaryTree();

        Assert.Null(new SecretStore(tree.Dir("secrets")).Get("no-such-secret"));
    }

    /// <summary>
    /// The point of the store: a settings file that gets pasted into a bug report must not carry the key.
    /// </summary>
    /// <remarks>
    /// The length assertion is what distinguishes real encryption from the documented non-Windows fallback,
    /// which is a keyed XOR and would produce a same-length file. On Windows the stored blob is longer than
    /// the plaintext because DPAPI wraps it in a header, so this test fails if the P/Invoke silently
    /// degraded to obfuscation.
    /// </remarks>
    [Fact]
    public void DoesNotWriteTheSecretInPlaintext()
    {
        using var tree = new TemporaryTree();
        var directory = tree.Dir("secrets");

        new SecretStore(directory).Set(Key, Value);

        var bytes = File.ReadAllBytes(Path.Combine(directory, "secrets.dat"));
        Assert.DoesNotContain(Value, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.True(
            bytes.Length > Value.Length,
            $"Expected an encrypted blob longer than the plaintext, got {bytes.Length} bytes.");
    }

    /// <summary>A secret must not be recoverable from a directory that was replaced by a different store.</summary>
    [Fact]
    public void IsolatesStoresByDirectory()
    {
        using var tree = new TemporaryTree();
        new SecretStore(tree.Dir("first")).Set(Key, Value);

        Assert.Null(new SecretStore(tree.Dir("second")).Get(Key));
    }
}
