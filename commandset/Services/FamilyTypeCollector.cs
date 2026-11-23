using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// <para>族类型的收集与转换，供 get_available_family_types 使用。</para>
    /// <para>Collects family types and converts them to FamilyTypeInfo, for
    /// get_available_family_types.</para>
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
        /// <para>可载入族的类型，加上墙、楼板、屋顶、天花板、幕墙系统五种系统族类型，依此顺序；
        /// 给了类别时只留这些类别的。</para>
        /// <para>Loadable family symbols, then the system types of five classes (wall, floor, roof,
        /// ceiling, curtain system), in this order; narrowed to the given categories when there
        /// are any.</para>
        /// </summary>
        public static IEnumerable<ElementType> Collect(Document doc, List<int> categoryIds)
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

            IEnumerable<ElementType> filteredElements = allElements;

            // 类别过滤
            if (categoryIds != null && categoryIds.Any())
            {
                filteredElements = filteredElements.Where(et => IsInCategories(et, categoryIds));
            }

            return filteredElements;
        }

        private static bool IsInCategories(ElementType et, List<int> categoryIds)
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
