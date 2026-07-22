/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using PerformanceMonitorDashboard.Interfaces;
using PerformanceMonitorDashboard.Services;
using PerformanceMonitor.Common;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitorDashboard.Models
{
    public class ServerConnection : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ServerName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Backward compatibility property for old servers.json files.
        /// Returns true if authentication type is Windows.
        /// Setter updates AuthenticationType for migration from old configs.
        /// </summary>
        public bool UseWindowsAuth
        {
            get => AuthenticationType == AuthenticationTypes.Windows;
            set
            {
                // During JSON deserialization of old configs, update AuthenticationType based on UseWindowsAuth
                // Only apply this if AuthenticationType is still at default (indicating old JSON without that field)
                if (AuthenticationType == AuthenticationTypes.Windows && !value)
                {
                    // Old config with UseWindowsAuth=false -> SQL Server auth
                    AuthenticationType = AuthenticationTypes.SqlServer;
                }
                // If value is true, keep Windows (already the default)
            }
        }

        /// <summary>
        /// Authentication type: Windows, SqlServer, EntraMFA, ServicePrincipal, or ManagedIdentity.
        /// </summary>
        public string AuthenticationType { get; set; } = AuthenticationTypes.Windows;

        /// <summary>
        /// Service-principal application/client id (used as UserID for ServicePrincipal auth).
        /// Non-secret; safe to persist in servers.json. The SP secret is NEVER stored here —
        /// it lives only in Windows Credential Manager (DPAPI) via CredentialService.
        /// </summary>
        public string? AzureClientId { get; set; }

        /// <summary>
        /// Optional user-assigned managed identity client id. Blank = system-assigned identity.
        /// Non-secret; safe to persist in servers.json.
        /// </summary>
        public string? ManagedIdentityClientId { get; set; }

        public string? Description { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime LastConnected { get; set; } = DateTime.Now;
        public bool IsFavorite { get; set; }

        /// <summary>
        /// Encryption mode for the connection. Valid values: Optional, Mandatory, Strict.
        /// Default is Mandatory for security. Users can opt down to Optional if needed.
        /// </summary>
        public string EncryptMode { get; set; } = "Mandatory";

        /// <summary>
        /// Whether to trust the server certificate without validation.
        /// Default is false for security. Enable for servers with self-signed certificates.
        /// </summary>
        public bool TrustServerCertificate { get; set; } = false;

        /// <summary>
        /// When true, sets ApplicationIntent=ReadOnly on the connection string.
        /// Required for connecting to AG listener read-only replicas and
        /// Azure SQL Business Critical / Managed Instance built-in read replicas.
        /// </summary>
        public bool ReadOnlyIntent { get; set; } = false;

        /// <summary>
        /// When true, sets MultiSubnetFailover=true on the connection string.
        /// Recommended for AG listeners and FCIs spanning multiple subnets.
        /// </summary>
        public bool MultiSubnetFailover { get; set; } = false;

        /// <summary>
        /// Monthly cost of this server in USD, used for FinOps cost attribution.
        /// Set to 0 to hide cost columns. All FinOps costs are proportional to this budget.
        /// </summary>
        public decimal MonthlyCostUsd { get; set; } = 0m;

        /// <summary>
        /// #1236: per-server override for the alert delivery mode (deadlock/blocking notifications).
        /// null = inherit the global AlertDeliveryMode setting; Summary/PerEvent forces that mode for
        /// this server regardless of the global default (e.g. Per-event for one noisy prod box, Summary
        /// everywhere else). Resolved via <see cref="PerformanceMonitor.Notifications.AlertDeliveryModeResolver"/>.
        /// </summary>
        public AlertNotificationMode? AlertDeliveryModeOverride { get; set; }

        /// <summary>
        /// Display name with "(Read-Only)" suffix when ReadOnlyIntent is enabled.
        /// Used for alerts, tray notifications, tab headers, and dialog messages.
        /// </summary>
        [JsonIgnore]
        public string DisplayNameWithIntent => ReadOnlyIntent ? $"{DisplayName} (Read-Only)" : DisplayName;

        private string? _installedVersion;

        /// <summary>
        /// Installed PerformanceMonitor version on this server. Populated asynchronously
        /// by the Manage Servers window. Not persisted — runtime-only display field.
        /// Conventional values: null (not yet probed), a 3-part version like "2.10.0",
        /// "Not installed", or "Unavailable" when the probe fails.
        /// </summary>
        [JsonIgnore]
        public string? InstalledVersion
        {
            get => _installedVersion;
            set
            {
                if (_installedVersion == value) return;
                _installedVersion = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstalledVersion)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Display-only property for showing authentication type in UI.
        /// </summary>
        [JsonIgnore]
        public string AuthenticationDisplay => AuthenticationType switch
        {
            AuthenticationTypes.EntraMFA => "Microsoft Entra MFA",
            AuthenticationTypes.SqlServer => "SQL Server",
            AuthenticationTypes.ServicePrincipal => "Azure — Service Principal",
            AuthenticationTypes.ManagedIdentity => "Azure — Managed Identity",
            _ => "Windows"
        };

        /// <summary>
        /// SECURITY: Credentials are NEVER serialized to JSON.
        /// They are stored securely in Windows Credential Manager.
        /// This method retrieves the connection string with credentials loaded from secure storage.
        /// </summary>
        /// <param name="credentialService">The credential service to use for retrieving stored credentials</param>
        /// <returns>Connection string for SQL Server</returns>
        public string GetConnectionString(ICredentialService credentialService)
        {
            // Single builder for every auth mode; the auth-specific keywords are applied by the shared
            // ApplyAuthentication helper so this production path can never drift from the Add/Edit
            // dialog's Test-Connection builder. These non-auth settings match the prior EntraMFA builder.
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = ServerName,
                InitialCatalog = "PerformanceMonitor",
                ApplicationName = "PerformanceMonitorDashboard",
                ConnectTimeout = 15,
                MultipleActiveResultSets = true,
                TrustServerCertificate = TrustServerCertificate,
                Encrypt = EncryptMode switch
                {
                    "Optional" => SqlConnectionEncryptOption.Optional,
                    "Strict" => SqlConnectionEncryptOption.Strict,
                    _ => SqlConnectionEncryptOption.Mandatory
                },
                ApplicationIntent = ReadOnlyIntent ? ApplicationIntent.ReadOnly : ApplicationIntent.ReadWrite,
                MultiSubnetFailover = MultiSubnetFailover
            };

            // Resolve credentials from secure storage per auth mode (mirrors Lite's inline resolution):
            //   SqlServer / ServicePrincipal -> stored username + password (SP: client id + client secret)
            //   EntraMFA                     -> stored username hint only (pre-populates the interactive prompt)
            //   ManagedIdentity / Windows    -> no stored secret
            string? username = null;
            string? password = null;

            if (AuthenticationType == AuthenticationTypes.SqlServer ||
                AuthenticationType == AuthenticationTypes.ServicePrincipal)
            {
                var cred = credentialService.GetCredential(Id);
                if (cred.HasValue)
                {
                    username = cred.Value.Username;
                    password = cred.Value.Password;
                }
            }
            else if (AuthenticationType == AuthenticationTypes.EntraMFA)
            {
                // Preserve the existing behavior: pre-populate UserID from the stored username hint.
                var cred = credentialService.GetCredential(Id);
                if (cred.HasValue && !string.IsNullOrEmpty(cred.Value.Username))
                {
                    username = cred.Value.Username;
                }
            }

            ApplyAuthentication(builder, AuthenticationType, username, password, AzureClientId, ManagedIdentityClientId);

            return builder.ConnectionString;
        }

        // SYNC: verbatim mirror of Lite ServerConnection.ApplyAuthentication — keep the two in lockstep.
        // Guarded by Dashboard.Tests/AzureAuthConnectionStringTests + Lite.Tests/AzureAuthConnectionStringTests.
        /// <summary>
        /// Applies the authentication-mode keywords (IntegratedSecurity / Authentication / UserID /
        /// Password) to a connection-string builder. Shared by <see cref="GetConnectionString"/> and
        /// the Add/Edit dialog's Test-Connection builder so the two build sites never diverge.
        /// </summary>
        /// <param name="builder">The builder to mutate.</param>
        /// <param name="authenticationType">One of the <see cref="AuthenticationTypes"/> constants.</param>
        /// <param name="username">SQL username, Entra UPN, or SP client id (depending on mode).</param>
        /// <param name="password">SQL password or SP client secret (depending on mode). Never logged.</param>
        /// <param name="azureClientId">SP client id fallback when <paramref name="username"/> is null.</param>
        /// <param name="managedIdentityClientId">User-assigned MI client id; blank = system-assigned.</param>
        internal static void ApplyAuthentication(
            SqlConnectionStringBuilder builder,
            string authenticationType,
            string? username,
            string? password,
            string? azureClientId,
            string? managedIdentityClientId)
        {
            if (authenticationType == AuthenticationTypes.Windows)
            {
                builder.IntegratedSecurity = true;
            }
            else if (authenticationType == AuthenticationTypes.SqlServer)
            {
                builder.IntegratedSecurity = false;
                builder.UserID = username ?? string.Empty;
                builder.Password = password ?? string.Empty;
            }
            else if (authenticationType == AuthenticationTypes.EntraMFA)
            {
                // Microsoft Entra MFA (Azure AD Interactive)
                builder.IntegratedSecurity = false;
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                // Optionally set UserID (email/UPN)
                if (!string.IsNullOrWhiteSpace(username))
                {
                    builder.UserID = username;
                }
            }
            else if (authenticationType == AuthenticationTypes.ServicePrincipal)
            {
                // Microsoft Entra service principal (non-interactive): client id + secret.
                builder.IntegratedSecurity = false;
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryServicePrincipal;
                builder.UserID = username ?? azureClientId ?? string.Empty;   // client/app id
                builder.Password = password ?? string.Empty;                  // client secret (from Credential Manager)
            }
            else if (authenticationType == AuthenticationTypes.ManagedIdentity)
            {
                // Azure managed identity (non-interactive): no secret.
                builder.IntegratedSecurity = false;
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryManagedIdentity;
                if (!string.IsNullOrWhiteSpace(managedIdentityClientId))
                {
                    builder.UserID = managedIdentityClientId;   // user-assigned MI; omit for system-assigned
                }
            }
        }

        /// <summary>
        /// Indicates whether credentials are stored in Windows Credential Manager for this server.
        /// Used to validate that SQL auth servers have credentials available.
        /// </summary>
        /// <param name="credentialService">The credential service to use for checking credentials</param>
        /// <returns>True for zero-touch modes (Windows, MFA, Managed Identity), or if a credential exists in credential manager</returns>
        public bool HasStoredCredentials(ICredentialService credentialService)
        {
            // Zero-touch auth modes need no stored secret.
            if (AuthenticationType == AuthenticationTypes.Windows ||
                AuthenticationType == AuthenticationTypes.EntraMFA ||
                AuthenticationType == AuthenticationTypes.ManagedIdentity)
            {
                return true;
            }

            // SqlServer (password) and ServicePrincipal (client secret) require a stored credential.
            return credentialService.CredentialExists(Id);
        }
    }
}
