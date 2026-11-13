using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Commands.AddinTest
{
    /// <summary>
    /// <para>活动平面视图的缩放与"模型点到屏幕像素"的换算（照搬评测里已验证的 screen_point.cs、zoom_to.cs）。
    /// 只在 API 上下文里调用。</para>
    /// <para>Zooming the active plan view and mapping a model point to a screen pixel (from the
    /// evaluation's verified screen_point.cs and zoom_to.cs). Call in API context only.</para>
    /// </summary>
    internal static class ViewTools
    {
        public const double MillimetresPerFoot = 304.8;

        // 点离视图边缘不到 2% 视为看不到（评测里用的同一界限）。
        // A point within 2 % of the view's edge counts as not visible (the evaluation's bound).
        private const double EdgeFraction = 0.02;

        public static UIView ActiveUIView(UIApplication app, out View view)
        {
            UIDocument uidoc = app.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
            view = uidoc.ActiveView ?? throw new InvalidOperationException("No active view.");
            ElementId viewId = view.Id;
            UIView uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == viewId);
            if (uiView == null)
                throw new InvalidOperationException("The active view has no open window: " + view.Name);
            return uiView;
        }

        public static int[] Rectangle(UIView uiView)
        {
            Rectangle rect = uiView.GetWindowRectangle();
            return new[] { rect.Left, rect.Top, rect.Right, rect.Bottom };
        }

        /// <summary>Zoom to an element with a margin, or to fit when <paramref name="elementId"/> is null.</summary>
        public static void Zoom(UIApplication app, long? elementId, double marginMm)
        {
            UIView uiView = ActiveUIView(app, out View view);
            if (elementId == null)
            {
                uiView.ZoomToFit();
                return;
            }
            Document document = app.ActiveUIDocument.Document;
            Element element = document.GetElement(ElementIdCompat.FromLong(elementId.Value))
                ?? throw new InvalidOperationException("Element not found: " + elementId.Value);
            BoundingBoxXYZ box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null)
                ?? throw new InvalidOperationException("Element " + elementId.Value + " has no bounding box.");
            double margin = marginMm / MillimetresPerFoot;
            var pad = new XYZ(margin, margin, 0);
            uiView.ZoomAndCenterRectangle(box.Min - pad, box.Max + pad);
        }

        /// <summary>
        /// <para>模型点在活动视图里的屏幕像素；点不在可见范围内时抛异常。</para>
        /// <para>The screen pixel of a model point in the active view; throws when the point is not in
        /// the visible part.</para>
        /// </summary>
        public static int[] ScreenPoint(UIView uiView, View view, XYZ point, string what)
        {
            Rectangle rect = uiView.GetWindowRectangle();
            IList<XYZ> corners = uiView.GetZoomCorners();
            XYZ c0 = corners[0], c1 = corners[1], right = view.RightDirection, up = view.UpDirection;
            double fr = (point - c0).DotProduct(right) / (c1 - c0).DotProduct(right);
            double fu = (point - c0).DotProduct(up) / (c1 - c0).DotProduct(up);
            if (!(fr > EdgeFraction && fr < 1 - EdgeFraction && fu > EdgeFraction && fu < 1 - EdgeFraction))
                throw new InvalidOperationException(what + " is outside the visible part of the view; zoom out or pass zoom_to.");
            int x = (int)Math.Round(rect.Left + fr * (rect.Right - rect.Left));
            int y = (int)Math.Round(rect.Bottom - fu * (rect.Bottom - rect.Top));
            return new[] { x, y };
        }

        /// <summary>A point on the element to click: on its location line at the fraction, or its location point.</summary>
        public static XYZ PickPoint(Document document, View view, long elementId, double fraction)
        {
            Element element = document.GetElement(ElementIdCompat.FromLong(elementId))
                ?? throw new InvalidOperationException("Element not found: " + elementId);
            if (element.IsHidden(view) || (element.Category != null && view.GetCategoryHidden(element.Category.Id)))
                throw new InvalidOperationException("Element " + elementId + " is hidden in the active view.");
            if (element.Location is LocationCurve curve && curve.Curve != null)
                return curve.Curve.Evaluate(fraction, true);
            if (element.Location is LocationPoint point && point.Point != null)
                return point.Point;
            throw new InvalidOperationException("Element " + elementId + " has no location line or point to click.");
        }
    }
}
