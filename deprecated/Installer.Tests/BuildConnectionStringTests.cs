/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using Installer.Core;
using Microsoft.Data.SqlClient;

namespace Installer.Tests;

/// <summary>
/// Unit tests for InstallationService.BuildConnectionString — the auth-mode mapping shared by the
/// CLI installer flags (Windows / SQL / --entra / --managed-identity / --service-principal, #1325).
/// Assertions round-trip the produced string through SqlConnectionStringBuilder so they check the
/// typed Authentication/UserID/Password/IntegratedSecurity properties rather than brittle text.
/// These run without a database.
/// </summary>
public class BuildConnectionStringTests
{
    private static SqlConnectionStringBuilder Parse(string connectionString) => new(connectionString);

    [Fact]
    public void WindowsAuth_UsesIntegratedSecurity_NoUserId()
    {
        var b = Parse(InstallationService.BuildConnectionString("SQL2022", useWindowsAuth: true));

        Assert.True(b.IntegratedSecurity);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
        Assert.True(string.IsNullOrEmpty(b.UserID));
        Assert.Equal("master", b.InitialCatalog);
    }

    [Fact]
    public void SqlAuth_SetsUserIdAndPassword_NoEntraAuth()
    {
        var b = Parse(InstallationService.BuildConnectionString(
            "SQL2022", useWindowsAuth: false, username: "sa", password: "p@ss!"));

        Assert.False(b.IntegratedSecurity);
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
        Assert.Equal("sa", b.UserID);
        Assert.Equal("p@ss!", b.Password);
    }

    [Fact]
    public void EntraInteractive_SetsInteractiveAuth_WithUpn()
    {
        var b = Parse(InstallationService.BuildConnectionString(
            "SQL2022", useWindowsAuth: false, username: "me@corp.com", useEntraAuth: true));

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive, b.Authentication);
        Assert.Equal("me@corp.com", b.UserID);
    }

    [Fact]
    public void ServicePrincipal_SetsSpAuth_WithClientIdAndSecret()
    {
        var b = Parse(InstallationService.BuildConnectionString(
            "mi.database.windows.net", useWindowsAuth: false,
            username: "client-id", password: "the-secret", authenticationType: "ServicePrincipal"));

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, b.Authentication);
        Assert.Equal("client-id", b.UserID);
        Assert.Equal("the-secret", b.Password);
    }

    [Fact]
    public void ServicePrincipal_FallsBackToAzureClientId_WhenUsernameNull()
    {
        var b = Parse(InstallationService.BuildConnectionString(
            "mi.database.windows.net", useWindowsAuth: false,
            username: null, password: "the-secret",
            authenticationType: "ServicePrincipal", azureClientId: "app-id"));

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, b.Authentication);
        Assert.Equal("app-id", b.UserID);
    }

    [Fact]
    public void ManagedIdentity_SystemAssigned_OmitsUserId()
    {
        var b = Parse(InstallationService.BuildConnectionString(
            "mi.database.windows.net", useWindowsAuth: false, authenticationType: "ManagedIdentity"));

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity, b.Authentication);
        Assert.True(string.IsNullOrEmpty(b.UserID));
    }

    [Fact]
    public void ManagedIdentity_UserAssigned_SetsClientIdAsUserId()
    {
        var b = Parse(InstallationService.BuildConnectionString(
            "mi.database.windows.net", useWindowsAuth: false,
            authenticationType: "ManagedIdentity", managedIdentityClientId: "mi-client-id"));

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity, b.Authentication);
        Assert.Equal("mi-client-id", b.UserID);
    }

    [Fact]
    public void AuthenticationType_TakesPrecedence_OverWindowsAndEntraInteractiveFlags()
    {
        /* An explicit non-interactive Entra type must win even if the older
           useWindowsAuth / useEntraAuth flags are also set by the caller. */
        var b = Parse(InstallationService.BuildConnectionString(
            "mi.database.windows.net", useWindowsAuth: true,
            username: "client-id", password: "the-secret",
            useEntraAuth: true, authenticationType: "ServicePrincipal"));

        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, b.Authentication);
        Assert.False(b.IntegratedSecurity);
    }

    [Theory]
    [InlineData("Optional")]
    [InlineData("Mandatory")]
    [InlineData("Strict")]
    public void Encryption_MapsToBuilderOption(string level)
    {
        var b = Parse(InstallationService.BuildConnectionString(
            "SQL2022", useWindowsAuth: true, encryption: level));

        var expected = level switch
        {
            "Optional" => SqlConnectionEncryptOption.Optional,
            "Strict" => SqlConnectionEncryptOption.Strict,
            _ => SqlConnectionEncryptOption.Mandatory
        };
        Assert.Equal(expected, b.Encrypt);
    }
}
