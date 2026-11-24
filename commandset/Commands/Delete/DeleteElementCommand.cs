using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Delete
{
    public class DeleteElementCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private DeleteElementEventHandler _handler => (DeleteElementEventHandler)Handler;

        public override string CommandName => "delete_element";

        public DeleteElementCommand(UIApplication uiApp)
            : base(new DeleteElementEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    // 解析数组参数
                    var elementIds = parameters?["elementIds"]?.ToObject<string[]>();
                    if (elementIds == null || elementIds.Length == 0)
                    {
                        throw new ArgumentException("元素ID列表不能为空");
                    }

                    // 设置要删除的元素ID数组
                    _handler.ElementIds = elementIds;

                    // 触发外部事件并等待完成
                    if (RaiseAndWaitForCompletion(15000))
                    {
                        if (_handler.IsSuccess)
                        {
                            return new { deleted = true, count = _handler.DeletedCount };
                        }
                        else
                        {
                            // 处理器给出原因时报告原因（如"提交被 Revit 拒绝: ..."），外层 catch 再加"删除元素失败: "。
                            // Report the handler's reason when it gives one (such as "提交被 Revit 拒绝: ...");
                            // the catch below prefixes "删除元素失败: ".
                            throw new Exception(string.IsNullOrEmpty(_handler.ErrorMessage) ? "删除元素失败" : _handler.ErrorMessage);
                        }
                    }
                    else
                    {
                        throw new TimeoutException("删除元素操作超时");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"删除元素失败: {ex.Message}");
                }
            }
        }
    }
}
