using System.IO;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Commands.Views
{
    public sealed class ExportViewImageCommand : ViewCommandBase
    {
        private const int MaxWidth = 1600;
        private const int MaxPngBytes = 1024 * 1024;
        public override string CommandName => "export_view_image_in_revit";

        public ExportViewImageCommand(UIApplication uiApp) : base(uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            long requested = ReadPositiveInteger(parameters?["width"], "width") ?? MaxWidth;
            if (requested > MaxWidth)
                throw new ArgumentException($"width must be between 1 and {MaxWidth} pixels.");
            int width = (int)requested;
            long? viewId = ReadPositiveInteger(parameters?["view_id"], "view_id");

            // ExportImage only accepts a path. SetOfViews appends a Revit-generated
            // suffix, so isolate each request and remove even partial export output.
            string folder = Path.Combine(Path.GetTempPath(), "revit-bridge-" + Guid.NewGuid().ToString("N"));
            string prefix = Path.Combine(folder, "view");
            Directory.CreateDirectory(folder);
            try
            {
                string name = RunInRevit(app => Export(app, viewId, width, prefix));
                string[] files = Directory.GetFiles(folder, "*.png");
                if (files.Length != 1)
                    throw new InvalidOperationException($"Revit exported {files.Length} PNG files for one view.");
                return ReadPng(files[0], width, name);
            }
            finally
            {
                // A scanner or the indexer may still hold the PNG; never let cleanup replace the reply or the real error.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try { Directory.Delete(folder, recursive: true); break; }
                    catch (IOException) { Thread.Sleep(200); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(200); }
                }
            }
        }

        private static long? ReadPositiveInteger(JToken token, string name)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type != JTokenType.Integer || !long.TryParse(token.ToString(), out long value) || value <= 0)
                throw new ArgumentException($"{name} must be a positive integer.");
            return value;
        }

        private static string Export(UIApplication app, long? viewId, int width, string prefix)
        {
            UIDocument uiDoc = app.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
            Document doc = uiDoc.Document;
            View view = viewId.HasValue ? doc.GetElement(ElementIdCompat.FromLong(viewId.Value)) as View : uiDoc.ActiveView;
            if (view == null || view.IsTemplate)
                throw new ArgumentException("Choose an existing, non-template view to export.");
            using var options = new ImageExportOptions
            {
                ExportRange = view.Id == uiDoc.ActiveView?.Id ? ExportRange.VisibleRegionOfCurrentView : ExportRange.SetOfViews,
                FilePath = prefix,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = FitDirectionType.Horizontal,
                PixelSize = width
            };
            if (options.ExportRange == ExportRange.SetOfViews)
                options.SetViewsAndSheets(new[] { view.Id });
            doc.ExportImage(options);
            return view.Name;
        }

        // Runs on the bridge worker with a closed Revit export and no Revit objects.
        private static JObject ReadPng(string path, int width, string name)
        {
            using var file = File.OpenRead(path);
            var frame = BitmapFrame.Create(file, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (file.Length <= MaxPngBytes && frame.PixelWidth <= width)
            {
                file.Position = 0;
                using var reader = new BinaryReader(file);
                return Reply(reader.ReadBytes((int)file.Length), frame.PixelWidth, frame.PixelHeight, name);
            }

            // Never upscale small requests. If Revit exported wider than requested,
            // try the requested width before the standard fallback sizes.
            int previousWidth = frame.PixelWidth;
            foreach (int candidate in new[] { width, 1200, 800 })
            {
                if (candidate >= previousWidth || candidate > width)
                    continue;
                previousWidth = candidate;
                file.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = candidate;
                image.StreamSource = file;
                image.EndInit();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = new MemoryStream();
                encoder.Save(output);
                if (output.Length <= MaxPngBytes)
                    return Reply(output.ToArray(), image.PixelWidth, image.PixelHeight, name);
            }
            throw new InvalidOperationException("The PNG exceeds 1 MiB. Choose a smaller view region and try again.");
        }

        private static JObject Reply(byte[] bytes, int width, int height, string name) => new JObject
        {
            ["png_base64"] = Convert.ToBase64String(bytes),
            ["width"] = width,
            ["height"] = height,
            ["view_name"] = name
        };
    }
}
