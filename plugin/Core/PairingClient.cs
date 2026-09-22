using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// <para>兑换配对码的结果：服务器一次性返回的设备标识、设备 token 与 WebSocket 地址。</para>
    /// <para>What the server returns once when a pairing code is redeemed.</para>
    /// </summary>
    public sealed class PairingResult
    {
        public string DeviceId { get; set; }
        public string DeviceToken { get; set; }
        public string WsUrl { get; set; }
    }

    /// <summary>
    /// <para>配对失败。Code 为服务器错误码（如 invalid_code）或本地码
    /// （unreachable / bad_response / http_&lt;status&gt;），Message 可直接展示。</para>
    /// <para>Pairing failed. Code is the server's error code (e.g. invalid_code) or a
    /// local one (unreachable / bad_response / http_&lt;status&gt;); Message is
    /// suitable for display.</para>
    /// </summary>
    public sealed class PairingException : Exception
    {
        public string Code { get; }

        public PairingException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// <para>配对客户端：POST &lt;server&gt;/api/v1/bridge/devices/redeem {code, addin_version}
    /// → {device_id, device_token, ws_url}。ws_url 只取服务器返回值，不在本地拼接。
    /// 设置窗口与安装器（install.ps1）做同一件事。</para>
    /// <para>Pairing client: POST &lt;server&gt;/api/v1/bridge/devices/redeem
    /// {code, addin_version} → {device_id, device_token, ws_url}. ws_url is taken
    /// from the reply, never assembled locally. The installer does the same.</para>
    /// </summary>
    public static class PairingClient
    {
        public const string RedeemPath = "/api/v1/bridge/devices/redeem";
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

        private static readonly Regex CodeShape = new Regex("^[A-Z0-9]{4}-[A-Z0-9]{4}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// <para>规范化服务器地址：https://host[:port]，无路径。http:// 只允许本机（联调用）。</para>
        /// <para>Normalise the server base to https://host[:port] with no path.
        /// http:// is accepted for loopback hosts only (local development).</para>
        /// </summary>
        public static string NormalizeServer(string server)
        {
            string text = (server ?? "").Trim();
            if (text.Length == 0)
                throw new ArgumentException("Enter the bridge server address, e.g. https://bridge.example.com");
            if (!text.Contains("://"))
                text = "https://" + text;

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri))
                throw new ArgumentException("Server address is not a valid URL: " + text);

            if (uri.Scheme == Uri.UriSchemeHttp)
            {
                if (!uri.IsLoopback)
                    throw new ArgumentException("Use https:// for the bridge server (http:// is only allowed for localhost).");
            }
            else if (uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("Server address must start with https:// (the site address, not the wss:// URL).");
            }

            return uri.GetLeftPart(UriPartial.Authority);
        }

        /// <summary>
        /// <para>规范化配对码：去空白、转大写，8 位无连字符时补上。字母表由服务器判定。</para>
        /// <para>Normalise a pairing code: trim, upper-case, insert the hyphen when
        /// 8 characters were typed without one. The alphabet is the server's call.</para>
        /// </summary>
        public static string NormalizeCode(string code)
        {
            string text = Regex.Replace(code ?? "", "\\s+", "").ToUpperInvariant();
            if (text.Length == 8 && !text.Contains("-"))
                text = text.Substring(0, 4) + "-" + text.Substring(4);
            if (!CodeShape.IsMatch(text))
                throw new ArgumentException("Enter the pairing code shown on the site, in the form XXXX-XXXX.");
            return text;
        }

        public static async Task<PairingResult> RedeemAsync(string server, string code, CancellationToken cancellationToken)
        {
            string baseUrl = NormalizeServer(server);
            string normalizedCode = NormalizeCode(code);
            string url = baseUrl + RedeemPath;

            string body = JsonConvert.SerializeObject(new
            {
                code = normalizedCode,
                addin_version = AddinInfo.Version
            });

            HttpResponseMessage response;
            string responseText;
            try
            {
                using (var client = new HttpClient { Timeout = RequestTimeout })
                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(AddinInfo.UserAgent);
                    response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
                    responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw new PairingException("unreachable", $"No answer from {baseUrl} within {RequestTimeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException ex)
            {
                throw new PairingException("unreachable", $"Could not reach {baseUrl}: {ex.GetBaseException().Message}");
            }

            if (response.StatusCode == HttpStatusCode.OK)
                return ParseRedeemReply(responseText);

            // Error payloads follow the web API rule {error: <code>, message?}.
            string errorCode = null;
            string errorMessage = null;
            try
            {
                var errorBody = JObject.Parse(responseText);
                errorCode = errorBody.Value<string>("error");
                errorMessage = errorBody.Value<string>("message") ?? errorBody.Value<string>("detail");
            }
            catch (JsonException)
            {
                // Not JSON (proxy page, HTML error); fall through to the status text.
            }

            if (response.StatusCode == HttpStatusCode.NotFound && errorCode == "invalid_code")
                throw new PairingException("invalid_code",
                    errorMessage ?? "Invalid or expired pairing code. Generate a new one on the site and try again.");

            string statusText = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
            throw new PairingException(errorCode ?? $"http_{(int)response.StatusCode}",
                errorMessage != null ? $"{statusText}: {errorMessage}" : $"{statusText} from {url}");
        }

        private static PairingResult ParseRedeemReply(string json)
        {
            JObject obj;
            try
            {
                obj = JObject.Parse(json);
            }
            catch (JsonException)
            {
                throw new PairingException("bad_response", "The server did not answer with JSON.");
            }

            var result = new PairingResult
            {
                DeviceId = obj.Value<string>("device_id"),
                DeviceToken = obj.Value<string>("device_token"),
                WsUrl = obj.Value<string>("ws_url")
            };

            if (string.IsNullOrWhiteSpace(result.DeviceId) ||
                string.IsNullOrWhiteSpace(result.DeviceToken) ||
                string.IsNullOrWhiteSpace(result.WsUrl))
                throw new PairingException("bad_response", "The server reply is missing device_id, device_token or ws_url.");

            if (!result.WsUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                !result.WsUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                throw new PairingException("bad_response", $"The server returned an unexpected ws_url: {result.WsUrl}");

            return result;
        }
    }
}
