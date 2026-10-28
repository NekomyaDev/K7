using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Client;

namespace K7.Clients.MAUI.Services.Authentication;

/// <summary>
/// Persisted symmetric credentials for the OpenIddict client. The client only uses its own
/// keys to protect the state tokens of interactive logins (browser round trip), never for the
/// refresh grant, and the server never sees them. Ephemeral RSA keys cost two RSA-2048
/// generations per launch (10 s of CPU on a 32-bit TV box, paid while resolving OpenIddict)
/// and invalidated any login whose process was killed before the callback. The keys live in
/// SecureStorage next to the refresh token.
/// </summary>
internal static class OpenIddictClientKeys
{
    private const string SigningKeyName = "openiddict_client_signing_key";
    private const string EncryptionKeyName = "openiddict_client_encryption_key";
    private const int KeySizeBytes = 32;

    /// <summary>
    /// Registers an HS256 signing key and an A256KW encryption key loaded from (or created in)
    /// secure storage, falling back to ephemeral RSA keys when secure storage is unavailable.
    /// The storage read runs when the options are built (DeferredHeavy worker thread), not
    /// during CreateMauiApp: the builder delegate itself runs on the main thread at startup
    /// and a SecureStorage read there cost ~0.4 s before the first window.
    /// </summary>
    public static void Register(OpenIddictClientBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Configure(AddCredentials);
    }

    /// <summary>
    /// Drops both keys (server reset). New ones are created on the next OpenIddict resolve,
    /// which invalidates any login still in flight.
    /// </summary>
    public static void Reset()
    {
        try
        {
            SecureStorage.Default.Remove(SigningKeyName);
            SecureStorage.Default.Remove(EncryptionKeyName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"K7 MAUI - OpenIddict key reset failed: {ex}");
        }
    }

    // Mirrors OpenIddictClientBuilder.AddSigningKey / AddEncryptionKey for symmetric keys
    // (HS256, and A256KW with Aes256CbcHmacSha512), which the builder cannot register lazily.
    private static void AddCredentials(OpenIddictClientOptions options)
    {
        try
        {
            var signing = new SymmetricSecurityKey(LoadOrCreate(SigningKeyName));
            var encryption = new SymmetricSecurityKey(LoadOrCreate(EncryptionKeyName));
            options.SigningCredentials.Add(new SigningCredentials(signing, SecurityAlgorithms.HmacSha256));
            options.EncryptionCredentials.Add(new EncryptingCredentials(
                encryption, SecurityAlgorithms.Aes256KW, SecurityAlgorithms.Aes256CbcHmacSha512));
            MauiStartupTrace.Mark("OpenIddictKeys-persisted");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"K7 MAUI - OpenIddict persisted keys unavailable, using ephemeral keys: {ex}");
            MauiStartupTrace.Mark("OpenIddictKeys-ephemeral-fallback");
            options.SigningCredentials.Add(new SigningCredentials(
                new RsaSecurityKey(RSA.Create(2048)), SecurityAlgorithms.RsaSha256));
            options.EncryptionCredentials.Add(new EncryptingCredentials(
                new RsaSecurityKey(RSA.Create(2048)), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        }
    }

    private static byte[] LoadOrCreate(string name)
    {
        var stored = Read(name);
        if (stored is not null)
        {
            try
            {
                var bytes = Convert.FromBase64String(stored);
                if (bytes.Length == KeySizeBytes)
                    return bytes;
            }
            catch (FormatException)
            {
                // Corrupt entry: regenerate below.
            }
        }

        var key = RandomNumberGenerator.GetBytes(KeySizeBytes);
        Write(name, Convert.ToBase64String(key));
        return key;
    }

    // SecureStorage is Task-based; the options are built on a worker thread.
    private static string? Read(string name) =>
        Task.Run(() => SecureStorage.Default.GetAsync(name)).GetAwaiter().GetResult();

    private static void Write(string name, string value) =>
        Task.Run(() => SecureStorage.Default.SetAsync(name, value)).GetAwaiter().GetResult();
}
