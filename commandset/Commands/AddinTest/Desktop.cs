using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RevitMCPCommandSet.Commands.AddinTest
{
    /// <summary>
    /// <para>鼠标、键盘、前台与截屏（Win32）。只在套接字线程上用，不碰 Revit API。时序照搬评测里已验证的做法
    /// （notes/research/ab-eval/autotest.py）。</para>
    /// <para>Mouse, keyboard, foreground and screen capture (Win32). Used on the socket thread only,
    /// never touching the Revit API. The timings are the ones verified in the evaluation
    /// (notes/research/ab-eval/autotest.py).</para>
    /// </summary>
    internal static class Desktop
    {
        private const uint MouseLeftDown = 0x0002;
        private const uint MouseLeftUp = 0x0004;
        private const uint KeyUp = 0x0002;
        public const byte VkEscape = 0x1B;
        private const byte VkMenu = 0x12;
        private const int SwRestore = 9;
        private const int SrcCopy = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

        /// <summary>
        /// <para>在 (x, y) 左键单击：先移光标、等 400 ms，再问 <paramref name="allowed"/>（这时光标已在目标点上，
        /// 可以检查光标位置和点下的窗口），允许才按下。调用方在调用之前自己检查对话框与前台。</para>
        /// <para>Left click at (x, y): move the cursor, wait 400 ms, then ask <paramref name="allowed"/>
        /// (the cursor is on the target by then, so it can check the cursor position and the window under
        /// it) and press only when it allows. Callers check dialogs and the foreground themselves before
        /// calling.</para>
        /// </summary>
        public static bool Click(int x, int y, Func<bool> allowed = null)
        {
            SetCursorPos(x, y);
            Thread.Sleep(400);
            if (allowed != null && !allowed()) return false;
            mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(400);
            return true;
        }

        public static void MoveCursor(int x, int y)
        {
            SetCursorPos(x, y);
        }

        public static void Key(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KeyUp, UIntPtr.Zero);
            Thread.Sleep(150);
        }

        /// <summary>
        /// <para>把窗口置前：最多四次；Alt 轻点让后台进程可以取得前台；不行就按 Esc 关掉外壳占住的东西，再点标题栏。</para>
        /// <para>Bring the window to the front: up to four tries; an Alt tap lets a background process
        /// take the foreground; failing that, Escape closes what the shell holds open and the title bar
        /// is clicked.</para>
        /// </summary>
        public static bool BringToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SwRestore);
                Key(VkMenu);
                SetForegroundWindow(hwnd);
                Thread.Sleep(600);
                if (GetForegroundWindow() == hwnd)
                    return true;
                Key(VkEscape);
                if (GetWindowRect(hwnd, out Rect rect))
                    Click(rect.Left + 400, rect.Top + 12);
            }
            return GetForegroundWindow() == hwnd;
        }

        /// <summary>The window's screen rectangle (left, top, right, bottom), or null.</summary>
        public static int[] WindowRectangle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out Rect rect))
                return null;
            return new[] { rect.Left, rect.Top, rect.Right, rect.Bottom };
        }

        /// <summary>
        /// <para>把屏幕矩形存成 PNG（在单独的 STA 线程上编码）。返回宽、高。</para>
        /// <para>Save a screen rectangle as PNG (encoded on its own STA thread). Returns width and height.</para>
        /// </summary>
        public static int[] CapturePng(int left, int top, int right, int bottom, string path)
        {
            int width = right - left;
            int height = bottom - top;
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"Empty capture rectangle ({left}, {top}, {right}, {bottom}).");

            Exception failure = null;
            var thread = new Thread(() =>
            {
                IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
                try
                {
                    screen = GetDC(IntPtr.Zero);
                    memory = CreateCompatibleDC(screen);
                    bitmap = CreateCompatibleBitmap(screen, width, height);
                    previous = SelectObject(memory, bitmap);
                    if (!BitBlt(memory, 0, 0, width, height, screen, left, top, SrcCopy))
                        throw new InvalidOperationException("BitBlt failed: " + Marshal.GetLastWin32Error());
                    SelectObject(memory, previous);
                    previous = IntPtr.Zero;

                    BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                        bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(source));
                    string folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder))
                        Directory.CreateDirectory(folder);
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                        encoder.Save(stream);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    if (previous != IntPtr.Zero) SelectObject(memory, previous);
                    if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                    if (memory != IntPtr.Zero) DeleteDC(memory);
                    if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(30000))
                throw new TimeoutException("Saving the screen capture took more than 30 s.");
            if (failure != null)
                throw new InvalidOperationException("Screen capture failed: " + failure.GetType().FullName + ": " + failure.Message, failure);
            return new[] { width, height };
        }

        /// <summary>
        /// <para>add-in 的日志目录下的 captures 文件夹（命令集在 revit-bridge\Commands\RevitMCPCommandSet\&lt;年份&gt;\）。</para>
        /// <para>The captures folder under the add-in's log folder (the command set sits in
        /// revit-bridge\Commands\RevitMCPCommandSet\&lt;year&gt;\).</para>
        /// </summary>
        public static string DefaultCapturePath(string prefix)
        {
            string own = Path.GetDirectoryName(typeof(Desktop).Assembly.Location) ?? Path.GetTempPath();
            string folder = Path.GetFullPath(Path.Combine(own, "..", "..", "..", "Logs", "captures"));
            return Path.Combine(folder, prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + ".png");
        }
    }
}
