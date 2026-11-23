namespace RevitMCPCommandSet.Models.Common
{
    /// <summary>
    /// <para>list_model_items 的返回结构。成功时 Success 为 true、Error 为 null；失败时 Success 为 false、
    /// Items 为空、Error 给出 Code 与 Message。Total 是按 q 过滤之后、按 limit 截断之前的行数，
    /// Truncated 表示 Items 比 Total 少。读不出的行不进 Items，各记一行到 Warnings。</para>
    /// <para>Reply of list_model_items. On success Success is true and Error null; on failure
    /// Success is false, Items empty and Error holds Code and Message. Total counts the rows
    /// after the q filter and before the limit; Truncated says Items holds fewer than Total.
    /// A row that cannot be read is left out of Items and gets a line in Warnings.</para>
    /// </summary>
    public class ModelItemsResult
    {
        public bool Success { get; set; }

        /// <summary>The requested kind, as sent (null when it was missing or not a string).</summary>
        public string Kind { get; set; }

        /// <summary>
        /// Rows: ModelLevelItem (levels), ModelLinkItem (links), FamilyTypeInfo (types),
        /// TypeCategoryItem (type_categories).
        /// </summary>
        public List<object> Items { get; set; } = new List<object>();

        public int Total { get; set; }

        public bool Truncated { get; set; }

        public List<string> Warnings { get; set; } = new List<string>();

        public ModelItemsError Error { get; set; }

        public static ModelItemsResult Fail(string kind, string code, string message)
        {
            return new ModelItemsResult
            {
                Success = false,
                Kind = kind,
                Error = new ModelItemsError { Code = code, Message = message },
            };
        }
    }

    /// <summary>A structured failure of list_model_items; Code is one of ModelItemsErrorCodes.</summary>
    public class ModelItemsError
    {
        public string Code { get; set; }
        public string Message { get; set; }
    }

    /// <summary>The values of ModelItemsError.Code.</summary>
    public static class ModelItemsErrorCodes
    {
        /// <summary>kind is missing or not levels, links, types or type_categories.</summary>
        public const string UnknownKind = "unknown_kind";
        /// <summary>category is not the name of a BuiltInCategory (OST_*).</summary>
        public const string BadCategory = "bad_category";
        /// <summary>link_id names no Revit link instance of the open document.</summary>
        public const string UnknownLink = "unknown_link";
        /// <summary>link_id names a link instance whose document is not loaded.</summary>
        public const string LinkNotLoaded = "link_not_loaded";
        /// <summary>A parameter has the wrong type or value, or is not used with this kind.</summary>
        public const string BadParams = "bad_params";
        /// <summary>No document is open in Revit.</summary>
        public const string NoDocument = "no_document";
        /// <summary>An earlier call that timed out is still running in Revit.</summary>
        public const string Busy = "busy";
        /// <summary>Revit did not answer in time.</summary>
        public const string Timeout = "timeout";
        /// <summary>Reading the model failed as a whole; Message names the exception.</summary>
        public const string Failed = "failed";
    }

    /// <summary>One level: {Id, Name, ElevationMm}, the shape query("levels") already returns.</summary>
    public class ModelLevelItem
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public double ElevationMm { get; set; }
    }

    /// <summary>
    /// <para>一个 Revit 链接实例。未加载时 DocumentTitle 为 null。Origin 是实例总变换的原点，毫米。
    /// Rotated 表示总变换不只是平移。</para>
    /// <para>One Revit link instance. DocumentTitle is null when the link is not loaded. Origin is
    /// the origin of the instance's total transform, in millimetres. Rotated is true when that
    /// transform is more than a translation.</para>
    /// </summary>
    public class ModelLinkItem
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public long TypeId { get; set; }
        public string TypeName { get; set; }
        public bool Loaded { get; set; }
        public string DocumentTitle { get; set; }
        public PointMm Origin { get; set; }
        public bool Rotated { get; set; }
    }

    /// <summary>A point in millimetres.</summary>
    public class PointMm
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    /// <summary>
    /// <para>有元素类型的一个类别：Category 是 BuiltInCategory 名（OST_*），DisplayName 是 Revit 界面语言下的
    /// 类别名，Count 是该类别的元素类型数（与 kind types 给这个类别时的行数相同）。</para>
    /// <para>A category that has element types: Category is the BuiltInCategory name (OST_*),
    /// DisplayName the category name in Revit's UI language, Count the number of element types
    /// in it (the number of rows kind types returns for that category).</para>
    /// </summary>
    public class TypeCategoryItem
    {
        public string Category { get; set; }
        public string DisplayName { get; set; }
        public int Count { get; set; }
    }
}
