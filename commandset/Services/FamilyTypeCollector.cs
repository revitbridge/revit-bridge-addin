using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// <para>族类型的收集与转换，供 get_available_family_types 与 list_model_items（kind types）使用。</para>
    /// <para>Collects family types and converts them to FamilyTypeInfo, for
    /// get_available_family_types and list_model_items (kind types).</para>
    /// </summary>
    internal static class FamilyTypeCollector
    {
        /// <summary>
        /// <para>类别名转成 BuiltInCategory 的整数值；解析不了的名字丢弃（与以前相同，用 Enum.TryParse）。</para>
        /// <para>Category names as BuiltInCategory integer values; a name that does not parse is
        /// dropped (as before, through Enum.TryParse).</para>
        /// </summary>
        public static List<int> ParseCategoryIds(IEnumerable<string> categoryNames)
        {
            var validCategoryIds = new List<int>();
            if (categoryNames == null)
                return validCategoryIds;

            foreach (var categoryName in categoryNames)
            {
                if (Enum.TryParse(categoryName, out BuiltInCategory bic))
                {
                    validCategoryIds.Add((int)bic);
                }
            }
            return validCategoryIds;
        }

        /// <summary>
        /// <para>没给类别：可载入族的类型，加上墙、楼板、屋顶、天花板、幕墙系统五种系统族类型，依此顺序（与以前相同）。
        /// 给了类别：这些类别的全部元素类型。先是上面那份列表里属于这些类别的（顺序不变），
        /// 再接上其余的（管道、风管、楼梯、栏杆等类型），按收集器的顺序。</para>
        /// <para>Without categories: loadable family symbols, then the system types of five
        /// classes (wall, floor, roof, ceiling, curtain system), in this order (as before).
        /// With categories: every element type of those categories. First the ones from that
        /// list, in the same order, then the others (pipe, duct, stair, railing types and so
        /// on) in collector order.</para>
        /// <para>类别判断逐个容错：读不出类别（抛异常）的元素类型当作不属于这些类别，跳过，每个只报一次给
        /// <paramref name="categoryUnreadable"/>（可为 null：get_available_family_types 的回复没有放警告的地方）。
        /// 不抛异常时结果与逐个直接判断完全相同。</para>
        /// <para>The category test is tolerant per element: an element type whose category cannot
        /// be read (it throws) counts as not in those categories and is skipped, reported once to
        /// <paramref name="categoryUnreadable"/> (may be null: get_available_family_types has no
        /// place for warnings in its reply). Without exceptions the result is exactly that of the
        /// plain test.</para>
        /// </summary>
        public static IEnumerable<ElementType> Collect(Document doc, List<int> categoryIds,
            Action<ElementType, Exception> categoryUnreadable = null)
        {
            // 可载入族
            var familySymbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>();
            // 系统族类型（墙、楼板等）
            var systemTypes = new List<ElementType>();
            systemTypes.AddRange(new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<ElementType>());
            systemTypes.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<ElementType>());
            systemTypes.AddRange(new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<ElementType>());
            systemTypes.AddRange(new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<ElementType>());
            systemTypes.AddRange(new FilteredElementCollector(doc).OfClass(typeof(CurtainSystemType)).Cast<ElementType>());
            // 合并结果
            var allElements = familySymbols
                .Cast<ElementType>()
                .Concat(systemTypes)
                .ToList();

            if (categoryIds == null || !categoryIds.Any())
            {
                return allElements;
            }

            // 类别过滤。以前只在上面六个类里找，管道、风管、楼梯、栏杆等类型找不到；
            // 现在再从全部元素类型里补上这些类别中其余的，以前就有的那部分结果与顺序不变。
            // Category filter. The six classes above miss pipe, duct, stair, railing and other
            // types; the rest of these categories' element types are appended, so the part the
            // old list already had keeps its content and order.
            var unreadable = new HashSet<long>();
            bool InCategories(ElementType et)
            {
                try
                {
                    return IsInCategories(et, categoryIds);
                }
                catch (Exception ex)
                {
                    // 两轮都可能碰到同一个元素：只报一次。
                    // Both passes can meet the same element: report it once.
                    if (unreadable.Add(et.Id.GetValue()))
                        categoryUnreadable?.Invoke(et, ex);
                    return false;
                }
            }

            var listed = allElements.Where(InCategories).ToList();
            var listedIds = new HashSet<long>(listed.Select(et => et.Id.GetValue()));
            var others = new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .OfType<ElementType>()
                .Where(et =>
                {
                    long id = et.Id.GetValue();
                    return !listedIds.Contains(id) && !unreadable.Contains(id) && InCategories(et);
                });

            return listed.Concat(others);
        }

        internal static bool IsInCategories(ElementType et, List<int> categoryIds)
        {
            // ElementId.Value (Int64) is new in Revit 2024; before it, ElementId.IntegerValue.
#if REVIT2024_OR_GREATER
            var categoryId = et.Category?.Id.Value;
#else
            var categoryId = et.Category?.Id.IntegerValue;
#endif
            return categoryId != null && categoryIds.Contains((int)categoryId.Value);
        }

        /// <summary>
        /// <para>名称过滤用的族名：可载入族取 FamilyName，系统族取 SYMBOL_FAMILY_NAME_PARAM，读不到为空串。</para>
        /// <para>Family name for the name filter: FamilyName of a family symbol, else
        /// SYMBOL_FAMILY_NAME_PARAM, else an empty string.</para>
        /// </summary>
        public static string FilterFamilyName(ElementType et)
        {
            return et is FamilySymbol fs ? fs.FamilyName : et.get_Parameter(
                BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM)?.AsString() ?? "";
        }

        /// <summary>
        /// <para>转换为 FamilyTypeInfo；系统族读不到族名时用类名去掉 "Type"。</para>
        /// <para>Converts to FamilyTypeInfo; a system type without a family name gets its class
        /// name without "Type".</para>
        /// </summary>
        public static FamilyTypeInfo ToInfo(ElementType et)
        {
            string familyName;
            if (et is FamilySymbol fs)
            {
                familyName = fs.FamilyName;
            }
            else
            {
                Parameter param = et.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                familyName = param?.AsString() ?? et.GetType().Name.Replace("Type", "");
            }
            return new FamilyTypeInfo
            {
                // ElementId.Value (Int64) is new in Revit 2024; before it, ElementId.IntegerValue.
#if REVIT2024_OR_GREATER
                FamilyTypeId = et.Id.Value,
#else
                FamilyTypeId = et.Id.IntegerValue,
#endif
                UniqueId = et.UniqueId,
                FamilyName = familyName,
                TypeName = et.Name,
                Category = et.Category?.Name
            };
        }
    }
}
