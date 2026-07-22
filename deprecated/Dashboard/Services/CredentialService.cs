/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using PerformanceMonitor.Common;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Interfaces;

namespace PerformanceMonitorDashboard.Services
{
    /// <summary>
    /// Secure credential storage service using Windows Credential Manager.
    /// Credentials are encrypted by Windows using DPAPI and stored per-user.
    /// NEVER stores passwords in plain text.
    ///
    /// Thin adapter over the shared <see cref="CredentialStore"/>. The
    /// <c>"PerformanceMonitor_"</c> prefix keeps Dashboard credentials isolated from
    /// Lite's (<c>"PerformanceMonitorLite_"</c>) in Windows Credential Manager — it MUST
    /// stay distinct. Warnings are routed to the app's static logger via
    /// <see cref="LoggerAdapter{T}"/>.
    /// </summary>
    public class CredentialService : ICredentialService
    {
        private const string CredentialPrefix = "PerformanceMonitor_";

        private readonly CredentialStore _store = new CredentialStore(
            CredentialPrefix,
            "SQL Server credentials for Performance Monitor Dashboard",
            new LoggerAdapter<CredentialService>());

        /// <inheritdoc />
        public bool SaveCredential(string serverId, string username, string password)
            => _store.SaveCredential(serverId, username, password);

        /// <inheritdoc />
        public (string Username, string Password)? GetCredential(string serverId)
            => _store.GetCredential(serverId);

        /// <inheritdoc />
        public bool DeleteCredential(string serverId)
            => _store.DeleteCredential(serverId);

        /// <inheritdoc />
        public bool CredentialExists(string serverId)
            => _store.CredentialExists(serverId);

        /// <inheritdoc />
        public bool UpdateCredential(string serverId, string username, string password)
            => _store.UpdateCredential(serverId, username, password);
    }
}
