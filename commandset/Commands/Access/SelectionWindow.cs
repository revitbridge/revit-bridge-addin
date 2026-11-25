using System.Runtime.InteropServices;

namespace RevitMCPCommandSet.Commands.Access
{
    internal static class SelectionWindow
    {
        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FlashWindowInfo info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowText(IntPtr window, string text);

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashWindowInfo
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint Flags;
            internal uint Count;
            internal uint Timeout;
        }

        internal static void FlashTaskbar(IntPtr window)
        {
            const uint FLASHW_TRAY = 2;
            var info = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                Window = window,
                Flags = FLASHW_TRAY,
                Count = 3
            };
            FlashWindowEx(ref info);
        }

        internal static void Restore(IntPtr window)
        {
            const int SW_RESTORE = 9;
            ShowWindow(window, SW_RESTORE);
        }

        internal static void SetStatusText(IntPtr window, string text)
        {
            var statusBar = FindWindowEx(window, IntPtr.Zero, "msctls_statusbar32", null);
            if (statusBar != IntPtr.Zero)
                SetWindowText(statusBar, text);
        }
    }
}
