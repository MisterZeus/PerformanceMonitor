/*
 * Performance Monitor Dashboard
 * Copyright (c) 2026 Darling Data, LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using PerformanceMonitor.Common;
using PerformanceMonitorDashboard.Interfaces;
using PerformanceMonitorDashboard.Models;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Connection-string SHAPE tests for the non-interactive Azure auth modes: service principal and
/// managed identity. Mirrors Lite.Tests/AzureAuthConnectionStringTests so the two apps' SP/MI keyword
/// mapping can never silently drift apart. The testable surface is the keywords emitted by
/// ServerConnection.ApplyAuthentication (shared by the production GetConnectionString builder and the
/// Add/Edit dialog's Test-Connection builder). Real Azure auth (token acquisition) is a maintainer
/// E2E step and is NOT exercised here.
/// </summary>
public class AzureAuthConnectionStringTests
{
    /// <summary>In-memory ICredentialService so the credential-fetch paths can be exercised without
    /// touching the real Windows Credential Manager.</summary>
    private sealed class FakeCredentialService : ICredentialService
    {
        private readonly Dictionary<string, (string Username, string Password)> _store = new();

        public bool SaveCredential(string serverId, string username, string password)
        {
            _store[serverId] = (username, password);
            return true;
        }

        public (string Username, string Password)? GetCredential(string serverId)
            => _store.TryGetValue(serverId, out var c) ? c : null;

        public bool DeleteCredential(string serverId)
        {
            _store.Remove(serverId);
            return true;
        }

        public bool CredentialExists(string serverId) => _store.ContainsKey(serverId);

        public bool UpdateCredential(string serverId, string username, string password)
        {
            _store[serverId] = (username, password);
            return true;
        }
    }

    // ---- ApplyAuthentication keyword mapping ----------------------------------------------
    // Tests the shared helper directly (internal, visible via InternalsVisibleTo=Dashboard.Tests).

    private static (SqlAuthenticationMethod Auth, string? User, string? Pwd, bool Integrated) AuthShape(
        string authType, string? user, string? pwd, string? azureClientId, string? miClientId)
    {
        var b = new SqlConnectionStringBuilder();
        ServerConnection.ApplyAuthentication(b, authType, user, pwd, azureClientId, miClientId);
        return (b.Authentication, b.UserID, b.Password, b.IntegratedSecurity);
    }

    [Fact]
    public void ServicePrincipal_SetsActiveDirectoryServicePrincipal_WithClientIdAndSecret()
    {
        var shape = AuthShape(AuthenticationTypes.ServicePrincipal, "client-id-123", "the-secret",
            azureClientId: null, miClientId: null);

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, shape.Auth);
        Assert.Equal("client-id-123", shape.User);
        Assert.Equal("the-secret", shape.Pwd);
        Assert.False(shape.Integrated);
    }

    [Fact]
    public void ServicePrincipal_FallsBackToAzureClientId_WhenUsernameNull()
    {
        var shape = AuthShape(AuthenticationTypes.ServicePrincipal, null, "secret",
            azureClientId: "model-client-id", miClientId: null);

        Assert.Equal("model-client-id", shape.User);
        Assert.Equal("secret", shape.Pwd);
    }

    [Fact]
    public void ManagedIdentity_SystemAssigned_NoUserIdNoPassword()
    {
        var shape = AuthShape(AuthenticationTypes.ManagedIdentity, null, null,
            azureClientId: null, miClientId: null);

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity, shape.Auth);
        Assert.True(string.IsNullOrEmpty(shape.User));
        Assert.True(string.IsNullOrEmpty(shape.Pwd));
        Assert.False(shape.Integrated);
    }

    [Fact]
    public void ManagedIdentity_UserAssigned_SetsUserIdToClientId()
    {
        var shape = AuthShape(AuthenticationTypes.ManagedIdentity, null, null,
            azureClientId: null, miClientId: "uami-client-id");

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity, shape.Auth);
        Assert.Equal("uami-client-id", shape.User);
        Assert.True(string.IsNullOrEmpty(shape.Pwd));
    }

    // ---- Regression: existing modes unchanged ---------------------------------------------

    [Fact]
    public void Windows_UsesIntegratedSecurity_NoAuthenticationKeyword()
    {
        var shape = AuthShape(AuthenticationTypes.Windows, null, null, null, null);

        Assert.True(shape.Integrated);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, shape.Auth);
    }

    [Fact]
    public void SqlServer_SetsUserIdAndPassword_NoAuthenticationKeyword()
    {
        var shape = AuthShape(AuthenticationTypes.SqlServer, "sa", "pw", null, null);

        Assert.False(shape.Integrated);
        Assert.Equal("sa", shape.User);
        Assert.Equal("pw", shape.Pwd);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, shape.Auth);
    }

    [Fact]
    public void EntraMfa_SetsActiveDirectoryInteractive()
    {
        var shape = AuthShape(AuthenticationTypes.EntraMFA, "user@tenant.com", null, null, null);

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive, shape.Auth);
        Assert.Equal("user@tenant.com", shape.User);
    }

    // ---- AuthenticationDisplay switch (label parity with Lite) ----------------------------

    [Theory]
    [InlineData(AuthenticationTypes.ServicePrincipal, "Azure — Service Principal")]
    [InlineData(AuthenticationTypes.ManagedIdentity, "Azure — Managed Identity")]
    [InlineData(AuthenticationTypes.EntraMFA, "Microsoft Entra MFA")]
    [InlineData(AuthenticationTypes.SqlServer, "SQL Server")]
    [InlineData(AuthenticationTypes.Windows, "Windows")]
    public void AuthenticationDisplay_MapsEachMode(string authType, string expected)
    {
        var server = new ServerConnection { AuthenticationType = authType };
        Assert.Equal(expected, server.AuthenticationDisplay);
    }

    // ---- Full GetConnectionString path (unified builder + credential resolution) ----------

    private static SqlConnectionStringBuilder Build(ServerConnection server, ICredentialService cs)
        => new(server.GetConnectionString(cs));

    [Fact]
    public void GetConnectionString_ServicePrincipal_FetchesStoredSecret()
    {
        var cs = new FakeCredentialService();
        var server = new ServerConnection { ServerName = "s", AuthenticationType = AuthenticationTypes.ServicePrincipal };
        cs.SaveCredential(server.Id, "stored-client-id", "stored-secret");

        var conn = Build(server, cs);

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, conn.Authentication);
        Assert.Equal("stored-client-id", conn.UserID);
        Assert.Equal("stored-secret", conn.Password);
        // Dashboard always targets the PerformanceMonitor database.
        Assert.Equal("PerformanceMonitor", conn.InitialCatalog);
    }

    [Fact]
    public void GetConnectionString_ServicePrincipal_FallsBackToAzureClientId_WhenNoStoredUsername()
    {
        // No stored credential — the model's AzureClientId supplies the UserID.
        var cs = new FakeCredentialService();
        var server = new ServerConnection
        {
            ServerName = "s",
            AuthenticationType = AuthenticationTypes.ServicePrincipal,
            AzureClientId = "model-client-id"
        };

        var conn = Build(server, cs);

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, conn.Authentication);
        Assert.Equal("model-client-id", conn.UserID);
    }

    [Fact]
    public void GetConnectionString_ManagedIdentity_FetchesNoSecret()
    {
        var cs = new FakeCredentialService();
        var server = new ServerConnection { ServerName = "s", AuthenticationType = AuthenticationTypes.ManagedIdentity };
        // Even if a stale credential somehow existed, MI must not pull it into the string.
        cs.SaveCredential(server.Id, "leftover", "leftover-secret");

        var conn = Build(server, cs);

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity, conn.Authentication);
        Assert.True(string.IsNullOrEmpty(conn.Password));
        Assert.NotEqual("leftover", conn.UserID);
    }

    [Fact]
    public void GetConnectionString_ManagedIdentity_UserAssigned_SetsUserId()
    {
        var cs = new FakeCredentialService();
        var server = new ServerConnection
        {
            ServerName = "s",
            AuthenticationType = AuthenticationTypes.ManagedIdentity,
            ManagedIdentityClientId = "uami-client-id"
        };

        var conn = Build(server, cs);

        Assert.Equal("uami-client-id", conn.UserID);
        Assert.True(string.IsNullOrEmpty(conn.Password));
    }

    [Fact]
    public void GetConnectionString_Windows_UsesIntegratedSecurity()
    {
        var cs = new FakeCredentialService();
        var server = new ServerConnection { ServerName = "s", AuthenticationType = AuthenticationTypes.Windows };

        var conn = Build(server, cs);

        Assert.True(conn.IntegratedSecurity);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, conn.Authentication);
    }

    [Fact]
    public void GetConnectionString_SqlServer_FetchesStoredUsernameAndPassword()
    {
        var cs = new FakeCredentialService();
        var server = new ServerConnection { ServerName = "s", AuthenticationType = AuthenticationTypes.SqlServer };
        cs.SaveCredential(server.Id, "sa", "pw");

        var conn = Build(server, cs);

        Assert.False(conn.IntegratedSecurity);
        Assert.Equal("sa", conn.UserID);
        Assert.Equal("pw", conn.Password);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, conn.Authentication);
    }

    [Fact]
    public void GetConnectionString_EntraMfa_SetsInteractive_AndUsernameHint()
    {
        var cs = new FakeCredentialService();
        var server = new ServerConnection { ServerName = "s", AuthenticationType = AuthenticationTypes.EntraMFA };
        cs.SaveCredential(server.Id, "user@tenant.com", string.Empty);

        var conn = Build(server, cs);

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive, conn.Authentication);
        Assert.Equal("user@tenant.com", conn.UserID);
    }

    // ---- HasStoredCredentials --------------------------------------------------------------

    [Fact]
    public void HasStoredCredentials_ManagedIdentity_IsTrue_WithoutCredential()
    {
        var cs = new FakeCredentialService();
        var server = new ServerConnection { AuthenticationType = AuthenticationTypes.ManagedIdentity };

        Assert.True(server.HasStoredCredentials(cs));
    }

    [Fact]
    public void HasStoredCredentials_ServicePrincipal_ReflectsCredentialStore()
    {
        var cs = new FakeCredentialService();
        var server = new ServerConnection { AuthenticationType = AuthenticationTypes.ServicePrincipal };

        Assert.False(server.HasStoredCredentials(cs));

        cs.SaveCredential(server.Id, "client", "secret");
        Assert.True(server.HasStoredCredentials(cs));
    }

    // ---- Two-build-site PARITY ------------------------------------------------------------
    // The dialog's Test-Connection builder and ServerConnection's production builder both route
    // their auth keywords through the shared ServerConnection.ApplyAuthentication helper, so they
    // emit identical Authentication / UserID / Password / IntegratedSecurity for each mode.

    [Theory]
    [InlineData(AuthenticationTypes.Windows)]
    [InlineData(AuthenticationTypes.SqlServer)]
    [InlineData(AuthenticationTypes.EntraMFA)]
    [InlineData(AuthenticationTypes.ServicePrincipal)]
    [InlineData(AuthenticationTypes.ManagedIdentity)]
    public void BuildSites_ProduceIdenticalAuthShape_PerMode(string authType)
    {
        string? user = authType switch
        {
            AuthenticationTypes.SqlServer => "sa",
            AuthenticationTypes.EntraMFA => "user@tenant.com",
            AuthenticationTypes.ServicePrincipal => "client-id",
            _ => null
        };
        string? pwd = authType is AuthenticationTypes.SqlServer or AuthenticationTypes.ServicePrincipal ? "secret" : null;
        string? mi = authType == AuthenticationTypes.ManagedIdentity ? "uami-id" : null;

        // Production builder passes the model's AzureClientId; the dialog builder passes null (it
        // surfaces the SP client id as the username). Both must reduce to the same auth shape.
        var production = AuthShape(authType, user, pwd, azureClientId: "client-id", miClientId: mi);
        var dialog = AuthShape(authType, user, pwd, azureClientId: null, miClientId: mi);

        Assert.Equal(production, dialog);
    }
}
