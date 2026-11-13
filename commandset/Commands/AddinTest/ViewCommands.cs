using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Commands.AddinTest
{
    /// <summary>
    /// <para>zoom_view_in_revit：{"element_id": ..., "margin_mm": 2500} 或 {"fit": true}；只改视图。
    /// 返回视图名与视图矩形的屏幕坐标。</para>
    /// <para>zoom_view_in_revit: {"element_id": ..., "margin_mm": 2500} or {"fit": true}; changes the
    /// view only. Returns the view name and the view rectangle in screen pixels.</para>
    /// </summary>
    public class ZoomViewCommand : ApiCallCommandBase
    {
        public override string CommandName => "zoom_view_in_revit";

        public ZoomViewCommand(UIApplication uiApp)
            : base(uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            if (AddinTestSession.IsBusy)
                throw new InvalidOperationException("An add-in test is running in Revit; the view cannot be changed now.");
            long? elementId = ViewRequest.ElementId(parameters);
            bool fit = parameters?["fit"]?.Type == JTokenType.Boolean && parameters["fit"].Value<bool>();
            if (elementId == null && !fit)
                throw new ArgumentException("Give element_id, or fit: true.");
            double margin = ViewRequest.MarginMm(parameters, 2500);
            return CallApi(app =>
            {
                ViewTools.Zoom(app, elementId, margin);
                UIView uiView = ViewTools.ActiveUIView(app, out View view);
                return new JObject
                {
                    ["view"] = view.Name,
                    ["rect"] = new JArray(ViewTools.Rectangle(uiView)),
                };
            }, 30000);
        }
    }

    /// <summary>
    /// <para>capture_view_in_revit：{"element_id": 可空, "margin_mm": 1500, "path": 可空}。先缩放（若给了元素），
    /// 置前，把鼠标移出视图，只截视图矩形存 PNG。返回 path、width、height、view、foreground_ok。</para>
    /// <para>capture_view_in_revit: {"element_id": optional, "margin_mm": 1500, "path": optional}. Zoom
    /// first (when an element is given), bring Revit to the front, move the mouse out of the view and save
    /// the view rectangle only as PNG. Returns path, width, height, view and foreground_ok.</para>
    /// </summary>
    public class CaptureViewCommand : ApiCallCommandBase
    {
        public override string CommandName => "capture_view_in_revit";

        public CaptureViewCommand(UIApplication uiApp)
            : base(uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            if (AddinTestSession.IsBusy)
                throw new InvalidOperationException("An add-in test is running in Revit; capture the view after it ends.");
            long? elementId = ViewRequest.ElementId(parameters);
            double margin = ViewRequest.MarginMm(parameters, 1500);
            string path = Desktop.DefaultCapturePath("view");
            JToken requested = parameters?["path"];
            if (requested != null && requested.Type != JTokenType.Null)
            {
                string given = requested.Type == JTokenType.String ? requested.Value<string>() : null;
                if (string.IsNullOrWhiteSpace(given) || !Path.IsPathRooted(given) || !given.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("'path' must be an absolute path ending in .png.");
                if (File.Exists(given))
                    throw new ArgumentException("'path' already exists; a capture never overwrites a file: " + given);
                path = given;
            }

            var shown = (JObject)CallApi(app =>
            {
                if (elementId != null)
                    ViewTools.Zoom(app, elementId, margin);
                UIView uiView = ViewTools.ActiveUIView(app, out View view);
                return new JObject
                {
                    ["view"] = view.Name,
                    ["rect"] = new JArray(ViewTools.Rectangle(uiView)),
                    ["window"] = app.MainWindowHandle.ToInt64(),
                };
            }, 30000);

            Thread.Sleep(1000); // let Revit redraw before the picture
            var window = new IntPtr(shown["window"].Value<long>());
            bool foreground = Desktop.BringToFront(window);
            int[] rect = shown["rect"].ToObject<int[]>();
            int[] frame = Desktop.WindowRectangle(window);
            if (frame != null)
                Desktop.MoveCursor(frame[0] + 10, frame[1] + 10); // the title bar: no hover highlight in the view
            Thread.Sleep(300);
            int[] size = Desktop.CapturePng(rect[0], rect[1], rect[2], rect[3], path);
            return new JObject
            {
                ["path"] = path,
                ["width"] = size[0],
                ["height"] = size[1],
                ["view"] = shown["view"],
                ["foreground_ok"] = foreground,
            };
        }
    }

    internal static class ViewRequest
    {
        public static long? ElementId(JObject parameters)
        {
            JToken token = parameters?["element_id"];
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type != JTokenType.Integer)
                throw new ArgumentException("'element_id' must be an integer.");
            return token.Value<long>();
        }

        public static double MarginMm(JObject parameters, double fallback)
        {
            JToken token = parameters?["margin_mm"];
            if (token == null || token.Type == JTokenType.Null)
                return fallback;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw new ArgumentException("'margin_mm' must be a number.");
            double value = token.Value<double>();
            if (double.IsNaN(value) || value < 0 || value > 100000)
                throw new ArgumentException("'margin_mm' must be between 0 and 100000.");
            return value;
        }
    }
}
