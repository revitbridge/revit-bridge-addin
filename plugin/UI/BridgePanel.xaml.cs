using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Core;
using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace revit_mcp_plugin.UI
{
    /// <summary>
    /// <para>Revit Bridge 面板（非模态，归属 Revit 主窗口，同一时间只开一个）：开关与实时状态、
    /// "允许接入我的工具"、"启动 Revit 时自动开启"、连接（本机 / 云端、配对、端口）、命令列表。
    /// 在 Revit UI 线程上运行，但不在 API 上下文中：开关经 BridgeController.Post 交给外部事件。</para>
    /// <para>The Revit Bridge panel (modeless, owned by Revit's main window, one at a time):
    /// switch and live status, "Allow my tools", "Turn the bridge on when Revit starts",
    /// connection (local / remote, pairing, port) and the command list. It runs on the Revit
    /// UI thread but outside an API context, so the switch goes through BridgeController.Post.</para>
    /// </summary>
    public partial class BridgePanel : Window
    {
        private static BridgePanel _open;

        private readonly DispatcherTimer _statusTimer;
        private bool _loading;
        private CommandSetSettingsPage _commandSetPage;
        // A reload of the commands this panel asked for and is waiting on (0: none).
        private int _reloadTicket;

        /// <summary>Open the panel, or bring the open one to the front. Revit UI thread.</summary>
        /// <param name="owner">Revit's main window handle (UIApplication.MainWindowHandle).</param>
        public static void ShowPanel(IntPtr owner)
        {
            if (_open != null)
            {
                if (_open.WindowState == WindowState.Minimized)
                    _open.WindowState = WindowState.Normal;
                _open.RefreshStatus();
                _open.Activate();
                return;
            }

            var panel = new BridgePanel();
            // Revit's own main window as the owner (as the Settings window before it): an owned
            // window is destroyed with its owner, and Process.MainWindowHandle can be a ribbon popup.
            _ = new WindowInteropHelper(panel) { Owner = owner };
            panel.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            panel.Closed += (_, _) =>
            {
                if (_open == panel)
                    _open = null;
            };
            _open = panel;
            panel.Show();
        }

        public BridgePanel()
        {
            InitializeComponent();
            ConnectionFrame.Navigate(new ConnectionSettingsPage());
            string error;
            ShowSettings(SettingsStore.LoadOrDefaults(out error).Settings, error);
            RefreshStatus();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += (_, _) => RefreshStatus();
            Loaded += (_, _) => _statusTimer.Start();

            // Saving a mode change (Connection) or pairing moves the default of an unset
            // "Allow my tools": show the new effective value at once, and why.
            Action onSettingsReloaded = () => ShowSettings(BridgeController.Settings, BridgeController.SettingsError);
            BridgeController.SettingsReloaded += onSettingsReloaded;
            Closed += (_, _) =>
            {
                _statusTimer.Stop();
                BridgeController.SettingsReloaded -= onSettingsReloaded;
            };
        }

        private void ShowSettings(ServiceSettings settings, string error)
        {
            _loading = true;
            try
            {
                MyToolsCheckBox.IsChecked = settings.LocalToolsEnabled;
                AutoStartCheckBox.IsChecked = settings.AutoStart;
                bool remote = string.Equals(settings.Mode, "websocket", StringComparison.OrdinalIgnoreCase);
                MyToolsDefaultText.Text = settings.LocalToolsIsDefault
                    ? $"Not set yet, so it follows the default for {(remote ? "remote" : "local")} mode: " +
                      $"{(settings.LocalToolsEnabled ? "on" : "off")}. Ticking or unticking it sets it for good."
                    : "Set by you; it no longer follows the connection mode.";
                if (error != null)
                    ShowSettingsMessage(error + ". The defaults are shown (local mode, Allow my tools on as the local default, " +
                                        "no auto start). Repair the file or re-run the installer; until then changes here cannot be saved.",
                                        isError: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"revit-bridge panel: could not show the settings: {ex.Message}");
            }
            finally
            {
                _loading = false;
            }
        }

        private void RefreshStatus()
        {
            try
            {
                BridgeStatus status = BridgeController.CurrentStatus();
                StatusText.Text = BridgeStatusText.StatusLine(status) +
                                  (BridgeController.HasPendingWork ? " Waiting for Revit to be idle..." : "");
                SwitchButton.Content = status.IsOn ? "Turn off" : "Turn on";
                SwitchButton.IsEnabled = BridgeController.CanPost;
                ReportReloadWhenDone();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"revit-bridge panel: status failed: {ex.Message}");
            }
        }

        private void SwitchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool on = BridgeController.IsOn;
                if (BridgeController.Post(on ? BridgeRequest.TurnOff : BridgeRequest.TurnOn))
                    StatusText.Text = on ? "Turning off..." : "Turning on...";
                else
                    StatusText.Text = "Revit did not take the request now (it may be busy, for example in a dialog). " +
                                      "Try again, or use the Revit Bridge button on the ribbon.";
            }
            catch (Exception ex)
            {
                ReportUnexpected("Could not switch the bridge", ex);
            }
        }

        private void MyToolsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool allowed = MyToolsCheckBox.IsChecked == true;
            try
            {
                SettingsStore.UpdateSettings(s => s.LocalTools = allowed);
            }
            catch (Exception ex)
            {
                SetQuietly(() => MyToolsCheckBox.IsChecked = !allowed);
                ShowSettingsMessage("Could not save 'Allow my tools': " + ex.Message, isError: true);
                return;
            }

            try
            {
                int ticket = BridgeController.SetUserToolsAllowed(allowed);
                _reloadTicket = 0;
                if (!allowed)
                {
                    // Refused at once by the services, whatever happens to the reload.
                    ShowSettingsMessage("Allow my tools is off: requests for your own tools are refused from now on. " +
                                        "Tools already loaded stay in memory until Revit restarts." +
                                        (ticket < 0 ? " (Revit did not take the request to update the command list now; " +
                                                      "turn the bridge off and on to update it.)" : ""),
                                        isError: false);
                }
                else if (ticket == 0)
                {
                    ShowSettingsMessage("Allow my tools is on. Your tools load the next time the bridge is turned on.", isError: false);
                }
                else if (ticket < 0)
                {
                    ShowSettingsMessage("Allow my tools is saved as on, but Revit did not take the request to load your tools now " +
                                        "(it may be busy, for example in a dialog). Turn the bridge off and on to load them.",
                                        isError: true);
                }
                else
                {
                    // Claimed only when Revit has actually reloaded (see ReportReloadWhenDone).
                    _reloadTicket = ticket;
                    ShowSettingsMessage("Allow my tools is on. Loading your tools...", isError: false);
                }
            }
            catch (Exception ex)
            {
                ReportUnexpected("Could not apply 'Allow my tools'", ex);
            }
        }

        // Called every second: says what the reload this panel asked for actually did.
        private void ReportReloadWhenDone()
        {
            if (_reloadTicket <= 0)
                return;
            string error;
            if (!BridgeController.TryGetReloadResult(_reloadTicket, out error))
                return;
            _reloadTicket = 0;

            if (error != null)
            {
                ShowSettingsMessage("Allow my tools is saved as on, but the commands could not be reloaded (" + error + "). " +
                                    "Turn the bridge off and on to load your tools.", isError: true);
                return;
            }

            var loaded = UserToolAccess.LoadedUserMethods;
            ShowSettingsMessage(loaded.Count > 0
                ? "Allow my tools is on. Your tools are loaded: " + string.Join(", ", loaded) + "."
                : "Allow my tools is on, but none of your tools is enabled. Tick them under Advanced: commands, " +
                  "save, then turn the bridge off and on.",
                isError: false);
        }

        private void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool autoStart = AutoStartCheckBox.IsChecked == true;
            try
            {
                SettingsStore.UpdateSettings(s => s.AutoStart = autoStart);
                BridgeController.SettingsChanged();
                ShowSettingsMessage(autoStart
                    ? "The bridge will turn on when Revit starts."
                    : "The bridge stays off when Revit starts until you turn it on.",
                    isError: false);
            }
            catch (Exception ex)
            {
                SetQuietly(() => AutoStartCheckBox.IsChecked = !autoStart);
                ShowSettingsMessage("Could not save 'Turn the bridge on when Revit starts': " + ex.Message, isError: true);
            }
        }

        private void CommandSetExpander_Expanded(object sender, RoutedEventArgs e)
        {
            // Built on first use: the page reads (and may tidy) the command list when it is created.
            if (_commandSetPage != null) return;
            try
            {
                _commandSetPage = new CommandSetSettingsPage();
                CommandSetFrame.Navigate(_commandSetPage);
            }
            catch (Exception ex)
            {
                ReportUnexpected("Could not show the command list", ex);
            }
        }

        private void SetQuietly(Action change)
        {
            _loading = true;
            try
            {
                change();
            }
            finally
            {
                _loading = false;
            }
        }

        private void ShowSettingsMessage(string text, bool isError)
        {
            SettingsMessageText.Text = text;
            SettingsMessageText.Foreground = isError
                ? System.Windows.Media.Brushes.Firebrick
                : System.Windows.Media.Brushes.DimGray;
            SettingsMessageText.Visibility = Visibility.Visible;
        }

        // An exception escaping a WPF event handler inside Revit can take Revit down with it.
        private void ReportUnexpected(string what, Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"{what}: {ex}");
            ShowSettingsMessage($"{what}: {ex.Message}", isError: true);
        }
    }
}
