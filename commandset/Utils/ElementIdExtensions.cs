using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Extension methods for ElementId to handle API differences between Revit versions.
    /// In Revit 2024+, ElementId.Value returns long.
    /// In earlier versions, ElementId.IntegerValue returns int.
    /// </summary>
    public static class ElementIdExtensions
    {
        /// <summary>
        /// Gets the numeric value of an ElementId as a long, compatible with all Revit versions.
        /// </summary>
#if REVIT2024_OR_GREATER
        public static long GetValue(this ElementId id) => id.Value;
#else
        public static long GetValue(this ElementId id) => id.IntegerValue;
#endif

        /// <summary>
        /// Gets the numeric value of an ElementId as an int, compatible with all Revit versions.
        /// Use this when you need an int (e.g., for serialization to existing schemas).
        /// </summary>
#if REVIT2024_OR_GREATER
        public static int GetIntValue(this ElementId id) => (int)id.Value;
#else
        public static int GetIntValue(this ElementId id) => id.IntegerValue;
#endif
    }

    /// <summary>
    /// Creates an ElementId from an int id (as read from a request), compatible with all Revit versions.
    /// A class of its own: Nice3point.Revit.Extensions (a global using) also has an ElementIdExtensions,
    /// so a static call on that name would be ambiguous.
    /// </summary>
    internal static class ElementIdCompat
    {
#if REVIT2024_OR_GREATER
        // ElementId(Int64) is new in Revit 2024, where ElementId(Int32) became obsolete
        // (2024 and 2025 still have it, 2026 removed it).
        public static ElementId FromInt(int value) => new ElementId((long)value);
#else
        // Before Revit 2024 ElementId(Int32) is the only numeric constructor.
        public static ElementId FromInt(int value) => new ElementId(value);
#endif
    }
}
