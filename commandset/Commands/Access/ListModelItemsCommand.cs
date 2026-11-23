using System.Text.RegularExpressions;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Access
{
    /// <summary>
    /// <para>list_model_items：列出标高、链接实例、某类别（或按 id）的族类型、有类型的类别，供宿主做选择与按 id 核对。
    /// 参数 {kind, category?, link_id?, ids?, q?, limit?}，kind 取 levels | links | types | type_categories。
    /// 只读，不开事务，不弹框。参数错误、类别不对、link_id 不存在等都以 Success=false 与 Error 返回，
    /// 不抛异常。参数在 Socket 线程上检查，通过后才进 Revit。</para>
    /// <para>list_model_items: lists levels, link instances, the family types of a category (or
    /// by id), or the categories that have types, for a host to choose from and to check ids
    /// against. Parameters {kind, category?, link_id?, ids?, q?, limit?}, kind one of levels |
    /// links | types | type_categories. Read-only: no transaction, no dialog. A bad parameter, a
    /// bad category or an unknown link_id comes back as Success=false with Error, not as an
    /// exception. Parameters are checked on the socket thread before anything reaches Revit.</para>
    /// </summary>
    public class ListModelItemsCommand : ExternalEventCommandBase
    {
        /// <summary>Same as get_available_family_types.</summary>
        public const int TimeoutMilliseconds = 15000;

        private static readonly object ExecutionLock = new object();

        private ListModelItemsEventHandler _handler => (ListModelItemsEventHandler)Handler;

        public override string CommandName => "list_model_items";

        public ListModelItemsCommand(UIApplication uiApp)
            : base(new ListModelItemsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            ModelItemsRequest request = ModelItemsRequest.Parse(parameters, out ModelItemsResult invalid);
            if (invalid != null)
                return invalid;

            // 同时来的调用依次执行；上一次超时后仍在 Revit 里运行时答 busy。
            // Concurrent calls run one after the other; while an earlier call that timed out is
            // still running in Revit, the answer is busy.
            lock (ExecutionLock)
            {
                if (!_handler.TryQueue(request))
                {
                    return ModelItemsResult.Fail(request.Kind, ModelItemsErrorCodes.Busy,
                        "An earlier list_model_items call is still running in Revit; try again when it has finished.");
                }

                try
                {
                    if (RaiseAndWaitForCompletion(TimeoutMilliseconds))
                    {
                        return _handler.TakeResult()
                            ?? ModelItemsResult.Fail(request.Kind, ModelItemsErrorCodes.Failed, "Revit finished the call without a result.");
                    }
                }
                catch (Exception ex)
                {
                    _handler.WithdrawIfNotStarted();
                    return ModelItemsResult.Fail(request.Kind, ModelItemsErrorCodes.Failed,
                        "Could not hand the call to Revit: " + ListModelItemsEventHandler.Describe(ex));
                }

                // 还没开始的撤回，之后不会再为无人等待的调用方运行。
                // Withdraw a call Revit has not started, so it does not run later for nobody.
                bool withdrawn = _handler.WithdrawIfNotStarted();
                return ModelItemsResult.Fail(request.Kind, ModelItemsErrorCodes.Timeout, withdrawn
                    ? $"Revit did not start the call within {TimeoutMilliseconds / 1000} s; it was withdrawn."
                    : $"Revit did not finish the call within {TimeoutMilliseconds / 1000} s.");
            }
        }
    }

    /// <summary>
    /// <para>检查过的 list_model_items 参数。</para>
    /// <para>The checked parameters of list_model_items.</para>
    /// </summary>
    public sealed class ModelItemsRequest
    {
        public const string Levels = "levels";
        public const string Links = "links";
        public const string Types = "types";
        public const string TypeCategories = "type_categories";

        /// <summary>At most this many entries in ids.</summary>
        public const int MaxIds = 200;

        // 与包里 query 的类别检查相同的写法；Enum.TryParse 另外接受数字与逗号组合，这里先挡掉。
        // The same shape the package's query checks; Enum.TryParse would also take numbers and
        // comma lists, which this rules out first.
        private static readonly Regex CategoryShape = new Regex("^OST_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);

        public string Kind { get; private set; }

        /// <summary>The BuiltInCategory name (types only); null when not given.</summary>
        public string Category { get; private set; }

        /// <summary>(int)BuiltInCategory of Category; null when Category is null.</summary>
        public int? CategoryId { get; private set; }

        /// <summary>Id of a RevitLinkInstance whose document is read instead of the open one.</summary>
        public long? LinkId { get; private set; }

        /// <summary>
        /// <para>ids 去重后按给出的顺序（levels、links、types 用；types 的行按这个顺序）；null 表示没给。</para>
        /// <para>ids without duplicates, in the order given (used by levels, links and types; the
        /// order of the types rows); null when not given.</para>
        /// </summary>
        public IReadOnlyList<long> Ids { get; private set; }

        private HashSet<long> _idSet;

        /// <summary>True when ids was not given or holds this element id.</summary>
        public bool IncludesId(long id)
        {
            return _idSet == null || _idSet.Contains(id);
        }

        /// <summary>Trimmed q; null when missing or blank.</summary>
        public string Query { get; private set; }

        /// <summary>Positive limit; null for no limit.</summary>
        public int? Limit { get; private set; }

        /// <summary>
        /// <para>解析参数；不合规时返回 null，并在 error 里给出结构化错误。</para>
        /// <para>Parses the parameters; returns null and a structured error when they are not valid.</para>
        /// </summary>
        public static ModelItemsRequest Parse(JObject parameters, out ModelItemsResult error)
        {
            error = null;
            JToken kindToken = parameters?["kind"];
            string kind = kindToken != null && kindToken.Type == JTokenType.String ? kindToken.Value<string>() : null;

            if (kind != Levels && kind != Links && kind != Types && kind != TypeCategories)
            {
                error = ModelItemsResult.Fail(kind, ModelItemsErrorCodes.UnknownKind,
                    "kind must be one of levels, links, types, type_categories.");
                return null;
            }

            var request = new ModelItemsRequest { Kind = kind };

            JToken categoryToken = parameters["category"];
            if (!IsAbsent(categoryToken))
            {
                if (kind != Types)
                {
                    error = ModelItemsResult.Fail(kind, ModelItemsErrorCodes.BadParams,
                        "category is used with kind types only.");
                    return null;
                }

                string name = categoryToken.Type == JTokenType.String ? categoryToken.Value<string>() : null;
                if (name == null
                    || !CategoryShape.IsMatch(name)
                    || !Enum.TryParse(name, false, out BuiltInCategory category)
                    || category == BuiltInCategory.INVALID
                    || !Enum.IsDefined(typeof(BuiltInCategory), category))
                {
                    error = ModelItemsResult.Fail(kind, ModelItemsErrorCodes.BadCategory,
                        "category must be the name of a BuiltInCategory, such as OST_Walls; got " + categoryToken.ToString(Newtonsoft.Json.Formatting.None) + ".");
                    return null;
                }

                request.Category = name;
                request.CategoryId = (int)category;
            }

            JToken linkToken = parameters["link_id"];
            if (!IsAbsent(linkToken))
            {
                if (kind == Links)
                {
                    error = ModelItemsResult.Fail(kind, ModelItemsErrorCodes.BadParams,
                        "link_id is not used with kind links.");
                    return null;
                }
                if (!TryGetInt64(linkToken, out long linkId))
                {
                    error = ModelItemsResult.Fail(kind, ModelItemsErrorCodes.BadParams,
                        "link_id must be an integer element id.");
                    return null;
                }
                request.LinkId = linkId;
            }

            JToken idsToken = parameters["ids"];
            if (!IsAbsent(idsToken))
            {
                if (kind == TypeCategories)
                {
                    error = ModelItemsResult.Fail(kind, ModelItemsErrorCodes.BadParams,
                        "ids is not used with kind type_categories.");
                    return null;
                }

                var order = new List<long>();
                var set = new HashSet<long>();
                bool valid = idsToken is JArray array && array.Count <= MaxIds;
                if (valid)
                {
                    foreach (JToken item in (JArray)idsToken)
                    {
                        if (!TryGetInt64(item, out long id))
                        {
                            valid = false;
                            break;
                        }
                        if (set.Add(id))
                            order.Add(id);
                    }
                }
                if (!valid)
                {
                    error = ModelItemsResult.Fail(kind, ModelItemsErrorCodes.BadParams,
                        $"ids must be an array of at most {MaxIds} integer element ids.");
                    return null;
                }
                request.Ids = order;
                request._idSet = set;
            }

            // types 必须带 category 或 ids；不带类别的旧列表只留在 get_available_family_types。
            // types needs category or ids; the old list without a category stays in
            // get_available_family_types only.
            if (kind == Types && request.CategoryId == null && request.Ids == null)
            {
                error = ModelItemsResult.Fail(kind, ModelItemsErrorCodes.BadParams,
                    "kind types needs category or ids.");
                return null;
            }

            JToken queryToken = parameters["q"];
            if (!IsAbsent(queryToken))
            {
                if (queryToken.Type != JTokenType.String)
                {
                    error = ModelItemsResult.Fail(kind, ModelItemsErrorCodes.BadParams, "q must be a string.");
                    return null;
                }
                string text = queryToken.Value<string>()?.Trim();
                request.Query = string.IsNullOrEmpty(text) ? null : text;
            }

            JToken limitToken = parameters["limit"];
            if (!IsAbsent(limitToken))
            {
                if (!TryGetInt64(limitToken, out long value) || value < 1 || value > int.MaxValue)
                {
                    error = ModelItemsResult.Fail(kind, ModelItemsErrorCodes.BadParams,
                        "limit must be a positive integer.");
                    return null;
                }
                request.Limit = (int)value;
            }

            return request;
        }

        private static bool IsAbsent(JToken token)
        {
            return token == null || token.Type == JTokenType.Null;
        }

        // Only a JSON integer that fits Int64; a larger one (BigInteger) is refused, not truncated.
        private static bool TryGetInt64(JToken token, out long value)
        {
            value = 0;
            if (token.Type != JTokenType.Integer)
                return false;
            object raw = ((JValue)token).Value;
            if (raw is long l)
            {
                value = l;
                return true;
            }
            if (raw is int i)
            {
                value = i;
                return true;
            }
            return false;
        }
    }
}
