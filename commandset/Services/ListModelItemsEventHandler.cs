using Autodesk.Revit.UI;
using RevitMCPCommandSet.Commands.Access;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// <para>list_model_items 的外部事件处理器。只读：不开事务，不弹框。一次只持有一个请求；
    /// 超时后撤回还没开始的请求，已开始的跑完后结果丢弃。逐行容错：一行读取出错只丢这一行，
    /// 在 Warnings 里记一行（id 与异常），整次调用仍然成功。</para>
    /// <para>External event handler of list_model_items. Read-only: no transaction, no dialog.
    /// Holds one request at a time; one Revit has not started when the caller times out is
    /// withdrawn, one that has started finishes and its result is dropped. Per-row tolerance:
    /// a row that fails to read is left out and gets a line in Warnings (its id and the
    /// exception); the call still succeeds.</para>
    /// </summary>
    public class ListModelItemsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        /// <summary>Feet (Revit internal length) to millimetres.</summary>
        private const double FeetToMm = 304.8;

        /// <summary>At most this many lines in Warnings; one more line says how many were left out.</summary>
        private const int MaxWarnings = 100;

        private readonly object _sync = new object();
        private readonly ManualResetEvent _done = new ManualResetEvent(false);

        private ModelItemsRequest _request;
        private bool _queued;
        private bool _running;
        private ModelItemsResult _result;

        /// <summary>
        /// <para>登记一次调用；已有一次在排队或运行时返回 false。</para>
        /// <para>Register a call; false while another one is queued or running.</para>
        /// </summary>
        public bool TryQueue(ModelItemsRequest request)
        {
            lock (_sync)
            {
                if (_queued || _running)
                    return false;

                _request = request;
                _result = null;
                _queued = true;
                _done.Reset();
                return true;
            }
        }

        /// <summary>
        /// <para>撤回一个 Revit 还没开始的请求；已开始运行时返回 false。</para>
        /// <para>Withdraw a request Revit has not started; false when it is already running.</para>
        /// </summary>
        public bool WithdrawIfNotStarted()
        {
            lock (_sync)
            {
                if (_queued && !_running)
                {
                    _queued = false;
                    return true;
                }
                return false;
            }
        }

        /// <summary>The result of the last finished call.</summary>
        public ModelItemsResult TakeResult()
        {
            lock (_sync)
            {
                return _result;
            }
        }

        // 不在这里 Reset：TryQueue 已经 Reset，处理器先于等待结束时信号也不会丢。
        // No Reset here: TryQueue already reset it, so a call that finishes before the wait
        // starts does not lose its signal.
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _done.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            ModelItemsRequest request;
            lock (_sync)
            {
                // 超时后已撤回，或没有排队的请求：什么也不做。
                // Withdrawn after a timeout, or nothing queued: do nothing.
                if (!_queued)
                    return;
                _queued = false;
                _running = true;
                request = _request;
            }

            ModelItemsResult result = null;
            try
            {
                result = Read(app, request);
            }
            catch (Exception ex)
            {
                result = ModelItemsResult.Fail(request.Kind, ModelItemsErrorCodes.Failed,
                    "Reading the model failed: " + Describe(ex));
            }
            finally
            {
                lock (_sync)
                {
                    _result = result;
                    _running = false;
                }
                _done.Set();
            }
        }

        public string GetName()
        {
            return "revit-bridge list model items";
        }

        /// <summary>"&lt;full exception type name&gt;: &lt;message&gt;".</summary>
        internal static string Describe(Exception ex)
        {
            Type type = ex.GetType();
            return (type.FullName ?? type.Name) + ": " + ex.Message;
        }

        // ------------------------------------------------------------------
        // Reading, in Revit API context only
        // ------------------------------------------------------------------

        private static ModelItemsResult Read(UIApplication app, ModelItemsRequest request)
        {
            Document host = app?.ActiveUIDocument?.Document;
            if (host == null)
            {
                return ModelItemsResult.Fail(request.Kind, ModelItemsErrorCodes.NoDocument,
                    "No document is open in Revit.");
            }

            // link_id：读该链接实例的文档，而不是打开的文档。
            // link_id: read that link instance's document instead of the open one.
            Document source = host;
            if (request.LinkId.HasValue)
            {
                ModelItemsResult linkError = ResolveLinkDocument(host, request, out source);
                if (linkError != null)
                    return linkError;
            }

            var rows = new RowSet(request);
            switch (request.Kind)
            {
                case ModelItemsRequest.Levels:
                    ReadLevels(source, rows);
                    break;
                case ModelItemsRequest.Links:
                    ReadLinks(host, rows);
                    break;
                case ModelItemsRequest.Types:
                    ReadTypes(source, request, rows);
                    break;
                case ModelItemsRequest.TypeCategories:
                    ReadTypeCategories(source, rows);
                    break;
                default:
                    // Parse lets only the four kinds through.
                    return ModelItemsResult.Fail(request.Kind, ModelItemsErrorCodes.UnknownKind,
                        "kind must be one of levels, links, types, type_categories.");
            }
            return rows.ToResult();
        }

        private static ModelItemsResult ResolveLinkDocument(Document host, ModelItemsRequest request, out Document linkDocument)
        {
            linkDocument = null;
            long linkId = request.LinkId.Value;

            ElementId id;
            try
            {
                id = ElementIdCompat.FromLong(linkId);
            }
            catch (OverflowException)
            {
                // Before Revit 2024 an id is Int32; a larger value names no element.
                return ModelItemsResult.Fail(request.Kind, ModelItemsErrorCodes.UnknownLink,
                    $"link_id {linkId} is not a Revit link instance in the open document.");
            }

            if (!(host.GetElement(id) is RevitLinkInstance link))
            {
                return ModelItemsResult.Fail(request.Kind, ModelItemsErrorCodes.UnknownLink,
                    $"link_id {linkId} is not a Revit link instance in the open document.");
            }

            linkDocument = link.GetLinkDocument();
            if (linkDocument == null)
            {
                return ModelItemsResult.Fail(request.Kind, ModelItemsErrorCodes.LinkNotLoaded,
                    $"The link {linkId} ({link.Name}) is not loaded.");
            }
            return null;
        }

        // 与包里 query("levels") 的片段相同：按 Elevation（英尺，未取整）排序，ElevationMm 取一位小数。
        // As the package's query("levels") snippet: ordered by Elevation (feet, not rounded),
        // ElevationMm rounded to one decimal.
        private static void ReadLevels(Document doc, RowSet rows)
        {
            var read = new List<KeyValuePair<double, ModelLevelItem>>();
            foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                try
                {
                    double elevation = level.Elevation;
                    read.Add(new KeyValuePair<double, ModelLevelItem>(elevation, new ModelLevelItem
                    {
                        Id = level.Id.GetValue(),
                        Name = level.Name,
                        ElevationMm = Math.Round(elevation * FeetToMm, 1),
                    }));
                }
                catch (Exception ex)
                {
                    rows.Skipped("level", level, ex);
                }
            }

            foreach (var pair in read.OrderBy(p => p.Key))
            {
                rows.Add(pair.Value, pair.Value.Name);
            }
        }

        private static void ReadLinks(Document host, RowSet rows)
        {
            foreach (RevitLinkInstance link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                ModelLinkItem item;
                try
                {
                    ElementId typeId = link.GetTypeId();
                    // 与快照的 links 相同，按链接类型判断是否已加载；未加载时不读文档标题。
                    // Loaded as the snapshot's links tell it, from the link type; no document title
                    // when it is not loaded.
                    bool loaded = RevitLinkType.IsLoaded(host, typeId);
                    Document linkDocument = loaded ? link.GetLinkDocument() : null;
                    Transform transform = link.GetTotalTransform();
                    XYZ origin = transform.Origin;

                    item = new ModelLinkItem
                    {
                        Id = link.Id.GetValue(),
                        Name = link.Name,
                        TypeId = typeId.GetValue(),
                        TypeName = host.GetElement(typeId)?.Name,
                        Loaded = loaded,
                        DocumentTitle = linkDocument?.Title,
                        Origin = new PointMm
                        {
                            X = Math.Round(origin.X * FeetToMm, 1),
                            Y = Math.Round(origin.Y * FeetToMm, 1),
                            Z = Math.Round(origin.Z * FeetToMm, 1),
                        },
                        Rotated = !transform.IsTranslation,
                    };
                }
                catch (Exception ex)
                {
                    rows.Skipped("link", link, ex);
                    continue;
                }

                rows.Add(item, item.Name, item.TypeName, item.DocumentTitle);
            }
        }

        // 与 get_available_family_types 同一份列表（FamilyTypeCollector）：给了类别时是该类别的全部元素类型，
        // 没给时是以前那六个类的类型。
        // The same list as get_available_family_types (FamilyTypeCollector): every element type
        // of the category when one is given, the types of the old six classes when not.
        private static void ReadTypes(Document doc, ModelItemsRequest request, RowSet rows)
        {
            var categoryIds = new List<int>();
            if (request.CategoryId.HasValue)
                categoryIds.Add(request.CategoryId.Value);

            foreach (ElementType type in FamilyTypeCollector.Collect(doc, categoryIds))
            {
                FamilyTypeInfo info;
                try
                {
                    info = FamilyTypeCollector.ToInfo(type);
                }
                catch (Exception ex)
                {
                    rows.Skipped("type", type, ex);
                    continue;
                }

                rows.Add(info, info.FamilyName, info.TypeName);
            }
        }

        // 全部元素类型按类别计数（与 kind types 给该类别时的行数相同）；按 DisplayName 排序。
        // Every element type counted by category (the row count kind types gives for that
        // category); ordered by DisplayName.
        private static void ReadTypeCategories(Document doc, RowSet rows)
        {
            var categories = new Dictionary<long, Category>();
            var counts = new Dictionary<long, int>();
            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                if (!(element is ElementType))
                    continue;

                try
                {
                    Category category = element.Category;
                    if (category == null)
                        continue;

                    long id = category.Id.GetValue();
                    if (counts.TryGetValue(id, out int count))
                    {
                        counts[id] = count + 1;
                    }
                    else
                    {
                        counts[id] = 1;
                        categories[id] = category;
                    }
                }
                catch (Exception ex)
                {
                    rows.Skipped("type", element, ex);
                }
            }

            var items = new List<TypeCategoryItem>();
            foreach (var pair in counts)
            {
                try
                {
                    Category category = categories[pair.Key];
                    if (!TryGetBuiltInCategoryName(pair.Key, out string name))
                    {
                        rows.Warn($"category {pair.Key} ({category.Name}) is not a built-in category; its {pair.Value} type(s) are not listed.");
                        continue;
                    }

                    items.Add(new TypeCategoryItem
                    {
                        Category = name,
                        DisplayName = category.Name,
                        Count = pair.Value,
                    });
                }
                catch (Exception ex)
                {
                    rows.Warn($"category {pair.Key} skipped: {Describe(ex)}");
                }
            }

            foreach (TypeCategoryItem item in items
                .OrderBy(i => i.DisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Category, StringComparer.Ordinal))
            {
                rows.Add(item, item.Category, item.DisplayName);
            }
        }

        // Category id to BuiltInCategory name, as the dry run's by_category does.
        private static bool TryGetBuiltInCategoryName(long value, out string name)
        {
            name = null;
            if (value < int.MinValue || value > int.MaxValue)
                return false;
            var builtIn = (BuiltInCategory)(int)value;
            if (builtIn == BuiltInCategory.INVALID || !Enum.IsDefined(typeof(BuiltInCategory), builtIn))
                return false;
            name = builtIn.ToString();
            return true;
        }

        /// <summary>
        /// <para>收集行：按 q 过滤（名字里含 q，不分大小写），记 Warnings，最后按 limit 截断。</para>
        /// <para>Collects rows: filtered by q (a name contains q, ignoring case), records
        /// Warnings, and truncates to limit at the end.</para>
        /// </summary>
        private sealed class RowSet
        {
            private readonly ModelItemsRequest _request;
            private readonly List<object> _rows = new List<object>();
            private readonly List<string> _warnings = new List<string>();
            private int _warningsLeftOut;

            public RowSet(ModelItemsRequest request)
            {
                _request = request;
            }

            public void Add(object row, params string[] names)
            {
                if (_request.Query != null
                    && !names.Any(n => n != null && n.IndexOf(_request.Query, StringComparison.OrdinalIgnoreCase) >= 0))
                    return;
                _rows.Add(row);
            }

            public void Skipped(string what, Element element, Exception ex)
            {
                string id;
                try
                {
                    id = element?.Id.GetValue().ToString() ?? "?";
                }
                catch (Exception idError)
                {
                    id = "? (" + Describe(idError) + ")";
                }
                Warn($"{what} {id} skipped: {Describe(ex)}");
            }

            public void Warn(string line)
            {
                if (_warnings.Count < MaxWarnings)
                    _warnings.Add(line);
                else
                    _warningsLeftOut++;
            }

            public ModelItemsResult ToResult()
            {
                var warnings = new List<string>(_warnings);
                if (_warningsLeftOut > 0)
                    warnings.Add($"{_warningsLeftOut} more warning(s) not listed.");

                List<object> items = _request.Limit.HasValue
                    ? _rows.Take(_request.Limit.Value).ToList()
                    : new List<object>(_rows);

                return new ModelItemsResult
                {
                    Success = true,
                    Kind = _request.Kind,
                    Items = items,
                    Total = _rows.Count,
                    Truncated = items.Count < _rows.Count,
                    Warnings = warnings,
                    Error = null,
                };
            }
        }
    }
}
