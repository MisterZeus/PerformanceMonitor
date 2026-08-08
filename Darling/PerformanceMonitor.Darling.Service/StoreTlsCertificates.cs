/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The store's TLS material, generated as a REAL two-cert chain (#2117): a throwaway local root CA
/// whose private key is discarded the moment it has signed the one server leaf, and the leaf
/// postgres serves. The old single self-signed end-entity cert (critical <c>CA=false</c> Basic
/// Constraints) was its own trust anchor, and Windows' chain engine refuses that shape under the
/// custom-root trust Npgsql uses for <c>Root Certificate=…</c> — so the exact connection string
/// <c>--print-viewer-connection</c> printed failed <c>VerifyFull</c> on the platform most viewers
/// run on, while the same certificate imported into the OS trust store validated fine (the field
/// report's workaround). A leaf under a real CA root is the textbook case every chain engine —
/// Windows, macOS, Linux, and libpq for non-Npgsql clients — accepts.
///
/// <para>Discarding the CA key is load-bearing: nothing can ever mint another certificate under
/// the distributed root, so trusting <c>root.crt</c> pins exactly one server identity, the same
/// security property the single self-signed cert had. Rotation regenerates BOTH (delete the server
/// cert + key and restart, exactly the old delete-to-rotate contract).</para>
///
/// <para>Pure — no file I/O, no logger — so the chain's validity under Npgsql's exact custom-root
/// trust semantics is pinned by tests on every OS CI runs.</para>
/// </summary>
internal static class StoreTlsCertificates
{
    /// <summary>What postgres serves (<c>ssl_cert_file</c> takes the whole chain, leaf first), the
    /// leaf's private key, and the root the operator distributes to viewers.</summary>
    internal sealed record Generated(string ServerCertChainPem, string ServerKeyPem, string RootCertPem);

    internal static Generated Create(string hostName, IPAddress listenIp, int validityYears)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);
        ArgumentNullException.ThrowIfNull(listenIp);

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.AddYears(validityYears);

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            $"CN=PerformanceMonitor Darling store root ({hostName})", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        /* pathLenConstraint 0: this root may sign end-entity certs only — even with the key discarded,
           the constraint documents the intent in the certificate itself. */
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var caCertificate = caRequest.CreateSelfSigned(notBefore, notAfter);

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            $"CN={hostName}", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(listenIp);
        sanBuilder.AddDnsName(hostName);
        leafRequest.CertificateExtensions.Add(sanBuilder.Build());
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leafRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") /* serverAuth */ }, false));

        var serialNumber = new byte[12];
        RandomNumberGenerator.Fill(serialNumber);
        /* The leaf's window may not exceed the issuer's — same instants, which Create() accepts. */
        using var leafCertificate = leafRequest.Create(caCertificate, notBefore, notAfter, serialNumber);

        return new Generated(
            ServerCertChainPem: leafCertificate.ExportCertificatePem() + "\n" + caCertificate.ExportCertificatePem() + "\n",
            ServerKeyPem: leafKey.ExportPkcs8PrivateKeyPem(),
            RootCertPem: caCertificate.ExportCertificatePem() + "\n");
    }
}
