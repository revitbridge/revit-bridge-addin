using Newtonsoft.Json;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Core;
using revit_mcp_plugin.Utils;
using System;
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
                var config = SettingsStore.Load();
                config.Settings.DeviceId = result.DeviceId;
                config.Settings.Token = result.DeviceToken;
                config.Settings.WsUrl = result.WsUrl;
                config.Settings.Mode = "websocket";
                config.Settings.AllowRemoteCodeExecution = true;
                SettingsStore.Save(config);

                WsUrlTextBox.Text = result.WsUrl;
                PairCodeTextBox.Text = "";
                ShowDevice(config.Settings);
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

        private void UnpairButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Forget this device's pairing?\n\nThe device id, token and server URL are cleared and " +
                "remote code execution is switched off. You will need a new pairing code to connect again.",
                "Unpair", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                var config = SettingsStore.Load();
                config.Settings.DeviceId = "";
                config.Settings.Token = "";
                config.Settings.WsUrl = "";
                config.Settings.AllowRemoteCodeExecution = false;
                SettingsStore.Save(config);

                if (WebSocketService.Instance.IsRunning)
                    WebSocketService.Instance.Stop();

                WsUrlTextBox.Text = "";
                ShowDevice(config.Settings);
                PairStatusText.Text = "Unpaired. Remote code execution is off.";
            }
            catch (Exception ex)
            {
                PairStatusText.Text = $"Could not unpair: {ex.Message}";
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
                var config = SettingsStore.Load();

                config.Settings.Mode = WsRadio.IsChecked == true ? "websocket" : "tcp";

                int port;
                if (int.TryParse(PortTextBox.Text, out port) && port > 0 && port < 65536)
                    config.Settings.Port = port;

                config.Settings.ConfirmEachRun = ConfirmEachRunCheckBox.IsChecked == true;

                SettingsStore.Save(config);

                MessageBox.Show("Settings saved.\nRestart the connection for changes to take effect.",
                    "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
