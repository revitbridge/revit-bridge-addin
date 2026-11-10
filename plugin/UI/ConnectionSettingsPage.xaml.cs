using Newtonsoft.Json;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Core;
using revit_mcp_plugin.Utils;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace revit_mcp_plugin.UI
{
    public partial class ConnectionSettingsPage : Page
    {
        private readonly DispatcherTimer _statusTimer;

        public ConnectionSettingsPage()
        {
            InitializeComponent();
            LoadSettings();
            UpdateStatus();

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _statusTimer.Tick += (_, _) => UpdateStatus();
            Loaded += (_, _) => _statusTimer.Start();
            Unloaded += (_, _) => _statusTimer.Stop();
        }

        private void LoadSettings()
        {
            try
            {
                var s = SettingsStore.Load().Settings ?? new ServiceSettings();

                PortTextBox.Text = s.Port.ToString();
                WsUrlTextBox.Text = s.WsUrl ?? "";
                ServerTextBox.Text = GuessServerFromWsUrl(s.WsUrl);
                ConfirmEachRunCheckBox.IsChecked = s.ConfirmEachRun;
                ShowDevice(s);

                // Set mode radio
                bool isWs = string.Equals(s.Mode, "websocket",
                    StringComparison.OrdinalIgnoreCase);
                TcpRadio.IsChecked = !isWs;
                WsRadio.IsChecked = isWs;
            }
            catch (Exception ex)
            {
                // 读取配置失败时回退到默认值，并记录原因便于排障
                // Fall back to defaults on read failure, logging the cause for diagnostics.
                System.Diagnostics.Trace.WriteLine(
                    $"加载连接设置失败，使用默认值 / Failed to load connection settings, using defaults: {ex.Message}");
            }
        }

        private void ShowDevice(ServiceSettings s)
        {
            DeviceText.Text = s.IsPaired ? $"Paired as {s.DeviceId}" : "Not paired";
        }

        /// <summary>
        /// Prefill the site address from a stored wsUrl (wss://host/... -> https://host).
        /// Only a convenience; the value is never written back.
        /// </summary>
        private static string GuessServerFromWsUrl(string wsUrl)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(wsUrl) || !Uri.TryCreate(wsUrl, UriKind.Absolute, out uri))
                return "";
            string scheme = uri.Scheme == "ws" ? "http" : "https";
            return $"{scheme}://{uri.Authority}";
        }

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (TcpPanel == null || WsPanel == null) return;

            if (WsRadio.IsChecked == true)
            {
                TcpPanel.Visibility = Visibility.Collapsed;
                WsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                TcpPanel.Visibility = Visibility.Visible;
                WsPanel.Visibility = Visibility.Collapsed;
            }
        }

        // ── Pairing ────────────────────────────────────────────────────

        private async void PairButton_Click(object sender, RoutedEventArgs e)
        {
            // async void inside Revit: nothing may escape into WPF's dispatcher.
            try
            {
                string server = ServerTextBox.Text;
                string code = PairCodeTextBox.Text;
                try
                {
                    PairingClient.NormalizeServer(server);
                    PairingClient.NormalizeCode(code);
                }
                catch (ArgumentException ex)
                {
                    PairStatusText.Text = ex.Message;
                    return;
                }

                PairButton.IsEnabled = false;
                PairStatusText.Text = "Pairing...";
                try
                {
                    PairingResult result = await PairingClient.RedeemAsync(server, code, CancellationToken.None);

                    // Only a successful reply touches the config: deviceId, token and the
                    // server-supplied wsUrl. Mode follows, since pairing is the remote setup.
                    // Pairing is also the authorisation to run ad-hoc code here; each run
                    // still asks while confirmEachRun is on. Unpair() and close 4003 undo it.
                    ServiceSettings saved;
                    try
                    {
                        saved = SettingsStore.UpdateSettings(s =>
                        {
                            s.DeviceId = result.DeviceId;
                            s.Token = result.DeviceToken;
                            s.WsUrl = result.WsUrl;
                            s.Mode = "websocket";
                            s.AllowRemoteCodeExecution = true;
                        });
                    }
                    catch (Exception ex)
                    {
                        // The code is single use and already redeemed: say plainly that this
                        // Revit is NOT paired, instead of leaving it to look paired.
                        PairStatusText.Text =
                            "服务器已接受配对码，但配对未能保存到 commandRegistry.json，此 Revit 仍未配对，且该配对码已用掉。请修复该文件后用新的配对码重新配对。\n" +
                            "The server accepted the code, but the pairing could NOT be saved to commandRegistry.json: this Revit is not paired and the code is used up. " +
                            $"Fix the file, then pair again with a new code. ({ex.Message})";
                        return;
                    }

                    WsUrlTextBox.Text = result.WsUrl;
                    PairCodeTextBox.Text = "";
                    ShowDevice(saved);
                    PairStatusText.Text = WebSocketService.Instance.IsRunning
                        ? $"Paired as {result.DeviceId}; remote code execution allowed. Click 'Revit MCP Switch' twice (stop, start) to reconnect as this device."
                        : $"Paired as {result.DeviceId}; remote code execution allowed. Click 'Revit MCP Switch' to connect.";
                }
                catch (PairingException ex)
                {
                    // Config unchanged. invalid_code and the rest all surface here.
                    PairStatusText.Text = ex.Message;
                }
                catch (Exception ex)
                {
                    PairStatusText.Text = $"Pairing failed: {ex.Message}";
                }
                finally
                {
                    PairButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                ReportUnexpected("Pairing failed", ex);
            }
        }

        private void UnpairButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var confirm = MessageBox.Show(
                    "Forget this device's pairing?\n\nThe device id, token and server URL are cleared and " +
                    "remote code execution is switched off. You will need a new pairing code to connect again.",
                    "Unpair", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes) return;

                ServiceSettings saved = SettingsStore.UpdateSettings(s =>
                {
                    s.DeviceId = "";
                    s.Token = "";
                    s.WsUrl = "";
                    s.AllowRemoteCodeExecution = false;
                });

                if (WebSocketService.Instance.IsRunning)
                    WebSocketService.Instance.Stop();

                WsUrlTextBox.Text = "";
                ShowDevice(saved);
                PairStatusText.Text = "Unpaired. Remote code execution is off.";
            }
            catch (Exception ex)
            {
                ReportUnexpected("Could not unpair", ex);
            }
        }

        // Last line of defence for the click handlers: an exception escaping a WPF event
        // handler inside Revit can take Revit down with it.
        private void ReportUnexpected(string what, Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"{what}: {ex}");
            try
            {
                MessageBox.Show($"{what}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception inner)
            {
                System.Diagnostics.Trace.WriteLine($"Could not show the error: {inner.Message}");
            }
        }

        // ── Live status ────────────────────────────────────────────────

        private void UpdateStatus()
        {
            bool tcpRunning = SocketService.Instance.IsRunning;
            WebSocketService wsService = WebSocketService.Instance;

            if (tcpRunning)
            {
                StatusText.Text = $"TCP Server running on port {SocketService.Instance.Port}";
            }
            else if (wsService.IsUnpaired)
            {
                StatusText.Text =
                    $"Unpaired or revoked: the server refused device {wsService.UnpairedDeviceId}. " +
                    "Enter a new pairing code above.";
            }
            else if (wsService.IsConnected)
            {
                StatusText.Text = $"WebSocket connected as {wsService.DeviceId}";
            }
            else if (wsService.IsRunning)
            {
                StatusText.Text = string.IsNullOrWhiteSpace(wsService.LastConnectionError)
                    ? $"WebSocket connecting as {wsService.DeviceId}..."
                    : $"WebSocket reconnecting as {wsService.DeviceId}. " +
                      $"Last error: {wsService.LastConnectionError}";
            }
            else
            {
                StatusText.Text = "Not connected";
            }
        }

        // ── Save ───────────────────────────────────────────────────────

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 端口无效时提示并且什么都不保存（原来静默忽略，用户以为已保存）。
                // An invalid port is reported and nothing is saved (it used to be ignored silently).
                int port;
                if (!int.TryParse(PortTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
                    port < 1 || port > 65535)
                {
                    MessageBox.Show(
                        $"端口“{PortTextBox.Text}”无效：请输入 1 到 65535 之间的整数。设置未保存。\n" +
                        $"Invalid port \"{PortTextBox.Text}\": enter a whole number from 1 to 65535. The settings were not saved.",
                        "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                    PortTextBox.Focus();
                    return;
                }

                string mode = WsRadio.IsChecked == true ? "websocket" : "tcp";
                bool confirmEachRun = ConfirmEachRunCheckBox.IsChecked == true;

                // Applied to the settings as they are on disk now, so a change the WebSocket
                // worker made meanwhile (close 4003) is not overwritten by a stale copy.
                SettingsStore.UpdateSettings(s =>
                {
                    s.Mode = mode;
                    s.Port = port;
                    s.ConfirmEachRun = confirmEachRun;
                });

                MessageBox.Show("Settings saved.\nRestart the connection for changes to take effect.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ReportUnexpected("Failed to save", ex);
            }
        }
    }
}
