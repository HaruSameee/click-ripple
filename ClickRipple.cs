using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Click Ripple")]
[assembly: System.Reflection.AssemblyDescription("Water ripples for mouse clicks")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace ClickRipple
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using (var instance = new Mutex(false, @"Local\ClickRipple-80d299ab"))
            {
                bool ownsMutex;
                try { ownsMutex = instance.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMutex = true; }

                if (!ownsMutex)
                    return;

                try
                {
                    Application.EnableVisualStyles();
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    using (var app = new RippleApp())
                        Application.Run(app);
                }
                catch (Exception error)
                {
                    MessageBox.Show("波紋を表示できませんでした。\n" + error.Message,
                        "Click Ripple", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    instance.ReleaseMutex();
                }
            }
        }
    }

    internal sealed class RippleApp : ApplicationContext
    {
        private readonly RippleDisplay display = new RippleDisplay();
        private readonly Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ClickReceiver receiver;
        private MouseListener listener;

        internal RippleApp()
        {
            receiver = new ClickReceiver(display.AddRipple);
            try
            {
                var enabled = new ToolStripMenuItem("波紋を表示") { Checked = true };
                enabled.Click += delegate
                {
                    if (listener == null)
                    {
                        StartListening();
                        enabled.Checked = true;
                    }
                    else
                    {
                        StopListening();
                        display.Clear();
                        enabled.Checked = false;
                    }
                };
                menu.Items.Add(enabled);
                menu.Items.Add("終了", null, delegate { ExitThread(); });

                StartListening();
                tray.Icon = icon ?? SystemIcons.Information;
                tray.Text = "Click Ripple";
                tray.ContextMenuStrip = menu;
                tray.Visible = true;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void StartListening()
        {
            listener = new MouseListener(receiver.Handle);
            receiver.Enabled = true;
        }

        private void StopListening()
        {
            receiver.Enabled = false;
            if (listener == null)
                return;
            listener.Dispose();
            listener = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopListening();
                receiver.Dispose();
                tray.Dispose();
                menu.Dispose();
                if (icon != null)
                    icon.Dispose();
                display.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ClickReceiver : NativeWindow, IDisposable
    {
        internal const int ClickMessage = 0x8001;
        internal bool Enabled { get; set; }
        private readonly Action<Point> click;

        internal ClickReceiver(Action<Point> click)
        {
            this.click = click;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == ClickMessage)
            {
                if (Enabled)
                    click(new Point(message.WParam.ToInt32(), message.LParam.ToInt32()));
                return;
            }
            base.WndProc(ref message);
        }

        public void Dispose() { DestroyHandle(); }
    }

    internal sealed class MouseListener : IDisposable
    {
        private readonly IntPtr receiver;
        private readonly Thread thread;
        private readonly Native.HookProc callback;
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
        private uint threadId;
        private int installError;

        internal MouseListener(IntPtr receiver)
        {
            this.receiver = receiver;
            callback = OnMouse;
            thread = new Thread(Run) { IsBackground = true, Name = "Click Ripple mouse input" };
            thread.Start();
            ready.Wait();
            if (installError != 0)
            {
                Dispose();
                throw new Win32Exception(installError);
            }
        }

        private void Run()
        {
            threadId = Native.GetCurrentThreadId();
            Native.Message message;
            Native.PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
            IntPtr hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
            {
                installError = Marshal.GetLastWin32Error();
                if (installError == 0)
                    installError = 31; // ERROR_GEN_FAILURE
            }
            ready.Set();

            if (hook == IntPtr.Zero)
                return;

            try
            {
                // GetMessage services the sent messages that invoke the hook.
                while (Native.GetMessage(out message, IntPtr.Zero, 0, 0) > 0) { }
            }
            finally
            {
                Native.UnhookWindowsHookEx(hook);
            }
        }

        private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
        {
            if (code == 0)
            {
                switch (message.ToInt32())
                {
                    case 0x0201: // WM_LBUTTONDOWN
                    case 0x0204: // WM_RBUTTONDOWN
                    case 0x0207: // WM_MBUTTONDOWN
                    case 0x020B: // WM_XBUTTONDOWN
                        // MSLLHOOKSTRUCT starts with a POINT in per-monitor screen coordinates.
                        int x = Marshal.ReadInt32(data, 0);
                        int y = Marshal.ReadInt32(data, 4);
                        Native.PostMessage(receiver, ClickReceiver.ClickMessage, new IntPtr(x), new IntPtr(y));
                        break;
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        public void Dispose()
        {
            if (thread.IsAlive)
            {
                Native.PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
                thread.Join();
            }
            ready.Dispose();
        }
    }

    internal sealed class RippleDisplay : IDisposable
    {
        private const int MaxRipples = 12;
        private readonly List<RippleWindow> windows = new List<RippleWindow>();
        private readonly Dictionary<int, RippleSurface> surfaces = new Dictionary<int, RippleSurface>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 15 };

        internal RippleDisplay()
        {
            timer.Tick += delegate { DrawFrame(); };
        }

        internal void AddRipple(Point point)
        {
            RippleWindow window = windows.Find(item => !item.Active);
            if (window == null)
            {
                if (windows.Count < MaxRipples)
                {
                    window = new RippleWindow();
                    windows.Add(window);
                }
                else
                {
                    window = windows[0];
                    foreach (var candidate in windows)
                    {
                        if (candidate.StartedAt < window.StartedAt)
                            window = candidate;
                    }
                }
            }

            IntPtr monitor = Native.MonitorFromPoint(point, 2);
            int percent;
            if (Native.GetScaleFactorForMonitor(monitor, out percent) != 0)
                percent = 100;

            RippleSurface surface;
            if (!surfaces.TryGetValue(percent, out surface))
            {
                surface = new RippleSurface(percent);
                surfaces.Add(percent, surface);
            }

            window.ShowRipple(surface, point, clock.Elapsed.TotalMilliseconds);
            timer.Start();
        }

        private void DrawFrame()
        {
            double time = clock.Elapsed.TotalMilliseconds;
            bool animating = false;
            foreach (var window in windows)
            {
                if (window.Active && window.Draw(time))
                    animating = true;
            }
            if (!animating)
                timer.Stop();
        }

        internal void Clear()
        {
            timer.Stop();
            foreach (var window in windows)
                window.HideRipple();
        }

        public void Dispose()
        {
            timer.Dispose();
            foreach (var window in windows)
                window.DestroyHandle();
            foreach (var surface in surfaces.Values)
                surface.Dispose();
        }
    }

    internal sealed class RippleWindow : NativeWindow
    {
        internal const int ExtendedStyle = 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080 | 0x00000008;
        // WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
        internal bool Active { get; private set; }
        internal double StartedAt { get; private set; }
        private RippleSurface surface;
        private Point center;

        internal RippleWindow()
        {
            CreateHandle(new CreateParams
            {
                Style = unchecked((int)0x80000000), // WS_POPUP
                ExStyle = ExtendedStyle
            });
        }

        internal void ShowRipple(RippleSurface surface, Point center, double timestamp)
        {
            this.surface = surface;
            this.center = center;
            StartedAt = timestamp;
            Active = true;
            // HWND_TOPMOST, SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE.
            if (Draw(timestamp) && !Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0053))
                HideRipple();
        }

        internal bool Draw(double timestamp)
        {
            double age = timestamp - StartedAt;
            if (age >= RippleSurface.Duration || !surface.ShowFrame(Handle, center, age))
            {
                HideRipple();
                return false;
            }
            return true;
        }

        internal void HideRipple()
        {
            if (!Active)
                return;
            Active = false;
            Native.ShowWindow(Handle, 0);
        }
    }

    internal sealed class RippleSurface : IDisposable
    {
        internal const double Duration = 360;
        internal int Size { get; private set; }
        private readonly double scale;
        private readonly int[] pixels;
        private IntPtr dc;
        private IntPtr dib;
        private IntPtr previousBitmap;
        private IntPtr bits;

        internal RippleSurface(int percent)
        {
            scale = percent / 100.0;
            Size = (int)Math.Ceiling(35 * scale) * 2 + 1;
            pixels = new int[Size * Size];

            var bitmapInfo = new Native.BitmapInfoHeader
            {
                Size = 40,
                Width = Size,
                Height = -Size,
                Planes = 1,
                BitCount = 32
            };
            try
            {
                dc = Native.CreateCompatibleDC(IntPtr.Zero);
                if (dc == IntPtr.Zero)
                    throw new Win32Exception();
                dib = Native.CreateDIBSection(dc, ref bitmapInfo, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                    throw new Win32Exception();
                previousBitmap = Native.SelectObject(dc, dib);
                if (previousBitmap == IntPtr.Zero)
                    throw new Win32Exception();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal bool ShowFrame(IntPtr window, Point center, double age)
        {
            Render(age);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var destination = new Point(center.X - Size / 2, center.Y - Size / 2);
            var dimensions = new Size(Size, Size);
            var origin = Point.Empty;
            var blend = new Native.BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
            return Native.UpdateLayeredWindow(window, IntPtr.Zero, ref destination, ref dimensions,
                dc, ref origin, 0, ref blend, 2); // ULW_ALPHA
        }

        internal int[] Render(double age)
        {
            Array.Clear(pixels, 0, pixels.Length);
            DrawRing(age / 320.0, 3.5, 31.0, 1.0);
            DrawRing((age - 65.0) / 295.0, 2.0, 21.0, 0.57);
            return pixels;
        }

        private void DrawRing(double progress, double start, double end, double strength)
        {
            if (progress < 0 || progress >= 1)
                return;

            double remaining = 1 - progress;
            double radius = scale * (start + (end - start) * (1 - remaining * remaining));
            double opacity = strength * remaining * remaining * Math.Min(1, 0.55 + progress * 6.0);
            double width = Math.Max(0.9, 1.35 * scale);
            int center = Size / 2;
            int bounds = Math.Min(center, (int)Math.Ceiling(radius + 2 * width));

            for (int y = -bounds; y <= bounds; y++)
            {
                for (int x = -bounds; x <= bounds; x++)
                {
                    double offset = (Math.Sqrt(x * x + y * y) - radius) / width;
                    double highlight = Math.Max(0, 1 - Math.Abs(offset + 0.32));
                    double shadow = Math.Max(0, 1 - Math.Abs(offset - 0.42));
                    int light = (int)(highlight * opacity * 93);
                    int dark = (int)(shadow * opacity * 65);
                    if ((light | dark) == 0)
                        continue;

                    int index = (y + center) * Size + x + center;
                    int underneath = pixels[index];
                    underneath = Blend(underneath, dark, 27, 112, 151);
                    pixels[index] = Blend(underneath, light, 115, 218, 255);
                }
            }
        }

        private static int Blend(int pixel, int alpha, int red, int green, int blue)
        {
            uint under = (uint)pixel;
            int inverse = 255 - alpha;
            int a = alpha + (int)(under >> 24) * inverse / 255;
            int r = (red * alpha + (int)((under >> 16) & 255) * inverse) / 255;
            int g = (green * alpha + (int)((under >> 8) & 255) * inverse) / 255;
            int b = (blue * alpha + (int)(under & 255) * inverse) / 255;
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        public void Dispose()
        {
            if (previousBitmap != IntPtr.Zero)
                Native.SelectObject(dc, previousBitmap);
            if (dib != IntPtr.Zero)
                Native.DeleteObject(dib);
            if (dc != IntPtr.Zero)
                Native.DeleteDC(dc);
            previousBitmap = dib = dc = bits = IntPtr.Zero;
        }
    }

    internal static class Native
    {
        internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            internal IntPtr Window;
            internal uint Id;
            internal IntPtr WParam, LParam;
            internal uint Time;
            internal Point Point;
            internal uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, BitCount;
            internal uint Compression, SizeImage;
            internal int XPelsPerMeter, YPelsPerMeter;
            internal uint ClrUsed, ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BlendFunction
        {
            internal byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [DllImport("shcore.dll")]
        internal static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc,
            ref Point destination, ref Size size, IntPtr sourceDc, ref Point source, int colorKey,
            ref BlendFunction blend, uint flags);
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader bitmapInfo,
            uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr SelectObject(IntPtr dc, IntPtr resource);
        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr resource);
        [DllImport("gdi32.dll")]
        internal static extern bool DeleteDC(IntPtr dc);
    }
}
