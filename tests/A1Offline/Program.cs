using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Bridge;
using RevitMCPCommandSet.Commands.Views;
using RevitMCPSDK.API.Interfaces;

internal static class Program
{
    static int errors;
    internal static void Check(bool ok, string note)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + note);
        if (!ok)
            errors++;
    }
    internal static bool Error(Action action, params string[] fragments)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception e)
        {
            return fragments.All(fragment => e.Message.Contains(fragment));
        }
    }
    static string directory;
    static void Emit(ImageExportOptions o, int height, bool noise)
    {
        directory = Path.GetDirectoryName(o.FilePath);
        byte[] pixels = new byte[height * 3 * o.PixelSize];
        if (noise)
            new Random(94).NextBytes(pixels);
        var bitmap = BitmapSource.Create(o.PixelSize, height, 96, 96, PixelFormats.Rgb24, null, pixels, 3 * o.PixelSize);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var f = File.Create(o.FilePath + " - Test.png");
        encoder.Save(f);
    }
    sealed class Registry : ICommandRegistry
    {
        public bool TryGetCommand(string name, out IRevitCommand command)
        {
            command = null;
            return name is not ("activate_view_in_revit" or "get_selection_state_in_revit");
        }
    }
    static int Main()
    {
        var doc = new Document();
        var plan = new View { Id = new(1), Name = "Plan", ViewType = ViewType.FloorPlan };
        var otherView = new View { Id = new(2), Name = "Other", ViewType = ViewType.FloorPlan };
        var sheet = new View { Id = new(3), Name = "Other", ViewType = ViewType.DrawingSheet };
        var template = new View { Id = new(4), Name = "Plan", IsTemplate = true };
        doc.Views.AddRange([plan, otherView, sheet, template]);
        var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc, ActiveView = otherView } };
        var caps = new GetBridgeCapabilitiesCommand(app);
        JObject reported = (JObject)caps.Execute(null, "c");
        var methods = reported["methods"].Values<string>().ToArray();
        Check((int)reported["protocol"] == 2 && methods.Contains("activate_view_in_revit") && methods.Contains("export_view_image_in_revit"), "capabilities protocol and view methods");
        var filtered = new GetBridgeCapabilitiesCommand(app, new Registry());
        var registered = ((JObject)filtered.Execute(null, "c"))["methods"].Values<string>().ToArray();
        Check(!registered.Contains("activate_view_in_revit") && registered.Contains("export_view_image_in_revit"), "capabilities respects registration");
        Check(methods.Contains("get_selection_state_in_revit") && !registered.Contains("get_selection_state_in_revit")
            && !methods.Contains("trigger_selection_in_revit"), "capabilities reports only the registered selection observer");
        SelectionStateChecks.Run();
        CheckActivation(app, doc, plan);
        CheckExport(app, doc, plan, otherView);
        return errors > 0 ? 1 : 0;
    }

    static void CheckActivation(UIApplication app, Document doc, View plan)
    {
        var activate = new ActivateViewCommand(app);
        JObject Call(string name) => (JObject)activate.Execute(new JObject { ["name"] = name }, "a");
        var response = Call("plan");
        Check(response.Count == 3 && (string)response["view_name"] == "Plan" && (long)response["view_id"] == 1 && (string)response["view_type"] == "FloorPlan" && app.ActiveUIDocument.ActiveView == plan, "activate unique and ignore template");
        Check(Error(() => Call("Other"), "id 2", "id 3") && app.ActiveUIDocument.ActiveView == plan, "duplicate lists both candidates");
        Check(Error(() => Call("missing"), "Plan", "Other"), "missing name lists candidates");
        doc.IsModifiable = true;
        Check(Error(() => Call("Plan"), "active edit"), "transaction guard");
        doc.IsModifiable = false;
        doc.IsReadOnly = true;
        Check(Error(() => Call("Plan"), "read-only"), "read-only guard");
        doc.IsReadOnly = false;
        Check(Error(() => Call("  "), "name"), "empty name");
        app.Withhold = true;
        Check(Error(() => Call("Plan"), "did not start"), "queued view request withdraws");
        app.Delayed.Execute(app);
        app.Withhold = false;
    }

    static void CheckExport(UIApplication app, Document doc, View plan, View otherView)
    {
        var export = new ExportViewImageCommand(app);
        app.ActiveUIDocument.ActiveView = plan;
        JObject Call(JObject p) => (JObject)export.Execute(p, "e");
        string tmp = Path.GetTempPath();
        var before = Directory.GetDirectories(tmp, "revit-bridge-*").ToHashSet();
        doc.ExportImage = options =>
        {
            Check(options.ExportRange == ExportRange.VisibleRegionOfCurrentView && options.PixelSize == 1600, "default current view and width");
            Emit(options, 80, false);
        };
        var response = Call(new JObject());
        Check(response.Count == 4 && (int)response["width"] == 1600 && (int)response["height"] == 80 && (string)response["view_name"] == "Plan", "default response");
        Check(!Directory.Exists(directory), "cleanup on success");
        foreach (var (height, expectedWidth) in new[] { (330, 1200), (880, 800), (1500, 0) })
        {
            doc.ExportImage = options =>
            {
                Check(options.ExportRange == ExportRange.SetOfViews && options.Selected.Single() == otherView.Id &&
                    app.ActiveUIDocument.ActiveView == plan, "inactive view exports in place");
                Emit(options, height, true);
            };
            var args = new JObject { ["view_id"] = 2 };
            if (expectedWidth == 0)
                Check(Error(() => Call(args), "exceeds"), "too large at 800");
            else
            {
                var result = Call(args);
                byte[] raw = Convert.FromBase64String((string)result["png_base64"]);
                using var ms = new MemoryStream(raw);
                var image = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check((int)result["width"] == expectedWidth && image.PixelWidth == (int)result["width"] &&
                    image.PixelHeight == (int)result["height"] && raw.Length <= 1024 * 1024, "actual dimensions and cap at " + expectedWidth);
            }
            Check(!Directory.Exists(directory), "cleanup after size fallback");
        }
        doc.ExportImage = options => Emit(options, 3000, true);
        Check(Error(() => Call(new JObject { ["width"] = 640 }), "exceeds"), "narrow requests never upscale");
        doc.ExportImage = options =>
        {
            Emit(options, 50, false);
            throw new Exception("partial export");
        };
        Check(Error(() => Call(new JObject()), "partial export") && !Directory.Exists(directory), "partial file cleanup");
        Check(Error(() => Call(new JObject { ["view_id"] = 50 }), "non-template"), "unknown view");
        Check(Error(() => Call(new JObject { ["width"] = 1601 }), "1600") && Error(() => Call(new JObject { ["width"] = 0 }), "positive"), "width bounds");
        app.Withhold = true;
        Check(Error(() => Call(new JObject()), "did not start"), "export queue withdrawal");
        app.Delayed.Execute(app);
        Check(before.SetEquals(Directory.GetDirectories(tmp, "revit-bridge-*")), "no export dirs remain");
    }
}
