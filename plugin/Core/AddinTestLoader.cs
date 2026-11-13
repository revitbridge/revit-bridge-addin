using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// <para>插件测试的加载器：revit-bridge.addin 里 Type="Command" 的那一条，只由 test_addin_command_in_revit
    /// 按其 AddInId 投递。它本身不做事，调用命令集经 AppDomain 数据槽 <see cref="SlotName"/> 登记的入口，
    /// 把自己的 ExternalCommandData 交给被测命令。没有待运行的测试（例如用户从外部工具里点了它）时什么也不做。
    /// 总是返回 Succeeded 或 Cancelled，不设 message，Revit 不弹失败框。</para>
    /// <para>The add-in test loader: the Type="Command" entry of revit-bridge.addin, posted by its AddInId
    /// from test_addin_command_in_revit only. It does nothing itself: it calls the entry the command set
    /// registered in the AppDomain data slot <see cref="SlotName"/>, which hands this command's
    /// ExternalCommandData to the command under test. With no test waiting (for example, the user ran it
    /// from External Tools) it does nothing. It always returns Succeeded or Cancelled and sets no message,
    /// so Revit shows no failure dialog.</para>
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AddinTestLoader : IExternalCommand
    {
        /// <summary>Must equal AddinTestSession.LoaderSlot in the command set.</summary>
        public const string SlotName = "revit-bridge.addin-test.loader";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var run = AppDomain.CurrentDomain.GetData(SlotName) as Func<ExternalCommandData, ElementSet, Result>;
            if (run == null)
                return Result.Cancelled;
            try
            {
                return run(commandData, elements);
            }
            catch (Exception ex)
            {
                // 命令集的入口自己处理异常；到这里说明它的收尾出错。记日志，不弹框。
                // The command set's entry handles its own exceptions; getting here means its clean-up
                // failed. Log it, show nothing.
                System.Diagnostics.Trace.WriteLine("revit-bridge test loader: " + ex.GetType().FullName + ": " + ex.Message);
                return Result.Cancelled;
            }
        }
    }
}
