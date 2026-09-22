using Newtonsoft.Json;

namespace revit_mcp_plugin.Configuration
{
    /// <summary>
    /// <para>服务设置类</para>
    /// <para>Service settings.</para>
    /// </summary>
    public class ServiceSettings
    {
        /// <summary>
        /// <para>日志级别</para>
        /// <para>Log level.</para>
        /// </summary>
        [JsonProperty("logLevel")]
        public string LogLevel { get; set; } = "Info";

        /// <summary>
        /// <para>socket服务端口</para>
        /// <para>Socket service port.</para>
        /// </summary>
        [JsonProperty("port")]
        public int Port { get; set; } = 18080;

        /// <summary>
        /// <para>连接模式: "tcp" 或 "websocket"</para>
        /// <para>Connection mode: "tcp" or "websocket".</para>
        /// </summary>
        [JsonProperty("mode")]
        public string Mode { get; set; } = "tcp";

        /// <summary>
        /// <para>WebSocket 服务器地址。无内置默认值：只来自 commandRegistry.json
        /// （由安装器 -Server 或 Settings 页写入）。</para>
        /// <para>WebSocket server URL (used when mode = "websocket"). No built-in
        /// default: it only comes from commandRegistry.json, written by the
        /// installer (-Server) or the Settings page.</para>
        /// </summary>
        [JsonProperty("wsUrl")]
        public string WsUrl { get; set; } = "";

        /// <summary>
        /// <para>设备标识（"dev_" + 12 位），由服务器在兑换配对码时分配。为空 = 未配对。
        /// 0.1.x 的配置没有此字段（只有 slotId），加载后即视为未配对。</para>
        /// <para>Device id ("dev_" + 12 chars) assigned by the server when a pairing
        /// code is redeemed. Empty = not paired. A 0.1.x config (slotId, no
        /// deviceId) loads as "not paired".</para>
        /// </summary>
        [JsonProperty("deviceId")]
        public string DeviceId { get; set; } = "";

        /// <summary>
        /// <para>设备 token：兑换配对码时服务器一次性返回，握手首条消息随 deviceId 发送。
        /// TCP 模式下仍作为可选的本地预共享 token 比对（为空则放行）。</para>
        /// <para>Device token, returned once by the server when the pairing code is
        /// redeemed and sent with deviceId in the auth handshake. In TCP mode it is
        /// still the optional local pre-shared token (empty = not checked).</para>
        /// </summary>
        [JsonProperty("token")]
        public string Token { get; set; } = "";

        /// <summary>
        /// <para>即席代码逐次确认：带 confirm 的 send_code_to_revit 请求先在 Revit 弹
        /// Yes/No 对话框。默认开。服务器不依赖此开关。</para>
        /// <para>Per-run confirmation for ad-hoc code: a send_code_to_revit request
        /// that carries a "confirm" object shows a Yes/No TaskDialog first. Default
        /// on. The server does not depend on this switch.</para>
        /// </summary>
        [JsonProperty("confirmEachRun")]
        public bool ConfirmEachRun { get; set; } = true;

        /// <summary>
        /// <para>是否允许远程下发的代码执行类高危方法（send_code_to_revit /
        /// manage_solidified_tools run 等）。默认关闭。</para>
        /// <para>Whether remote code-execution methods are allowed. Default off.</para>
        /// </summary>
        [JsonProperty("allowRemoteCodeExecution")]
        public bool AllowRemoteCodeExecution { get; set; } = false;

        /// <summary>
        /// <para>是否已配对：deviceId 与 token 均非空。</para>
        /// <para>Paired = both deviceId and token are present.</para>
        /// </summary>
        [JsonIgnore]
        public bool IsPaired =>
            !string.IsNullOrWhiteSpace(DeviceId) && !string.IsNullOrWhiteSpace(Token);
    }
}
