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

        // ── Config file access ─────────────────────────────────────────

        private static FrameworkConfig LoadConfig()
        {
            string configPath = PathManager.GetCommandRegistryFilePath();
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                return JsonConvert.DeserializeObject<FrameworkConfig>(json) ?? new FrameworkConfig();
            }
            return new FrameworkConfig();
        }

        private static void SaveConfig(FrameworkConfig config)
        {
            string configPath = PathManager.GetCommandRegistryFilePath();
            string output = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(configPath, output);
        }

        private void LoadSettings()
        {
            try
            {
                var s = LoadConfig().Settings ?? new ServiceSettings();

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
                var config = LoadConfig();
                config.Settings.DeviceId = result.DeviceId;
                config.Settings.Token = result.DeviceToken;
                config.Settings.WsUrl = result.WsUrl;
                config.Settings.Mode = "websocket";
                SaveConfig(config);

                WsUrlTextBox.Text = result.WsUrl;
                PairCodeTextBox.Text = "";
                ShowDevice(config.Settings);
                PairStatusText.Text = WebSocketService.Instance.IsRunning
                    ? $"Paired as {result.DeviceId}. Click 'Revit MCP Switch' twice (stop, start) to reconnect as this device."
                    : $"Paired as {result.DeviceId}. Click 'Revit MCP Switch' to connect.";
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

        // ── Live status ────────────────────────────────────────────────

        private void UpdateStatus()
        {
            bool tcpRunning = SocketService.Instance.IsRunning;
            WebSocketService wsService = WebSocketService.Instance;

            if (tcpRunning)
            {
                StatusText.Text = $"TCP Server running on port {SocketService.Instance.Port}";
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
                var config = LoadConfig();

                config.Settings.Mode = WsRadio.IsChecked == true ? "websocket" : "tcp";

                int port;
                if (int.TryParse(PortTextBox.Text, out port) && port > 0 && port < 65536)
                    config.Settings.Port = port;

                config.Settings.ConfirmEachRun = ConfirmEachRunCheckBox.IsChecked == true;

                SaveConfig(config);

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
