/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// DPAPI protection for SQL-auth passwords in darling.json. LocalMachine scope deliberately —
/// the service runs under a service account, and machine scope lets an administrator encrypt a
/// password interactively (--encrypt-password) that the service account can later decrypt on
/// the same machine. (Lite uses the Windows Credential Manager instead, which is user-profile
/// scoped — right for an interactive app, wrong for a service.) The blob is machine-bound:
/// moving darling.json to another machine requires re-encrypting.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DarlingSecrets
{
    private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("PerformanceMonitor.Darling.v1");

    public static string Protect(string plaintext)
    {
        if (plaintext is null)
        {
            throw new ArgumentNullException(nameof(plaintext));
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), s_entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string base64Blob)
    {
        if (string.IsNullOrWhiteSpace(base64Blob))
        {
            throw new ArgumentException("Encrypted password blob is empty.", nameof(base64Blob));
        }

        var plainBytes = ProtectedData.Unprotect(
            Convert.FromBase64String(base64Blob), s_entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plainBytes);
    }

    /// <summary>
    /// Resolves a monitored server's SQL-auth password: DPAPI blob preferred, then the <c>password</c>
    /// slot — which since #1804 may be an <c>env:</c>/<c>file:</c> REFERENCE
    /// (<see cref="DarlingSecretSource"/>) rather than a literal. <paramref name="usedPlaintext"/> is true
    /// only for a LITERAL (a reference is not plaintext-in-config, so callers do not warn on it).
    /// </summary>
    public static string ResolvePassword(MonitoredServer server, out bool usedPlaintext)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        if (!string.IsNullOrWhiteSpace(server.EncryptedPassword))
        {
            usedPlaintext = false;

            /* #2087: add_servers stores env:/file: REFERENCES verbatim in this slot on Linux (a pointer is
               not a secret; DPAPI cannot exist there). A reference can never be confused with a DPAPI blob:
               blobs are base64 and contain no ':' prefix match. */
            if (DarlingSecretSource.IsReference(server.EncryptedPassword))
            {
                return DarlingSecretSource.Resolve(server.EncryptedPassword, $"servers['{server.DisplayName}'].encryptedPassword");
            }

            return Unprotect(server.EncryptedPassword);
        }

        if (!string.IsNullOrWhiteSpace(server.Password))
        {
            usedPlaintext = !DarlingSecretSource.IsReference(server.Password);
            return DarlingSecretSource.Resolve(server.Password, $"servers['{server.DisplayName}'].password");
        }

        throw new InvalidOperationException(
            $"Server '{server.DisplayName}' uses sql auth but has neither encryptedPassword nor password.");
    }
}
