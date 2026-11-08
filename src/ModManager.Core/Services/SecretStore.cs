using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ModManager.Core.Services;

/// <summary>
/// Stores secrets separately from settings, with light encryption at rest.
/// </summary>
/// <remarks>
/// <para>
/// WHY SEPARATE FROM <see cref="AppSettings"/>: the settings file is the artifact users paste into
/// bug reports and that a support tool is most likely to dump. Keeping the Nexus API key out of it
/// means an accidental share cannot leak a credential — the requirement in the user's spec §7.
/// </para>
/// <para>
/// WHAT THIS IS AND IS NOT: values are encrypted with DPAPI (<c>CryptProtectData</c>) on Windows,
/// which ties the ciphertext to the current user account, and are merely obfuscated on other
/// platforms. So this protects against a settings file being copied off the machine or read by
/// another user account — it is <b>not</b> a defence against malware running as the same user, and
/// the code deliberately claims no more than that. Nothing here is a substitute for the user
/// treating their own API key as a credential.
/// </para>
/// </remarks>
public sealed class SecretStore : ISecretStore
{
    private const string FileName = "secrets.dat";
    private const string entropy = "ModManager.SecretStore.v1";

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly string _filePath;
    private readonly IAppLogger? _logger;
    /// <summary>
    /// Serialises rewrites of the secrets file so two saves cannot interleave. A plain
    /// <see cref="object"/> because <c>System.Threading.Lock</c> requires .NET 9 and this project targets
    /// <c>net8.0</c>.
    /// </summary>
    private readonly object _writeLock = new();

    /// <summary>Creates the store and loads any existing secrets.</summary>
    /// <param name="directory">Directory holding the secrets file.</param>
    /// <param name="logger">Optional logger.</param>
    public SecretStore(string directory, IAppLogger? logger = null)
    {
        _filePath = Path.Combine(directory, FileName);
        _logger = logger;
        Load();
    }

    /// <inheritdoc />
    public string? Get(string name) => _cache.TryGetValue(name, out var value) ? value : null;

    /// <inheritdoc />
    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _cache[name] = value;
        Persist();
    }

    /// <inheritdoc />
    public void Remove(string name)
    {
        if (_cache.TryRemove(name, out _))
        {
            Persist();
        }
    }

    /// <inheritdoc />
    public bool Contains(string name) => _cache.ContainsKey(name) && !string.IsNullOrEmpty(_cache[name]);

    /// <summary>Reads and decrypts the secrets file, tolerating absence and corruption.</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var protectedBytes = File.ReadAllBytes(_filePath);
            var plaintext = Unprotect(protectedBytes);
            if (plaintext is null)
            {
                _logger?.Warn("Secrets", "密钥文件无法解密，将在下次保存时重写。");
                return;
            }

            foreach (var line in Encoding.UTF8.GetString(plaintext).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                var name = line[..separator];
                var value = line[(separator + 1)..];
                if (name.Length > 0 && value.Length > 0)
                {
                    _cache[name] = value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger?.Warn("Secrets", "无法加载密钥，将以空密钥库继续。", ex.Message);
        }
    }

    /// <summary>Encrypts and writes the whole store atomically.</summary>
    private void Persist()
    {
        try
        {
            var builder = new StringBuilder();
            foreach (var (name, value) in _cache.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrEmpty(value))
                {
                    builder.Append(name).Append('=').Append(value).Append('\n');
                }
            }

            var protectedBytes = Protect(Encoding.UTF8.GetBytes(builder.ToString()));

            lock (_writeLock)
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temp = _filePath + ".tmp";
                File.WriteAllBytes(temp, protectedBytes);
                File.Move(temp, _filePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger?.Error("Secrets", "无法保存密钥。", ex.Message);
        }
    }

    /// <summary>
    /// Encrypts with DPAPI on Windows (see <see cref="DpapiInterop"/> for why that is a P/Invoke rather than
    /// a package). Elsewhere it applies a keyed XOR so the file is not trivially readable, and the
    /// documentation states plainly that this is obfuscation only.
    /// </summary>
    /// <param name="plaintext">Bytes to protect.</param>
    private static byte[] Protect(byte[] plaintext)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var protectedBytes = DpapiInterop.Protect(plaintext, Encoding.UTF8.GetBytes(entropy));
                if (protectedBytes is not null)
                {
                    return protectedBytes;
                }
            }
            catch (CryptographicException)
            {
                // Fall through to the weak path rather than losing the user's credentials.
            }
        }

        return Obfuscate(plaintext);
    }

    /// <summary>Reverses <see cref="Protect"/>.</summary>
    /// <param name="protectedBytes">Bytes to unprotect.</param>
    private static byte[]? Unprotect(byte[] protectedBytes)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var plaintext = DpapiInterop.Unprotect(protectedBytes, Encoding.UTF8.GetBytes(entropy));
                if (plaintext is not null)
                {
                    return plaintext;
                }
            }
            catch (CryptographicException)
            {
                // Might be a weak-form file written on another platform; try that before giving up.
            }
        }

        try
        {
            return Deobfuscate(protectedBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Weak obfuscation for non-Windows platforms. Not a security boundary.</summary>
    /// <param name="plaintext">Bytes to obfuscate.</param>
    private static byte[] Obfuscate(byte[] plaintext)
    {
        var key = Encoding.UTF8.GetBytes(entropy);
        var output = new byte[plaintext.Length];
        for (var index = 0; index < plaintext.Length; index++)
        {
            output[index] = (byte)(plaintext[index] ^ key[index % key.Length]);
        }

        return output;
    }

    /// <summary>Reverses <see cref="Obfuscate"/>.</summary>
    /// <param name="ciphertext">Bytes to deobfuscate.</param>
    private static byte[] Deobfuscate(byte[] ciphertext) => Obfuscate(ciphertext);
}
