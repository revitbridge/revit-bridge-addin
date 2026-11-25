global using System.Threading;
global using System.IO;
global using System.Collections.Generic;
global using System.Linq;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
namespace RevitMCPSDK.API.Interfaces
{
    public interface IRevitCommand
    {
        string CommandName { get; }
        object Execute(JObject p, string id);
    }
    public interface ICommandRegistry
    {
        bool TryGetCommand(string name, out IRevitCommand result);
    }
    public interface IWaitableExternalEventHandler
    {
        bool WaitForCompletion(int timeoutMilliseconds);
    }
}
namespace RevitMCPSDK.API.Base
{
    public abstract class ExternalEventCommandBase : IRevitCommand
    {
        protected IExternalEventHandler Handler;
        private readonly UIApplication app;
        protected ExternalEventCommandBase(IExternalEventHandler handler, UIApplication app)
        {
            Handler = handler;
            this.app = app;
        }
        public abstract string CommandName { get; }
        public abstract object Execute(JObject parameters, string id);
        protected bool RaiseAndWaitForCompletion(int ms)
        {
            if (app.Withhold)
            {
                app.Delayed = Handler;
                return false;
            }
            Handler.Execute(app);
            return ((IWaitableExternalEventHandler)Handler).WaitForCompletion(ms);
        }
    }
}
namespace RevitMCPCommandSet.Commands.AddinTest
{
    internal static class AddinTestSession
    {
        internal static bool IsBusy { get; set; }
    }
}
namespace Autodesk.Revit.UI
{
    public interface IExternalEventHandler
    {
        void Execute(UIApplication app);
        string GetName();
    }
    public class VendorApp
    {
        public string VersionNumber => "2024";
        public string VersionBuild => "test";
        public string VersionName => "fake";
    }
    public class UIApplication
    {
        public VendorApp Application = new();
        public UIDocument ActiveUIDocument;
        public IntPtr MainWindowHandle;
        public bool Withhold;
        public IExternalEventHandler Delayed;
    }
    public class UIDocument
    {
        public Document Document;
        public View ActiveView { get; set; }
        public Selection Selection { get; } = new();
    }
    public class Selection
    {
        public ElementId[] Ids = [];
        public int Reads;
        public ICollection<ElementId> GetElementIds()
        {
            Reads++;
            return Ids;
        }
    }
}
namespace Autodesk.Revit.DB
{
    public enum ViewType
    {
        FloorPlan, DrawingSheet, Internal, ProjectBrowser, SystemBrowser, Undefined
    }
    public enum BuiltInParameter
    {
        VIEW_NAME, SHEET_NAME
    }
    public enum ExportRange
    {
        VisibleRegionOfCurrentView, SetOfViews
    }
    public enum ImageFileType
    {
        PNG
    }
    public enum ZoomFitType
    {
        FitToPage
    }
    public enum FitDirectionType
    {
        Horizontal
    }
    public sealed record ElementId(long Value);
    public class Element
    {
        public ElementId Id;
    }
    public sealed class View : Element
    {
        public string Name;
        public bool IsTemplate;
        public ViewType ViewType;
    }
    public class Document
    {
        public bool IsModifiable, IsReadOnly;
        public List<View> Views = new();
        public int ElementReads;
        public Element GetElement(ElementId id)
        {
            ElementReads++;
            return Views.SingleOrDefault(v => v.Id == id);
        }
        public Action<ImageExportOptions> ExportImage;
    }
    public class ImageExportOptions : IDisposable
    {
        public string FilePath;
        public ImageFileType HLRandWFViewsFileType, ShadowViewsFileType;
        public ZoomFitType ZoomType;
        public FitDirectionType FitDirection;
        public int PixelSize;
        public ExportRange ExportRange;
        public List<ElementId> Selected;
        public void SetViewsAndSheets(IList<ElementId> ids) => Selected = ids.ToList();
        public void Dispose()
        {
        }
    }
    public class FilterRule : IDisposable
    {
        public string Match;
        public void Dispose()
        {
        }
    }
    public static class ParameterFilterRuleFactory
    {
        public static FilterRule CreateEqualsRule(ElementId p, string n) => new() { Match = n };
    }
    public class ElementParameterFilter : IDisposable
    {
        public string Match;
        public ElementParameterFilter(FilterRule r)
        {
            Match = r.Match;
        }
        public void Dispose()
        {
        }
    }
    public class LogicalOrFilter : IDisposable
    {
        public string Match;
        public LogicalOrFilter(ElementParameterFilter a, ElementParameterFilter b)
        {
            Match = a.Match;
        }
        public void Dispose()
        {
        }
    }
    public class FilteredElementCollector : IDisposable, IEnumerable<Element>
    {
        private IEnumerable<Element> rows;
        public FilteredElementCollector(Document d)
        {
            rows = d.Views;
        }
        public FilteredElementCollector OfClass(Type t) => this;
        public FilteredElementCollector WherePasses(LogicalOrFilter f)
        {
            rows = rows.Cast<View>().Where(v => v.Name.Equals(f.Match, StringComparison.OrdinalIgnoreCase));
            return this;
        }
        public IEnumerator<Element> GetEnumerator() => rows.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose()
        {
        }
    }
}
