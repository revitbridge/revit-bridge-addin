using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.ExecuteDynamicCode;

namespace RevitMCPCommandSet.Commands.AddinTest
{
    /// <summary>
    /// <para>test_addin_command_in_revit 的请求。形状不对时抛 ArgumentException（调用方收到 JSON-RPC 错误）。</para>
    /// <para>The request of test_addin_command_in_revit. A malformed request throws ArgumentException
    /// (the caller receives a JSON-RPC error).</para>
    /// </summary>
    internal sealed class AddinTestRequest
    {
        public const string RestoreGroupRollback = "group_rollback";
        public const string RestoreUndo = "undo";

        public string Dll { get; private set; }
        public string ClassName { get; private set; }
        public List<PickRequest> Picks { get; } = new List<PickRequest>();
        public JToken Input { get; private set; }
        public string ReadbackCode { get; private set; }
        public long? ZoomTo { get; private set; }
        public int TimeoutSeconds { get; private set; } = 120;
        public string Restore { get; private set; } = RestoreGroupRollback;

        public static AddinTestRequest Parse(JObject parameters)
        {
            if (parameters == null)
                throw new ArgumentException("Missing parameters.");
            var request = new AddinTestRequest
            {
                Dll = RequiredString(parameters, "dll"),
                ClassName = RequiredString(parameters, "class"),
            };

            JToken picks = parameters["picks"];
            if (picks != null && picks.Type != JTokenType.Null)
            {
                if (!(picks is JArray array))
                    throw new ArgumentException("'picks' must be an array.");
                for (int i = 0; i < array.Count; i++)
                    request.Picks.Add(PickRequest.Parse(array[i], i));
            }

            JToken input = parameters["input"];
            request.Input = input == null || input.Type == JTokenType.Null ? null : input;

            JToken readback = parameters["readback_code"];
            if (readback != null && readback.Type != JTokenType.Null)
            {
                if (readback.Type != JTokenType.String)
                    throw new ArgumentException("'readback_code' must be a string.");
                string text = readback.Value<string>();
                request.ReadbackCode = string.IsNullOrWhiteSpace(text) ? null : text;
            }

            JToken zoom = parameters["zoom_to"];
            if (zoom != null && zoom.Type != JTokenType.Null)
            {
                if (zoom.Type != JTokenType.Integer)
                    throw new ArgumentException("'zoom_to' must be an element id (an integer).");
                request.ZoomTo = zoom.Value<long>();
            }

            JToken timeout = parameters["timeout_seconds"];
            if (timeout != null && timeout.Type != JTokenType.Null)
            {
                if (timeout.Type != JTokenType.Integer && timeout.Type != JTokenType.Float)
                    throw new ArgumentException("'timeout_seconds' must be a number.");
                double seconds = timeout.Value<double>();
                if (double.IsNaN(seconds) || seconds < 5 || seconds > 1800)
                    throw new ArgumentException("'timeout_seconds' must be between 5 and 1800.");
                request.TimeoutSeconds = (int)Math.Round(seconds);
            }

            JToken restore = parameters["restore"];
            if (restore != null && restore.Type != JTokenType.Null)
            {
                string value = restore.Type == JTokenType.String ? restore.Value<string>() : null;
                if (value != RestoreGroupRollback && value != RestoreUndo)
                    throw new ArgumentException("'restore' must be \"group_rollback\" or \"undo\".");
                request.Restore = value;
            }
            return request;
        }

        private static string RequiredString(JObject parameters, string key)
        {
            JToken token = parameters[key];
            if (token == null || token.Type != JTokenType.String || string.IsNullOrWhiteSpace(token.Value<string>()))
                throw new ArgumentException($"Missing required parameter: '{key}' (a non-empty string).");
            return token.Value<string>();
        }
    }

    /// <summary>
    /// <para>一次点选：元素定位线上的点、模型坐标点，或按键 Escape。</para>
    /// <para>One pick: a point on an element's location line, a model point, or the Escape key.</para>
    /// </summary>
    internal sealed class PickRequest
    {
        public JToken Raw { get; private set; }
        public long? ElementId { get; private set; }
        public double Fraction { get; private set; } = 0.5;
        public double[] PointMm { get; private set; }
        public bool IsEscape { get; private set; }

