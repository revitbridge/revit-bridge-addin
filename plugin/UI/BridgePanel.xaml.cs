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
            LoadSettings();
            RefreshStatus();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += (_, _) => RefreshStatus();
            Loaded += (_, _) => _statusTimer.Start();
            Closed += (_, _) => _statusTimer.Stop();
        }

        private void LoadSettings()
        {
            _loading = true;
            try
            {
                string error;
                ServiceSettings settings = SettingsStore.LoadOrDefaults(out error).Settings;
                MyToolsCheckBox.IsChecked = settings.LocalToolsEnabled;
                AutoStartCheckBox.IsChecked = settings.AutoStart;
                if (error != null)
                    ShowSettingsMessage(error + ". The defaults are shown (local, Allow my tools off, no auto start). " +
                                        "Repair the file or re-run the installer; until then changes here cannot be saved.",
                                        isError: true);
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
                    StatusText.Text = "The switch is not available here; use the Revit Bridge button on the ribbon.";
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
                BridgeController.SetUserToolsAllowed(allowed);
                ShowSettingsMessage(allowed
                    ? "Allow my tools is on: your command sets and solidified tools can be used."
                    : "Allow my tools is off: only the commands installed with the add-in answer; requests for your own tools are refused.",
                    isError: false);
            }
            catch (Exception ex)
            {
                SetQuietly(() => MyToolsCheckBox.IsChecked = !allowed);
                ShowSettingsMessage("Could not save 'Allow my tools': " + ex.Message, isError: true);
            }
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
