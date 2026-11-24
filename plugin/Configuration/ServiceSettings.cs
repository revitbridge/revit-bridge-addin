using Newtonsoft.Json;

namespace revit_mcp_plugin.Configuration
{
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    /// <summary>
    /// <para>服务设置类</para>
    /// <para>Service settings.</para>
    /// </summary>
    [JsonConverter(typeof(ServiceSettingsConverter))]
    public class ServiceSettings
    {
        // Every setter records its JSON key while a change is being applied (BeginTracking),
        // so that SettingsStore writes a setting the user set even when the value equals
        // what was read (a default standing in for a value it could not read).
        private HashSet<string> _assigned;
        private string _logLevel = "Info";
        private int _port = 18080;
        private string _mode = "tcp";
        private string _wsUrl = "";
        private string _deviceId = "";
        private string _token = "";
        private bool _confirmEachRun = true;
        private bool _allowRemoteCodeExecution;
        private bool? _localTools;
        private bool _autoStart;
        private bool _panelShown;

        /// <summary>Start recording which settings are assigned (SettingsStore, around a change).</summary>
        internal void BeginTracking()
        {
            _assigned = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>The JSON keys assigned since <see cref="BeginTracking"/>.</summary>
        internal ICollection<string> AssignedKeys => (ICollection<string>)_assigned ?? new string[0];

        private void Touch(string key)
        {
            _assigned?.Add(key);
        }

        /// <summary>
        /// <para>日志级别</para>
        /// <para>Log level.</para>
        /// </summary>
        [JsonProperty("logLevel")]
        public string LogLevel { get => _logLevel; set { _logLevel = value; Touch("logLevel"); } }

        /// <summary>
        /// <para>socket服务端口</para>
        /// <para>Socket service port.</para>
        /// </summary>
        [JsonProperty("port")]
        public int Port { get => _port; set { _port = value; Touch("port"); } }

        /// <summary>
        /// <para>连接模式: "tcp" 或 "websocket"</para>
        /// <para>Connection mode: "tcp" or "websocket".</para>
        /// </summary>
        [JsonProperty("mode")]
        public string Mode { get => _mode; set { _mode = value; Touch("mode"); } }

        /// <summary>
        /// <para>WebSocket 服务器地址。无内置默认值：只来自 commandRegistry.json
        /// （由安装器 -Server 或 Settings 页写入）。</para>
        /// <para>WebSocket server URL (used when mode = "websocket"). No built-in
        /// default: it only comes from commandRegistry.json, written by the
        /// installer (-Server) or the Settings page.</para>
        /// </summary>
        [JsonProperty("wsUrl")]
        public string WsUrl { get => _wsUrl; set { _wsUrl = value; Touch("wsUrl"); } }

        /// <summary>
        /// <para>设备标识（"dev_" + 12 位），由服务器在兑换配对码时分配。为空 = 未配对。
        /// 0.1.x 的配置没有此字段（只有 slotId），加载后即视为未配对。</para>
        /// <para>Device id ("dev_" + 12 chars) assigned by the server when a pairing
        /// code is redeemed. Empty = not paired. A 0.1.x config (slotId, no
        /// deviceId) loads as "not paired".</para>
        /// </summary>
        [JsonProperty("deviceId")]
        public string DeviceId { get => _deviceId; set { _deviceId = value; Touch("deviceId"); } }

        /// <summary>
        /// <para>设备 token：兑换配对码时服务器一次性返回，握手首条消息随 deviceId 发送。
        /// TCP 模式下仍作为可选的本地预共享 token 比对（为空则放行）。</para>
        /// <para>Device token, returned once by the server when the pairing code is
        /// redeemed and sent with deviceId in the auth handshake. In TCP mode it is
        /// still the optional local pre-shared token (empty = not checked).</para>
        /// </summary>
        [JsonProperty("token")]
        public string Token { get => _token; set { _token = value; Touch("token"); } }

        /// <summary>
        /// <para>即席代码逐次确认：带 confirm 的 send_code_to_revit 请求先在 Revit 弹
        /// Yes/No 对话框。默认开。服务器不依赖此开关。</para>
        /// <para>Per-run confirmation for ad-hoc code: a send_code_to_revit request
        /// that carries a "confirm" object shows a Yes/No TaskDialog first. Default
        /// on. The server does not depend on this switch.</para>
        /// </summary>
        [JsonProperty("confirmEachRun")]
        public bool ConfirmEachRun { get => _confirmEachRun; set { _confirmEachRun = value; Touch("confirmEachRun"); } }

        /// <summary>
        /// <para>是否允许远程下发的代码执行类高危方法（send_code_to_revit /
        /// dry_run_code_in_revit / manage_solidified_tools run 等）。默认关闭。</para>
        /// <para>Whether remote code-execution methods are allowed. Default off.</para>
        /// </summary>
        [JsonProperty("allowRemoteCodeExecution")]
        public bool AllowRemoteCodeExecution
        {
            get => _allowRemoteCodeExecution;
            set { _allowRemoteCodeExecution = value; Touch("allowRemoteCodeExecution"); }
        }

        /// <summary>
        /// <para>"Allow my tools": user command sets (other folders under Commands\) and
        /// manage_solidified_tools register/run. An explicit true or false always wins.
        /// Without the key (a new install, a registry from 0.5.0 or older) it follows the
        /// mode: on in local mode, off in remote mode. The key stays absent when another
        /// setting is saved, so a later mode change moves the default with it. A value
        /// that is present but cannot be read ("off", "no") counts as false.</para>
        /// </summary>
        [JsonProperty("localTools", NullValueHandling = NullValueHandling.Ignore)]
        public bool? LocalTools { get => _localTools; set { _localTools = value; Touch("localTools"); } }

        [JsonIgnore]
        public bool LocalToolsEnabled => LocalTools ??
            !string.Equals(Mode, "websocket", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when "Allow my tools" was never set and follows the mode's default.</summary>
        [JsonIgnore]
        public bool LocalToolsIsDefault => LocalTools == null;

        /// <summary>
        /// <para>Turn the bridge on when Revit starts. Default off.</para>
        /// </summary>
        [JsonProperty("autoStart")]
        public bool AutoStart { get => _autoStart; set { _autoStart = value; Touch("autoStart"); } }

        /// <summary>
        /// <para>The Revit Bridge panel has opened by itself once (on the first switch-on of
        /// this install); it never opens by itself again for that reason.</para>
        /// </summary>
        [JsonProperty("panelShown")]
        public bool PanelShown { get => _panelShown; set { _panelShown = value; Touch("panelShown"); } }

        /// <summary>True when the file had a "token" whose value could not be read as text.</summary>
        [JsonIgnore]
        public bool TokenUnreadable =>
            InvalidFields.Exists(name => string.Equals(name, "token", StringComparison.OrdinalIgnoreCase));

        // Only field names are retained, never rejected values (which can be secrets).
        [JsonIgnore]
        public List<string> InvalidFields { get; } = new List<string>();

        /// <summary>
        /// <para>读不出、被跳过的设置及其后果（只有键名，不含值）；没有则为 null。面板与按钮提示显示它。</para>
        /// <para>The settings that could not be read and what was used instead (key names
        /// only, never values); null when there are none. The panel and the button's tooltip
        /// show it.</para>
        /// </summary>
        [JsonIgnore]
        public string Problems
        {
            get
            {
                if (InvalidFields.Count == 0)
                    return null;
                var parts = new List<string>();
                foreach (string name in InvalidFields)
                {
                    switch (name.ToLowerInvariant())
                    {
                        case "settings": parts.Add("the whole \"settings\" value (defaults used)"); break;
                        case "token": parts.Add("token (the local bridge does not start)"); break;
                        case "localtools": parts.Add("localTools (treated as off)"); break;
                        case "port": parts.Add("port (18080 used)"); break;
                        case "mode": parts.Add("mode (local used)"); break;
                        case "confirmeachrun": parts.Add("confirmEachRun (on used)"); break;
                        default: parts.Add(name + " (default used)"); break;
                    }
                }
                return "commandRegistry.json has settings that could not be read: " + string.Join(", ", parts);
            }
        }

        /// <summary>
        /// <para>两份设置在与安全有关的值上的差别（只给键名）：token 文本、token 能否读出、模式、端口、
        /// allowRemoteCodeExecution。运行中的服务据此拒绝在设置已变时重载命令。</para>
        /// <para>The security-relevant settings in which two settings differ (key names
        /// only): token text, whether the token could be read, mode, port,
        /// allowRemoteCodeExecution. A running service refuses to reload its commands when the
        /// settings on disk differ from what it enforces.</para>
        /// </summary>
        public static IList<string> SecurityDifferences(ServiceSettings running, ServiceSettings onDisk)
        {
            var differences = new List<string>();
            running = running ?? new ServiceSettings();
            onDisk = onDisk ?? new ServiceSettings();
            if (!string.Equals(running.Token ?? "", onDisk.Token ?? "", StringComparison.Ordinal))
                differences.Add("token");
            if (running.TokenUnreadable != onDisk.TokenUnreadable && !differences.Contains("token"))
                differences.Add("token");
            if (!string.Equals(running.Mode, onDisk.Mode, StringComparison.OrdinalIgnoreCase))
                differences.Add("mode");
            if (running.Port != onDisk.Port)
                differences.Add("port");
            if (running.AllowRemoteCodeExecution != onDisk.AllowRemoteCodeExecution)
                differences.Add("allowRemoteCodeExecution");
            return differences;
        }

        /// <summary>
        /// <para>是否已配对：deviceId 与 token 均非空。</para>
        /// <para>Paired = both deviceId and token are present.</para>
        /// </summary>
        [JsonIgnore]
        public bool IsPaired =>
            !string.IsNullOrWhiteSpace(DeviceId) && !string.IsNullOrWhiteSpace(Token);
    }

    /// <summary>
    /// <para>Read each setting independently. A bad value must not discard pairing,
    /// command selections or unrelated valid settings in an older registry.
    /// Applies to all existing JsonConvert and SettingsStore load paths.</para>
    /// <para>Each value is read through Json.NET's reader for the property's type: a
    /// number or a boolean where text is expected becomes text ("token": 12345 is the
    /// secret "12345", true is "true"; a number is written in plain decimal notation, so
    /// 1.50 stays "1.50" but 1e2 is "100"), a port may be a numeric string (" 18090 "),
    /// and "true"/"false"/1/0 are booleans. NaN, Infinity and numbers out of range are
    /// their own text for a text setting and unreadable for any other (RegistryJson).
    /// A value that cannot be read is listed in InvalidFields and left at its default
    /// (localTools: false); added checks: port 1 to 65535, mode "tcp" or "websocket" (any
    /// case).</para>
    /// </summary>
    public sealed class ServiceSettingsConverter : JsonConverter
    {
        // Per-value reads, as the whole-file read of 0.5.0 did them: no date conversion of
        // text, decimals so that a number's text keeps its trailing zeros.
        private static readonly JsonSerializer LiteralSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal,
            Culture = CultureInfo.InvariantCulture,
        });