        public static PickRequest Parse(JToken token, int index)
        {
            if (!(token is JObject pick))
                throw new ArgumentException($"picks[{index}] must be an object.");
            var result = new PickRequest { Raw = token.DeepClone() };

            if (pick["element_id"] != null)
            {
                if (pick["element_id"].Type != JTokenType.Integer)
                    throw new ArgumentException($"picks[{index}].element_id must be an integer.");
                result.ElementId = pick["element_id"].Value<long>();
                JToken fraction = pick["fraction"];
                if (fraction != null && fraction.Type != JTokenType.Null)
                {
                    if (fraction.Type != JTokenType.Integer && fraction.Type != JTokenType.Float)
                        throw new ArgumentException($"picks[{index}].fraction must be a number.");
                    double value = fraction.Value<double>();
                    if (double.IsNaN(value) || value < 0 || value > 1)
                        throw new ArgumentException($"picks[{index}].fraction must be between 0 and 1.");
                    result.Fraction = value;
                }
                return result;
            }

            if (pick["point_mm"] != null)
            {
                if (!(pick["point_mm"] is JArray point) || point.Count < 2 || point.Count > 3 ||
                    point.Any(v => v.Type != JTokenType.Integer && v.Type != JTokenType.Float))
                    throw new ArgumentException($"picks[{index}].point_mm must be [x, y] or [x, y, z] in mm.");
                result.PointMm = new[] { point[0].Value<double>(), point[1].Value<double>(), point.Count == 3 ? point[2].Value<double>() : 0.0 };
                return result;
            }

            if (pick["key"] != null)
            {
                if (pick["key"].Type != JTokenType.String || pick["key"].Value<string>() != "Escape")
                    throw new ArgumentException($"picks[{index}].key: only \"Escape\" is supported.");
                result.IsEscape = true;
                return result;
            }

            throw new ArgumentException($"picks[{index}] needs element_id, point_mm or key.");
        }
    }

    /// <summary>
    /// <para>test_addin_command_in_revit 的返回。每个键恒在。</para>
    /// <para>Reply of test_addin_command_in_revit. Every key is always present.</para>
    /// </summary>
    public class AddinTestResult
    {
        [JsonProperty("loaded")] public bool Loaded { get; set; }
        [JsonProperty("load_error")] public string LoadError { get; set; } = string.Empty;

        /// <summary>"Succeeded", "Cancelled", "Failed", or "(none)" when the command did not return.</summary>
        [JsonProperty("returned")] public string Returned { get; set; } = "(none)";

        [JsonProperty("command_message")] public string CommandMessage { get; set; } = string.Empty;

        /// <summary>The parsed &lt;name&gt;.result.json, or null when the command wrote none.</summary>
        [JsonProperty("result_file")] public JToken ResultFile { get; set; }

        [JsonProperty("changes")] public DryRunChanges Changes { get; set; } = new DryRunChanges { Note = AddinTestSession.ChangesNote };
        [JsonProperty("failures")] public List<DryRunFailure> Failures { get; set; } = new List<DryRunFailure>();

        /// <summary>The read-back snippet's return value as JSON; empty when there was none or it threw.</summary>
        [JsonProperty("readback")] public string Readback { get; set; } = string.Empty;

        /// <summary>Why the read-back did not run or threw; empty otherwise.</summary>
        [JsonProperty("readback_error")] public string ReadbackError { get; set; } = string.Empty;

        [JsonProperty("picks_done")] public List<PickDone> PicksDone { get; set; } = new List<PickDone>();
        [JsonProperty("restore")] public string Restore { get; set; } = AddinTestRequest.RestoreGroupRollback;
        [JsonProperty("rolled_back")] public bool RolledBack { get; set; }
        [JsonProperty("undone")] public int Undone { get; set; }

        /// <summary>True when the model is as it was: restored and checked, or nothing ran.</summary>
        [JsonProperty("restored_clean")] public bool RestoredClean { get; set; } = true;

        [JsonProperty("timed_out")] public bool TimedOut { get; set; }
        [JsonProperty("foreground_ok")] public bool ForegroundOk { get; set; }

        /// <summary>Path of a picture of the Revit window, taken on a timeout or a load failure only.</summary>
        [JsonProperty("screenshot")] public string Screenshot { get; set; } = string.Empty;

        [JsonProperty("dll_sha256")] public string DllSha256 { get; set; } = string.Empty;

        /// <summary>Problems of the test itself (loader, restore, reading changes); empty when none.</summary>
        [JsonProperty("error")] public string Error { get; set; } = string.Empty;

        internal void AddError(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            Error = string.IsNullOrEmpty(Error) ? text : Error + " | " + text;
        }
    }

    public class PickDone
    {
        /// <summary>The pick as requested (element id, point_mm or "Escape").</summary>
        [JsonProperty("requested")] public JToken Requested { get; set; }

        /// <summary>The screen pixel clicked, or null for a key.</summary>
        [JsonProperty("screen")] public int[] Screen { get; set; }

        [JsonProperty("attempts")] public int Attempts { get; set; }
    }
}
