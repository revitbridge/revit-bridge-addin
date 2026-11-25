using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Commands.Views
{
    public sealed class ActivateViewCommand : ViewCommandBase
    {
        private const int CandidateLimit = 20;
        public override string CommandName => "activate_view_in_revit";

        public ActivateViewCommand(UIApplication uiApp) : base(uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            JToken token = parameters?["name"];
            if (token?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                throw new ArgumentException("Give the name of an existing view.");
            string name = (string)token;
            return RunInRevit(app => Activate(app, name));
        }

        private static JObject Activate(UIApplication app, string name)
        {
            UIDocument uiDoc = app.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
            Document doc = uiDoc.Document;
            if (doc.IsModifiable || doc.IsReadOnly)
                throw new InvalidOperationException("Finish the active edit or read-only operation before switching views.");

            // Sheets can share a name. Let Revit filter both name parameters before
            // materializing candidates, then compare the public view name itself.
            using var byView = NameFilter(BuiltInParameter.VIEW_NAME, name);
            using var bySheet = NameFilter(BuiltInParameter.SHEET_NAME, name);
            using var either = new LogicalOrFilter(byView, bySheet);
            using var collector = new FilteredElementCollector(doc);
            var matches = collector.OfClass(typeof(View)).WherePasses(either).Cast<View>()
                .Where(v => CanOpen(v) && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                .Take(CandidateLimit + 1).ToList();
            if (matches.Count == 0)
            {
                using var all = new FilteredElementCollector(doc);
                var candidates = all.OfClass(typeof(View)).Cast<View>().Where(CanOpen).Take(CandidateLimit + 1).ToList();
                throw new ArgumentException($"View '{name}' was not found. Available views: {Describe(candidates)}");
            }
            if (matches.Count > 1)
                throw new ArgumentException($"Several views are named '{name}'. Matching views: {Describe(matches)}");

            View target = matches[0];
            if (uiDoc.ActiveView?.Id != target.Id)
                uiDoc.ActiveView = target;
            View active = uiDoc.ActiveView;
            if (active?.Id != target.Id)
                throw new InvalidOperationException($"Revit did not activate '{name}'.");
            return new JObject
            {
                ["view_id"] = active.Id.GetValue(),
                ["view_name"] = active.Name,
                ["view_type"] = active.ViewType.ToString()
            };
        }

        private static ElementParameterFilter NameFilter(BuiltInParameter parameter, string name)
        {
            var id = ElementIdCompat.FromInt((int)parameter);
#if REVIT2023_OR_GREATER
            // Revit 2023 introduced the case-insensitive two-argument CreateEqualsRule.
            using var rule = ParameterFilterRuleFactory.CreateEqualsRule(id, name);
#else
            using var rule = ParameterFilterRuleFactory.CreateEqualsRule(id, name, false);
#endif
            return new ElementParameterFilter(rule);
        }

        private static bool CanOpen(View view) => !view.IsTemplate && view.ViewType != ViewType.Internal
            && view.ViewType != ViewType.ProjectBrowser && view.ViewType != ViewType.SystemBrowser
            && view.ViewType != ViewType.Undefined;

        private static string Describe(List<View> views)
        {
            if (views.Count == 0)
                return "(none)";
            var names = views.Take(CandidateLimit).Select(v => $"{v.Name} ({v.ViewType}, id {v.Id.GetValue()})");
            return string.Join(", ", names) + (views.Count > CandidateLimit ? ", ..." : "");
        }
    }
}
