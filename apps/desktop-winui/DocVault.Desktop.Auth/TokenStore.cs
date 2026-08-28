// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocVault.Desktop.Auth;

/// <summary>Tokens held between application runs.</summary>
public sealed record StoredTokens(string? AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// True when the access token is gone or close enough to expiry to be worth refreshing.
    /// </summary>
    /// <remarks>
    /// The 60-second margin absorbs clock drift and request latency. Without it a token can pass
    /// this check and still be rejected by the API by the time the request lands — an
    /// intermittent 401 that is unpleasant to reproduce.
    /// </remarks>
    public bool NeedsRefresh => string.IsNullOrEmpty(AccessToken)
                                || DateTimeOffset.UtcNow >= ExpiresAt.AddSeconds(-60);
}

public interface ITokenStore
{
    StoredTokens? Load();
    void Save(StoredTokens tokens);
    void Clear();
}

/// <summary>Non-persistent store. Used by the tests, and a safe default off Windows.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private StoredTokens? _tokens;

    public StoredTokens? Load() => _tokens;
    public void Save(StoredTokens tokens) => _tokens = tokens;
    public void Clear() => _tokens = null;
}

/// <summary>
/// Persists tokens encrypted with Windows DPAPI, scoped to the current user.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DataProtectionScope.CurrentUser"/> ties the ciphertext to the signed-in Windows
/// account, so another user on the same machine cannot read it even with the file in hand.
/// </para>
/// <para>
/// The obvious alternative, <c>Windows.Security.Credentials.PasswordVault</c>, is a better fit in
/// some ways but requires a <em>packaged</em> (MSIX) app — it throws for unpackaged desktop apps.
/// This sample runs unpackaged so it can be launched with <c>dotnet run</c>, hence DPAPI.
/// </para>
/// <para>
/// DPAPI is Windows-only. This library targets plain <c>net10.0</c> so its logic can be built and
/// tested on any OS, so the platform check is a runtime guard rather than a compile-time one.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore : ITokenStore
{
    private readonly string _path;

    public DpapiTokenStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocVault",
            "tokens.bin");
    }

    public StoredTokens? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_path))
        {
            return null;
        }

        try
        {
            var plaintext = ProtectedData.Unprotect(
                File.ReadAllBytes(_path), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredTokens>(Encoding.UTF8.GetString(plaintext));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Unreadable means a different Windows account, a corrupted file, or a format change.
            // Treat it as "not signed in" and move on: failing to start because an old cache
            // cannot be decrypted would be a worse outcome than one extra sign-in.
            Clear();
            return null;
        }
    }

    public void Save(StoredTokens tokens)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI token storage requires Windows.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var ciphertext = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens)),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, ciphertext);
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
