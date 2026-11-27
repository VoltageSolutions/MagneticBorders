// Native helper for tools/bench/bench.ps1, compiled at run time with Add-Type. It has no dependencies beyond the
// .NET runtime that PowerShell 7 ships with. It provides three things:
//   Rig   - plain top-level windows at exact visible rects, scripted border drags with SendInput, and lag sampling.
//   Pipe  - one request and one response line on the engine's named pipe.
//   Proc  - GDI and USER object counts of a process.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MagneticBorders.Bench
{
    /// <summary>The desktop or the input path is not usable, for example in a session without an interactive desktop.</summary>
    public sealed class RigException : Exception
    {
        public RigException(string message) : base(message) { }
    }

    internal static class Native
    {
        internal delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool UnregisterClass(string name, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        internal static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        internal static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        internal static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll")]
        internal static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SystemParametersInfo(uint action, uint param, out RECT rect, uint flags);

        [DllImport("user32.dll")]
        internal static extern uint GetGuiResources(IntPtr process, uint flags);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern void NotifyWinEvent(uint winEvent, IntPtr hwnd, int idObject, int idChild);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForSystem();

        [DllImport("winmm.dll")]
        internal static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        internal static extern uint timeEndPeriod(uint ms);
    }

    /// <summary>Summary of window-edge disagreement samples, in pixels.</summary>
    public sealed class Lag
    {
        public int Samples;
        public double Mean;
        public double P50;
        public double P95;
        public double P99;
        public int Max;
        public double DisagreePct;
    }

    /// <summary>The outcome of one scripted drag.</summary>
    public sealed class DragResult
    {
        public int Steps;
        public double DurationMs;
        public int MaxTravelPx;
        public int[] Gaps = new int[0];
    }

    /// <summary>Open test windows at exact visible rects and drag the border between them.</summary>
    public sealed class Rig : IDisposable
    {
        private const uint WmQuit = 0x0012;
        private const uint WmNcHitTest = 0x0084;
        private const int HtRight = 11;
        private const int DwmaExtendedFrameBounds = 9;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint EventSystemMoveSizeStart = 0x000A;
        private const uint EventSystemMoveSizeEnd = 0x000B;
        private const uint MouseMove = 0x0001;
        private const uint MouseLeftDown = 0x0002;
        private const uint MouseLeftUp = 0x0004;
        private const uint MouseVirtualDesk = 0x4000;
        private const uint MouseAbsolute = 0x8000;

        private static readonly Native.WndProc s_proc = DefProc;
        private readonly string _layout;
        private static int s_count;
        private readonly string _className = "MbBenchWindow" + Process.GetCurrentProcess().Id + "_" + Interlocked.Increment(ref s_count);
        private readonly List<IntPtr> _windows = new List<IntPtr>();
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private Native.RECT[] _targets = new Native.RECT[0];
        private Native.RECT[] _insets = new Native.RECT[0];
        private bool _dwm = true;
        private int[] _members = new int[0];
        private bool[] _memberLeft = new bool[0];
        private Thread _thread;
        private uint _threadId;
        private Exception _error;

        private Rig(string layout)
        {
            _layout = layout;
        }

        /// <summary>
        /// When true, Drag does not use the mouse. It announces the start and the end of a resize with NotifyWinEvent
        /// (EVENT_SYSTEM_MOVESIZESTART and EVENT_SYSTEM_MOVESIZEEND, which Windows raises for a real border drag) and
        /// resizes the dragged window with SetWindowPos in between. Engines see the same events as in a real drag,
        /// but Windows' own resize loop does not run.
        /// </summary>
        public bool Simulated { get; set; }

        /// <summary>The number of windows in the layout.</summary>
        public int Count { get { return _windows.Count; } }

        /// <summary>The number of windows whose edge is on the dragged seam.</summary>
        public int MemberCount { get { return _members.Length; } }

        /// <summary>Screen size and system DPI, for the report.</summary>
        public static string Describe()
        {
            TrySetDpiAware();
            return Native.GetSystemMetrics(0) + "x" + Native.GetSystemMetrics(1) + " at " + Native.GetDpiForSystem() + " dpi";
        }

        /// <summary>Creates the windows of a layout: pair, row3 or grid. Throws RigException when no window can be created.</summary>
        public static Rig Open(string layout)
        {
            TrySetDpiAware();
            var rig = new Rig(layout);
            rig._thread = new Thread(rig.Run) { IsBackground = true, Name = "MbBenchWindows" };
            rig._thread.Start();
            if (!rig._ready.Wait(15000))
            {
                throw new RigException("timed out creating the test windows");
            }
            if (rig._error != null)
            {
                throw new RigException("could not create the test windows: " + rig._error.Message);
            }
            return rig;
        }

        /// <summary>Closes the windows and ends the window thread.</summary>
        public void Dispose()
        {
            if (_thread != null && _threadId != 0)
            {
                Native.PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
                _thread.Join(3000);
                _thread = null;
            }
        }

        /// <summary>Visible frame (DWMWA_EXTENDED_FRAME_BOUNDS) of window <paramref name="index"/> as left, top, right, bottom.</summary>
        public int[] Frame(int index)
        {
            Native.RECT r = Visible(_windows[index]);
            return new[] { r.Left, r.Top, r.Right, r.Bottom };
        }

        /// <summary>Puts every window back at its layout rect.</summary>
        public void Reset()
        {
            for (int i = 0; i < _windows.Count; i++)
            {
                Native.RECT t = _targets[i];
                Native.RECT n = _insets[i];
                Native.SetWindowPos(_windows[i], IntPtr.Zero, t.Left - n.Left, t.Top - n.Top, (t.Right - n.Right) - (t.Left - n.Left), (t.Bottom - n.Bottom) - (t.Top - n.Top), SwpNoZOrder | SwpNoActivate);
            }
        }

        /// <summary>
        /// Drags the right border of the first window out by <paramref name="amplitudePx"/> and back, in steps of
        /// <paramref name="stepPx"/> every <paramref name="stepMs"/> milliseconds. While the button is down it samples, as fast
        /// as it can, the largest disagreement between the dragged edge and the edges of the windows on the same line.
        /// </summary>
        public DragResult Drag(int amplitudePx, int stepPx, int stepMs, int settleMs, bool recordLag)
        {
            Reset();
            Thread.Sleep(40);
            Native.POINT grab = new Native.POINT();
            if (!Simulated)
            {
                grab = FindGrabPoint();
                if (!Native.SetCursorPos(grab.X, grab.Y))
                {
                    throw new RigException("SetCursorPos failed (error " + Marshal.GetLastWin32Error() + ")");
                }
                Thread.Sleep(15);
            }
            int startEdge = Visible(_windows[0]).Right;
            Native.RECT outer;
            Native.GetWindowRect(_windows[0], out outer);

            var gaps = new List<int>(4096);
            int maxTravel = 0;
            bool sampling = false;
            bool stop = false;
            var poller = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    if (Volatile.Read(ref sampling))
                    {
                        int dragged = Visible(_windows[0]).Right;
                        int travel = Math.Abs(dragged - startEdge);
                        if (travel > maxTravel) { maxTravel = travel; }
                        if (recordLag) { gaps.Add(WorstGap(dragged)); }
                    }
                    Thread.Sleep(recordLag ? 1 : 3);
                }
            }) { IsBackground = true, Name = "MbBenchPoller" };
            poller.Start();

            var offsets = new List<int>();
            int steps = Math.Max(1, amplitudePx / stepPx);
            for (int i = 1; i <= steps; i++) { offsets.Add(i * stepPx); }
            for (int i = steps - 1; i >= 0; i--) { offsets.Add(i * stepPx); }

            string diagnostics = string.Empty;
            bool pressed = false;
            var clock = Stopwatch.StartNew();
            double durationMs = 0;
            try
            {
                if (Simulated)
                {
                    Native.NotifyWinEvent(EventSystemMoveSizeStart, _windows[0], 0, 0);
                }
                else
                {
                    Button(MouseLeftDown);
                    pressed = true;
                }
                Thread.Sleep(30);
                if (!Simulated) { diagnostics = Diagnose(grab); }
                Volatile.Write(ref sampling, true);
                double t0 = clock.Elapsed.TotalMilliseconds;
                for (int j = 0; j < offsets.Count; j++)
                {
                    double deadline = t0 + ((j + 1) * (double)stepMs);
                    while (clock.Elapsed.TotalMilliseconds < deadline)
                    {
                        if (deadline - clock.Elapsed.TotalMilliseconds > 1.0) { Thread.Sleep(1); } else { Thread.Yield(); }
                    }
                    if (Simulated)
                    {
                        Native.SetWindowPos(_windows[0], IntPtr.Zero, outer.Left, outer.Top, (outer.Right - outer.Left) + offsets[j], outer.Bottom - outer.Top, SwpNoZOrder | SwpNoActivate);
                    }
                    else
                    {
                        MoveTo(grab.X + offsets[j], grab.Y);
                    }
                }
                Thread.Sleep(stepMs);
            }
            finally
            {
                Volatile.Write(ref sampling, false);
                durationMs = clock.Elapsed.TotalMilliseconds;
                if (pressed) { Button(MouseLeftUp); }
                if (Simulated) { Native.NotifyWinEvent(EventSystemMoveSizeEnd, _windows[0], 0, 0); }
                Volatile.Write(ref stop, true);
                poller.Join(2000);
            }
            Thread.Sleep(settleMs);

            int minTravel = Math.Max(2, Math.Min(amplitudePx / 2, 20));
            if (maxTravel < minTravel)
            {
                throw new RigException("the dragged window did not follow the injected mouse input (moved " + maxTravel + " px; " + diagnostics + ")");
            }
            return new DragResult { Steps = offsets.Count, DurationMs = durationMs, MaxTravelPx = maxTravel, Gaps = gaps.ToArray() };
        }

        /// <summary>Mean, percentiles, maximum and the share of samples that are not 0 px.</summary>
        public static Lag Summarize(int[] gaps)
        {
            var lag = new Lag();
            if (gaps == null || gaps.Length == 0) { return lag; }
            int[] sorted = (int[])gaps.Clone();
            Array.Sort(sorted);
            long sum = 0;
            int nonZero = 0;
            foreach (int g in sorted)
            {
                sum += g;
                if (g != 0) { nonZero++; }
            }
            lag.Samples = sorted.Length;
            lag.Mean = (double)sum / sorted.Length;
            lag.P50 = Percentile(sorted, 0.50);
            lag.P95 = Percentile(sorted, 0.95);
            lag.P99 = Percentile(sorted, 0.99);
            lag.Max = sorted[sorted.Length - 1];
            lag.DisagreePct = 100.0 * nonZero / sorted.Length;
            return lag;
        }

        private static double Percentile(int[] sorted, double p)
        {
            int rank = (int)Math.Ceiling(p * sorted.Length) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Length - 1, rank))];
        }

        private static IntPtr DefProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private static void TrySetDpiAware()
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
        }

        private int WorstGap(int draggedEdge)
        {
            int worst = 0;
            for (int k = 0; k < _members.Length; k++)
            {
                Native.RECT r = Visible(_windows[_members[k]]);
                int edge = _memberLeft[k] ? r.Left : r.Right;
                int gap = Math.Abs(edge - draggedEdge);
                if (gap > worst) { worst = gap; }
            }
            return worst;
        }

        private Native.RECT Visible(IntPtr hwnd)
        {
            Native.RECT r;
            if (_dwm && Native.DwmGetWindowAttribute(hwnd, DwmaExtendedFrameBounds, out r, Marshal.SizeOf(typeof(Native.RECT))) == 0)
            {
                return r;
            }
            Native.GetWindowRect(hwnd, out r);
            return r;
        }

        // Finds a point on the right resize border of the first window that the system routes to that window.
        private Native.POINT FindGrabPoint()
        {
            IntPtr hwnd = _windows[0];
            Native.RECT v = Visible(hwnd);
            int y = (v.Top + v.Bottom) / 2;
            for (int x = v.Right - 3; x <= v.Right + 8; x++)
            {
                var p = new Native.POINT { X = x, Y = y };
                if (Native.WindowFromPoint(p) != hwnd) { continue; }
                IntPtr lParam = new IntPtr((y << 16) | (x & 0xFFFF));
                if (Native.SendMessage(hwnd, WmNcHitTest, IntPtr.Zero, lParam).ToInt64() == HtRight) { return p; }
            }
            throw new RigException("no resize border point on the first test window is reachable (the desktop may be locked or covered)");
        }

        // What the desktop reports right after the button press, to explain a drag that did not start.
        private string Diagnose(Native.POINT grab)
        {
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            IntPtr under = Native.WindowFromPoint(cursor);
            bool buttonDown = (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;
            return "grab " + grab.X + "," + grab.Y + "; cursor " + cursor.X + "," + cursor.Y
                + "; window under cursor is test window " + _windows.IndexOf(under)
                + "; left button down " + buttonDown
                + "; foreground is test window " + _windows.IndexOf(Native.GetForegroundWindow());
        }

        private static void MoveTo(int x, int y)
        {
            int vx = Native.GetSystemMetrics(76);
            int vy = Native.GetSystemMetrics(77);
            int vw = Math.Max(2, Native.GetSystemMetrics(78));
            int vh = Math.Max(2, Native.GetSystemMetrics(79));
            var input = new Native.INPUT { type = 0 };
            input.mi.dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
            input.mi.dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
            input.mi.dwFlags = MouseMove | MouseAbsolute | MouseVirtualDesk;
            Send(input);
        }

        private static void Button(uint flag)
        {
            var input = new Native.INPUT { type = 0 };
            input.mi.dwFlags = flag;
            Send(input);
        }

        private static void Send(Native.INPUT input)
        {
            if (Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.INPUT))) != 1)
            {
                throw new RigException("SendInput failed (error " + Marshal.GetLastWin32Error() + ")");
            }
        }

        private void Run()
        {
            IntPtr instance = Native.GetModuleHandle(null);
            try
            {
                _threadId = Native.GetCurrentThreadId();
                var wc = new Native.WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                    lpfnWndProc = s_proc,
                    hInstance = instance,
                    hbrBackground = new IntPtr(6),
                    lpszClassName = _className,
                };
                if (Native.RegisterClassEx(ref wc) == 0)
                {
                    throw new InvalidOperationException("RegisterClassEx failed (error " + Marshal.GetLastWin32Error() + ")");
                }
                Native.RECT work;
                if (!Native.SystemParametersInfo(0x0030, 0, out work, 0))
                {
                    throw new InvalidOperationException("SystemParametersInfo failed (error " + Marshal.GetLastWin32Error() + ")");
                }
                BuildLayout(work);
                for (int i = 0; i < _targets.Length; i++)
                {
                    Native.RECT t = _targets[i];
                    IntPtr h = Native.CreateWindowEx(0, _className, "MB Bench " + (i + 1), 0x10CF0000, t.Left, t.Top, t.Right - t.Left, t.Bottom - t.Top, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                    if (h == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("CreateWindowEx failed (error " + Marshal.GetLastWin32Error() + ")");
                    }
                    _windows.Add(h);
                    Native.ShowWindow(h, 4);
                }
                MeasureInsets();
                Reset();
                // The dragged window is on top so its resize border wins where the frames overlap.
                Native.SetWindowPos(_windows[0], IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
            catch (Exception e)
            {
                _error = e;
                _ready.Set();
                return;
            }
            _ready.Set();

            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            foreach (IntPtr h in _windows) { Native.DestroyWindow(h); }
            Native.UnregisterClass(_className, instance);
        }

        // Window rect minus visible frame, per side, so a visible rect can be turned into a SetWindowPos rect.
        private void MeasureInsets()
        {
            _insets = new Native.RECT[_windows.Count];
            for (int i = 0; i < _windows.Count; i++)
            {
                Native.RECT w;
                Native.RECT f;
                Native.GetWindowRect(_windows[i], out w);
                if (Native.DwmGetWindowAttribute(_windows[i], DwmaExtendedFrameBounds, out f, Marshal.SizeOf(typeof(Native.RECT))) != 0)
                {
                    _dwm = false;
                    _insets[i] = new Native.RECT();
                    continue;
                }
                _insets[i] = new Native.RECT { Left = f.Left - w.Left, Top = f.Top - w.Top, Right = f.Right - w.Right, Bottom = f.Bottom - w.Bottom };
            }
        }

        // Window 0 is the dragged window. _members lists the other windows with an edge on the dragged window's right border:
        // the left edge of a window across the border, or the right edge of a window stacked under the dragged one.
        private void BuildLayout(Native.RECT work)
        {
            int margin = 24;
            int w = Math.Min(work.Right - work.Left - (2 * margin), 1200);
            int h = Math.Min(work.Bottom - work.Top - (2 * margin), 640);
            if (w < 600 || h < 240)
            {
                throw new InvalidOperationException("the work area is too small for the test windows");
            }
            int x0 = work.Left + ((work.Right - work.Left - w) / 2);
            int y0 = work.Top + ((work.Bottom - work.Top - h) / 2);
            switch (_layout)
            {
                case "pair":
                    _targets = new[] { Box(x0, y0, x0 + (w / 2), y0 + h), Box(x0 + (w / 2), y0, x0 + w, y0 + h) };
                    _members = new[] { 1 };
                    _memberLeft = new[] { true };
                    break;
                case "row3":
                    int third = w / 3;
                    _targets = new[] { Box(x0, y0, x0 + third, y0 + h), Box(x0 + third, y0, x0 + (2 * third), y0 + h), Box(x0 + (2 * third), y0, x0 + w, y0 + h) };
                    _members = new[] { 1 };
                    _memberLeft = new[] { true };
                    break;
                case "grid":
                    int mx = x0 + (w / 2);
                    int my = y0 + (h / 2);
                    _targets = new[] { Box(x0, y0, mx, my), Box(mx, y0, x0 + w, my), Box(x0, my, mx, y0 + h), Box(mx, my, x0 + w, y0 + h) };
                    _members = new[] { 1, 2, 3 };
                    _memberLeft = new[] { true, false, true };
                    break;
                default:
                    throw new InvalidOperationException("unknown layout '" + _layout + "' (use pair, row3, or grid)");
            }
        }

        private static Native.RECT Box(int l, int t, int r, int b)
        {
            return new Native.RECT { Left = l, Top = t, Right = r, Bottom = b };
        }
    }

    /// <summary>One request line and one response line on \\.\pipe\MagneticBorders.</summary>
    public static class Pipe
    {
        /// <summary>Returns the response line, or null when the pipe is missing, busy, or silent.</summary>
        public static string Request(string json, int connectTimeoutMs, int readTimeoutMs)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", "MagneticBorders", PipeDirection.InOut))
                {
                    client.Connect(connectTimeoutMs);
                    byte[] request = new UTF8Encoding(false).GetBytes(json + "\n");
                    client.Write(request, 0, request.Length);
                    client.Flush();
                    var response = new StringBuilder();
                    var buffer = new byte[1024];
                    var decoder = new UTF8Encoding(false).GetDecoder();
                    var chars = new char[1024];
                    var deadline = Stopwatch.StartNew();
                    while (deadline.ElapsedMilliseconds < readTimeoutMs)
                    {
                        var read = client.ReadAsync(buffer, 0, buffer.Length);
                        if (!read.Wait(Math.Max(1, readTimeoutMs - (int)deadline.ElapsedMilliseconds))) { return null; }
                        if (read.Result == 0) { break; }
                        int n = decoder.GetChars(buffer, 0, read.Result, chars, 0);
                        response.Append(chars, 0, n);
                        int eol = response.ToString().IndexOf('\n');
                        if (eol >= 0) { return response.ToString(0, eol).TrimEnd('\r'); }
                    }
                    return response.Length > 0 ? response.ToString().TrimEnd('\r', '\n') : null;
                }
            }
            catch (Exception e) when (e is TimeoutException || e is System.IO.IOException || e is UnauthorizedAccessException || e is AggregateException || e is ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>Per-process counters that System.Diagnostics.Process does not expose.</summary>
    public static class Proc
    {
        /// <summary>GDI objects of the process behind <paramref name="handle"/>.</summary>
        public static int Gdi(IntPtr handle) { return (int)Native.GetGuiResources(handle, 0); }

        /// <summary>USER objects of the process behind <paramref name="handle"/>.</summary>
        public static int User(IntPtr handle) { return (int)Native.GetGuiResources(handle, 1); }

        /// <summary>Asks Windows for a 1 ms timer resolution so sleeps are short and steady.</summary>
        public static void BeginHighResolutionTimer() { Native.timeBeginPeriod(1); }

        /// <summary>Undoes <see cref="BeginHighResolutionTimer"/>.</summary>
        public static void EndHighResolutionTimer() { Native.timeEndPeriod(1); }
    }
}