        public override bool CanConvert(Type objectType) => objectType == typeof(ServiceSettings);
        public override bool CanWrite => false;

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
            JsonSerializer serializer)
        {
            var result = new ServiceSettings();
            // Credentials and addresses are literal strings, including strings
            // that look like ISO dates or numbers. The default reader otherwise converts
            // them to Date tokens (and numbers to doubles) before the values are read.
            var previousDateHandling = reader.DateParseHandling;
            var previousFloatHandling = reader.FloatParseHandling;
            JToken token;
            try
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Decimal;
                token = JToken.Load(reader);
            }
            finally
            {
                reader.DateParseHandling = previousDateHandling;
                reader.FloatParseHandling = previousFloatHandling;
            }
            if (!(token is JObject settings))
            {
                result.InvalidFields.Add("settings");
                return result;
            }

            foreach (var property in settings.Properties())
            {
                try
                {
                    var value = property.Value;
                    switch (property.Name.ToLowerInvariant())
                    {
                        case "loglevel": result.LogLevel = ReadAs<string>(value); break;
                        case "port":
                            int port = ReadAs<int>(value);
                            if (port < 1 || port > 65535)
                                throw new JsonSerializationException("Port is out of range.");
                            result.Port = port;
                            break;
                        case "mode":
                            string mode = ReadAs<string>(value);
                            if (!string.Equals(mode, "tcp", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(mode, "websocket", StringComparison.OrdinalIgnoreCase))
                                throw new JsonSerializationException("Unknown connection mode.");
                            result.Mode = mode;
                            break;
                        case "wsurl": result.WsUrl = ReadAs<string>(value); break;
                        case "deviceid": result.DeviceId = ReadAs<string>(value); break;
                        case "token": result.Token = ReadAs<string>(value); break;
                        case "confirmeachrun": result.ConfirmEachRun = ReadAs<bool>(value); break;
                        case "allowremotecodeexecution": result.AllowRemoteCodeExecution = ReadAs<bool>(value); break;
                        case "localtools": result.LocalTools = ReadAs<bool?>(value); break;
                        case "autostart": result.AutoStart = ReadAs<bool>(value); break;
                        case "panelshown": result.PanelShown = ReadAs<bool>(value); break;
                    }
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidCastException ||
                                           ex is FormatException || ex is OverflowException ||
                                           ex is ArgumentException)
                {
                    result.InvalidFields.Add(property.Name);
                    // "Allow my tools" set to something unreadable ("off", "no") means off,
                    // not the mode's default.
                    if (string.Equals(property.Name, "localTools", StringComparison.OrdinalIgnoreCase))
                        result.LocalTools = false;
                }
            }
            return result;
        }

        // One value through Json.NET's own reader for that type, from the value's JSON text.
        // A token RegistryJson had to protect (NaN, Infinity, a number out of range) is its
        // own text for a text setting and unreadable for any other.
        private static T ReadAs<T>(JToken value)
        {
            string raw;
            if (RegistryJson.TryGetProtected(value, out raw))
            {
                if (typeof(T) == typeof(string))
                    return (T)(object)raw;
                throw new JsonSerializationException("Not a value of this type.");
            }

            using (var text = new StringReader(value.ToString(Formatting.None)))
            using (var reader = new JsonTextReader(text))
            {
                return LiteralSerializer.Deserialize<T>(reader);
            }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            throw new NotSupportedException("Default property serialization is used for settings.");
        }
    }
}
