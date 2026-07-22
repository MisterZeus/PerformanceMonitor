/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Installer.Core;
using Installer.Core.Models;
using Microsoft.Data.SqlClient;
// The state machine and its pure predicates live in Installer.Core (ServerSetupState / ServerSetupPolicy)
// so CI can test them; the dialog is not CI-wired. Aliased so the flow reads as "DialogState" in situ.
using DialogState = Installer.Core.ServerSetupState;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;
using PerformanceMonitor.Common;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitorDashboard
{
    public partial class AddServerDialog : Window
    {
        /*
        Non-null when installing would be unsafe, and why. Four causes, all of which would otherwise act
        on a database whose real state we do not know:
          - discovery threw (a transient error is indistinguishable from "no database"),
          - the installed version is NEWER than this Dashboard (installing would downgrade it),
          - the installed version will not parse at all,
          - THIS BUILD's own version will not parse (a fresh install would write it to the ledger).
        Guards the install path structurally rather than relying on the button being hidden.
        */
        private string? _installBlockedReason;

        /*
        The block's REAL reason, as it applies to the stamped server -- kept so a retarget can substitute the
        generic notice and a retarget BACK can put the truth in again. Without it, typing a character into
        the server box and deleting it replaced "SQL Server 2014 is not supported" with "the server name
        changed" permanently, and only Test Connection could ever get the real answer back.
        */
        private string? _serverBlockReason;

        /*
        Said in one voice from every place that can withdraw something: the demotion in TransitionToState,
        the post-run claims, and a real block whose server is no longer in the box. It was three
        hand-copied strings that had already drifted apart.
        */
        private const string StaleServerNotice =
            "The server name changed after this server's status was checked, so what is on screen may " +
            "describe a different server.\n\nClick Test Connection to check the server now in the box.";

        public ServerConnection ServerConnection { get; private set; }
        public string? Username { get; private set; }
        public string? Password { get; private set; }
        private bool _isEditMode;

        private CancellationTokenSource? _installCts;
        private Installer.Core.Models.ServerInfo? _coreServerInfo;
        private string? _installedVersion;
        private InstallationResult? _installResult;
        private string? _reportPath;
        private DialogState _currentState = DialogState.Initial;
        private string? _serverVersion;

        /*
        Cancel the install on ANY close. Cancel_Click covers the Cancel button and ESC (IsCancel), but the
        title-bar X and Alt+F4 bypass it entirely -- without this the install keeps running against the
        database, history write included, with the dialog already destroyed and nothing left to stop it.
        */
        private void CancelInstallOnClose(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _dialogClosed = true;
            _installCts?.Cancel();
        }

        /*
        Setting DialogResult on a window that has already closed throws. A save in flight is the way in:
        press ESC (or Cancel) while its connection test is running -- which is a plain 10-second wait, and
        unbounded for Entra MFA -- and the close lands first, then the test completes and SaveAsync walks on
        into DialogResult = true. The re-entrancy flag does not cover this: it is one Save, not two.
        */
        private bool _dialogClosed;

        public AddServerDialog()
        {
            InitializeComponent();
            SizeToWorkArea();
            Closing += CancelInstallOnClose;
            _isEditMode = false;
            ServerConnection = new ServerConnection();
            Title = "Add SQL Server";
        }

        public AddServerDialog(ServerConnection existingServer)
        {
            InitializeComponent();
            SizeToWorkArea();
            Closing += CancelInstallOnClose;
            _isEditMode = true;
            ServerConnection = existingServer;
            Title = "Edit SQL Server";

            DisplayNameTextBox.Text = existingServer.DisplayName;
            ServerNameTextBox.Text = existingServer.ServerName;
            DescriptionTextBox.Text = existingServer.Description;
            IsFavoriteCheckBox.IsChecked = existingServer.IsFavorite;
            MonthlyCostTextBox.Text = existingServer.MonthlyCostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Load encryption settings
            EncryptModeComboBox.SelectedIndex = existingServer.EncryptMode switch
            {
                "Mandatory" => 1,
                "Strict" => 2,
                _ => 0 // Optional
            };
            TrustServerCertificateCheckBox.IsChecked = existingServer.TrustServerCertificate;
            ReadOnlyIntentCheckBox.IsChecked = existingServer.ReadOnlyIntent;
            MultiSubnetFailoverCheckBox.IsChecked = existingServer.MultiSubnetFailover;
            AlertDeliveryOverrideComboBox.SelectedIndex = existingServer.AlertDeliveryModeOverride switch
            {
                AlertNotificationMode.Summary => 1,
                AlertNotificationMode.PerEvent => 2,
                _ => 0
            };

            if (existingServer.AuthenticationType == AuthenticationTypes.EntraMFA)
            {
                EntraMfaAuthRadio.IsChecked = true;

                var credentialService = new CredentialService();
                var cred = credentialService.GetCredential(existingServer.Id);
                if (cred.HasValue && !string.IsNullOrEmpty(cred.Value.Username))
                {
                    EntraMfaUsernameBox.Text = cred.Value.Username;
                }
            }
            else if (existingServer.AuthenticationType == AuthenticationTypes.SqlServer)
            {
                SqlAuthRadio.IsChecked = true;

                var credentialService = new CredentialService();
                var cred = credentialService.GetCredential(existingServer.Id);
                if (cred.HasValue)
                {
                    UsernameTextBox.Text = cred.Value.Username;
                    PasswordBox.Password = cred.Value.Password;
                }
            }
            else if (existingServer.AuthenticationType == AuthenticationTypes.ServicePrincipal)
            {
                ServicePrincipalAuthRadio.IsChecked = true;
                ServicePrincipalClientIdBox.Text = existingServer.AzureClientId ?? string.Empty;

                // Pre-fill the client id and the client secret from Credential Manager, matching how SQL
                // Server auth pre-fills its password above (and how Lite pre-fills the SP secret). The
                // model's AzureClientId takes precedence for the client id, falling back to the stored
                // credential username. The user can leave the secret untouched to keep it, or overwrite to
                // rotate it — the save path re-persists whatever is in the box.
                var credentialService = new CredentialService();
                var cred = credentialService.GetCredential(existingServer.Id);
                if (cred.HasValue)
                {
                    if (string.IsNullOrEmpty(ServicePrincipalClientIdBox.Text) && !string.IsNullOrEmpty(cred.Value.Username))
                    {
                        ServicePrincipalClientIdBox.Text = cred.Value.Username;
                    }
                    ServicePrincipalSecretBox.Password = cred.Value.Password;
                }
            }
            else if (existingServer.AuthenticationType == AuthenticationTypes.ManagedIdentity)
            {
                ManagedIdentityAuthRadio.IsChecked = true;
                ManagedIdentityClientIdBox.Text = existingServer.ManagedIdentityClientId ?? string.Empty;
            }
            else
            {
                WindowsAuthRadio.IsChecked = true;
            }
        }

        private void SizeToWorkArea()
        {
            var workArea = SystemParameters.WorkArea;
            Height = workArea.Height;
            Top = workArea.Top;
            Left = workArea.Left + (workArea.Width - Width) / 2;
        }

        private void AuthType_Changed(object sender, RoutedEventArgs e)
        {
            if (SqlAuthPanel != null && EntraMfaPanel != null &&
                ServicePrincipalPanel != null && ManagedIdentityPanel != null)
            {
                SqlAuthPanel.Visibility = SqlAuthRadio.IsChecked == true
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

                EntraMfaPanel.Visibility = EntraMfaAuthRadio.IsChecked == true
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

                ServicePrincipalPanel.Visibility = ServicePrincipalAuthRadio.IsChecked == true
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

                ManagedIdentityPanel.Visibility = ManagedIdentityAuthRadio.IsChecked == true
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            }
        }

        private AlertNotificationMode? GetSelectedDeliveryOverride() => AlertDeliveryOverrideComboBox.SelectedIndex switch
        {
            1 => AlertNotificationMode.Summary,
            2 => AlertNotificationMode.PerEvent,
            _ => null
        };

        private string GetSelectedEncryptMode()
        {
            return EncryptModeComboBox.SelectedIndex switch
            {
                1 => "Mandatory",
                2 => "Strict",
                _ => "Optional"
            };
        }

        private static SqlConnectionEncryptOption ParseEncryptOption(string mode)
        {
            return mode switch
            {
                "Mandatory" => SqlConnectionEncryptOption.Mandatory,
                "Strict" => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Optional
            };
        }

        private SqlConnectionStringBuilder BuildConnectionBuilder()
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = ServerNameTextBox.Text.Trim(),
                InitialCatalog = "PerformanceMonitor",
                ApplicationName = "PerformanceMonitorDashboard",
                ConnectTimeout = 10,
                TrustServerCertificate = TrustServerCertificateCheckBox.IsChecked == true,
                Encrypt = ParseEncryptOption(GetSelectedEncryptMode()),
                ApplicationIntent = ReadOnlyIntentCheckBox.IsChecked == true
                    ? ApplicationIntent.ReadOnly
                    : ApplicationIntent.ReadWrite,
                MultiSubnetFailover = MultiSubnetFailoverCheckBox.IsChecked == true
            };

            // Resolve auth type + per-mode credentials from the live controls, then apply via the SHARED
            // helper so this Test-Connection builder can never diverge from ServerConnection's production
            // builder (the two-bodies trap). See ServerConnection.ApplyAuthentication.
            string authType;
            string? userId = null;
            string? secret = null;
            string? managedIdentityClientId = null;

            if (WindowsAuthRadio.IsChecked == true)
            {
                authType = AuthenticationTypes.Windows;
            }
            else if (SqlAuthRadio.IsChecked == true)
            {
                authType = AuthenticationTypes.SqlServer;
                userId = UsernameTextBox.Text.Trim();
                secret = PasswordBox.Password;
            }
            else if (EntraMfaAuthRadio.IsChecked == true)
            {
                authType = AuthenticationTypes.EntraMFA;
                userId = EntraMfaUsernameBox.Text.Trim();
            }
            else if (ServicePrincipalAuthRadio.IsChecked == true)
            {
                authType = AuthenticationTypes.ServicePrincipal;
                userId = ServicePrincipalClientIdBox.Text.Trim();
                secret = ServicePrincipalSecretBox.Password;
            }
            else if (ManagedIdentityAuthRadio.IsChecked == true)
            {
                authType = AuthenticationTypes.ManagedIdentity;
                managedIdentityClientId = ManagedIdentityClientIdBox.Text.Trim();
            }
            else
            {
                authType = AuthenticationTypes.Windows;
            }

            ServerConnection.ApplyAuthentication(builder, authType, userId, secret, azureClientId: null, managedIdentityClientId);

            return builder;
        }

        private string BuildInstallerConnectionString()
        {
            string server = ServerNameTextBox.Text.Trim();
            bool useWindowsAuth = WindowsAuthRadio.IsChecked == true;
            bool useEntraAuth = EntraMfaAuthRadio.IsChecked == true;
            string? username = null;
            string? password = null;
            string? authenticationType = null;
            string? azureClientId = null;
            string? managedIdentityClientId = null;

            if (SqlAuthRadio.IsChecked == true)
            {
                username = UsernameTextBox.Text.Trim();
                password = PasswordBox.Password;
            }
            else if (useEntraAuth)
            {
                username = EntraMfaUsernameBox.Text.Trim();
            }
            else if (ServicePrincipalAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.ServicePrincipal;
                azureClientId = ServicePrincipalClientIdBox.Text.Trim();
                username = azureClientId;
                password = ServicePrincipalSecretBox.Password;
            }
            else if (ManagedIdentityAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.ManagedIdentity;
                managedIdentityClientId = ManagedIdentityClientIdBox.Text.Trim();
            }

            return InstallationService.BuildConnectionString(
                server,
                useWindowsAuth,
                username,
                password,
                GetSelectedEncryptMode(),
                TrustServerCertificateCheckBox.IsChecked == true,
                useEntraAuth,
                authenticationType,
                azureClientId,
                managedIdentityClientId);
        }

        private static string GetAppVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(infoVersion))
            {
                /* Strip any +metadata suffix (e.g. "2.4.1+abc123" -> "2.4.1") */
                int plusIndex = infoVersion.IndexOf('+');
                return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
            }

            var version = assembly.GetName().Version;

            /*
            An assembly with no AssemblyVersion reports 0.0.0.0 -- which IS the unknown sentinel. Falling
            through to it would write a parseable "0.0.0" into a server's ledger, and every later read
            would then take that server's real version as "unknown" and replay every migration. So a
            version-less build must land on something UNPARSEABLE, which InstallGuard blocks
            (UnreadableBuildVersion). Checked here, not one branch later, because this branch is the one
            that can produce it. The CLI's Main has the identical guard, for the identical reason -- both
            surfaces write to the same ledger, so the hole had to be closed on both.
            */
            if (version != null && (version.Major | version.Minor | version.Build) != 0)
            {
                /* Normalize 4-part to 3-part: "2.4.1.0" -> "2.4.1" */
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }

            return "Unknown";
        }

        /// <summary>
        /// Normalize a version string to 3-part for comparison (e.g., "2.4.1.0" -> "2.4.1").
        /// </summary>
        /*
        Reduce a version to its three-part numeric core, stripping any SemVer build (+sha) or
        pre-release (-rc1) suffix. Without the strip, a "3.2.0-rc1" InformationalVersion fails to
        parse, and the install/upgrade routing below then treats EVERY server as up to date. The
        Build clamp matters for the same reason: TryParse("3.1") succeeds with Build == -1, which
        would otherwise format as "3.1.-1" and fail to re-parse. Matches ScriptProvider.ParseVersionCore.
        */
        private static string NormalizeVersion(string version) =>
            ScriptProvider.TryParseVersionCore(version)?.ToString() ?? version;

        private bool ValidateInputs()
        {
            if (string.IsNullOrWhiteSpace(ServerNameTextBox.Text))
            {
                MessageBox.Show(
                    this,
                    "Please enter a server name or address.",
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return false;
            }

            if (SqlAuthRadio.IsChecked == true && string.IsNullOrWhiteSpace(UsernameTextBox.Text))
            {
                MessageBox.Show(
                    this,
                    "Please enter a username for SQL Server authentication.",
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return false;
            }

            if (ServicePrincipalAuthRadio.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(ServicePrincipalClientIdBox.Text))
                {
                    MessageBox.Show(
                        this,
                        "Please enter the Application (Client) ID for service principal authentication.",
                        "Validation Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return false;
                }

                if (string.IsNullOrEmpty(ServicePrincipalSecretBox.Password))
                {
                    MessageBox.Show(
                        this,
                        "Please enter the client secret for service principal authentication.",
                        "Validation Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return false;
                }
            }

            return true;
        }

        private async System.Threading.Tasks.Task<(bool Connected, string? ErrorMessage, bool MfaCancelled)> RunConnectionTestAsync(Button triggerButton)
        {
            /*
            Disable BOTH entry points, not just the one that was clicked. Test Connection stayed live during
            a SAVE's test, so two of these could overlap -- and then the second one borrowed the FIRST one's
            "Testing connection..." label and dutifully restored it at the end, leaving that message on
            screen forever with nothing running behind it. (It is also what re-armed Save mid-save and gave
            the double-DialogResult its way in; _saveInProgress catches that, but two concurrent connection
            tests are nonsense in the first place.) One test at a time; the finally re-arms both.
            */
            triggerButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            TestConnectionButton.IsEnabled = false;
            CheckForUpdatesButton.IsEnabled = false;

            /*
            StatusText may already be carrying something the user still needs -- most importantly the
            "the server name changed, what is on screen may describe a different server" notice, which is
            the ONLY thing on screen after a retarget withdraws the post-run panels. Blanking it in the
            finally below left a dialog with no panels and no explanation whenever the test then failed.
            Borrow it, and give it back.

            Epoch-stamped, because the borrow spans the awaits and the give-back happens after them -- the
            same read-before / write-after shape this whole branch exists to kill, and giving it back
            unconditionally resurrected a notice that had already stopped being true. (Retype away, click
            Test Connection, retype BACK during the connect: the screen correctly restores itself, then the
            failed test's finally pastes the stale "server name changed" notice back over it.) If anything
            bumped the epoch while we were away, the label is no longer ours to restore.
            */
            int statusEpoch = _detectEpoch;
            string priorStatus = StatusText.Text;
            var priorStatusVisibility = StatusText.Visibility;

            StatusText.Text = EntraMfaAuthRadio.IsChecked == true
                ? "Testing connection — please complete authentication in the popup window..."
                : "Testing connection...";
            StatusText.Visibility = System.Windows.Visibility.Visible;

            bool connected = false;
            string? errorMessage = null;
            bool mfaCancelled = false;
            try
            {
                /* Connect to master (not PerformanceMonitor) so the test succeeds
                   even when the database doesn't exist yet — installation detection
                   happens after the connection test in DetectDatabaseStatusAsync() */
                var builder = BuildConnectionBuilder();
                builder.InitialCatalog = "master";
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();

                /*
                Kept as a round-trip liveness check -- Open() alone can be satisfied from the pool -- but the
                version it returns is deliberately DISCARDED. It used to be handed back and assigned straight
                to _serverVersion, which is one of the four facts that may only be committed together, by
                PublishProbedServer. DetectDatabaseStatusAsync re-probes and publishes them as a set.
                */
                using var cmd = new SqlCommand("SELECT @@VERSION", connection);
                _ = await cmd.ExecuteScalarAsync();

                connected = true;
            }
            catch (Exception ex)
            {
                connected = false;
                errorMessage = ex.Message;
                if (EntraMfaAuthRadio.IsChecked == true && MfaAuthenticationHelper.IsMfaCancelledException(ex))
                    mfaCancelled = true;
            }
            finally
            {
                /* Do not re-arm over a running install: SetFormEnabled(false) disabled these deliberately. */
                if (!InstallInProgress)
                {
                    triggerButton.IsEnabled = true;
                    SaveButton.IsEnabled = true;
                    TestConnectionButton.IsEnabled = true;
                    CheckForUpdatesButton.IsEnabled = true;

                    /*
                    Give back what we borrowed -- but only if it is still ours. A retype or a newer detection
                    during the await means the label has moved on, and restoring it would be pasting a stale
                    notice over a fresher one. On success the detection repaints it anyway.
                    */
                    if (statusEpoch == _detectEpoch)
                    {
                        StatusText.Text = priorStatus;
                        StatusText.Visibility = priorStatusVisibility;
                    }
                }
            }

            return (connected, errorMessage, mfaCancelled);
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            CheckForUpdatesButton.IsEnabled = false;
            CheckForUpdatesButton.Content = "Checking...";

            try
            {
                await DetectDatabaseStatusAsync();
            }
            finally
            {
                CheckForUpdatesButton.Content = "Check for Updates";
                /* Do not re-arm over a running install: SetFormEnabled(false) disabled this deliberately. */
                if (!InstallInProgress)
                {
                    CheckForUpdatesButton.IsEnabled = true;
                }
            }
        }

        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInputs()) return;

            /*
            The server this test is actually about, captured with the connection string built from it. The
            box is editable for the whole ~10s test (unbounded on Entra MFA), and the failure message below
            used to re-read it AFTER the await -- so retyping during the connect produced "Could not connect
            to A" about a server that is fine, for a failure that belonged to B. The last read-the-box-after-
            the-await in this file.
            */
            string testedServerName = ServerNameTextBox.Text.Trim();

            var (connected, errorMessage, mfaCancelled) = await RunConnectionTestAsync(TestConnectionButton);

            /*
            The dialog was closed while this test ran. Round 19 swept exactly this out of SaveAsync and left
            the same shape sitting in its two siblings: without it, the failure and MFA-cancel boxes below
            pop up with no dialog behind them -- one of them helpfully telling the user to "Click Test to try
            again" on a window that no longer exists -- and the success path runs a full two-round-trip
            detection against a destroyed one.
            */
            if (_dialogClosed) return;

            if (connected)
            {
                /*
                Deliberately does NOT write _serverVersion. It is one of the four facts that may only ever be
                committed together, by PublishProbedServer -- and this write was the last place that set one
                of them on its own, post-await, with no stamp beside it. The box is editable during that
                await, so it could stamp the PREVIOUS server's engine version into place while the verdict
                and the state still described someone else. DetectDatabaseStatusAsync re-probes and publishes
                them as a set, which is where it belongs.
                */
                await DetectDatabaseStatusAsync();
            }
            else if (mfaCancelled)
            {
                MessageBox.Show(
                    this,
                    "Authentication was cancelled. Click Test to try again.",
                    "Authentication Cancelled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }
            else
            {
                var detail = errorMessage != null ? $"\n\nError: {errorMessage}" : string.Empty;
                MessageBox.Show(
                    this,
                    $"Could not connect to {testedServerName}.{detail}\n\nPlease check:\n" +
                    "• Server name/address is correct\n" +
                    "• Server is accessible from this machine\n" +
                    "• Firewall allows SQL Server connections\n" +
                    "• SQL Server service is running\n" +
                    "• You have the 'PerformanceMonitor' database and access to it",
                    "Connection Test Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        /*
        True once an install has started. Detection runs are async and can still be in flight when the
        user starts an install, and their continuation would otherwise transition back to a Connected_*
        state -- un-collapsing the panel over a RUNNING install, re-showing the install button, and
        re-enabling Advanced Options. From there a second click starts a concurrent install (and a
        newly-reachable "Clean install" tick drops the database out from under the first one).
        Disabling the buttons is not enough: the detection paths re-enable them in their finally blocks.
        */
        private bool InstallInProgress => ServerSetupPolicy.IsInstalling(_currentState);

        /*
        Two detections can be in flight at once (Test Connection re-arms its button in a finally before
        the detection it triggered has finished, and Check for Updates disables only its own). They then
        race to write _installedVersion / _coreServerInfo / _currentState, and a late-landing one renders
        the FIRST server's verdict under the SECOND server's name. Each run takes an epoch; a superseded
        continuation discards itself instead of publishing a verdict nobody asked for.
        */
        private int _detectEpoch;

        /*
        TWO different things, deliberately two fields. Conflating them is what let a button reading
        "Upgrade Now" perform a full fresh install on a retargeted bare server:

          _launchState     -- where the click came FROM, i.e. what the button PROMISED. Fixed at click
                              time and never re-derived. Only meaningful for "did the user ask us to act
                              on an existing installation?".
          _preInstallState -- where a failure should RESTORE to, i.e. what the FACTS say. Always a
                              Connected_* state, and refreshed from the install-time re-read, because the
                              server box stays editable and the facts may belong to a different server.

        One field served both, was consumed for the promise BEFORE being re-derived for the restore, and
        so answered the promise question with a value that could be InstallComplete -- exactly the state
        the repair handoff puts a live "Upgrade Now" button in.
        */
        private DialogState _launchState = DialogState.Initial;

        /* Null until THIS server's version has actually been read. See RestoreAfterInstall. */
        private DialogState? _preInstallState;

        /*
        Which of the two reasons nulled _preInstallState: a clean install throwing the verdict away on
        purpose, or the run ending before the version was ever read. They get different words, and reset
        per run -- a flag that outlives its run is how the repair handoff ended up describing an upgrade
        that had already been applied.
        */
        private bool _verdictDiscardedByCleanInstall;

        /*
        THE structural guard. Every critical found in rounds 6-10 was one bug wearing different clothes:
        the server box stays editable, so _installedVersion / _coreServerInfo / _currentState can all
        describe a server that is no longer the one in the box -- and some consumer renders or acts on
        them anyway. Nine rounds of patching each consumer in turn left the next one uncovered.

        So the verdict is stamped with the server it was read FROM, and TransitionToState refuses to
        render a Connected_* verdict whose stamp no longer matches the box. One check, one place,
        structurally uncheatable: a stale verdict cannot be displayed, whoever forgets to re-check.
        */
        private string? _verdictServerName;

        private void RecordVerdictFor(string serverName) => _verdictServerName = serverName.Trim();

        /*
        The probed server's three facts are committed TOGETHER, and only where a verdict about it is
        actually published -- never in the prologue, and never before a guard that can still abandon the
        publish.

        They have to move as one because the stamp is what every consumer tests before rendering the other
        two. Hoisting the stamp to the top of the probe (so that a BLOCK would be attributed) looked
        harmless -- "re-stamped on the success path with the same value" -- but it is only re-stamped if
        that path is REACHED. A detection superseded at the version read returned having already moved the
        stamp and the engine version to the new server, while _installedVersion and _currentState still
        described the old one. VerdictMatchesServerBox() then answered TRUE, so the guard did not merely
        miss the lie, it AFFIRMED it: "PerformanceMonitor v3.1.0 is up to date", with Save enabled, over a
        server that had no PerformanceMonitor database at all.

        A half-replaced set of facts is the one thing the guard cannot catch. Stale-but-consistent it
        catches every time.

        _installedVersion is in here for that reason and no other. It is the FOURTH fact, and leaving it
        behind at the two block sites -- where we either never read it (unsupported version) or could not
        (the read threw) -- left them describing the PREVIOUS server. That is sealed today only because a
        block lands in Connected_StatusUnknown, which happens to collapse both buttons: a rendering
        accident standing in for an invariant, which is the exact thing this file has been bitten by over
        and over. A block passes null, because null is the truth there: we do not know.
        */
        private void PublishProbedServer(
            ServerInfo info,
            string serverVersion,
            string serverName,
            string? installedVersion)
        {
            _coreServerInfo = info;
            _serverVersion = serverVersion;
            _installedVersion = installedVersion;
            RecordVerdictFor(serverName);
        }

        /*
        The previous run's install log is discarded WITH the verdict that replaces it, never ahead of it.

        Clearing it in the detection's prologue looked equivalent and was not: a probe that publishes NOTHING
        -- superseded, or the not-connected branch that deliberately refuses to move the four facts -- left
        the screen still in InstallComplete with the panel visible, now showing a blank log, an empty status
        line and a 0% bar. On a FAILED install that destroys the only on-screen copy of the SQL error text,
        and nothing repopulates it: RenderPostRunClaims only shows and hides the panel.

        Same rule as _installBlockedReason, for the same reason: state that describes a verdict is committed
        with the verdict. Called only from the detection -- the install path clears the log itself, at the
        top of the run, and must not have it wiped from under the run it is recording.
        */
        private void DiscardPreviousRunLog()
        {
            InstallLogTextBox.Clear();
            InstallStatusText.Text = string.Empty;
            InstallProgressBar.Value = 0;
        }

        /*
        The repair handoff's version claim, kept so the handoff can be RE-RENDERED rather than painted
        once. Painting it once made withdrawing it one-way: a retarget correctly hid the claim and the
        "Upgrade Now" button, but typing the server name back -- or typing one character and deleting it --
        never brought them back, so the only button that applies the pending upgrade was gone for good
        while the install log still said to click it. Repair writes no history row, so the upgrade was
        still offered on the next dialog open; nothing on screen said so.
        */
        private string? _handoffStatusText;

        /*
        The verdict state a stale-name demotion came FROM, so retyping the name back can undo it. Set only
        by that demotion and cleared by every other transition, so a REAL block -- unsupported SQL version,
        unreadable version, database newer than the binary -- can never be typed away.
        */
        private DialogState? _demotedFromState;

        /*
        Idempotent, and safe to call on every keystroke. No-ops unless a repair actually left an upgrade
        pending -- for an ordinary install, upgrade or reinstall there is no handoff to render. The reason
        the panels went away is stated once, by RenderPostRunClaims; this just shows or hides them.
        MonitoringCredsPanel is a separate grid row and is deliberately untouched: the SQL-auth user still
        needs it either way.
        */
        private void RenderRepairHandoff(bool addressed)
        {
            if (_handoffStatusText == null) return;

            if (!addressed)
            {
                DatabaseStatusPanel.Visibility = Visibility.Collapsed;
                InstallUpgradeButton.Visibility = Visibility.Collapsed;
                return;
            }

            ConnectionInfoText.Text = $"Connected to {_verdictServerName}";
            DatabaseStatusText.Text = _handoffStatusText;
            DatabaseStatusPanel.Visibility = Visibility.Visible;
            InstallUpgradeButton.Content = "Upgrade Now";
            InstallUpgradeButton.Visibility = Visibility.Visible;

            /*
            That Border also holds the "Skip, just add server" link, which would otherwise become clickable
            for the first time in MonitoringCredentials -- an abandon-the-upgrade action sitting inches from
            the Upgrade Now button we just put there.
            */
            SkipInstallText.Visibility = Visibility.Collapsed;
        }

        /*
        Editing the server name invalidates everything on screen: the verdict, the connection info, and any
        detection still in flight all describe the server that WAS in the box. Bumping the epoch makes an
        in-flight detection discard itself instead of publishing a verdict for the old server under the new
        name, and re-entering the current state lets the stamp guard demote a now-stale verdict to
        "re-check me". Without this the box could be retyped mid-detect and the guard would never notice.
        */
        private void ServerNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (InstallInProgress) return;

            /*
            Any detection in flight was probing the PREVIOUS name -- discard it. It painted "Checking
            database status..." before it awaited, and its Superseded() early-outs return without clearing
            that, so the label would sit there indefinitely announcing a check nobody is running any more.
            */
            _detectEpoch++;
            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;

            /*
            The completion states are not verdict states, so the guard below never sees them -- but they
            assert plenty about a specific server: "Installation completed successfully!", the install log,
            and (after a repair) a live "Upgrade Now" with a version claim. All of it describes the server
            the run went to. Withdraw it when the box no longer names that server, and RESTORE it when the
            box names it again: typing one character and deleting it must not permanently destroy the only
            button that applies a pending upgrade, nor permanently hide the result of an install that did
            happen.
            */
            if (_currentState is DialogState.InstallComplete or DialogState.MonitoringCredentials)
            {
                RenderPostRunClaims();
                return;
            }

            if (_currentState == DialogState.Connected_StatusUnknown)
            {
                /*
                Connected_StatusUnknown is where a demotion LANDS, so the guard in TransitionToState -- which
                only demotes -- can never cover it. It is also where a real block sits, and a real block is a
                fact about a specific server: "SQL Server 2014 is not supported" is not true of the instance
                now in the box, and we have not looked at it.
                */
                if (VerdictMatchesServerBox())
                {
                    /*
                    Undo a stale-name demotion when the name comes back. The verdict was never WRONG, only
                    mis-addressed. _demotedFromState is set ONLY by that demotion, so a real block -- an
                    unsupported version, an unreadable version, a database newer than the binary -- cannot be
                    typed away here.
                    */
                    if (_demotedFromState != null)
                    {
                        DialogState restored = _demotedFromState.Value;
                        _installBlockedReason = null;
                        _serverBlockReason = null;
                        TransitionToState(restored);
                        return;
                    }

                    /*
                    A real block, about the server now back in the box. Put its ACTUAL reason back. A retype
                    and a retype-back had swapped it for the generic notice below and never swapped it
                    return -- so "SQL Server 2014 is not supported" became "the server name changed", which by
                    then was not even true, and only Test Connection could get the real answer back.
                    */
                    if (_serverBlockReason != null && _installBlockedReason != _serverBlockReason)
                    {
                        _installBlockedReason = _serverBlockReason;
                        DatabaseStatusText.Text = _serverBlockReason;
                    }

                    return;
                }

                /*
                The box names someone else. A demotion's message already says exactly that, and its
                _demotedFromState must survive so typing the name back can still undo it -- so only a REAL
                block needs restating, and it must lose the previous server's specifics.

                _verdictServerName != null is what makes this correct rather than merely plausible. A clean
                install deliberately forgets the stamp (it is about to drop the database), so
                VerdictMatchesServerBox() is false for the rest of that dialog's life -- and without this
                test, the FIRST keystroke in the box, including one that changes nothing, replaced "a clean
                install was requested, which drops the database" with "the server name changed after this
                server's status was checked". That is the one sentence telling the user their database is
                gone, and it was being overwritten with a sentence that is not even true. No stamp means
                nothing to compare the box against, so there is nothing to restate.
                */
                if (_demotedFromState == null &&
                    _installBlockedReason != null &&
                    _verdictServerName != null)
                {
                    _installBlockedReason = StaleServerNotice;
                    DatabaseStatusText.Text = StaleServerNotice;
                }

                return;
            }

            if (ServerSetupPolicy.IsServerVerdictState(_currentState) && !VerdictMatchesServerBox())
            {
                /* Re-entering the state lets the stamp guard demote it. */
                TransitionToState(_currentState);
            }
        }

        /*
        Everything a completed run leaves on screen describes the server the run actually went to: the
        install log and its "Installation completed successfully!" headline, the View Report link, and after
        a repair the "Upgrade Now" handoff with its version claim. Retyping the box left all of it standing
        under the new name -- so a user could retarget after an install and click Save believing
        PerformanceMonitor had been installed on the server now in the box.

        Only the repair handoff was withdrawn before, because that was the path the previous round happened
        to be looking at; the ordinary install, upgrade and reinstall are the COMMON case and had no
        withdrawal at all. Withdraw all of it together, restore all of it together, and say once why it went
        away -- idempotent, so it is safe to call on every keystroke.
        */
        private void RenderPostRunClaims()
        {
            bool addressed = VerdictMatchesServerBox();

            InstallationPanel.Visibility = addressed ? Visibility.Visible : Visibility.Collapsed;

            if (_reportPath != null)
            {
                ViewReportButton.Visibility = addressed ? Visibility.Visible : Visibility.Collapsed;
            }

            RenderRepairHandoff(addressed);

            if (addressed)
            {
                StatusText.Text = string.Empty;
                StatusText.Visibility = Visibility.Collapsed;
                return;
            }

            /* The panels are gone; without this the dialog would simply look empty for no stated reason. */
            StatusText.Text = StaleServerNotice;
            StatusText.Visibility = Visibility.Visible;
        }

        private bool VerdictMatchesServerBox() =>
            _verdictServerName != null &&
            string.Equals(_verdictServerName, ServerNameTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);

        /*
        Return to the state the install was launched from -- NOT Initial.

        Initial does not reset InstallUpgradeButton.Content or DatabaseStatusText, and the exit paths
        used to force-show InstallationPanel on top of it. Launched from Connected_Current that produced
        a genuinely dangerous screen: the button still read "Repair", the text still read "is up to
        date", and Advanced Options -- holding "Perform clean install (drops existing database)" -- was
        now visible behind it. Ticking that and clicking [Repair] dropped the database. Connected_Current
        keeps its options panel collapsed precisely so that cannot happen, so restoring it honestly is
        the fix; there, the failure is surfaced in the status panel that IS visible.
        */
        private void RestoreAfterInstall(string message)
        {
            /*
            REDUNDANT, and deliberately kept. TransitionToState clears these on every non-Installing
            transition, and every path out of here goes through it -- that choke point is the guarantee,
            not this. An earlier comment here claimed this was "the only exit path from a run", which was
            wrong twice over (BlockInstall and the stale-name demotion are exits too, and the demotion
            never reaches either) and would have sent the next reader hunting the wrong mechanism on the
            code path that runs DROP DATABASE. Belt and braces on that path is worth three lines.
            */
            RepairCheckBox.IsChecked = false;
            CleanInstallCheckBox.IsChecked = false;
            ResetScheduleCheckBox.IsChecked = false;

            /*
            We have no verdict to restore, for one of two reasons -- and they need DIFFERENT words. Either
            the run ended before this server's version was read (a cancel or a connect failure during the
            install-time re-read), or a clean install deliberately discarded it because it was about to drop
            the database. Telling a user whose database we just dropped that "the installed version was not
            read" is simply false, and it is the wrong thing to be reading at that moment.

            Either way, do not restore a Connected_* state: that renders the previous server's verdict under
            this one's name. Say we do not know.
            */
            if (_preInstallState == null)
            {
                string reason =
                    _verdictDiscardedByCleanInstall
                        ? "A clean install was requested, which drops the database, so this server's status " +
                          "is unknown until it is re-checked."
                        : "This server's installed version was not read, so its status is unknown.";

                BlockInstall($"{message}\n\n{reason} Click Test Connection to check it.");

                /*
                And SHOW THE LOG. BlockInstall lands in Connected_StatusUnknown, which collapses
                InstallationPanel -- so this branch used to return having hidden InstallLogTextBox, which is
                the only place the actual SQL error text lives. On the clean-install path that is the worst
                possible moment to hide it: the database has already been dropped (CleanInstallAsync runs
                before the file loop, so any cancel or failure after that point arrives here with the
                database gone), and the user was left with a single exception message to work out why. The
                branch below has said "show the log either way" since it was written; this one just didn't.

                Advanced Options stays disabled: the install button is collapsed in this state anyway, so
                the options are not actionable, and the ticks were cleared on the way in.
                */
                InstallationPanel.Visibility = Visibility.Visible;
                InstallStatusText.Text = message;
                AdvancedOptionsExpander.IsEnabled = false;
                return;
            }

            TransitionToState(_preInstallState.Value);

            DatabaseStatusPanel.Visibility = Visibility.Visible;
            InstallationPanel.Visibility = Visibility.Visible;
            InstallStatusText.Text = message;

            /*
            Show the log either way -- it is the only place the actual SQL error text lives, and hiding it
            leaves the user one exception message to work with. But when the run was launched from
            Connected_Current the button still reads "Reinstall Objects" and the clean-install checkbox
            must stay out of reach, so re-show the panel with Advanced Options DISABLED rather than
            collapsed: WPF propagates IsEnabled to children, so the checkbox cannot be ticked.
            */
            AdvancedOptionsExpander.IsEnabled = _preInstallState.Value != DialogState.Connected_Current;
        }

        private async System.Threading.Tasks.Task DetectDatabaseStatusAsync()
        {
            if (InstallInProgress) return;

            /*
            One probe at a time. A detection left Test Connection and Save armed, so a connection test could
            start on top of a running detection -- and that test captured the DETECTION's "Checking database
            status..." label as the thing to restore when it finished, then faithfully put it back over a
            screen where no detection was running. The epoch does not catch it: a detection bumps the epoch
            when it STARTS, not when it finishes, so the borrow still looked current. If the test then failed
            it showed a MessageBox and never repainted, and the label stayed there for good.

            The finally re-arms only if an install did not start meanwhile -- SetFormEnabled(false) disabled
            them deliberately in that case, and every non-Installing state re-arms them on the way out.
            */
            DialogState stateBeforeProbe = _currentState;
            bool saveWasEnabled = SaveButton.IsEnabled;

            TestConnectionButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            CheckForUpdatesButton.IsEnabled = false;

            try
            {
                await DetectDatabaseStatusCoreAsync();
            }
            finally
            {
                if (!InstallInProgress)
                {
                    /* Nothing owns these two: they are always available whenever the form is live. */
                    TestConnectionButton.IsEnabled = true;
                    CheckForUpdatesButton.IsEnabled = true;

                    /*
                    Save is OWNED BY THE STATE, and this finally must not overrule it. Connected_NoDatabase
                    turns Save OFF on purpose -- the user has to click Install Now, or take the explicit
                    "Skip, just add server" link, which is the CONSENTED way to add a server with no
                    PerformanceMonitor database (SkipInstall_Click re-enables Save and relabels it). Blindly
                    re-arming it here handed that consent away on the most ordinary path in the dialog: point
                    at a bare instance, click Check for Updates, read "No PerformanceMonitor database found"
                    -- and Save is live, and it is IsDefault, so ENTER commits a server the Dashboard can
                    read nothing from.

                    If the probe published a verdict, the state it landed in has already set Save correctly.
                    Only put back what we borrowed when the probe changed nothing -- superseded, or one that
                    deliberately published nothing.
                    */
                    if (_currentState == stateBeforeProbe)
                    {
                        SaveButton.IsEnabled = saveWasEnabled;
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task DetectDatabaseStatusCoreAsync()
        {
            int epoch = ++_detectEpoch;

            /*
            _dialogClosed belongs in here, not at the call sites. "The dialog is gone" is just another reason
            this detection's answer is no longer wanted, and Superseded() is already the one question every
            publish point in this method asks before committing anything -- so putting it here covers all of
            them at once, including the second DB round-trip, instead of a guard per caller that the next
            caller forgets.
            */
            bool Superseded() => epoch != _detectEpoch || InstallInProgress || _dialogClosed;

            try
            {
                StatusText.Text = "Checking database status...";
                StatusText.Visibility = Visibility.Visible;

                /*
                A fresh detection means a fresh server/state, so the destructive and mode-changing
                options start clear. Without this they persist across a retarget of the server box.
                */
                RepairCheckBox.IsChecked = false;
                CleanInstallCheckBox.IsChecked = false;
                /* Sticky and INVISIBLE behind a collapsed panel otherwise -- it TRUNCATEs config.collection_schedule,
                   so a tick left over from another server would wipe this one's tuned intervals with no consent. */
                ResetScheduleCheckBox.IsChecked = false;

                /*
                Capture the server name HERE, alongside the connection string built from it -- not after
                the awaits below. The box stays editable throughout, so re-reading it later would stamp
                the verdict with whatever is in the box NOW rather than the server we actually read, and
                the guard would then be comparing the box to itself: it could never fail.
                */
                string probedServerName = ServerNameTextBox.Text;
                string installerConnStr = BuildInstallerConnectionString();
                string appVersion = GetAppVersion();

                /*
                Land in a LOCAL, guard, and only then commit to the shared field. An install may have
                started while this was in flight (against a different server -- the box stays editable),
                and it reads _coreServerInfo. Guarding after the field write would already have clobbered
                it with the previous server's ServerInfo.
                */
                var probedServerInfo = await InstallationService.TestConnectionAsync(installerConnStr);

                /* An install started, or a newer detection superseded us: publish nothing. */
                if (Superseded()) return;

                if (probedServerInfo == null || !probedServerInfo.IsConnected)
                {
                    /*
                    Publish NOTHING -- deliberately. We established no facts, so the previous server's four
                    facts and its stamp stay together and stale, which the guard catches. Stamping this
                    server's NAME here without the facts to go with it is the half-replaced state the guard
                    structurally cannot catch, and it is what made it affirm a lie once already.

                    But SAY something. This used to blank the label and return, which turned the one
                    instruction on screen after a demotion -- "Click Test Connection to check the server now
                    in the box" -- into a dead end: the installer-side probe can fail where the plain
                    connection test just succeeded (different connection string, different database), and
                    the user clicked, and nothing happened. No message, no state change. Forever.

                    A STATUS LINE, not a BlockInstall. A block is a fact about a specific server, and the
                    guard attributes it by the stamp -- which we just refused to move, correctly. Blocking
                    here would leave the block attributed to the PREVIOUS server, so the next keystroke would
                    restate it as "the server name changed", which is not what happened. And the only way to
                    attribute it would be to stamp without the facts, which is the half-replaced state that
                    made the guard affirm a lie in the first place. So: no new claim about any server, just
                    a plain report of what failed. The install stays blocked by whatever already blocked it.
                    */
                    string detail =
                        string.IsNullOrWhiteSpace(probedServerInfo?.ErrorMessage)
                            ? "."
                            : $": {probedServerInfo.ErrorMessage}";

                    StatusText.Text = $"Could not read this server's PerformanceMonitor status{detail}";
                    StatusText.Visibility = Visibility.Visible;
                    return;
                }

                string probedServerVersion = probedServerInfo.SqlServerVersion.Split('\n')[0].Trim();

                if (!probedServerInfo.IsSupportedVersion)
                {
                    /*
                    A publish: "SQL Server 2014 is not supported" is a fact ABOUT this server, so it is
                    stamped with it -- otherwise retyping past an unsupported 2014 instance to a 2022 one
                    left the panel still saying 2014, with no stamp for the guard to catch it on. Safe to
                    commit here: no await stands between the guard above and this line, so the publish
                    cannot be abandoned after the fields are written.

                    Routed through BlockInstall rather than hand-poked. Writing the panels directly here
                    bypassed the stale-verdict guard entirely and left _currentState pointing at whatever
                    the previous server's state was -- so an unsupported server could be described using
                    the previous one's facts, under this one's name.
                    */
                    /* The verdict that replaces the previous run's log arrives WITH it, not before it. */
                    DiscardPreviousRunLog();

                    PublishProbedServer(
                        probedServerInfo,
                        probedServerVersion,
                        probedServerName,
                        /* Never read on this path -- we bail before the version probe. */
                        installedVersion: null);

                    StatusText.Text = string.Empty;
                    StatusText.Visibility = Visibility.Collapsed;
                    BlockInstall(
                        $"{probedServerInfo.ProductMajorVersionName} is not supported.\n\n" +
                        "PerformanceMonitor requires SQL Server 2016 or later.");
                    return;
                }

                /*
                Check installed version. throwOnError matters: with the soft overload a transient
                SqlException -- a timeout, the database OFFLINE/RESTORING, a permissions blip -- comes
                back as null, which is indistinguishable from "no database". That drops us into the
                fresh-install path, which skips every migration, reinstalls over the existing
                database, and then stamps installation_history SUCCESS at the target version --
                stranding every pending hop permanently. The CLI passes throwOnError: true for
                exactly this reason; the Dashboard's install path must too.
                */
                try
                {
                    /* Local first, guard, then commit -- a running install reads _installedVersion. */
                    var probedVersion = await InstallationService.GetInstalledVersionAsync(
                        installerConnStr,
                        throwOnError: true);

                    /* An install started, or a newer detection superseded us: publish nothing. */
                    if (Superseded()) return;

                    /*
                    Cleared HERE, not before the await. Clearing it in the prologue makes the clear
                    unconditional while the publish it belongs to stays guarded: an install that blocked
                    itself mid-flight (a cancel, a fatal) would be retroactively un-blocked by a detection
                    that then declines to publish anything -- leaving a live button whose every click is a
                    "Blocked" dialog. State that describes a verdict is committed with the verdict.
                    */
                    _installBlockedReason = null;
                    _serverBlockReason = null;

                    /* The verdict that replaces the previous run's log arrives WITH it, not before it. */
                    DiscardPreviousRunLog();

                    PublishProbedServer(
                        probedServerInfo,
                        probedServerVersion,
                        probedServerName,
                        probedVersion);
                }
                catch (Exception ex)
                {
                    if (Superseded()) return;

                    /*
                    A publish: the block names THIS server's failure, so it is stamped with it -- and the
                    installed version is null, because that is exactly what just failed to be read. Carrying
                    the previous server's version through here is how a block ends up describing the wrong
                    database.
                    */
                    /* The verdict that replaces the previous run's log arrives WITH it, not before it. */
                    DiscardPreviousRunLog();

                    PublishProbedServer(
                        probedServerInfo,
                        probedServerVersion,
                        probedServerName,
                        installedVersion: null);

                    BlockInstall(
                        $"Could not determine the installed PerformanceMonitor version: {ex.Message}\n\n" +
                        "Install and upgrade are blocked until this resolves. Proceeding could reinstall over an " +
                        "existing database, skip its pending migrations, and record it as up to date.");
                    return;
                }

                string? blockReason = GetInstallBlockReason(_installedVersion, appVersion);
                if (blockReason != null)
                {
                    BlockInstall(blockReason);
                    return;
                }

                TransitionToState(ServerSetupPolicy.DeriveConnectedState(_installedVersion, appVersion));
            }
            catch (Exception ex)
            {
                /*
                The one continuation in this method that was not guarded. A stale detection whose connection
                test throws would paint its error over a running install's screen, or over a newer
                detection's result -- reporting a failure against a server nobody is looking at any more.
                */
                if (Superseded()) return;

                StatusText.Text = $"Could not check database status: {ex.Message}";
                StatusText.Visibility = Visibility.Visible;
            }
        }

        /* Record why installing is unsafe and drop into the state that hides the install button. */
        private void BlockInstall(string reason)
        {
            _installBlockedReason = reason;

            /* Kept so a retarget can swap in the generic notice and a retarget BACK can restore the truth. */
            _serverBlockReason = reason;

            /*
            The destructive/mode ticks are cleared by TransitionToState, for every state but Installing --
            deliberately NOT here. Clearing them at each exit is what kept failing: the stale-name demotion
            rewrites the state in place and never reaches this method, so a clear written here would have
            missed it exactly as the three before it did.
            */
            TransitionToState(DialogState.Connected_StatusUnknown);
        }

        /*
        Decide whether installing against this installed version is safe, and if not, why. Returns null
        when it is safe (including "no database", which is a fresh install).

        Both the Test Connection path and the install click run this. It cannot be computed once and
        cached: the server box stays editable and BuildInstallerConnectionString() rebuilds the
        connection string live, so a verdict from the last detection may belong to a different server.
        Refreshing the version alone is not enough either -- the "installed is NEWER than this build"
        case produces zero hops and zero failures, so nothing downstream catches it, and the install
        would silently revert a newer database to this binary's older object definitions and record
        the lower version as SUCCESS.
        */
        private static string? GetInstallBlockReason(string? installedVersion, string appVersion)
        {
            /*
            The DECISION lives in Installer.Core/InstallGuard, shared with the CLI and pinned by tests
            that actually run in CI. Only the wording is ours. Two of these cases are invisible to every
            other guard -- they produce zero upgrade hops AND zero failures, so the migration-failure
            abort never fires and nothing downstream notices.
            */
            switch (InstallGuard.Check(installedVersion, appVersion))
            {
                case InstallBlock.UnreadableBuildVersion:
                    return $"This Dashboard reports its own version as '{appVersion}', which is not a valid version.\n\n" +
                        "Install and upgrade are blocked: without a comparable version we cannot tell which " +
                        "migrations still need to run, and a fresh install would record that value as the " +
                        "server's version. This is a build problem, not a problem with the server.";

                case InstallBlock.UnreadableInstalledVersion:
                    return $"Could not interpret the installed PerformanceMonitor version ('{installedVersion}').\n\n" +
                        "Install and upgrade are blocked: without a comparable version we cannot tell which " +
                        "migrations still need to run. Correct the most recent SUCCESS row in " +
                        "PerformanceMonitor.config.installation_history, or rebuild the database with the " +
                        "CLI installer's --reinstall (destructive).";

                case InstallBlock.InstalledIsNewerThanBuild:
                    /*
                    Everything is blocked here, clean install included -- Connected_StatusUnknown collapses
                    the panel the checkbox lives in. So this message has to carry the whole escape route, or
                    the user is simply stuck.
                    */
                    return $"PerformanceMonitor v{NormalizeVersion(installedVersion!)} is installed, which is newer than this " +
                        $"Dashboard (v{NormalizeVersion(appVersion)}).\n\n" +
                        "Install, upgrade and repair are blocked: running the older installer would revert this " +
                        "server's objects to the older definitions and record it at the lower version.\n\n" +
                        "Update this Dashboard to v" + NormalizeVersion(installedVersion!) + " or later. If you " +
                        "genuinely need to move this server BACK to an older version, the CLI installer's " +
                        "--reinstall does that (it drops the database first, so there is nothing left to downgrade).";

                case InstallBlock.None:
                    return null;

                default:
                    /*
                    Never default to "safe to install". A new InstallBlock member silently becoming an
                    allow is the one failure mode this whole guard exists to prevent.
                    */
                    throw new NotSupportedException(
                        $"Unhandled {nameof(InstallBlock)}: {InstallGuard.Check(installedVersion, appVersion)}");
            }
        }

        private void TransitionToState(DialogState newState)
        {
            /*
            Refuse to render a verdict that belongs to a different server. The states below assert facts
            about "this server" -- its installed version, whether it needs an upgrade, whether it is up to
            date -- and every one of those facts came from a read of some server. If the box has since
            been retyped, rendering them puts the OLD server's verdict under the NEW server's name:
            "PerformanceMonitor v3.1.0 is up to date" over a database four migrations behind, with Save
            enabled. That exact lie has been reached three different ways across nine review rounds, so it
            is stopped here, once, rather than at each consumer.
            */
            if (ServerSetupPolicy.IsServerVerdictState(newState) && !VerdictMatchesServerBox())
            {
                /*
                Remembered so the demotion can be UNDONE. The verdict was never wrong -- it was
                mis-addressed -- so if the box comes back to the name it was read from, it is valid again.
                Null on every other transition, which is what keeps a REAL block (unsupported version,
                unreadable version, newer-than-binary) from being undone by typing.
                */
                _demotedFromState = newState;

                _installBlockedReason = StaleServerNotice;
                newState = DialogState.Connected_StatusUnknown;
            }
            else
            {
                _demotedFromState = null;
            }

            /*
            Consent for a destructive option is per-run and per-SERVER. These three ticks authorize
            DROP DATABASE (clean install), skipping the migrations (repair), and TRUNCATE
            config.collection_schedule (reset schedule) -- against the server that was on screen when they
            were ticked. ANY transition other than starting the run invalidates that: the run ended, or the
            verdict was demoted because the box now names someone else.

            Cleared HERE, at the one choke point every state change passes through, because clearing it at
            each exit is precisely what kept failing. It was patched into RestoreAfterInstall, then the
            detection prologue, then BlockInstall -- and the stale-name demotion, which rewrites newState in
            place a dozen lines above and never calls BlockInstall at all, still slipped past every one of
            them. That path is unreachable today only because the state it lands in happens to collapse the
            panel holding these checkboxes: a rendering accident standing in for a consent check, on the
            code path that drops a database.

            Installing is the sole exception -- the run is about to READ these ticks. That "sole exception"
            is StateConsumesModeSelections, in ServerSetupPolicy, pinned by a test over every state so a new
            state cannot silently join the exception and carry a live tick onto a server.
            */
            if (!ServerSetupPolicy.StateConsumesModeSelections(newState))
            {
                CleanInstallCheckBox.IsChecked = false;
                RepairCheckBox.IsChecked = false;
                ResetScheduleCheckBox.IsChecked = false;
            }

            _currentState = newState;
            string appVersion = GetAppVersion();

            /* Reset panel visibility */
            DatabaseStatusPanel.Visibility = Visibility.Collapsed;
            InstallationPanel.Visibility = Visibility.Collapsed;
            MonitoringCredsPanel.Visibility = Visibility.Collapsed;
            ViewReportButton.Visibility = Visibility.Collapsed;
            /*
            Unfreeze Advanced Options. The install states below re-disable it immediately, but without
            this it was only ever re-enabled on the abort/cancel/fatal paths -- so after a completed
            install it stayed disabled for the rest of the dialog's life, stranding whatever mode
            checkboxes were ticked inside it with no way to untick them.
            */
            AdvancedOptionsExpander.IsEnabled = true;
            /* Only the Installing state arms Cancel; otherwise it paints "Cancelling..." over nothing. */
            CancelInstallButton.IsEnabled = false;
            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;
            ConnectionInfoText.Text = string.Empty;
            InstallUpgradeButton.Visibility = Visibility.Visible;
            SkipInstallText.Visibility = Visibility.Visible;

            /*
            Built from the STAMPED server, never the live box, and only when the stamp still matches it.
            Reading the box here rendered "Connected to S (Microsoft SQL Server 2019 ...)" mid-keystroke:
            a connection that was never made, to a half-typed name, carrying the PREVIOUS server's engine
            version. The three verdict states below reach this line only with a matching stamp (the guard
            demotes them otherwise), but Connected_StatusUnknown is not a verdict state -- it is where a
            demotion LANDS -- so it was never covered. Asserting "Connected to" a server we did not
            connect to is the same lie the guard exists to stop; say nothing instead.
            */
            string connectionHeader = string.Empty;

            if (VerdictMatchesServerBox())
            {
                connectionHeader = _serverVersion != null
                    ? $"Connected to {_verdictServerName} ({_serverVersion})"
                    : $"Connected to {_verdictServerName}";
            }

            switch (newState)
            {
                case DialogState.Connected_NoDatabase:
                    ConnectionInfoText.Text = connectionHeader;
                    DatabaseStatusText.Text = "No PerformanceMonitor database found on this server. " +
                        $"Click Install Now to create the monitoring database, collection jobs, and stored procedures (v{appVersion}).";
                    InstallUpgradeButton.Content = "Install Now";
                    DatabaseStatusPanel.Visibility = Visibility.Visible;
                    InstallationPanel.Visibility = Visibility.Visible;
                    /* Reachable from Installing (the "nothing to repair" refusal), which disabled the form. */
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = false;
                    break;

                case DialogState.Connected_NeedsUpgrade:
                    string normalizedInstalled = NormalizeVersion(_installedVersion ?? "unknown");
                    ConnectionInfoText.Text = connectionHeader;
                    /* Never state the sentinel as a fact: it means "installed, version unreadable". */
                    DatabaseStatusText.Text = RepairOutcome.IsVersionUnknown(_installedVersion)
                        ? "PerformanceMonitor is installed, but its recorded version could not be read, so it is " +
                          $"unknown which migrations have run. Click Upgrade Now to attempt every upgrade and bring it to v{appVersion}."
                        : $"PerformanceMonitor v{normalizedInstalled} is installed. " +
                          $"v{appVersion} is available — click Upgrade Now to apply the update.";
                    InstallUpgradeButton.Content = "Upgrade Now";
                    DatabaseStatusPanel.Visibility = Visibility.Visible;
                    InstallationPanel.Visibility = Visibility.Visible;
                    /* Reachable from Installing (every upgrade abort restores here), which disabled the form. */
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = true;
                    break;

                case DialogState.Connected_Current:
                    string normalizedCurrent = NormalizeVersion(_installedVersion ?? "unknown");
                    ConnectionInfoText.Text = connectionHeader;
                    DatabaseStatusText.Text = $"PerformanceMonitor v{normalizedCurrent} is up to date. " +
                        "If objects are missing or damaged, click Reinstall Objects to restore them.";
                    /*
                    Repair stays reachable at the current version: the install scripts are idempotent,
                    so re-running them restores missing objects. This state now means installed ==
                    target exactly, so there are no migrations to skip and this is a plain reinstall at
                    the same version. The CLI can already do this with --repair; without it the
                    Dashboard had no non-destructive recovery for a healthy-version-but-damaged database.

                    InstallationPanel stays collapsed on purpose: it holds the "drops existing database"
                    clean-install checkbox, which must never sit behind this button.

                    Named "Reinstall Objects", NOT "Repair": the Repair CHECKBOX means something different
                    (skip the migrations, write no history row), and it is deliberately unreachable here.
                    This button runs the ordinary install path -- with installed == target there are no
                    migrations to skip, so it is a same-version REINSTALL and correctly records one.
                    */
                    InstallUpgradeButton.Content = "Reinstall Objects";
                    SkipInstallText.Visibility = Visibility.Collapsed;
                    DatabaseStatusPanel.Visibility = Visibility.Visible;
                    /* Reachable from Installing (a failed reinstall restores here), which disabled the form. */
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = true;
                    break;

                case DialogState.Connected_StatusUnknown:
                    ConnectionInfoText.Text = connectionHeader;
                    DatabaseStatusText.Text = _installBlockedReason ?? "The database status could not be determined.";
                    InstallUpgradeButton.Visibility = Visibility.Collapsed;
                    SkipInstallText.Visibility = Visibility.Collapsed;
                    DatabaseStatusPanel.Visibility = Visibility.Visible;
                    /* Reachable from Installing (the install-time re-check blocks), which disabled the form. */
                    CancelInstallButton.IsEnabled = false;
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = true;
                    break;

                case DialogState.Installing:
                    DatabaseStatusPanel.Visibility = Visibility.Collapsed;
                    InstallationPanel.Visibility = Visibility.Visible;
                    AdvancedOptionsExpander.IsEnabled = false;
                    CancelInstallButton.IsEnabled = true;
                    SetFormEnabled(false);
                    break;

                case DialogState.InstallComplete:
                    InstallationPanel.Visibility = Visibility.Visible;
                    /*
                    Clear both mode checkboxes once a run completes, on EVERY outcome. They are sticky
                    otherwise: the form is re-enabled here, so the user can retype the server box and
                    click again -- carrying "Repair" (skip every migration) or, worse, "Clean install"
                    (drop the database) onto a server that was never consented to.
                    */
                    RepairCheckBox.IsChecked = false;
                    CleanInstallCheckBox.IsChecked = false;
                    /* Sticky and INVISIBLE behind a collapsed panel otherwise -- it TRUNCATEs config.collection_schedule,
                       so a tick left over from another server would wipe this one's tuned intervals with no consent. */
                    ResetScheduleCheckBox.IsChecked = false;
                    AdvancedOptionsExpander.IsEnabled = false;
                    CancelInstallButton.IsEnabled = false;
                    SetFormEnabled(true);
                    if (_reportPath != null)
                    {
                        ViewReportButton.Visibility = Visibility.Visible;
                    }
                    /* Transition to monitoring credentials if using SQL auth */
                    if (SqlAuthRadio.IsChecked == true)
                    {
                        TransitionToState(DialogState.MonitoringCredentials);
                        return;
                    }
                    SaveButton.IsEnabled = true;
                    SaveButton.Content = "Save & Connect";
                    break;

                case DialogState.MonitoringCredentials:
                    InstallationPanel.Visibility = Visibility.Visible;
                    AdvancedOptionsExpander.IsEnabled = false;
                    CancelInstallButton.IsEnabled = false;
                    MonitoringCredsPanel.Visibility = Visibility.Visible;
                    if (_reportPath != null)
                    {
                        ViewReportButton.Visibility = Visibility.Visible;
                    }
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = true;
                    SaveButton.Content = "Save & Connect";
                    break;

                case DialogState.Initial:
                default:
                    /*
                    The cancel and fatal-error paths land here, and both re-enable the form and re-show
                    the panels -- so a ticked "Clean install (drops existing database)" would survive a
                    cancelled run, and the server box could then be retargeted and clicked, dropping a
                    database that was never consented to. Clear the mode flags here as well as at
                    InstallComplete, so every exit from a run clears them.
                    */
                    RepairCheckBox.IsChecked = false;
                    CleanInstallCheckBox.IsChecked = false;
                    /* Sticky and INVISIBLE behind a collapsed panel otherwise -- it TRUNCATEs config.collection_schedule,
                       so a tick left over from another server would wipe this one's tuned intervals with no consent. */
                    ResetScheduleCheckBox.IsChecked = false;
                    SetFormEnabled(true);
                    SaveButton.IsEnabled = true;
                    SaveButton.Content = "Save";
                    break;
            }
        }

        private async void InstallOrUpgrade_Click(object sender, RoutedEventArgs e)
        {
            /*
            Never install when the detected state said it is unsafe: we cannot tell an absent database
            from an existing one we simply could not read, and guessing "absent" reinstalls over it with
            the migrations skipped and then records it as up to date.
            */
            /* Structural backstop: never start a second install over a running one. */
            if (InstallInProgress) return;

            /*
            Permanently supersede any detection still in flight. Superseded() also tests InstallInProgress,
            but that is only read when the continuation RESUMES -- so a detection that spans the whole run
            wakes up AFTER it, sees the install finished, and publishes its pre-install verdict over the
            result: the "Upgrade Now" button returns on a server that was just upgraded, or comes back live
            while _installBlockedReason is still set, so every click is a "Blocked" box. The epoch is the
            only part of that test which cannot un-fire.
            */
            _detectEpoch++;

            /*
            Every run invalidates the previous run's handoff. Without this, the handoff text outlived the
            repair that produced it: repair, then click the "Upgrade Now" it hands you, and the upgrade
            succeeds -- but this field still held "PerformanceMonitor is still at v3.0.0, the pending
            upgrade has not been applied". Any later keystroke in the server box re-rendered that, with a
            live Upgrade Now button, on a server that had just been upgraded. The completion block below
            re-sets it only when THIS run was a repair that left an upgrade pending.
            */
            _handoffStatusText = null;

            if (_installBlockedReason != null)
            {
                MessageBox.Show(
                        this,_installBlockedReason, "Install Blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            /*
            Freeze the UI and arm cancellation BEFORE the first await. TransitionToState(Installing)
            collapses DatabaseStatusPanel, which is the Border InstallUpgradeButton lives in -- and that
            collapse is the ONLY thing that makes the button unclickable, since SetFormEnabled never
            touches it. Awaiting the version re-read in front of it would leave the button live for the
            whole connect timeout (unbounded for interactive Entra auth), and this handler is async void:
            a second click starts a second concurrent install that clobbers _installCts and
            _installResult, races two clean installs, and leaves Cancel pointing at the wrong run.
            Creating the CTS first also means Cancel and ESC work DURING the probe rather than no-oping
            on a null and letting the install continue after the dialog has closed.
            */
            /*
            The promise is fixed here and never re-derived. The restore target starts from the best facts
            we currently have -- and is refreshed from the install-time re-read below -- but it is a
            Connected_* state from the outset, so a cancel DURING the re-read can never restore into
            InstallComplete and unfreeze the clean-install checkbox the repair handoff had frozen.
            */
            _launchState = _currentState;
            _preInstallState = null;

            /* Per-run, like _handoffStatusText. A stale one would misexplain the NEXT run's failure. */
            _verdictDiscardedByCleanInstall = false;

            /*
            Per-run too, and it was the one that got missed. GenerateSummaryReport is inside a try whose
            catch only logs -- so if it throws on THIS run (long path, disk full, permissions), _reportPath
            still holds the LAST run's file, for what may be a different server, and InstallComplete shows
            "View Report" because it only tests for null. RenderPostRunClaims will even bring the link back
            on a retype-and-back.
            */
            _reportPath = null;

            TransitionToState(DialogState.Installing);
            InstallLogTextBox.Clear();
            InstallProgressBar.Value = 0;
            InstallStatusText.Text = "Checking installed version...";

            _installCts?.Dispose();

            /*
            Held locally as well as in the field, so the finally can tear down the token IT created and
            nothing else. Disposing the field unconditionally meant a run that unwound LATE could dispose
            and null a token belonging to a run that had started since -- leaving that one uncancellable by
            the Cancel button, by Cancel_Click, and by CancelInstallOnClose, so closing the dialog would no
            longer stop an install running against the database.
            */
            var installCts = new CancellationTokenSource();
            _installCts = installCts;
            var cancellationToken = installCts.Token;

            try
            {
                var provider = ScriptProvider.FromEmbeddedResources();
                /* Captured with the connection string built from it -- the box is frozen here, but stamp
                   what we read, never what the box happens to say later. */
                string installTimeServerName = ServerNameTextBox.Text;
                string installerConnStr = BuildInstallerConnectionString();
                string appVersion = GetAppVersion();

                /*
                Re-read the installed version against the connection we are about to WRITE to, and
                re-run the safety verdict on the result. The cached _installedVersion and
                _installBlockedReason come from the last Test Connection, but the server box stays
                editable and the connection string is rebuilt live -- so retyping it and clicking
                straight through would install into a different server while still reasoning about the
                previous one. Refreshing the version alone is not enough: the "installed is NEWER than
                this build" verdict produces zero hops and zero failures, so nothing downstream catches
                it, and the install would silently downgrade the server and record the lower version.
                */
                try
                {
                    /*
                    Refresh the server facts too, not just the version. The cached ones came from the last
                    Test Connection, and after a retarget they describe a different box -- so the summary
                    report would name THIS server with the previous one's SQL version and edition.

                    Landed in LOCALS and published as a SET, exactly as the detect path does. This method
                    used to write _coreServerInfo, then await, then write _installedVersion, then stamp --
                    three of the four coupled facts, committed separately across an await, and never the
                    fourth (_serverVersion kept whatever the last detection left). It is not exploitable,
                    because the box is frozen for the whole run and every other handler bails on
                    InstallInProgress. But "not exploitable because something else happens to be disabled"
                    is enablement standing in for an invariant, and that is the exact substitution this file
                    has been bitten by over and over. Publish them together and it is true by construction.
                    */
                    var installTimeInfo = await InstallationService.TestConnectionAsync(
                        installerConnStr,
                        cancellationToken: cancellationToken);

                    /*
                    And ACT on those facts. The SQL-2016+ gate was detect-time only, so retargeting the box
                    to an older server and clicking through installed 2016+ syntax onto it -- creating a
                    junk database and a FAILED history row on a server the gate exists to refuse. Re-reading
                    the fact and then not using it was the whole hole.
                    */
                    if (installTimeInfo is not { IsConnected: true })
                    {
                        throw new InvalidOperationException("Could not connect to this server.");
                    }

                    if (!installTimeInfo.IsSupportedVersion)
                    {
                        throw new InvalidOperationException(
                            $"{installTimeInfo.ProductMajorVersionName} is not supported. PerformanceMonitor requires SQL Server 2016 or later.");
                    }

                    string? installTimeVersion = await InstallationService.GetInstalledVersionAsync(
                        installerConnStr,
                        throwOnError: true,
                        cancellationToken: cancellationToken);

                    /* Stamped with the server we just read -- this is the connection we will write to. */
                    PublishProbedServer(
                        installTimeInfo,
                        installTimeInfo.SqlServerVersion.Split('\n')[0].Trim(),
                        installTimeServerName,
                        installTimeVersion);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    /*
                    SqlCommand does NOT surface cancellation as an OperationCanceledException: it cancels
                    the in-flight command and the task faults with a SqlException ("Operation cancelled by
                    user."). Without this, the user's own Cancel would be reported as an
                    unknown-database-state block, hiding the install button until they re-tested.
                    */
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() => BlockInstall(
                        $"Could not determine the installed PerformanceMonitor version on this server: {ex.Message}\n\n" +
                        "Install and upgrade are blocked: proceeding could reinstall over an existing database " +
                        "and skip its pending migrations."));
                    return;
                }

                /*
                Refuse to reinstall what is not there. Falling through would silently run a FULL fresh
                install and stamp the target version -- a whole new database, Agent jobs and XE sessions on
                a mistyped server. Both buttons that promise to act on an EXISTING install are covered:
                "Upgrade Now" (Connected_NeedsUpgrade) and "Reinstall Objects" (Connected_Current), plus the
                Repair checkbox. The server box stays editable and the re-read above runs against whatever
                is in it NOW, so a retarget to a bare instance reaches all three.
                */
                bool promisedAnExistingInstall = ServerSetupPolicy.PromisesExistingInstall(_launchState);

                if ((RepairCheckBox.IsChecked == true || promisedAnExistingInstall) &&
                    CleanInstallCheckBox.IsChecked != true &&
                    _installedVersion == null)
                {
                    /*
                    The button arms matter as much as the checkbox: those states' buttons say "Upgrade Now"
                    and "Reinstall Objects", the server box stays editable, and the install-time re-read runs
                    against whatever is in it now. Retype it to a bare instance and the button would
                    silently perform a FULL fresh install -- new database, jobs, XE sessions -- from a
                    button that promised to reinstall existing objects.
                    */
                    await Dispatcher.InvokeAsync(() =>
                    {
                        /* Every other exit clears all three; this one must too. ResetSchedule TRUNCATEs
                           config.collection_schedule, so a tick surviving a retarget wipes the wrong server. */
                        RepairCheckBox.IsChecked = false;
                        CleanInstallCheckBox.IsChecked = false;
                        ResetScheduleCheckBox.IsChecked = false;
                        InstallStatusText.Text = string.Empty;

                        /*
                        The box goes up BEFORE the state is unfrozen, and the order is load-bearing.

                        MessageBox.Show pumps a nested message loop. TransitionToState(Connected_NoDatabase)
                        re-enables the form and puts a live "Install Now" back on screen, and it clears
                        Installing -- so with the transition first, the user could click Install Now while
                        this box was still standing, and the InstallInProgress backstop was already false and
                        would not stop them. That starts a SECOND install, which allocates its own
                        CancellationTokenSource -- and then this run unwinds into its finally, which used to
                        dispose and null _installCts unconditionally: the second install's token. It would
                        then be uncancellable by anything, including CLOSING THE DIALOG, which is the exact
                        hazard CancelInstallOnClose exists to prevent.

                        Modal first, while the form is still frozen. The transition happens after it is
                        dismissed, when nothing can be clicked underneath.
                        */
                        MessageBox.Show(
                            this,
                            "There is no existing PerformanceMonitor installation on this server to reinstall.\n\n" +
                            "Repair, Upgrade and Reinstall Objects restore or update the objects of an EXISTING " +
                            "installation; none of them creates one. Click Install Now to perform a fresh install instead.",
                            "Nothing to Reinstall",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);

                        TransitionToState(DialogState.Connected_NoDatabase);
                    });
                    return;
                }

                string? blockReason = GetInstallBlockReason(_installedVersion, appVersion);
                if (blockReason != null)
                {
                    await Dispatcher.InvokeAsync(() => BlockInstall(blockReason));
                    return;
                }

                /*
                Re-derive where a failure should land us from what we just READ, not from where we came
                from. Those differ whenever the server box was retyped: restoring the old state would
                render the old server's verdict over the new server's version -- "PerformanceMonitor v2.5.0
                is up to date" on a database four migrations behind, with Save enabled. It also keeps
                _preInstallState out of InstallComplete/MonitoringCredentials (reachable via the repair
                handoff), which RestoreAfterInstall would otherwise re-enter with Advanced Options unfrozen.
                */
                _preInstallState = ServerSetupPolicy.DeriveConnectedState(_installedVersion, appVersion);

                InstallStatusText.Text = "Preparing installation...";

                bool cleanInstall = CleanInstallCheckBox.IsChecked == true;

                /*
                Repair only means anything for an existing installation we are not about to drop.
                Resolving it to the version we repair FROM (rather than a bare bool) keeps the
                history write below from ever recording the old version after a clean install.
                */
                string? repairFromVersion = RepairCheckBox.IsChecked == true && !cleanInstall
                    ? _installedVersion
                    : null;

                bool resetSchedule = ResetScheduleCheckBox.IsChecked == true;
                bool runValidation = ValidationCheckBox.IsChecked == true;
                bool installDeps = InstallDepsCheckBox.IsChecked == true;

                var progress = new Progress<InstallationProgress>(p =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        /* Update progress bar */
                        if (p.ProgressPercent.HasValue)
                        {
                            InstallProgressBar.Value = p.ProgressPercent.Value;
                        }

                        /* Update status text */
                        if (!string.IsNullOrEmpty(p.Message))
                        {
                            InstallStatusText.Text = p.Message;
                        }

                        /* Append to log (filter out Debug messages) */
                        if (p.Status != "Debug")
                        {
                            AppendInstallLog(p.Message, p.Status);
                        }
                    });
                });

                /* Run upgrades if applicable (existing database, not clean install) */
                int upgradeSuccess = 0;
                int upgradeFailure = 0;

                if (repairFromVersion != null)
                {
                    /*
                    Repair reinstalls the schema objects (install scripts are idempotent) without
                    running migrations, so a hop that failed on a missing or damaged object can be
                    recovered without dropping the database. The pending upgrade runs afterwards.
                    */
                    AppendInstallLog(
                        "Repair mode: skipping upgrade scripts. Objects will be reinstalled at their current definitions; the pending upgrade still needs to run afterwards.",
                        "Warning");
                }
                else if (!cleanInstall && _installedVersion != null)
                {
                    /*
                    The sentinel parses, so interpolating it logged "Checking for upgrades from v0.0.0" --
                    stating the "I could not read this server's version" guess as a fact. Every other
                    consumer in this file guards it with IsVersionUnknown; this line did not.
                    */
                    AppendInstallLog(
                        RepairOutcome.IsVersionUnknown(_installedVersion)
                            ? $"This server's recorded version could not be read. Attempting every upgrade up to v{appVersion}..."
                            : $"Checking for upgrades from v{NormalizeVersion(_installedVersion)} to v{appVersion}...",
                        "Info");

                    var upgradeResult = await InstallationService.ExecuteAllUpgradesAsync(
                        provider,
                        installerConnStr,
                        _installedVersion,
                        appVersion,
                        progress,
                        cancellationToken);

                    upgradeSuccess = upgradeResult.totalSuccessCount;
                    upgradeFailure = upgradeResult.totalFailureCount;

                    if (upgradeResult.upgradeCount > 0)
                    {
                        AppendInstallLog($"Upgrades complete: {upgradeSuccess} succeeded, {upgradeFailure} failed", upgradeFailure == 0 ? "Success" : "Warning");
                    }

                    /*
                    Abort when an upgrade script fails, matching the CLI installer.
                    Reinstalling over a partially-upgraded database compounds the damage, and
                    recording a successful install at the target version would strand the failed
                    migration permanently: version detection reads the most recent SUCCESS row, so
                    the hop would never be offered again. Writing no history row leaves the server
                    at its current version, and upgrade scripts are idempotent, so re-running after
                    fixing the error resumes cleanly.
                    */
                    if (upgradeFailure > 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            InstallProgressBar.Value = 0;
                            AppendInstallLog(
                                $"Installation aborted: {upgradeFailure} upgrade script(s) failed. Upgrade scripts must succeed before installation can proceed.",
                                "Error");
                            AppendInstallLog(
                                "Fix the errors above and run the upgrade again. The server remains at its current version.",
                                "Info");
                            AppendInstallLog(
                                "If the failure is a missing or damaged object, tick 'Repair' in Advanced Options to reinstall the schema objects without running migrations, then run the upgrade again.",
                                "Info");
                            RestoreAfterInstall($"Upgrade aborted: {upgradeFailure} upgrade script(s) failed.");
                        });

                        return;
                    }
                }

                /*
                A repair reinstalls objects without running migrations, so it must NOT record the
                target version -- that would strand every pending hop, which is exactly the bug the
                abort above exists to prevent.

                So a repair writes NO history row at all. It does not change the version, and
                installation_history is the version ledger -- writing back a version we merely READ is
                how a guess becomes a fact. Concretely: GetInstalledVersionAsync returns the unknown sentinel as a
                #538 fallback when the database exists but has no SUCCESS row, meaning "unknown, try
                every upgrade". Echoing that back as a SUCCESS row would persist the guess as truth.
                Writing nothing leaves the previous row as the version of record, which is exactly
                right -- the pending upgrade is still offered afterwards.
                */
                bool isRepair = repairFromVersion != null;

                /* Run main installation */
                AppendInstallLog("Starting main installation...", "Info");

                Func<System.Threading.Tasks.Task>? preValidationAction = null;
                if (installDeps)
                {
                    preValidationAction = async () =>
                    {
                        AppendInstallLog("Installing community dependencies...", "Info");
                        string communityDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "community");
                        using var depInstaller = new DependencyInstaller(communityDir);
                        await depInstaller.InstallDependenciesAsync(installerConnStr, progress, cancellationToken);
                    };
                }

                if (cleanInstall)
                {
                    /*
                    A clean install DROPS the database. From here the version we read is about to stop being
                    true, so a cancel or a failure must not restore a verdict describing it -- that renders
                    "PerformanceMonitor v3.0.0 is installed" over a server whose database no longer exists,
                    with Save enabled. Forget the verdict; RestoreAfterInstall will say "status unknown".
                    */
                    _preInstallState = null;
                    _verdictServerName = null;

                    /*
                    Remember WHY we forgot it. Both causes of a null _preInstallState land in the same
                    branch of RestoreAfterInstall, which told every one of them "this server's installed
                    version was not read" -- which on THIS path is simply false. It was read; we discarded
                    it on purpose, because we are about to drop the database out from under it.
                    */
                    _verdictDiscardedByCleanInstall = true;
                }

                _installResult = await InstallationService.ExecuteInstallationAsync(
                    installerConnStr,
                    provider,
                    cleanInstall,
                    resetSchedule,
                    progress,
                    preValidationAction,
                    cancellationToken);

                /*
                A clean install that failed is not "completed with N error(s)". CleanInstallAsync drops the
                Agent jobs, the XE sessions and then the DATABASE before a single install file runs -- so if
                it failed, the database may be gone, half-gone, or stuck in SINGLE_USER, and we do not know
                which. ExecuteInstallationAsync returns early in that case with no files attempted.

                Route it exactly where a cancel goes: no history row, no verdict, the log left on screen, and
                install blocked until the server is re-checked. The completion path below would instead have
                announced a completion, restored the discarded verdict, and armed a Save that skips the
                connection test -- on a server whose database we may have just destroyed.
                */
                if (cleanInstall && !_installResult.Success && _installResult.FilesSucceeded == 0)
                {
                    string cleanInstallError =
                        _installResult.Errors.Count > 0
                            ? _installResult.Errors[0].ErrorMessage
                            : "the database could not be recreated.";

                    await Dispatcher.InvokeAsync(() =>
                        RestoreAfterInstall($"Clean install failed: {cleanInstallError}"));
                    return;
                }

                /* Log installation history -- but never for a repair, which changes no version (see above). */
                if (isRepair)
                {
                    AppendInstallLog(
                        "Repair does not change the recorded version, so no installation history row was written.",
                        "Info");
                }
                else
                {
                    try
                    {
                        AppendInstallLog("Recording installation history...", "Info");

                        /*
                        Fold the upgrade script counts in. InstallationResult only covers the install
                        files, so passing it alone would under-report files_executed and could record a
                        SUCCESS at the target version even when a migration had failed.
                        */
                        await InstallationService.LogInstallationHistoryAsync(
                            installerConnStr,
                            appVersion,
                            appVersion,
                            _installResult.StartTime,
                            upgradeSuccess + _installResult.FilesSucceeded,
                            upgradeFailure + _installResult.FilesFailed,
                            upgradeFailure == 0 && _installResult.Success,
                            progress);
                        AppendInstallLog("Installation history recorded", "Success");
                    }
                    catch (Exception ex)
                    {
                        AppendInstallLog($"Could not record installation history: {ex.Message}", "Warning");
                    }
                }

                /* Run validation if requested */
                if (runValidation && _installResult.Success)
                {
                    try
                    {
                        AppendInstallLog("Running post-install validation...", "Info");
                        var (collectorsSucceeded, collectorsFailed) = await InstallationService.RunValidationAsync(
                            installerConnStr,
                            progress,
                            cancellationToken);
                        AppendInstallLog($"Validation: {collectorsSucceeded} collectors succeeded, {collectorsFailed} failed",
                            collectorsFailed == 0 ? "Success" : "Warning");
                    }
                    catch (Exception ex)
                    {
                        AppendInstallLog($"Validation failed: {ex.Message}", "Warning");
                    }
                }

                /* Generate summary report */
                try
                {
                    /* This parameter is printed as "Installer Version" -- it names the BINARY that ran, not the database. */
                    /*
                    installTimeServerName, not the live box. This was the last read-the-box-after-the-await
                    in the file: the report names the server the install actually went to, and it is written
                    after every await in the run. Un-exploitable today only because the box is disabled for
                    the duration -- which is enablement standing in for an invariant, and the report would
                    have carried the wrong server's name the moment that stopped being true.
                    */
                    _reportPath = InstallationService.GenerateSummaryReport(
                        installTimeServerName.Trim(),
                        _coreServerInfo?.SqlServerVersion ?? "",
                        _coreServerInfo?.SqlServerEdition ?? "",
                        appVersion,
                        _installResult);
                    AppendInstallLog($"Report saved: {_reportPath}", "Info");
                }
                catch (Exception ex)
                {
                    AppendInstallLog($"Could not generate report: {ex.Message}", "Warning");
                }

                /*
                Did a CRITICAL file (01_/02_/03_) fail? ExecuteInstallationAsync aborts the whole pass on
                those, so the repair reinstalled almost nothing. That is NOT the expected Msg 207 outcome
                below, and must not be dressed up as one.
                */
                bool criticalFileFailed = _installResult.Errors.Any(e => Patterns.IsCriticalFile(e.FileName));

                /* Shared with the CLI so the two cannot drift -- see RepairOutcome. */
                /*
                THREE different questions, and they disagree for the unknown sentinel. Answering all of
                them with one flag told the operator "already at the current version, so there is no
                upgrade to apply" for a server with every hop pending, and withheld the button that would
                have applied them.
                */
                bool failuresExcused = RepairOutcome.FailuresAreExpected(
                    isRepair, repairFromVersion, appVersion, criticalFileFailed);
                bool hasPendingUpgrade = RepairOutcome.HasPendingUpgrade(repairFromVersion, appVersion);
                bool versionUnknown = RepairOutcome.IsVersionUnknown(repairFromVersion);

                /* The repair ran and was not aborted by a critical file, so its handoff is still owed. */
                bool repairCompleted = isRepair && !criticalFileFailed;

                /* Update final status */
                await Dispatcher.InvokeAsync(() =>
                {
                    InstallProgressBar.Value = 100;
                    if (repairCompleted)
                    {
                        /*
                        A repair on a database with pending migrations is EXPECTED to report file errors,
                        and that is not a failed repair. The install scripts compile against the CURRENT
                        schema -- e.g. install/23_process_blocked_process_xml.sql reads
                        collect.blocking_BlockedProcessReport.monitor_loop, a column the 3.0.0-to-3.1.0
                        migration adds -- and ALTER PROCEDURE binds columns at compile time, so those
                        procedures cannot compile until the upgrade runs (Msg 207, "Invalid column name").
                        A failed CREATE OR ALTER leaves the old body intact, so nothing is damaged; the
                        upgrade's own install pass recompiles them once the schema is current.

                        So the completion block is NOT gated on _installResult.Success: gating it would
                        leave the user staring at "completed with N error(s)" and no next step, which is
                        how the half-migrated database this feature exists to escape gets left behind.
                        */
                        InstallStatusText.Text =
                            _installResult.Success ? "Repair completed."
                            : failuresExcused ? $"Repair completed with {_installResult.FilesFailed} expected error(s)."
                            : $"Repair completed with {_installResult.FilesFailed} error(s).";

                        if (versionUnknown)
                        {
                            AppendInstallLog(
                                "Repair complete. No version was recorded, and this server's recorded version could not be read -- " +
                                "it is unknown which migrations have run. Click Upgrade Now to attempt every upgrade, then re-check.",
                                "Warning");
                        }
                        else if (hasPendingUpgrade)
                        {
                            AppendInstallLog(
                                $"Repair complete. The server is still at v{NormalizeVersion(repairFromVersion!)} and no version was recorded -- click Upgrade Now to apply the pending migrations.",
                                "Success");

                            if (!_installResult.Success && failuresExcused)
                            {
                                AppendInstallLog(
                                    $"{_installResult.FilesFailed} object(s) could not be compiled because the pending upgrade has not run yet. " +
                                    "This is expected -- they reference columns the upgrade adds, and the upgrade will recompile them.",
                                    "Info");
                            }
                        }
                        else
                        {
                            AppendInstallLog(
                                "Repair complete. This server was already at the current version, so no version was recorded and there is no upgrade to apply.",
                                "Success");
                        }
                    }
                    else if (_installResult.Success)
                    {
                        InstallStatusText.Text = "Installation completed successfully!";
                        AppendInstallLog("Installation completed successfully!", "Success");
                    }
                    else
                    {
                        InstallStatusText.Text = $"Installation completed with {_installResult.FilesFailed} error(s).";
                        AppendInstallLog($"Installation completed with {_installResult.FilesFailed} error(s).", "Error");
                    }

                    /*
                    Re-publish the four facts for the server the run actually went to. A clean install forgets
                    the verdict on the way in (it is about to DROP the database, so a cancel must not restore
                    a verdict describing it) -- and if the run SUCCEEDED we now know exactly which server this
                    screen describes: the one we just wrote to. Without this the stamp stays null and
                    everything keyed on it treats a completed clean install as un-addressed: Save re-runs the
                    connection test (a second interactive MFA prompt on Entra), and the first keystroke in the
                    server box withdraws a success message that was perfectly true.

                    GATED on success, which it was not. The comment used to assert "but the run SUCCEEDED"
                    while the code never checked -- so a clean install that FAILED restored the very verdict
                    it had thrown away for safety, over a database DROP DATABASE may have already taken. And
                    skipConnectionTest keys on this stamp: Save would then have persisted that server without
                    ever reconnecting to it. A failed run knows nothing. Leave the stamp null and make Save
                    prove the connection.

                    A PUBLISH, not a lone re-stamp. Moving the stamp by itself put it back while
                    _installedVersion still held the version read BEFORE the run -- the pre-drop version, on
                    the clean-install path -- so VerdictMatchesServerBox() went true over a half-replaced fact
                    set, which is the one shape the guard structurally cannot catch. It is unexploitable today
                    only because nothing reads _installedVersion from InstallComplete, and reader-absence
                    standing in for an invariant is exactly the substitution that produced the worst bug in
                    this file. The database now holds appVersion -- except after a repair, which changes no
                    version at all.
                    */
                    if (_installResult.Success && _coreServerInfo != null)
                    {
                        PublishProbedServer(
                            _coreServerInfo,
                            _serverVersion ?? string.Empty,
                            installTimeServerName,
                            isRepair ? _installedVersion : appVersion);
                    }

                    TransitionToState(DialogState.InstallComplete);

                    /*
                    A successful repair leaves the migrations UNAPPLIED by design, so the user has to run
                    the upgrade next. InstallComplete collapses DatabaseStatusPanel, which is the Border
                    InstallUpgradeButton lives in -- so without re-showing it, the completion message
                    points at a button that is not on screen and the obvious next click is Save & Connect,
                    leaving exactly the half-migrated database this feature exists to escape.

                    MonitoringCredentials is included: InstallComplete chains straight into it for SQL
                    auth, and that state does not re-show the panel either -- so without it, every SQL-auth
                    user (the ones most likely to hit this) would get the dead end. The three panels live in
                    separate grid rows and coexist fine.

                    Not gated on _installResult.Success, for the reason above: a repair with the expected
                    compile errors still needs the handoff. It IS gated on no critical file having failed,
                    because that repair reinstalled nothing -- and on there actually being an upgrade to
                    hand off TO, which for the unknown sentinel means yes (every hop may be pending).
                    */
                    if (repairCompleted && hasPendingUpgrade &&
                        (_currentState == DialogState.InstallComplete ||
                         _currentState == DialogState.MonitoringCredentials))
                    {
                        _handoffStatusText = versionUnknown
                            ? "Objects were reinstalled. This server's recorded version could not be read, so it is " +
                              "unknown which migrations have run. Click Upgrade Now to attempt every upgrade."
                            : $"Objects were reinstalled. PerformanceMonitor is still at v{NormalizeVersion(repairFromVersion!)} " +
                              "-- the pending upgrade has not been applied. Click Upgrade Now to apply it.";

                        /* Addressed by construction: the box was frozen for the run and just re-stamped. */
                        RenderRepairHandoff(addressed: true);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    AppendInstallLog("Installation was cancelled by user.", "Warning");
                    InstallProgressBar.Value = 0;
                    RestoreAfterInstall("Installation cancelled.");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    AppendInstallLog($"Fatal error: {ex.Message}", "Error");
                    RestoreAfterInstall($"Installation failed: {ex.Message}");
                });
            }
            finally
            {
                /* Only clear the field if it still points at OUR token -- see where it was created. */
                if (ReferenceEquals(_installCts, installCts))
                {
                    _installCts = null;
                }

                installCts.Dispose();
            }
        }

        private void CancelInstall_Click(object sender, RoutedEventArgs e)
        {
            _installCts?.Cancel();
            CancelInstallButton.IsEnabled = false;
            InstallStatusText.Text = "Cancelling...";
        }

        /* A clean install drops and recreates the database, so there is nothing left to repair. */
        private void CleanInstallCheckBox_Checked(object sender, RoutedEventArgs e) =>
            RepairCheckBox.IsChecked = false;

        /* Repair is the non-destructive recovery path, so it cannot mean "drop the database". */
        private void RepairCheckBox_Checked(object sender, RoutedEventArgs e) =>
            CleanInstallCheckBox.IsChecked = false;

        private void AppendInstallLog(string message, string status)
        {
            if (string.IsNullOrEmpty(message))
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendInstallLog(message, status));
                return;
            }

            string prefix = status switch
            {
                "Success" => "[OK] ",
                "Error" => "[ERROR] ",
                "Warning" => "[WARN] ",
                _ => ""
            };

            InstallLogTextBox.AppendText($"{prefix}{message}\n");
            InstallLogTextBox.ScrollToEnd();
        }

        private void SkipInstall_Click(object sender, MouseButtonEventArgs e)
        {
            /*
            Supersede any detection in flight. "Skip, just add server" is live while one is running, and
            this method writes _currentState directly rather than transitioning -- so the detection landed
            afterwards, published its verdict, and put the install panel and the install button straight
            back on screen, silently undoing the skip the user had just chosen.

            Clearing StatusText is part of superseding, not decoration: the detection painted "Checking
            database status..." before it awaited, and its Superseded() early-outs return without clearing
            it. Every other epoch-bumper does this; this one didn't, so the label sat there for the rest of
            the dialog's life announcing a check nobody was running.
            */
            _detectEpoch++;
            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;

            /* This bypasses TransitionToState, so it is the one exit that never cleared these. */
            RepairCheckBox.IsChecked = false;
            CleanInstallCheckBox.IsChecked = false;
            ResetScheduleCheckBox.IsChecked = false;

            DatabaseStatusPanel.Visibility = Visibility.Collapsed;
            InstallationPanel.Visibility = Visibility.Collapsed;
            SaveButton.IsEnabled = true;
            SaveButton.Content = "Save";
            _currentState = DialogState.Initial;
        }

        private void ViewReport_Click(object sender, RoutedEventArgs e)
        {
            if (_reportPath != null && File.Exists(_reportPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _reportPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        $"Could not open report: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }
            }
        }

        private void UseSameCredsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (MonitorCredsFieldsPanel != null)
            {
                MonitorCredsFieldsPanel.Visibility = UseSameCredsCheckBox.IsChecked == true
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        private void SetFormEnabled(bool enabled)
        {
            ServerNameTextBox.IsEnabled = enabled;
            DisplayNameTextBox.IsEnabled = enabled;
            WindowsAuthRadio.IsEnabled = enabled;
            SqlAuthRadio.IsEnabled = enabled;
            EntraMfaAuthRadio.IsEnabled = enabled;
            ServicePrincipalAuthRadio.IsEnabled = enabled;
            ManagedIdentityAuthRadio.IsEnabled = enabled;
            UsernameTextBox.IsEnabled = enabled;
            PasswordBox.IsEnabled = enabled;
            EntraMfaUsernameBox.IsEnabled = enabled;
            ServicePrincipalClientIdBox.IsEnabled = enabled;
            ServicePrincipalSecretBox.IsEnabled = enabled;
            ManagedIdentityClientIdBox.IsEnabled = enabled;
            EncryptModeComboBox.IsEnabled = enabled;
            TrustServerCertificateCheckBox.IsEnabled = enabled;
            ReadOnlyIntentCheckBox.IsEnabled = enabled;
            MultiSubnetFailoverCheckBox.IsEnabled = enabled;
            IsFavoriteCheckBox.IsEnabled = enabled;
            MonthlyCostTextBox.IsEnabled = enabled;
            AlertDeliveryOverrideComboBox.IsEnabled = enabled;
            DescriptionTextBox.IsEnabled = enabled;
            TestConnectionButton.IsEnabled = enabled;
            /*
            Check for Updates re-runs DetectDatabaseStatusAsync, which transitions back to a Connected_*
            state -- re-showing the install button and collapsing the running install's progress panel.
            Leaving it live during Installing let a second click start a concurrent install against the
            same database, clobbering _installResult and leaving Cancel pointing at the wrong run.
            */
            CheckForUpdatesButton.IsEnabled = enabled;
            SaveButton.IsEnabled = enabled;
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            /*
            Save is re-enterable. RunConnectionTestAsync(SaveButton) disables Save for the duration -- but
            Test Connection stays live, and ITS finally re-enables Save (it re-enables the form wholesale,
            not just the button it took). Click Save, click Test Connection, click Save again: both tests
            complete, the first sets DialogResult and closes the window, and the second assigns DialogResult
            to a CLOSED window, which throws.

            It does NOT take the app down -- App.OnDispatcherUnhandledException handles it (App.xaml.cs:173)
            -- so an earlier version of this comment overstated it. What the user gets is an alarming
            "An error occurred" box and a server that was silently not saved. Pre-existing; the epoch guard
            below happens to close every variant where a detect or install intervenes, but not this one.
            (The close-during-save variant is handled separately, by _dialogClosed.)
            */
            if (_saveInProgress) return;
            _saveInProgress = true;

            try
            {
                await SaveAsync();
            }
            finally
            {
                _saveInProgress = false;
            }
        }

        private bool _saveInProgress;

        private async Task SaveAsync()
        {
            if (!ValidateInputs()) return;

            /*
            "We just installed, so the connection is proven" is only true if the box still names the server
            we installed to. Keyed on _currentState alone, retyping the box after an install skipped the
            test entirely: the new name was persisted un-contacted, and under SQL auth the INSTALLER's
            credentials -- routinely sa -- were saved as the ongoing monitoring account for a server nobody
            had ever connected to. The stamp is what makes the claim true, so test the stamp.
            */
            bool skipConnectionTest =
                (_currentState == DialogState.InstallComplete ||
                 _currentState == DialogState.MonitoringCredentials) &&
                VerdictMatchesServerBox();

            if (!skipConnectionTest)
            {
                /*
                An install can start AND FINISH while this connection test is in flight (InstallUpgradeButton
                stays hit-testable -- SetFormEnabled does not touch it). InstallInProgress alone is the
                half of that test that un-fires: it is only read when this continuation resumes, so a run
                that completed in the meantime reads as "no install", and we fall through to DialogResult
                and Close() -- destroying the dialog the instant the install lands, before the SQL-auth user
                is ever shown the monitoring-credentials panel, and persisting the installer account in its
                place. The epoch cannot un-fire; InstallOrUpgrade_Click bumps it on every start.

                It also covers the retype: TextChanged bumps the epoch too, so a box edited while this test
                was in flight abandons the save rather than persisting the new name on the strength of a
                connection proven against the old one.
                */
                int epoch = _detectEpoch;

                var (connected, errorMessage, mfaCancelled) = await RunConnectionTestAsync(SaveButton);

                if (InstallInProgress) return;

                /*
                The dialog was CLOSED while this test was running -- ESC, Cancel, the X. Bail here, before
                anything is written, because in edit mode ServerConnection is not a copy: it is the very
                object ServerManager holds in its list (MainWindow hands us item.Server directly). The
                assignments below mutate it IN PLACE, so a save the user cancelled still renames their
                monitored server -- and it reaches disk on its own, the next time UpdateLastConnected fires
                for ANY server. PROD01 quietly becomes TEST99, keeps PROD01's id and credential, and PROD01
                stops being monitored.

                This guard used to sit 47 lines lower, next to the DialogResult write. That stopped the
                THROW and not the WRITE -- and the throw was the only thing the user ever saw, so moving it
                down there turned a loud bug into a silent one. Guard before you commit the facts; it is the
                same rule the detection path already follows.

                One check covers everything: RunConnectionTestAsync is the only await in this method (the
                skipConnectionTest path has none), so this is the only window in which _dialogClosed can
                flip. It also kills the orphaned "Do you still want to save this connection?" box below,
                which was popping up with no dialog left behind it.
                */
                if (_dialogClosed) return;

                if (epoch != _detectEpoch)
                {
                    /*
                    SAY SO. An earlier version of this comment claimed every epoch bump has a visible cause,
                    so the abandoned save could not be silent. That was wrong: from Initial -- a fresh dialog,
                    which is the most common way to reach Save -- a retype renders nothing at all, and this
                    return then swallowed the click. The user fixes a typo while the 10-second connection test
                    is running, clicks nothing else, and believes the server was added.

                    Worded for all four bumpers, not just the retype. Check for Updates and "Skip, just add
                    server" bump the epoch too, and blaming "the server details changed" for a change the
                    user never made is its own small lie.
                    */
                    StatusText.Text =
                        "Nothing was saved -- something changed while the connection was being tested. " +
                        "Click Save again.";
                    StatusText.Visibility = Visibility.Visible;
                    return;
                }

                if (!connected)
                {
                    if (mfaCancelled)
                    {
                        MessageBox.Show(
                            this,
                            "Authentication was cancelled. Click Save to try again, or Cancel to abort.",
                            "Authentication Cancelled",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                        return;
                    }

                    var detail = errorMessage != null ? $"\n\nError: {errorMessage}" : string.Empty;
                    var result = MessageBox.Show(
                        this,
                        $"Could not connect to {ServerNameTextBox.Text}.{detail}\n\n" +
                        "Do you still want to save this connection?",
                        "Connection Failed",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning
                    );

                    if (result != MessageBoxResult.Yes)
                        return;
                }
            }

            // Determine authentication type and credentials
            string authenticationType;
            string? azureClientId = null;
            string? managedIdentityClientId = null;
            if (WindowsAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.Windows;
                Username = null;
                Password = null;
            }
            else if (EntraMfaAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.EntraMFA;
                Username = EntraMfaUsernameBox.Text.Trim();
                Password = null;
            }
            else if (ServicePrincipalAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.ServicePrincipal;
                // Surface the client id as the Username and the secret as the Password so the existing
                // ServerManager credential-save path persists them (client id + client secret).
                azureClientId = ServicePrincipalClientIdBox.Text.Trim();
                Username = azureClientId;
                Password = ServicePrincipalSecretBox.Password;
            }
            else if (ManagedIdentityAuthRadio.IsChecked == true)
            {
                authenticationType = AuthenticationTypes.ManagedIdentity;
                managedIdentityClientId = string.IsNullOrWhiteSpace(ManagedIdentityClientIdBox.Text)
                    ? null
                    : ManagedIdentityClientIdBox.Text.Trim();
                // No credential to store for managed identity.
                Username = null;
                Password = null;
            }
            else
            {
                authenticationType = AuthenticationTypes.SqlServer;

                /*
                Use the monitoring credentials if the user actually entered them.

                Deliberately NOT gated on _currentState: the repair handoff puts a live "Upgrade Now"
                button in front of MonitoringCredentials, which used to be terminal. Any exit from the
                install that button starts (abort, cancel, fatal, version block) transitions away, and
                the typed monitoring login would then be silently discarded and the INSTALLER account --
                typically sysadmin -- persisted as the ongoing monitoring credential instead. Keying on
                what was entered rather than on a transient state cannot drift that way.
                */
                if (UseSameCredsCheckBox.IsChecked == false &&
                    !string.IsNullOrWhiteSpace(MonitorUsernameTextBox.Text))
                {
                    Username = MonitorUsernameTextBox.Text.Trim();
                    Password = MonitorPasswordBox.Password;
                }
                else
                {
                    Username = UsernameTextBox.Text.Trim();
                    Password = PasswordBox.Password;
                }
            }

            // Use server name as display name if not provided
            var displayName = string.IsNullOrWhiteSpace(DisplayNameTextBox.Text)
                ? ServerNameTextBox.Text.Trim()
                : DisplayNameTextBox.Text.Trim();

            /*
            THE guard, immediately before the commit, with nothing between.

            There is one further up, straight after the await, and it is not enough -- because "await is the
            only place this method yields" is FALSE. MessageBox.Show pumps a nested Win32 message loop, and
            the "Do you still want to save this connection?" box above sits between that guard and these
            assignments.

            Every box in this file now passes `this` as its owner, so Win32 disables the dialog while one is
            up -- which is what closes the concrete route. This guard stays anyway, and not out of caution:
            a dispatcher work item or an application shutdown can still close a window during a nested pump,
            and an already-closed window has no HWND to own the next box with. The whole reason the previous
            fix was wrong is that it reasoned about which re-entrancy windows exist instead of guarding where
            the damage happens. Do not repeat that by deleting this.

            And these are not writes to a copy. In edit mode ServerConnection IS the object ServerManager
            holds -- MainWindow hands us item.Server -- so they rename the user's live, monitored server in
            place, and it reaches disk on its own the next time UpdateLastConnected fires for anyone.

            The previous fix put the guard after the await and reasoned that nothing could re-enter before
            the commit. That reasoning leaned on a modal box happening to disable the window, which is the
            same "unreachable because something else happens to be disabled" substitution this file has been
            bitten by over and over. Do not reason about re-entrancy windows. Guard where the damage is.
            */
            if (_dialogClosed) return;

            if (_isEditMode)
            {
                ServerConnection.DisplayName = displayName;
                ServerConnection.ServerName = ServerNameTextBox.Text.Trim();
                ServerConnection.AuthenticationType = authenticationType;
                ServerConnection.AzureClientId = azureClientId;
                ServerConnection.ManagedIdentityClientId = managedIdentityClientId;
                ServerConnection.Description = DescriptionTextBox.Text.Trim();
                ServerConnection.IsFavorite = IsFavoriteCheckBox.IsChecked == true;
                ServerConnection.EncryptMode = GetSelectedEncryptMode();
                ServerConnection.TrustServerCertificate = TrustServerCertificateCheckBox.IsChecked == true;
                ServerConnection.ReadOnlyIntent = ReadOnlyIntentCheckBox.IsChecked == true;
                ServerConnection.MultiSubnetFailover = MultiSubnetFailoverCheckBox.IsChecked == true;
                if (decimal.TryParse(MonthlyCostTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var editCost) && editCost >= 0)
                    ServerConnection.MonthlyCostUsd = editCost;
                ServerConnection.AlertDeliveryModeOverride = GetSelectedDeliveryOverride();
            }
            else
            {
                decimal monthlyCost = 0m;
                if (decimal.TryParse(MonthlyCostTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var newCost) && newCost >= 0)
                    monthlyCost = newCost;

                ServerConnection = new ServerConnection
                {
                    DisplayName = displayName,
                    ServerName = ServerNameTextBox.Text.Trim(),
                    AuthenticationType = authenticationType,
                    AzureClientId = azureClientId,
                    ManagedIdentityClientId = managedIdentityClientId,
                    Description = DescriptionTextBox.Text.Trim(),
                    IsFavorite = IsFavoriteCheckBox.IsChecked == true,
                    CreatedDate = DateTime.Now,
                    LastConnected = DateTime.Now,
                    EncryptMode = GetSelectedEncryptMode(),
                    TrustServerCertificate = TrustServerCertificateCheckBox.IsChecked == true,
                    ReadOnlyIntent = ReadOnlyIntentCheckBox.IsChecked == true,
                    MultiSubnetFailover = MultiSubnetFailoverCheckBox.IsChecked == true,
                    MonthlyCostUsd = monthlyCost,
                    AlertDeliveryModeOverride = GetSelectedDeliveryOverride()
                };
            }

            /*
            The user closed the dialog while this save's connection test was still running -- ESC, Cancel,
            the X, Alt+F4. The window is gone; assigning DialogResult to it throws. Nothing to save to.
            */
            if (_dialogClosed) return;

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _installCts?.Cancel();
            DialogResult = false;
            Close();
        }

    }
}
