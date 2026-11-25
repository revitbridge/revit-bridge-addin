using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Access;
using static Program;

internal static class SelectionStateChecks
{
    internal static void Run()
    {
        var document = new Document { IsReadOnly = true };
        var uiDoc = new UIDocument { Document = document };
        var app = new UIApplication { ActiveUIDocument = uiDoc, MainWindowHandle = (IntPtr)42 };
        var command = new GetSelectionStateCommand(app);
        JObject Poll(JObject parameters = null) => (JObject)command.Execute(parameters, "selection");

        var empty = Poll();
        Check(empty.Count == 3 && (int)empty["count"] == 0 &&
            (string)empty["stamp"] == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" &&
            (string)empty["document_id"] == document.GetHashCode().ToString(), "empty read-only document and exact wire shape");
        Check(JToken.DeepEquals(empty, Poll(new JObject { ["focus"] = null, ["prompt"] = null })),
            "null optional arguments behave like omitted ones");

        uiDoc.Selection.Ids = [new(4294967297), new(-7), new(2)];
        var initial = Poll();
        Check((int)initial["count"] == 3 &&
            (string)initial["stamp"] == "11ce6869680eb18f6b04aafe1baf5fe43523256695244b47a61f37d022ae12d4",
            "SHA-256 over numerically sorted signed 64-bit IDs");
        Array.Reverse(uiDoc.Selection.Ids);
        Check(JToken.DeepEquals(initial, Poll()), "reordering preserves the stamp");
        uiDoc.Selection.Ids[2] = new ElementId(1);
        var changed = Poll(new JObject { ["focus"] = false, ["prompt"] = "unused" });
        Check((int)changed["count"] == 3 && (string)changed["stamp"] != (string)initial["stamp"],
            "same count and same low 32 bits can still change the stamp");
        Check(SelectionWindow.Calls.Count == 0, "ordinary polls leave the window alone");

        var secondDoc = new UIDocument { Document = new Document() };
        secondDoc.Selection.Ids = uiDoc.Selection.Ids;
        app.ActiveUIDocument = secondDoc;
        var switched = Poll();
        Check((string)switched["stamp"] == (string)changed["stamp"] &&
            (string)switched["document_id"] == secondDoc.Document.GetHashCode().ToString(), "active document identity is read on each poll");
        app.ActiveUIDocument = uiDoc;

        const string prompt = "在 Revit 中选择";
        var focus = new JObject { ["focus"] = true, ["prompt"] = prompt };
        SelectionWindow.Granted = false;
        Check(JToken.DeepEquals(changed, Poll(focus)) &&
            SelectionWindow.TakeCalls().SequenceEqual(["foreground", "flash", prompt]), "foreground refusal flashes once and preserves the result and prompt");
        SelectionWindow.Granted = true;
        SelectionWindow.Minimized = true;
        Check(JToken.DeepEquals(changed, Poll(focus)) &&
            SelectionWindow.TakeCalls().SequenceEqual(["foreground", "is iconic", "restore", prompt]), "restore a minimized window on a successful foreground request");
        SelectionWindow.Minimized = false;
        Poll(new JObject { ["focus"] = true });
        Check(SelectionWindow.TakeCalls().SequenceEqual(["foreground", "is iconic"]), "foreground without a prompt");
        app.MainWindowHandle = IntPtr.Zero;
        Check(JToken.DeepEquals(changed, Poll(focus)) && SelectionWindow.Calls.Count == 0, "a missing Revit window cannot target another window");
        app.MainWindowHandle = (IntPtr)42;

        int reads = uiDoc.Selection.Reads;
        Check(Error(() => Poll(new JObject { ["focus"] = "true" }), "boolean") &&
            Error(() => Poll(new JObject { ["prompt"] = 17 }), "string"), "invalid parameter types are rejected");
        document.IsModifiable = true;
        Check(Error(() => Poll(focus), "transaction"), "reject an active transaction");
        document.IsModifiable = false;
        app.ActiveUIDocument = null;
        Check(Error(() => Poll(focus), "No active document"), "no active document");
        app.ActiveUIDocument = uiDoc;
        app.Withhold = true;
        Check(Error(() => Poll(focus), "did not start"), "withdraw an unstarted selection request");
        app.Delayed.Execute(app);
        Check(uiDoc.Selection.Reads == reads && SelectionWindow.Calls.Count == 0, "rejected and withdrawn calls cannot read or focus later");
        app.Withhold = false;
        uiDoc.Selection.Ids = [];
        Check(JToken.DeepEquals(empty, Poll()), "clearing the selection restores its original stamp");
        Check(document.ElementReads == 0 && secondDoc.Document.ElementReads == 0, "selection polling never resolves an element");
    }
}

namespace RevitMCPCommandSet.Commands.Access
{
    // Stand in for user32. No test takes foreground or sends input on the desktop.
    internal static class SelectionWindow
    {
        internal static bool Granted, Minimized;
        internal static readonly List<string> Calls = new();

        private static void Record(IntPtr window, string call)
        {
            if (window != (IntPtr)42)
                throw new InvalidOperationException("Wrong main window handle.");
            Calls.Add(call);
        }

        internal static string[] TakeCalls()
        {
            var result = Calls.ToArray();
            Calls.Clear();
            return result;
        }

        internal static bool SetForegroundWindow(IntPtr window)
        {
            Record(window, "foreground");
            return Granted;
        }

        internal static bool IsIconic(IntPtr window)
        {
            Record(window, "is iconic");
            return Minimized;
        }

        internal static void FlashTaskbar(IntPtr window) => Record(window, "flash");
        internal static void Restore(IntPtr window) => Record(window, "restore");
        internal static void SetStatusText(IntPtr window, string text) => Record(window, text);
    }
}
