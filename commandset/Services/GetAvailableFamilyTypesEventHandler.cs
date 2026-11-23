using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class GetAvailableFamilyTypesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        // 执行结果
        public List<FamilyTypeInfo> ResultFamilyTypes { get; private set; }

        // 状态同步对象
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        // 过滤条件
        public List<string> CategoryList { get; set; }
        public string FamilyNameFilter { get; set; }
        public int? Limit { get; set; }

        // 执行时间，略微比调用超时更短一些
        public bool WaitForCompletion(int timeoutMilliseconds = 12500)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;

                // 收集与类别过滤（FamilyTypeCollector）。读不出类别的元素类型算作不属于所求类别，跳过；
                // 这个回复（FamilyTypeInfo 数组）没有放警告的地方。
                // An element type whose category cannot be read counts as not in the requested
                // categories and is skipped; this reply (a FamilyTypeInfo array) has no place for warnings.
                List<int> validCategoryIds = FamilyTypeCollector.ParseCategoryIds(CategoryList);
                IEnumerable<ElementType> filteredElements = FamilyTypeCollector.Collect(doc, validCategoryIds);

                // 名称模糊匹配（同时匹配族名和类型名）
                if (!string.IsNullOrEmpty(FamilyNameFilter))
                {
                    filteredElements = filteredElements.Where(et =>
                    {
                        string familyName = FamilyTypeCollector.FilterFamilyName(et);

                        return familyName?.IndexOf(FamilyNameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               et.Name.IndexOf(FamilyNameFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                    });
                }

                // 限制返回数量
                if (Limit.HasValue && Limit.Value > 0)
                {
                    filteredElements = filteredElements.Take(Limit.Value);
                }

                // 转换为FamilyTypeInfo列表
                ResultFamilyTypes = filteredElements.Select(FamilyTypeCollector.ToInfo).ToList();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", "获取族类型失败: " + ex.Message);
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "GetAvailableFamilyTypes";
        }
    }
}
