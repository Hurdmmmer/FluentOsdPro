using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace FluentOsdPro
{
    /// <summary>
    /// Detects when Win+Ctrl+Left/Right hits the first or last Windows 11 virtual desktop boundary,
    /// and renders a synchronous 120Hz 1:1 native screen bounce / head-shake feedback across all active monitors
    /// without bleeding across monitor seams or waking auto-hidden taskbars.
    /// </summary>
    internal static class DesktopBoundaryBounceService
    {
        private const string VirtualDesktopsRegPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
        private const uint WsPopup = 0x80000000u;
        private const uint WsExTopmost = 0x00000008u;
        private const uint WsExToolWindow = 0x00000080u;
        private const uint WsExTransparent = 0x00000020u;
        private const uint WsExNoActivate = 0x08000000u;
        private const int SwShowNoActivate = 4;
        private const int SwHide = 0;
        private const int WmEraseBkgnd = 0x0014;
        private const uint SrcCopy = 0x00CC0020u;
        private const uint MonitorDefaultToNearest = 2u;

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (DPI_AWARENESS_CONTEXT)-4
        private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new IntPtr(-4);

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32MonitorInfo
        {
            public int cbSize;
            public Win32Rect rcMonitor;
            public Win32Rect rcWork;
            public uint dwFlags;
        }

        private sealed class MonitorBounceSurface
        {
            public int MonX;
            public int MonY;
            public int MonW;
            public int MonH;
            public IntPtr HdcCapture;
            public IntPtr HbmpCapture;
            public IntPtr OldCapture;
            public IntPtr HdcBack;
            public IntPtr HbmpBack;
            public IntPtr OldBack;
            public IntPtr Hwnd;
            public IntPtr HdcWnd;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Win32Rect lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassExW
        {
            public uint cbSize;
            public uint style;
            public WndProcDelegate lpfnWndProc;
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

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
        private interface IVirtualDesktopManager
        {
            [PreserveSig]
            int IsWindowOnCurrentVirtualDesktop([In] IntPtr topLevelWindow, [Out] out int onCurrentDesktop);

            [PreserveSig]
            int GetWindowDesktopId([In] IntPtr topLevelWindow, [Out] out Guid desktopId);

            [PreserveSig]
            int MoveWindowToDesktop([In] IntPtr topLevelWindow, [MarshalAs(UnmanagedType.LPStruct)][In] Guid desktopId);
        }

        [ComImport]
        [Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
        private class CVirtualDesktopManager
        {
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc,
            IntPtr lprcClip,
            MonitorEnumProc lpfnEnum,
            IntPtr dwData);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Win32Point lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Win32Point pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref Win32MonitorInfo lpmi);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WndClassExW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hDC, ref Win32Rect lprc, IntPtr hbr);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BitBlt(
            IntPtr hdc,
            int x,
            int y,
            int cx,
            int cy,
            IntPtr hdcSrc,
            int x1,
            int y1,
            uint rop);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        private const string OverlayClassName = "FluentOsdPro_BoundaryBounceOverlay";
        private static readonly WndProcDelegate SharedWndProc = OverlayWndProc;
        private static bool _classRegistered;
        private static readonly object ClassSync = new object();
        private static int _isAnimating;
        private static long _lastTriggerTickMs;

        private static IntPtr OverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WmEraseBkgnd)
            {
                return new IntPtr(1);
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private static void EnsureWindowClassRegistered()
        {
            if (_classRegistered)
            {
                return;
            }

            lock (ClassSync)
            {
                if (_classRegistered)
                {
                    return;
                }

                WndClassExW wc = new WndClassExW
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(WndClassExW)),
                    style = 0,
                    lpfnWndProc = SharedWndProc,
                    hInstance = GetModuleHandleW(null),
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = OverlayClassName
                };
                RegisterClassExW(ref wc);
                _classRegistered = true;
            }
        }

        /// <summary>
        /// Checks whether the user is currently on the first (index 0) or last (index count-1) virtual desktop.
        /// Queries live COM IVirtualDesktopManager and Registry CurrentVirtualDesktop / SessionInfo.
        /// </summary>
        public static bool IsAtVirtualDesktopBoundary(bool goingLeft, out int currentIndex, out int totalCount)
        {
            currentIndex = -1;
            totalCount = 0;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(VirtualDesktopsRegPath, writable: false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    byte[] idsBytes = key.GetValue("VirtualDesktopIDs") as byte[];
                    if (idsBytes == null || idsBytes.Length < 16 || (idsBytes.Length % 16) != 0)
                    {
                        currentIndex = 0;
                        totalCount = 1;
                        return true;
                    }

                    totalCount = idsBytes.Length / 16;
                    if (totalCount <= 1)
                    {
                        currentIndex = 0;
                        totalCount = 1;
                        return true;
                    }

                    Guid currentGuid = Guid.Empty;

                    // 1. Primary: Registry CurrentVirtualDesktop (updated by Explorer on desktop switch)
                    byte[] currentBytes = key.GetValue("CurrentVirtualDesktop") as byte[];
                    if (currentBytes != null && currentBytes.Length == 16)
                    {
                        currentGuid = new Guid(currentBytes);
                    }

                    // 2. Fallback: SessionInfo\<SessionId>\VirtualDesktops\CurrentVirtualDesktop
                    if (currentGuid == Guid.Empty)
                    {
                        try
                        {
                            int sessionId = Process.GetCurrentProcess().SessionId;
                            string sessionPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\" + sessionId + @"\VirtualDesktops";
                            using (RegistryKey sessionKey = Registry.CurrentUser.OpenSubKey(sessionPath, writable: false))
                            {
                                byte[] sesBytes = sessionKey?.GetValue("CurrentVirtualDesktop") as byte[];
                                if (sesBytes != null && sesBytes.Length == 16)
                                {
                                    currentGuid = new Guid(sesBytes);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }

                    // 3. Live COM verification via IVirtualDesktopManager on the foreground window if needed
                    if (currentGuid == Guid.Empty)
                    {
                        try
                        {
                            IntPtr fg = GetForegroundWindow();
                            if (fg != IntPtr.Zero)
                            {
                                IVirtualDesktopManager vdm = (IVirtualDesktopManager)new CVirtualDesktopManager();
                                if (vdm.GetWindowDesktopId(fg, out Guid fgDesktopId) == 0 && fgDesktopId != Guid.Empty)
                                {
                                    currentGuid = fgDesktopId;
                                }
                            }
                        }
                        catch
                        {
                        }
                    }

                    if (currentGuid == Guid.Empty)
                    {
                        return false;
                    }

                    byte[] buf = new byte[16];
                    for (int i = 0; i < totalCount; i++)
                    {
                        Buffer.BlockCopy(idsBytes, i * 16, buf, 0, 16);
                        if (new Guid(buf) == currentGuid)
                        {
                            currentIndex = i;
                            break;
                        }
                    }

                    if (currentIndex < 0)
                    {
                        return false;
                    }

                    return goingLeft ? (currentIndex == 0) : (currentIndex == totalCount - 1);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Trigger(bool isLeftEdge, DesktopBoundaryFeedbackStyle style)
        {
            if (style == DesktopBoundaryFeedbackStyle.None)
            {
                return;
            }

            long nowMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
            if (nowMs - Interlocked.Read(ref _lastTriggerTickMs) < 180)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _isAnimating, 1, 0) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _lastTriggerTickMs, nowMs);

            Thread worker = new Thread(() =>
            {
                try
                {
                    RunBounceAnimationOnCurrentThread(isLeftEdge, style);
                }
                catch (Exception ex)
                {
                    Program.LogError("DesktopBoundaryBounceService animation failed", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _isAnimating, 0);
                }
            })
            {
                IsBackground = true,
                Name = "FluentOsdPro_BoundaryBounceWorker"
            };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private static List<Win32Rect> GetAllMonitorPhysicalBounds()
        {
            List<Win32Rect> monitors = new List<Win32Rect>(4);
            try
            {
                EnumDisplayMonitors(
                    IntPtr.Zero,
                    IntPtr.Zero,
                    (IntPtr hMonitor, IntPtr hdcMonitor, ref Win32Rect lprcMonitor, IntPtr dwData) =>
                    {
                        Win32MonitorInfo mi = new Win32MonitorInfo { cbSize = Marshal.SizeOf(typeof(Win32MonitorInfo)) };
                        if (GetMonitorInfo(hMonitor, ref mi))
                        {
                            int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                            int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                            if (w > 10 && h > 10)
                            {
                                monitors.Add(mi.rcMonitor);
                            }
                        }
                        return true;
                    },
                    IntPtr.Zero);
            }
            catch
            {
            }

            if (monitors.Count == 0)
            {
                if (GetCursorPos(out Win32Point pt))
                {
                    IntPtr hMon = MonitorFromPoint(pt, MonitorDefaultToNearest);
                    if (hMon != IntPtr.Zero)
                    {
                        Win32MonitorInfo mi = new Win32MonitorInfo { cbSize = Marshal.SizeOf(typeof(Win32MonitorInfo)) };
                        if (GetMonitorInfo(hMon, ref mi))
                        {
                            monitors.Add(mi.rcMonitor);
                            return monitors;
                        }
                    }
                }

                monitors.Add(new Win32Rect
                {
                    Left = 0,
                    Top = 0,
                    Right = Math.Max(1, GetSystemMetrics(0)),
                    Bottom = Math.Max(1, GetSystemMetrics(1))
                });
            }

            return monitors;
        }

        private static void RunBounceAnimationOnCurrentThread(bool isLeftEdge, DesktopBoundaryFeedbackStyle style)
        {
            // Force PerMonitorV2 DPI awareness on this worker thread so multi-monitor setups with mixed DPIs
            // (e.g. 4K 150% + 2.5K 125%/150%) or negative coordinates always use raw 1:1 physical pixels!
            try
            {
                SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
            }
            catch
            {
            }

            EnsureWindowClassRegistered();
            List<Win32Rect> monitorRects = GetAllMonitorPhysicalBounds();

            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
            {
                return;
            }

            List<MonitorBounceSurface> surfaces = new List<MonitorBounceSurface>(monitorRects.Count);
            IntPtr hbrushBackdrop = IntPtr.Zero;
            IntPtr hbrushAccent = IntPtr.Zero;

            timeBeginPeriod(1);
            try
            {
                // BGR colors: deep matte dark backdrop (#0E1012 -> 0x0012100E) and Fluent sky accent (#3399FF -> 0x00FF9933)
                hbrushBackdrop = CreateSolidBrush(0x0012100Eu);
                hbrushAccent = CreateSolidBrush(0x00FF9933u);
                IntPtr hwndDesktop = GetDesktopWindow();
                IntPtr hModule = GetModuleHandleW(null);

                // Phase 1: Capture all monitors BEFORE showing any overlay window so no overlay captures another!
                for (int i = 0; i < monitorRects.Count; i++)
                {
                    Win32Rect rc = monitorRects[i];
                    int fullW = rc.Right - rc.Left;
                    int fullH = rc.Bottom - rc.Top;

                    // Inset all 4 edges by 1 physical pixel on EVERY monitor so:
                    // 1) explorer.exe (CRudeAppManager) NEVER treats the overlay as a fullscreen app (ABN_FULLSCREENAPP)
                    // 2) Auto-hidden taskbars on primary OR secondary monitors never pop up
                    // 3) Horizontal pixel shifts stay strictly clipped inside each monitor without bleeding across monitor seams
                    int monX = rc.Left + 1;
                    int monY = rc.Top + 1;
                    int monW = Math.Max(1, fullW - 2);
                    int monH = Math.Max(1, fullH - 2);

                    MonitorBounceSurface s = new MonitorBounceSurface
                    {
                        MonX = monX,
                        MonY = monY,
                        MonW = monW,
                        MonH = monH
                    };

                    s.HdcCapture = CreateCompatibleDC(hdcScreen);
                    s.HbmpCapture = CreateCompatibleBitmap(hdcScreen, monW, monH);
                    s.OldCapture = SelectObject(s.HdcCapture, s.HbmpCapture);
                    BitBlt(s.HdcCapture, 0, 0, monW, monH, hdcScreen, monX, monY, SrcCopy);

                    s.HdcBack = CreateCompatibleDC(hdcScreen);
                    s.HbmpBack = CreateCompatibleBitmap(hdcScreen, monW, monH);
                    s.OldBack = SelectObject(s.HdcBack, s.HbmpBack);

                    s.Hwnd = CreateWindowExW(
                        WsExTopmost | WsExToolWindow | WsExNoActivate | WsExTransparent,
                        OverlayClassName,
                        "FluentOsdPro_BoundaryBounce",
                        WsPopup,
                        monX,
                        monY,
                        monW,
                        monH,
                        hwndDesktop,
                        IntPtr.Zero,
                        hModule,
                        IntPtr.Zero);

                    if (s.Hwnd != IntPtr.Zero)
                    {
                        s.HdcWnd = GetDC(s.Hwnd);
                        surfaces.Add(s);
                    }
                    else
                    {
                        CleanupSurface(s);
                    }
                }

                if (surfaces.Count == 0)
                {
                    return;
                }

                double durationMs = style == DesktopBoundaryFeedbackStyle.RubberBandBounce ? 275.0 : 255.0;

                // Phase 2: Pre-render frame 0 on all monitors and show all overlays simultaneously
                EvaluateOffset(style, isLeftEdge, 0.0, out int firstDx, out int firstDy);
                for (int i = 0; i < surfaces.Count; i++)
                {
                    MonitorBounceSurface s = surfaces[i];
                    RenderFrame(s.HdcWnd, s.HdcBack, s.HdcCapture, s.MonW, s.MonH, firstDx, firstDy, hbrushBackdrop, hbrushAccent);
                }
                for (int i = 0; i < surfaces.Count; i++)
                {
                    ShowWindow(surfaces[i].Hwnd, SwShowNoActivate);
                }

                // Phase 3: Synchronous 120Hz trajectory across all monitors
                long startTicks = Stopwatch.GetTimestamp();
                while (true)
                {
                    double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs >= durationMs)
                    {
                        break;
                    }

                    double progress = Math.Max(0.0, Math.Min(1.0, elapsedMs / durationMs));
                    EvaluateOffset(style, isLeftEdge, progress, out int dx, out int dy);

                    for (int i = 0; i < surfaces.Count; i++)
                    {
                        MonitorBounceSurface s = surfaces[i];
                        RenderFrame(s.HdcWnd, s.HdcBack, s.HdcCapture, s.MonW, s.MonH, dx, dy, hbrushBackdrop, hbrushAccent);
                    }
                    Thread.Sleep(4);
                }

                // Phase 4: Settle at (0, 0) before hiding
                for (int i = 0; i < surfaces.Count; i++)
                {
                    MonitorBounceSurface s = surfaces[i];
                    RenderFrame(s.HdcWnd, s.HdcBack, s.HdcCapture, s.MonW, s.MonH, 0, 0, hbrushBackdrop, hbrushAccent);
                }
            }
            finally
            {
                timeEndPeriod(1);

                for (int i = 0; i < surfaces.Count; i++)
                {
                    CleanupSurface(surfaces[i]);
                }

                if (hbrushAccent != IntPtr.Zero) DeleteObject(hbrushAccent);
                if (hbrushBackdrop != IntPtr.Zero) DeleteObject(hbrushBackdrop);
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        private static void CleanupSurface(MonitorBounceSurface s)
        {
            if (s == null)
            {
                return;
            }

            if (s.Hwnd != IntPtr.Zero)
            {
                ShowWindow(s.Hwnd, SwHide);
                if (s.HdcWnd != IntPtr.Zero)
                {
                    ReleaseDC(s.Hwnd, s.HdcWnd);
                    s.HdcWnd = IntPtr.Zero;
                }
                DestroyWindow(s.Hwnd);
                s.Hwnd = IntPtr.Zero;
            }

            if (s.HdcBack != IntPtr.Zero)
            {
                if (s.OldBack != IntPtr.Zero) SelectObject(s.HdcBack, s.OldBack);
                if (s.HbmpBack != IntPtr.Zero) DeleteObject(s.HbmpBack);
                DeleteDC(s.HdcBack);
                s.HdcBack = IntPtr.Zero;
            }

            if (s.HdcCapture != IntPtr.Zero)
            {
                if (s.OldCapture != IntPtr.Zero) SelectObject(s.HdcCapture, s.OldCapture);
                if (s.HbmpCapture != IntPtr.Zero) DeleteObject(s.HbmpCapture);
                DeleteDC(s.HdcCapture);
                s.HdcCapture = IntPtr.Zero;
            }
        }

        private static void EvaluateOffset(
            DesktopBoundaryFeedbackStyle style,
            bool isLeftEdge,
            double p,
            out int dx,
            out int dy)
        {
            double sign = isLeftEdge ? 1.0 : -1.0;

            switch (style)
            {
                case DesktopBoundaryFeedbackStyle.HorizontalShake:
                    {
                        const double amplitude = 28.0;
                        double decay = (1.0 - p) * Math.Exp(-2.6 * p);
                        double osc = Math.Sin(p * 2.5 * 2.0 * Math.PI);
                        dx = (int)Math.Round(sign * amplitude * decay * osc);
                        dy = 0;
                        return;
                    }

                case DesktopBoundaryFeedbackStyle.ImpactThud:
                    {
                        const double amplitude = 34.0;
                        double decay = Math.Exp(-4.8 * p) * (1.0 - p);
                        double xVal = sign * amplitude * decay * Math.Cos(p * 3.0 * Math.PI);
                        double yVal = (amplitude * 0.22) * decay * Math.Sin(p * 6.0 * Math.PI);
                        dx = (int)Math.Round(xVal);
                        dy = (int)Math.Round(yVal);
                        return;
                    }

                default: // RubberBandBounce (macOS Mission Control style)
                    {
                        const double maxPullPx = 42.0;
                        double disp;
                        if (p < 0.25)
                        {
                            double t = p / 0.25;
                            double inv = 1.0 - t;
                            disp = maxPullPx * (1.0 - (inv * inv * inv));
                        }
                        else
                        {
                            double t = (p - 0.25) / 0.75;
                            disp = maxPullPx * Math.Exp(-5.2 * t) * Math.Cos(1.45 * Math.PI * t);
                        }
                        dx = (int)Math.Round(sign * disp);
                        dy = 0;
                        return;
                    }
            }
        }

        private static void RenderFrame(
            IntPtr hdcWnd,
            IntPtr hdcBack,
            IntPtr hdcCapture,
            int w,
            int h,
            int dx,
            int dy,
            IntPtr hbrushBackdrop,
            IntPtr hbrushAccent)
        {
            Win32Rect rcFull = new Win32Rect { Left = 0, Top = 0, Right = w, Bottom = h };
            FillRect(hdcBack, ref rcFull, hbrushBackdrop);

            if (dx > 2)
            {
                Win32Rect rcEdge = new Win32Rect { Left = 0, Top = 0, Right = Math.Min(6, dx), Bottom = h };
                FillRect(hdcBack, ref rcEdge, hbrushAccent);
            }
            else if (dx < -2)
            {
                Win32Rect rcEdge = new Win32Rect { Left = Math.Max(w - 6, w + dx), Top = 0, Right = w, Bottom = h };
                FillRect(hdcBack, ref rcEdge, hbrushAccent);
            }

            BitBlt(hdcBack, dx, dy, w, h, hdcCapture, 0, 0, SrcCopy);
            BitBlt(hdcWnd, 0, 0, w, h, hdcBack, 0, 0, SrcCopy);
        }
    }
}
