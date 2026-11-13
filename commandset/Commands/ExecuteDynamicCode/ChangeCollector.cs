using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// <para>收集本文档本次提交的增、改、删（只认 TransactionCommitted；事务组回滚另有事件，不计入）。</para>
    /// <para>Collects what this document's commit added, modified and deleted (TransactionCommitted
    /// only; the group rollback raises its own event, which is not counted).</para>
    /// </summary>
    internal sealed class ChangeCollector
    {
        private readonly Document _document;
        private readonly HashSet<ElementId> _added = new HashSet<ElementId>();
        private readonly HashSet<ElementId> _modified = new HashSet<ElementId>();
        private readonly HashSet<ElementId> _deleted = new HashSet<ElementId>();

        public ChangeCollector(Document document)
        {
            _document = document;
        }

        /// <summary>Set when an event could not be read; the counts are then incomplete.</summary>
        public string Problem { get; private set; }

        public void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            // 事件处理器不能把异常抛回 Revit：记下来，由 Run 报告。
            // An event handler must not throw into Revit: record it for Run to report.
            try
            {
                if (e.Operation != UndoOperation.TransactionCommitted)
                    return;
                Document changed = e.GetDocument();
                if (changed is null || !changed.Equals(_document))
                    return;

                _added.UnionWith(e.GetAddedElementIds());
                _modified.UnionWith(e.GetModifiedElementIds());
                _deleted.UnionWith(e.GetDeletedElementIds());
            }
            catch (Exception ex)
            {
                Problem = "dry run: reading the document changes failed, the counts are incomplete: " + CodeExecutor.FormatRuntimeError(ex);
            }
        }

        /// <summary>
        /// <para>写入计数、类别与前 50 个 id。必须在事务组回滚之前调用（新增的元素此时还在）。</para>
        /// <para>Fill counts, categories and the first 50 ids. Call it before the group is rolled
        /// back, while the added elements still exist.</para>
        /// </summary>
        public void Fill(DryRunChanges changes)
        {
            FillSet(changes.Added, _added);
            FillSet(changes.Modified, _modified);

            changes.Deleted.Count = _deleted.Count;
            changes.Deleted.Ids = FirstIds(_deleted);
        }

        private void FillSet(DryRunChangeSet set, HashSet<ElementId> ids)
        {
            set.Count = ids.Count;
            set.Ids = FirstIds(ids);
            foreach (ElementId id in ids)
            {
                string key = CategoryKey(id);
                set.ByCategory.TryGetValue(key, out int count);
                set.ByCategory[key] = count + 1;
            }
        }

        private static List<long> FirstIds(HashSet<ElementId> ids)
        {
            return ids
                .Select(id => id.GetValue())
                .OrderBy(value => value)
                .Take(DryRunChanges.MaxIds)
                .ToList();
        }

        private string CategoryKey(ElementId id)
        {
            // 逐项容错：读不出的元素记在 "(none)"，不让一个元素拖垮整次统计。
            // Per item: an element that cannot be read counts under "(none)" instead of
            // failing the whole summary.
            try
            {
                Element element = _document.GetElement(id);
                if (element is null)
                    return "(none)";
                if (element is ElementType)
                    return "(types)";

                Category category = element.Category;
                if (category is null)
                    return "(none)";

                long value = category.Id.GetValue();
                if (value < int.MinValue || value > int.MaxValue)
                    return "(other)";
                var builtIn = (BuiltInCategory)(int)value;
                return Enum.IsDefined(typeof(BuiltInCategory), builtIn) ? builtIn.ToString() : "(other)";
            }
            catch (Exception ex)
            {
                if (Problem == null)
                    Problem = $"dry run: reading the category of element {id.GetValue()} failed: " + CodeExecutor.FormatRuntimeError(ex);
                return "(none)";
            }
        }
    }
}
