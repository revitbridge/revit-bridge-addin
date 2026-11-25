using System.Security.Cryptography;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Commands.Views;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Commands.Access
{
    public sealed class GetSelectionStateCommand : ViewCommandBase
    {
        public override string CommandName => "get_selection_state_in_revit";
        protected override string RequestKind => "selection";

        public GetSelectionStateCommand(UIApplication uiApp) : base(uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            JToken focus = parameters?["focus"];
            JToken prompt = parameters?["prompt"];
            if (focus?.Type is not (null or JTokenType.Null or JTokenType.Boolean))
                throw new ArgumentException("focus must be a boolean.");
            if (prompt?.Type is not (null or JTokenType.Null or JTokenType.String))
                throw new ArgumentException("prompt must be a string.");
            bool requestFocus = (bool?)focus ?? false;
            string statusText = (string)prompt;

            var snapshot = RunInRevit(app =>
            {
                var uiDoc = app.ActiveUIDocument ?? throw new InvalidOperationException("No active document.");
                var document = uiDoc.Document;
                if (document.IsModifiable)
                    throw new InvalidOperationException("Finish the active transaction before reading the selection.");

                // Take the baseline before inviting the user to change the selection.
                string documentId = document.GetHashCode().ToString();
                var ids = uiDoc.Selection.GetElementIds().Select(id => id.GetValue()).ToArray();
                if (requestFocus)
                    FocusWindow(app.MainWindowHandle, statusText);
                return (documentId, ids);
            });

            return new JObject
            {
                ["count"] = snapshot.ids.Length,
                ["stamp"] = ComputeStamp(snapshot.ids),
                ["document_id"] = snapshot.documentId
            };
        }

        private static void FocusWindow(IntPtr window, string prompt)
        {
            if (window == IntPtr.Zero)
                return;
            if (!SelectionWindow.SetForegroundWindow(window))
                SelectionWindow.FlashTaskbar(window);
            else if (SelectionWindow.IsIconic(window))
                SelectionWindow.Restore(window);
            if (!string.IsNullOrEmpty(prompt))
                SelectionWindow.SetStatusText(window, prompt);
        }

        private static string ComputeStamp(long[] ids)
        {
            Array.Sort(ids);
            // All supported Revit targets are little-endian Windows. Fixed-width IDs
            // keep the hash independent of locale, enumeration order and Revit year.
            var bytes = new byte[checked(ids.Length * sizeof(long))];
            Buffer.BlockCopy(ids, 0, bytes, 0, bytes.Length);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
