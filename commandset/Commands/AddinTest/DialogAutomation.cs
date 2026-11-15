using System.Runtime.InteropServices;

namespace RevitMCPCommandSet.Commands.AddinTest
{
    /// <summary>
    /// Tracks windows created during this command on Revit's UI thread. Sends a
    /// bounded native Close command, never a confirmation, key or mouse event.
    /// Existing windows and ambiguous targets are never closed.
    /// </summary>
    internal sealed class DialogAutomation
    {
        private readonly IntPtr main;
        private readonly uint processId;
        private readonly uint threadId;
        private readonly HashSet<IntPtr> existing;
        private readonly WaitHandle commandReturned;
        private delegate bool EnumWindowProc(IntPtr window, IntPtr state);

        /// <summary>The refusal when Revit's main window is minimized (the model view is not on screen).</summary>
        internal const string MinimizedMessage =
            "Revit's window is minimized; restore it so that the model view is visible on screen, then run the test again.";

        private const uint GaRootOwner = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr state);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(
            IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        internal DialogAutomation(IntPtr mainWindow, WaitHandle returned)
        {
            main = mainWindow;
            commandReturned = returned;
            threadId = GetWindowThreadProcessId(main, out processId);
            if (main == IntPtr.Zero || threadId == 0 || !IsWindowEnabled(main))
                throw new InvalidOperationException("Revit's main window is unavailable or blocked by an existing dialog.");
            // 最小化时换算出的屏幕坐标不在屏幕上：不准备点选。
            // Minimized, the mapped screen points are off screen: no picks are prepared.
            if (IsIconic(main))
                throw new InvalidOperationException(MinimizedMessage);
            existing = new HashSet<IntPtr>(Windows(false));
        }

        /// <summary>True when <paramref name="window"/> is minimized (IsIconic).</summary>
        internal static bool IsMinimized(IntPtr window) => IsIconic(window);

        /// <summary>
        /// <para>最近一次 <see cref="CanClickPickAt"/> 因点本身不成立而拒绝时的原因；通过了或因别的原因拒绝时为 null。</para>
        /// <para>Why the last <see cref="CanClickPickAt"/> refused because of the point itself; null when it
        /// passed or refused for another reason.</para>
        /// </summary>
        internal string PointRefusal { get; private set; }

        private List<IntPtr> Windows(bool visibleOnly)
        {
            var result = new List<IntPtr>();
            if (!EnumWindows((window, state) =>
            {
                uint thread = GetWindowThreadProcessId(window, out uint process);
                if (window != main && thread == threadId && process == processId &&
                    (!visibleOnly || IsWindowVisible(window)))
                    result.Add(window);
                return true;
            }, IntPtr.Zero))
                throw new InvalidOperationException("Cannot enumerate Revit's command windows; no input was sent.");
            return result;
        }

        private List<IntPtr> NewWindows() => Windows(true).Where(window => !existing.Contains(window)).ToList();
        internal bool CanSendPick() => !commandReturned.WaitOne(0) && IsWindow(main) && IsWindowEnabled(main) && NewWindows().Count == 0;
        internal bool CanClickPick() => CanSendPick() && GetForegroundWindow() == main;

        /// <summary>
        /// <para>光标已移到 (x, y) 之后、按下之前调用：<see cref="CanClickPick"/> 成立，Revit 没有最小化，光标确实在
        /// (x, y)（屏幕外的点会被夹到屏幕边上），且这个像素所在窗口的根所有者是 Revit 主窗口（主窗口本身或它拥有的
        /// 浮动面板、视图窗口也算 Revit）。</para>
        /// <para>Called after the cursor was moved to (x, y) and before the button goes down:
        /// <see cref="CanClickPick"/> holds, Revit is not minimized, the cursor really is at (x, y) (a point
        /// off screen is clamped to the screen edge), and the window under that pixel has Revit's main
        /// window as its root owner (the main window itself, or a palette or view window it owns, counts
        /// as Revit).</para>
        /// </summary>
        internal bool CanClickPickAt(int x, int y)
        {
            PointRefusal = null;
            if (!CanClickPick())
                return false;
            if (IsIconic(main))
            {
                PointRefusal = "Revit is minimized";
                return false;
            }
            if (!GetCursorPos(out NativePoint cursor) || cursor.X != x || cursor.Y != y)
            {
                PointRefusal = $"the cursor is not at ({x}, {y}) after moving it there (the point is off screen, or the mouse moved)";
                return false;
            }
            IntPtr window = WindowFromPoint(cursor);
            if (window == IntPtr.Zero || GetAncestor(window, GaRootOwner) != main)
            {
                PointRefusal = $"the window at ({x}, {y}) is not Revit's";
                return false;
            }
            return true;
        }

        internal bool FocusForPick()
        {
            if (!CanSendPick()) return false;
            // Native activation only: no Alt, Escape or title-bar click fallback.
            SetForegroundWindow(main);
            Thread.Sleep(200);
            return CanSendPick() && GetForegroundWindow() == main;
        }

        private bool IsOwnedModal(IntPtr window)
        {
            if (IsWindowEnabled(main) || !IsWindowEnabled(window)) return false;
            IntPtr owner = GetWindow(window, 4); // GW_OWNER
            if (owner == IntPtr.Zero || IsWindowEnabled(owner)) return false;
            for (int depth = 0; depth < 16 && owner != IntPtr.Zero; depth++)
            {
                if (owner == main) return true;
                if (GetWindowThreadProcessId(owner, out uint process) != threadId || process != processId) return false;
                owner = GetWindow(owner, 4);
            }
            return false;
        }

        internal bool CloseOwnedModal(out string error)
        {
            var windows = NewWindows();
            if (windows.Count != 1 || !IsOwnedModal(windows[0]))
            {
                error = "Cannot identify one new owned modal window for this command; no cancellation input was sent.";
                return false;
            }
            IntPtr target = windows[0];
            if (commandReturned.WaitOne(0)) { error = string.Empty; return true; }
            // WM_CLOSE is a window command, not simulated keyboard/mouse input.
            // A bounded native send avoids leaving an automation worker behind.
            // Delivery is not completion: the caller still waits for the command
            // and rollback. On timeout the current handler may still be running.
            IntPtr sent = SendMessageTimeout(target, 0x0010, IntPtr.Zero, IntPtr.Zero,
                0x0001 | 0x0002 | 0x0020, 5000, out IntPtr ignored);
            if (sent == IntPtr.Zero && !commandReturned.WaitOne(0) && IsWindow(target))
            {
                error = "Native dialog Close was not acknowledged; completion is unconfirmed. No input fallback was sent.";
                return false;
            }
            error = string.Empty;
            return true;
        }
    }
}
