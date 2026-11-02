using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Media;

namespace FluentOsdPro
{
    internal readonly struct OsdPlacement
    {
        public OsdPlacement(double left, double finalTop, double startTop, double dpiScaleX, double dpiScaleY)
        {
            Left = left;
            FinalTop = finalTop;
            StartTop = startTop;
            DpiScaleX = dpiScaleX <= 0 ? 1.0 : dpiScaleX;
            DpiScaleY = dpiScaleY <= 0 ? 1.0 : dpiScaleY;
        }

        public double Left { get; }
        public double FinalTop { get; }
        public double StartTop { get; }
        public double DpiScaleX { get; }
        public double DpiScaleY { get; }

        public int LeftPx => (int)Math.Round(Left * DpiScaleX);
        public int FinalTopPx => (int)Math.Round(FinalTop * DpiScaleY);
        public int StartTopPx => (int)Math.Round(StartTop * DpiScaleY);
    }

    internal static class OsdPlacementService
    {
        private const uint MonitorDefaultToNearest = 0x00000002;
        private const uint ObjIdClient = 0xFFFFFFFC;
        private const uint ObjIdCaret = 0xFFFFFFF8;
        private const uint WmGetObject = 0x003D;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint ClsctxInprocServer = 1;
        private const int UiaValuePatternId = 10002;
        private const int UiaTextPatternId = 10014;
        private const int UiaTextPattern2Id = 10024;
        private const int UiaBoundingRectanglePropertyId = 30001;
        private const int UiaControlTypePropertyId = 30003;
        private const double BottomScreenMinMarginDip = 28.0;
        private const double AboveTaskbarGapDip = 18.0;
        private const double CornerMarginDip = 24.0;
        private const double StackSpacingDip = 10.0;
        private const double TopSlideOffsetDip = 10.0;
        private const double BottomSlideOffsetDip = 10.0;
        private const double CursorSlideOffsetDip = 3.0;

        private static Guid IidIAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        private static Guid ClsidCUIAutomation8 = new Guid("E22AD333-B25F-460C-83D0-0581107395C9");
        private static Guid ClsidCUIAutomation = new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E");
        private static Guid IidIUIAutomation = new Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
        private static Guid IidIUIAutomationTextPattern = new Guid("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A");
        private static Guid IidIUIAutomationTextPattern2 = new Guid("506A921A-FCC9-409F-B23B-37EB74106872");

        private static long _lastCaretLookupTick;
        private static IntPtr _lastCaretLookupFg;
        private static bool _lastCaretLookupSuccess;
        private static int _lastCaretXPx;
        private static int _lastCaretYPx;
        private static int _lastCaretHeightPx;
        private static string _lastCaretStrategy = string.Empty;
        private static IntPtr _lastWarmedFg;
        private static long _lastWarmedTick;
        private static IntPtr _lastMyGoCursorFg;
        private static long _lastMyGoCursorTick;
        private static int _lastMyGoCursorXPx;
        private static int _lastMyGoCursorYPx;
        private static int _lastMyGoCursorHeightPx;
        private static readonly object _caretCacheLock = new object();

        private sealed class MyGoBlinkFrame
        {
            public IntPtr FgHwnd;
            public int ScanLeft;
            public int ScanTop;
            public int ScanW;
            public int ScanH;
            public int BgR;
            public int BgG;
            public int BgB;
            public int Tick;
            public int[] Pixels;
        }

        private static readonly object _myGoBlinkLock = new object();
        private static readonly MyGoBlinkFrame[] _myGoBlinkRing = new MyGoBlinkFrame[3];
        private static int _myGoBlinkRingWriteIndex;
        private static int _myGoBlinkSamplingActive;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComGetFocusedElementDelegate(IntPtr thisPtr, out IntPtr element);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComElementFromHandleDelegate(IntPtr thisPtr, IntPtr hwnd, out IntPtr element);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComGetRawViewWalkerDelegate(IntPtr thisPtr, out IntPtr walker);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComTreeWalkerNavigateDelegate(IntPtr thisPtr, IntPtr element, out IntPtr targetElement);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComGetCurrentPatternDelegate(IntPtr thisPtr, int patternId, out IntPtr patternObj);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComGetCurrentControlTypeDelegate(IntPtr thisPtr, out int controlType);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComGetCurrentBoolPropertyDelegate(IntPtr thisPtr, out int retVal);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComGetCurrentBoundingRectDelegate(IntPtr thisPtr, out Win32Rect rect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComGetCaretRangeDelegate(IntPtr thisPtr, out int isActive, out IntPtr caretRange);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComGetSelectionDelegate(IntPtr thisPtr, out IntPtr rangeArray);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComRangeArrayGetLengthDelegate(IntPtr thisPtr, out int length);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComRangeArrayGetElementDelegate(IntPtr thisPtr, int index, out IntPtr element);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComTextRangeCloneDelegate(IntPtr thisPtr, out IntPtr clonedRange);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComTextRangeExpandToEnclosingUnitDelegate(IntPtr thisPtr, int unit);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComTextRangeGetBoundingRectanglesDelegate(IntPtr thisPtr, out IntPtr safeArray);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComTextRangeMoveEndpointByUnitDelegate(IntPtr thisPtr, int endpoint, int unit, int count, out int moved);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComTextRangeMoveEndpointByRangeDelegate(IntPtr thisPtr, int targetEndpoint, IntPtr srcRange, int srcEndpoint);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComAccLocationDelegate(IntPtr thisPtr, out int pxLeft, out int pyTop, out int pcxWidth, out int pcyHeight, IntPtr varChild);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComGetAccNameDelegate(IntPtr thisPtr, IntPtr varChild, out IntPtr pszName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ComGetAccFocusDelegate(IntPtr thisPtr, IntPtr pvarChild);

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public Win32Rect rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Win32Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Win32Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Win32MonitorInfo
        {
            public int cbSize;
            public Win32Rect rcMonitor;
            public Win32Rect rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetCaretBlinkTime();

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Win32Point lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Win32Point pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref Win32MonitorInfo lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo lpgui);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref Win32Point lpPoint);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(
            IntPtr hwnd,
            uint dwId,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppvObject);

        [DllImport("oleacc.dll", EntryPoint = "AccessibleObjectFromWindow")]
        private static extern int AccessibleObjectFromWindowPtr(
            IntPtr hwnd,
            uint dwId,
            ref Guid riid,
            out IntPtr ppvObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsContext,
            ref Guid riid,
            out IntPtr ppv);

        [DllImport("oleaut32.dll")]
        private static extern int SafeArrayGetLBound(IntPtr psa, uint nDim, out int plLbound);

        [DllImport("oleaut32.dll")]
        private static extern int SafeArrayGetUBound(IntPtr psa, uint nDim, out int plUbound);

        [DllImport("oleaut32.dll")]
        private static extern int SafeArrayAccessData(IntPtr psa, out IntPtr ppvData);

        [DllImport("oleaut32.dll")]
        private static extern int SafeArrayUnaccessData(IntPtr psa);

        [DllImport("oleaut32.dll")]
        private static extern int SafeArrayDestroy(IntPtr psa);

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader bmiHeader;
            public uint bmiColors;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BitmapInfo pbmi,
            uint iUsage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint dwOffset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(
            IntPtr hdcDest,
            int nXDest,
            int nYDest,
            int nWidth,
            int nHeight,
            IntPtr hdcSrc,
            int nXSrc,
            int nYSrc,
            uint dwRop);

        public static OsdPlacement Calculate(
            Visual visual,
            double widthDip,
            double heightDip,
            OsdPosition position,
            Rect? stackAboveDipRect = null)
        {
            double dpiX = 1.0;
            double dpiY = 1.0;
            try
            {
                if (visual != null)
                {
                    DpiScale dpi = VisualTreeHelper.GetDpi(visual);
                    if (dpi.DpiScaleX > 0) dpiX = dpi.DpiScaleX;
                    if (dpi.DpiScaleY > 0) dpiY = dpi.DpiScaleY;
                }
            }
            catch
            {
            }

            Rect monitorBoundsDip = SystemParameters.WorkArea;
            double taskbarBottomInsetDip = 0.0;

            int caretXPx = 0;
            int caretYPx = 0;
            int caretHeightPx = 18;
            bool hasTextCaret = false;

            IntPtr hMon;
            if (position == OsdPosition.FollowCursor &&
                TryGetTextCaretPhysicalPoint(out caretXPx, out caretYPx, out caretHeightPx))
            {
                hasTextCaret = true;
                hMon = MonitorFromPoint(new Win32Point { X = caretXPx, Y = caretYPx }, MonitorDefaultToNearest);
            }
            else
            {
                hMon = ResolveActiveMonitor();
            }

            if (hMon != IntPtr.Zero)
            {
                try
                {
                    if (GetDpiForMonitor(hMon, 0, out uint monDpiX, out uint monDpiY) == 0 &&
                        monDpiX > 0 && monDpiY > 0)
                    {
                        dpiX = monDpiX / 96.0;
                        dpiY = monDpiY / 96.0;
                    }
                }
                catch
                {
                }

                Win32MonitorInfo mi = new Win32MonitorInfo { cbSize = Marshal.SizeOf(typeof(Win32MonitorInfo)) };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    double mLeft = mi.rcMonitor.Left / dpiX;
                    double mTop = mi.rcMonitor.Top / dpiY;
                    double mWidth = Math.Max(320.0, (mi.rcMonitor.Right - mi.rcMonitor.Left) / dpiX);
                    double mHeight = Math.Max(200.0, (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / dpiY);
                    monitorBoundsDip = new Rect(mLeft, mTop, mWidth, mHeight);
                    taskbarBottomInsetDip = GetVisibleBottomTaskbarInsetDip(hMon, ref mi, dpiY);
                }
            }

            double left;
            double finalTop;
            double startTop;

            // InputSourcePro behavior (PreferencesVM+IndicatorPosition.swift):
            // If FollowCursor is selected but no text caret / input field is active, fall back to DEFAULT (SameAsVolume),
            // NEVER jumping to the mouse pointer.
            OsdPosition effectivePosition = (position == OsdPosition.FollowCursor && !hasTextCaret)
                ? OsdPosition.SameAsVolume
                : position;

            switch (effectivePosition)
            {
                case OsdPosition.FollowCursor:
                    {
                        double anchorXDip = caretXPx / dpiX;
                        double anchorYDip = caretYPx / dpiY;
                        double anchorHDip = Math.Max(14.0, caretHeightPx / dpiY);

                        double minLeft = monitorBoundsDip.Left + 8.0;
                        double maxLeft = Math.Max(minLeft, monitorBoundsDip.Right - widthDip - 8.0);

                        // Place at the Top-Right of the text caret (光标的右上角):
                        // Horizontally starts slightly to the right of the cursor (+4 DIP),
                        // and if the right edge of the current screen cannot fit the OSD,
                        // automatically flip to the left side of the cursor.
                        left = anchorXDip + 4.0;
                        if (left + widthDip > monitorBoundsDip.Right - 8.0)
                        {
                            left = anchorXDip - widthDip - 8.0;
                        }

                        if (left < minLeft) left = minLeft;
                        if (left > maxLeft) left = maxLeft;

                        // Vertically sits 6 DIP ABOVE the top edge of the text caret (anchorYDip).
                        // Flip below the caret only if the caret is near the very top edge of the screen.
                        finalTop = anchorYDip - heightDip - 6.0;
                        if (finalTop < monitorBoundsDip.Top + 8.0)
                        {
                            finalTop = anchorYDip + anchorHDip + 6.0;
                        }

                        double minTop = monitorBoundsDip.Top + 8.0;
                        double maxTop = Math.Max(minTop, monitorBoundsDip.Bottom - taskbarBottomInsetDip - heightDip - 8.0);
                        if (finalTop < minTop) finalTop = minTop;
                        if (finalTop > maxTop) finalTop = maxTop;

                        startTop = Math.Min(maxTop, finalTop + CursorSlideOffsetDip);
                        break;
                    }

                case OsdPosition.TopLeft:
                    left = monitorBoundsDip.Left + CornerMarginDip;
                    finalTop = monitorBoundsDip.Top + CornerMarginDip;
                    startTop = Math.Max(monitorBoundsDip.Top + 2.0, finalTop - TopSlideOffsetDip);
                    break;

                case OsdPosition.TopRight:
                    left = monitorBoundsDip.Right - widthDip - CornerMarginDip;
                    finalTop = monitorBoundsDip.Top + CornerMarginDip;
                    startTop = Math.Max(monitorBoundsDip.Top + 2.0, finalTop - TopSlideOffsetDip);
                    break;

                case OsdPosition.TopCenter:
                    left = monitorBoundsDip.Left + ((monitorBoundsDip.Width - widthDip) / 2.0);
                    finalTop = monitorBoundsDip.Top + CornerMarginDip;
                    startTop = Math.Max(monitorBoundsDip.Top + 2.0, finalTop - TopSlideOffsetDip);
                    break;

                case OsdPosition.BottomLeft:
                    {
                        double bottomInset = Math.Max(CornerMarginDip, taskbarBottomInsetDip + CornerMarginDip);
                        left = monitorBoundsDip.Left + CornerMarginDip;
                        finalTop = monitorBoundsDip.Bottom - heightDip - bottomInset;
                        startTop = Math.Min(monitorBoundsDip.Bottom - heightDip - 2.0, finalTop + BottomSlideOffsetDip);
                        break;
                    }

                case OsdPosition.BottomRight:
                    {
                        double bottomInset = Math.Max(CornerMarginDip, taskbarBottomInsetDip + CornerMarginDip);
                        left = monitorBoundsDip.Right - widthDip - CornerMarginDip;
                        finalTop = monitorBoundsDip.Bottom - heightDip - bottomInset;
                        startTop = Math.Min(monitorBoundsDip.Bottom - heightDip - 2.0, finalTop + BottomSlideOffsetDip);
                        break;
                    }

                case OsdPosition.SameAsVolume:
                default:
                    {
                        double bottomOffsetDip = Math.Max(BottomScreenMinMarginDip, taskbarBottomInsetDip + AboveTaskbarGapDip);
                        left = monitorBoundsDip.Left + ((monitorBoundsDip.Width - widthDip) / 2.0);
                        finalTop = monitorBoundsDip.Bottom - heightDip - bottomOffsetDip;
                        if (stackAboveDipRect.HasValue && !stackAboveDipRect.Value.IsEmpty)
                        {
                            double candidateTop = stackAboveDipRect.Value.Top - heightDip - StackSpacingDip;
                            if (candidateTop < finalTop)
                            {
                                finalTop = candidateTop;
                            }
                        }
                        startTop = Math.Min(monitorBoundsDip.Bottom - heightDip - 2.0, finalTop + BottomSlideOffsetDip);
                        break;
                    }
            }

            return new OsdPlacement(Math.Round(left), Math.Round(finalTop), Math.Round(startTop), dpiX, dpiY);
        }

        /// <summary>
        /// Proactively warms up Chromium/Electron (Kero, VS Code, Chrome, Edge) and Windows Terminal
        /// accessibility providers on all visible top-level windows at startup so that Chromium's
        /// internal ax_system_caret_ (AXSystemCaretWin) is instantiated before the user types.
        /// </summary>
        public static void WarmUpAllVisibleWindows()
        {
            Task.Run(() =>
            {
                try
                {
                    EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                    {
                        if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd))
                        {
                            return true;
                        }

                        if (!GetWindowRect(hWnd, out Win32Rect wr) ||
                            (wr.Right - wr.Left) < 300 ||
                            (wr.Bottom - wr.Top) < 200 ||
                            wr.Left <= -30000)
                        {
                            return true;
                        }

                        string className = GetWindowClassNameSafe(hWnd);
                        if (string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        WarmUpWindowTreeSync(hWnd, logDiagnostic: true);
                        return true;
                    }, IntPtr.Zero);
                }
                catch
                {
                }
            });
        }

        /// <summary>
        /// Proactively warms up the foreground window's accessibility bridge and ax_system_caret_
        /// when the user switches windows, so Electron/Chromium (Kero) and Windows Terminal track
        /// caret changes in real time.
        /// </summary>
        public static void WarmUpForegroundWindow(IntPtr fg)
        {
            if (fg == IntPtr.Zero)
            {
                return;
            }

            long now = Environment.TickCount & int.MaxValue;
            if (fg == _lastWarmedFg && (now - _lastWarmedTick) < 1500)
            {
                return;
            }
            _lastWarmedFg = fg;
            _lastWarmedTick = now;

            Task.Run(() =>
            {
                try
                {
                    WarmUpWindowTreeSync(fg);
                    ScheduleMyGoBlinkSamplesIfNeeded(fg);
                }
                catch
                {
                }
            });
        }

        private static void ScheduleMyGoBlinkSamplesIfNeeded(IntPtr fg)
        {
            if (fg == IntPtr.Zero)
            {
                return;
            }

            string cls = GetWindowClassNameSafe(fg);
            if (cls.IndexOf("MyGo", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _myGoBlinkSamplingActive, 1, 0) != 0)
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    uint sysBlink = GetCaretBlinkTime();
                    int baseHalfCycleMs = (sysBlink >= 180 && sysBlink <= 1200) ? (int)sysBlink : 500;
                    int delay1 = Math.Max(180, (baseHalfCycleMs * 48) / 100); // ~240ms
                    int delay2 = Math.Max(240, (baseHalfCycleMs * 62) / 100); // +~310ms (total ~550ms)

                    TryComputeTextCaretPhysicalPoint(fg, out _, out _, out _, out _);

                    await Task.Delay(delay1).ConfigureAwait(false);
                    if (GetForegroundWindow() != fg)
                    {
                        return;
                    }
                    if (TryComputeTextCaretPhysicalPoint(fg, out int cx1, out int cy1, out int ch1, out string st1) &&
                        string.Equals(st1, "MyGoTemporalBlinkCursor", StringComparison.Ordinal))
                    {
                        lock (_caretCacheLock)
                        {
                            _lastCaretLookupFg = fg;
                            _lastCaretLookupTick = Environment.TickCount & int.MaxValue;
                            _lastCaretLookupSuccess = true;
                            _lastCaretXPx = cx1;
                            _lastCaretYPx = cy1;
                            _lastCaretHeightPx = ch1;
                            _lastCaretStrategy = st1;
                        }
                        return;
                    }

                    await Task.Delay(delay2).ConfigureAwait(false);
                    if (GetForegroundWindow() != fg)
                    {
                        return;
                    }
                    if (TryComputeTextCaretPhysicalPoint(fg, out int cx2, out int cy2, out int ch2, out string st2))
                    {
                        lock (_caretCacheLock)
                        {
                            _lastCaretLookupFg = fg;
                            _lastCaretLookupTick = Environment.TickCount & int.MaxValue;
                            _lastCaretLookupSuccess = true;
                            _lastCaretXPx = cx2;
                            _lastCaretYPx = cy2;
                            _lastCaretHeightPx = ch2;
                            _lastCaretStrategy = st2;
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _myGoBlinkSamplingActive, 0);
                }
            });
        }

        /// <summary>
        /// Triggered on IME hotkey press (e.g. Shift keydown) ~60-120ms before the OSD pops up,
        /// warming up the renderer and prefetching the caret on a background MTA thread.
        /// </summary>
        public static void PrefetchCaretAsync()
        {
            if (AppSettings.Current.StatusOsdPosition != OsdPosition.FollowCursor)
            {
                return;
            }

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    if (TryComputeTextCaretPhysicalPoint(fg, out int cx, out int cy, out int ch, out string strategy))
                    {
                        lock (_caretCacheLock)
                        {
                            _lastCaretLookupFg = fg;
                            _lastCaretLookupTick = Environment.TickCount & int.MaxValue;
                            _lastCaretLookupSuccess = true;
                            _lastCaretXPx = cx;
                            _lastCaretYPx = cy;
                            _lastCaretHeightPx = ch;
                            _lastCaretStrategy = strategy;
                        }
                    }
                    ScheduleMyGoBlinkSamplesIfNeeded(fg);
                }
                catch
                {
                }
            });
        }

        private static bool TryGetTextCaretPhysicalPoint(
            out int xPx,
            out int yPx,
            out int heightPx)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 18;

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                return false;
            }

            long now = Environment.TickCount & int.MaxValue;
            string cachedStrategy = null;
            lock (_caretCacheLock)
            {
                if (_lastCaretLookupFg == fg &&
                    _lastCaretLookupSuccess &&
                    (now - _lastCaretLookupTick) >= 0 &&
                    (now - _lastCaretLookupTick) < 420)
                {
                    xPx = _lastCaretXPx;
                    yPx = _lastCaretYPx;
                    heightPx = _lastCaretHeightPx;
                    cachedStrategy = _lastCaretStrategy;
                }
            }

            if (cachedStrategy != null)
            {
                Program.LogInfo("[OSD-Caret] Found (" + xPx + "," + yPx + ",h=" + heightPx + ") via " + cachedStrategy + " [cached] for HWND=" + fg + " (" + GetWindowClassNameSafe(fg) + ")");
                return true;
            }

            if (TryComputeTextCaretPhysicalPoint(fg, out xPx, out yPx, out heightPx, out string strategy))
            {
                lock (_caretCacheLock)
                {
                    _lastCaretLookupFg = fg;
                    _lastCaretLookupTick = Environment.TickCount & int.MaxValue;
                    _lastCaretLookupSuccess = true;
                    _lastCaretXPx = xPx;
                    _lastCaretYPx = yPx;
                    _lastCaretHeightPx = heightPx;
                    _lastCaretStrategy = strategy;
                }
                Program.LogInfo("[OSD-Caret] Found (" + xPx + "," + yPx + ",h=" + heightPx + ") via " + strategy + " for HWND=" + fg + " (" + GetWindowClassNameSafe(fg) + ")");
                return true;
            }

            Program.LogInfo("[OSD-Caret] Not found for HWND=" + fg + " (" + GetWindowClassNameSafe(fg) + ") -> fallback BottomCenter");
            return false;
        }

        private static bool TryComputeTextCaretPhysicalPoint(
            IntPtr fg,
            out int xPx,
            out int yPx,
            out int heightPx,
            out string strategy)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 18;
            strategy = string.Empty;

            if (fg == IntPtr.Zero || !GetWindowRect(fg, out Win32Rect fgRect))
            {
                return false;
            }

            uint pid;
            uint threadId = GetWindowThreadProcessId(fg, out pid);
            GuiThreadInfo guiInfo = new GuiThreadInfo { cbSize = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            bool hasGuiInfo = threadId != 0 && GetGUIThreadInfo(threadId, ref guiInfo);

            // 1. Standard Win32 system caret via GetGUIThreadInfo
            if (hasGuiInfo && guiInfo.hwndCaret != IntPtr.Zero)
            {
                int cw = guiInfo.rcCaret.Right - guiInfo.rcCaret.Left;
                int ch = guiInfo.rcCaret.Bottom - guiInfo.rcCaret.Top;
                if (ch > 0 || cw > 0 || guiInfo.rcCaret.Left > 0 || guiInfo.rcCaret.Top > 0)
                {
                    Win32Point pt = new Win32Point { X = guiInfo.rcCaret.Left, Y = guiInfo.rcCaret.Top };
                    if (ClientToScreen(guiInfo.hwndCaret, ref pt) &&
                        IsReasonableCaretPoint(pt.X, pt.Y, ref fgRect))
                    {
                        xPx = pt.X;
                        yPx = pt.Y;
                        heightPx = ch > 2 && ch < 120 ? ch : 20;
                        strategy = "Win32Caret";
                        return true;
                    }
                }
            }

            IntPtr focusHwnd = hasGuiInfo && guiInfo.hwndFocus != IntPtr.Zero ? guiInfo.hwndFocus : fg;
            List<IntPtr> childRenderWindows = CollectRelevantChildWindows(fg);

            // 2. MSAA OBJID_CARET (works in Win32, Office, and Chromium/Electron AXSystemCaretWin once warmed)
            if (TryGetMsaaCaretFromHierarchy(focusHwnd, fg, childRenderWindows, ref fgRect, out xPx, out yPx, out heightPx))
            {
                strategy = "MsaaCaret";
                return true;
            }

            // 3. UIA (CUIAutomation8 TextPattern2::GetCaretRange + TextPattern::GetSelection + ExpandToEnclosingUnit
            //    + Monaco/xterm.js floating IME textarea BoundingRectangle + MyGoSurface UIA subtree) executed on an MTA thread.
            if (System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.MTA)
            {
                if (TryGetUiaCaretOnMtaThread(focusHwnd, fg, childRenderWindows, fgRect, out xPx, out yPx, out heightPx, out strategy))
                {
                    return true;
                }
            }
            else
            {
                Win32Rect capturedFgRect = fgRect;
                int uiaX = 0, uiaY = 0, uiaH = 20;
                string uiaStrategy = string.Empty;
                bool uiaFound = false;

                try
                {
                    Task uiaTask = Task.Run(() =>
                    {
                        uiaFound = TryGetUiaCaretOnMtaThread(focusHwnd, fg, childRenderWindows, capturedFgRect, out uiaX, out uiaY, out uiaH, out uiaStrategy);
                    });

                    if (uiaTask.Wait(150) && uiaFound)
                    {
                        xPx = uiaX;
                        yPx = uiaY;
                        heightPx = uiaH;
                        strategy = uiaStrategy;
                        return true;
                    }
                }
                catch
                {
                }
            }

            // 4. Re-check MSAA OBJID_CARET once more after UIA woke up the renderer
            if (TryGetMsaaCaretFromHierarchy(focusHwnd, fg, childRenderWindows, ref fgRect, out xPx, out yPx, out heightPx))
            {
                strategy = "MsaaCaretPostUia";
                return true;
            }

            return false;
        }

        private static List<IntPtr> CollectRelevantChildWindows(IntPtr fg)
        {
            List<IntPtr> result = new List<IntPtr>();
            if (fg == IntPtr.Zero)
            {
                return result;
            }

            try
            {
                EnumChildWindows(fg, delegate(IntPtr child, IntPtr lParam)
                {
                    if (child == IntPtr.Zero || !IsWindowVisible(child))
                    {
                        return true;
                    }

                    string cls = GetWindowClassNameSafe(child);
                    if (cls.IndexOf("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("RenderWidget", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("MyGoSurface", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("InputSite", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("DesktopWindowContentBridge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("ApplicationFrameInputSinkWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("TermControl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        cls.IndexOf("Scintilla", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result.Add(child);
                    }
                    return result.Count < 12;
                }, IntPtr.Zero);
            }
            catch
            {
            }

            return result;
        }

        private static string GetWindowClassNameSafe(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                StringBuilder sb = new StringBuilder(128);
                int len = GetClassName(hwnd, sb, sb.Capacity);
                if (len > 0)
                {
                    return sb.ToString();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static void WarmUpWindowTreeSync(IntPtr fg, bool logDiagnostic = false)
        {
            if (fg == IntPtr.Zero)
            {
                return;
            }

            uint pid;
            uint threadId = GetWindowThreadProcessId(fg, out pid);
            GuiThreadInfo guiInfo = new GuiThreadInfo { cbSize = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            bool hasGui = threadId != 0 && GetGUIThreadInfo(threadId, ref guiInfo);
            if (hasGui && guiInfo.hwndFocus != IntPtr.Zero && guiInfo.hwndFocus != fg)
            {
                WarmUpSingleWindowAccessibility(guiInfo.hwndFocus);
            }

            WarmUpSingleWindowAccessibility(fg);

            List<IntPtr> children = CollectRelevantChildWindows(fg);
            for (int i = 0; i < children.Count; i++)
            {
                WarmUpSingleWindowAccessibility(children[i]);
            }

            string fgCls = GetWindowClassNameSafe(fg);
            bool isMyGo = fgCls.IndexOf("MyGo", StringComparison.OrdinalIgnoreCase) >= 0;

            if (logDiagnostic || isMyGo)
            {
                try
                {
                    GetWindowRect(fg, out Win32Rect r);
                    TryGetMsaaCaretRect(fg, ref r, out int cx, out int cy, out int ch);
                    string childSummary = string.Empty;
                    for (int i = 0; i < children.Count; i++)
                    {
                        IntPtr chHwnd = children[i];
                        TryGetMsaaCaretRect(chHwnd, ref r, out int chX, out int chY, out int chH);
                        childSummary += "[" + chHwnd + ":" + GetWindowClassNameSafe(chHwnd) + ",loc=" + chX + "," + chY + ",h=" + chH + "]";
                    }
                    if (children.Count == 0)
                    {
                        EnumChildWindows(fg, delegate(IntPtr c, IntPtr lp)
                        {
                            if (c != IntPtr.Zero && IsWindowVisible(c))
                            {
                                childSummary += "(" + c + ":" + GetWindowClassNameSafe(c) + ")";
                            }
                            return childSummary.Length < 180;
                        }, IntPtr.Zero);
                    }
                    string procName = string.Empty;
                    try
                    {
                        if (pid != 0)
                        {
                            using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById((int)pid))
                            {
                                procName = p.ProcessName ?? string.Empty;
                            }
                        }
                    }
                    catch
                    {
                    }

                    string uiaDiag = string.Empty;
                    if (isMyGo || procName.IndexOf("Kero", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        IntPtr targetUiaHwnd = (hasGui && guiInfo.hwndFocus != IntPtr.Zero)
                            ? guiInfo.hwndFocus
                            : (children.Count > 0 ? children[0] : fg);
                        uiaDiag = ", UiaDiag=" + InspectUiaTreeDiagnostic(fg, targetUiaHwnd);
                    }

                    Program.LogInfo("[OSD-WarmUp] HWND=" + fg + " (" + procName + "/" + fgCls + ", PID=" + pid +
                                    ", Rect=" + r.Left + "," + r.Top + ".." + r.Right + "," + r.Bottom +
                                    ", Focus=" + guiInfo.hwndFocus + ", CaretLoc=" + cx + "," + cy + ",h=" + ch +
                                    ", Children=" + children.Count + childSummary + uiaDiag + ")");
                }
                catch
                {
                }
            }
        }

        private static string InspectUiaTreeDiagnostic(IntPtr fg, IntPtr surfaceHwnd)
        {
            StringBuilder sb = new StringBuilder();
            IntPtr uia = IntPtr.Zero;
            IntPtr walker = IntPtr.Zero;
            IntPtr rootEle = IntPtr.Zero;
            try
            {
                Guid clsid8 = ClsidCUIAutomation8;
                Guid iidUia = IidIUIAutomation;
                int hr = CoCreateInstance(ref clsid8, IntPtr.Zero, ClsctxInprocServer, ref iidUia, out uia);
                if (hr != 0 || uia == IntPtr.Zero)
                {
                    Guid clsidLegacy = ClsidCUIAutomation;
                    hr = CoCreateInstance(ref clsidLegacy, IntPtr.Zero, ClsctxInprocServer, ref iidUia, out uia);
                }

                if (hr != 0 || uia == IntPtr.Zero)
                {
                    return "noUia";
                }

                ComElementFromHandleDelegate elemFromHandle = GetVtableDelegate<ComElementFromHandleDelegate>(uia, 6);
                ComGetRawViewWalkerDelegate getRawWalker = GetVtableDelegate<ComGetRawViewWalkerDelegate>(uia, 14);
                if (elemFromHandle(uia, surfaceHwnd, out rootEle) != 0 || rootEle == IntPtr.Zero ||
                    getRawWalker(uia, out walker) != 0 || walker == IntPtr.Zero)
                {
                    return "noRoot";
                }

                ComTreeWalkerNavigateDelegate getLastChild = GetVtableDelegate<ComTreeWalkerNavigateDelegate>(walker, 5);
                ComTreeWalkerNavigateDelegate getPrevSibling = GetVtableDelegate<ComTreeWalkerNavigateDelegate>(walker, 7);

                Stack<IntPtr> stack = new Stack<IntPtr>();
                if (getLastChild(walker, rootEle, out IntPtr lastChild) == 0 && lastChild != IntPtr.Zero)
                {
                    stack.Push(lastChild);
                }

                int total = 0;
                int logged = 0;
                while (stack.Count > 0 && total < 300)
                {
                    IntPtr curr = stack.Pop();
                    total++;
                    try
                    {
                        if (getPrevSibling(walker, curr, out IntPtr prev) == 0 && prev != IntPtr.Zero)
                        {
                            stack.Push(prev);
                        }
                        if (getLastChild(walker, curr, out IntPtr chLast) == 0 && chLast != IntPtr.Zero)
                        {
                            stack.Push(chLast);
                        }

                        ComGetCurrentControlTypeDelegate getCt = GetVtableDelegate<ComGetCurrentControlTypeDelegate>(curr, 21);
                        ComGetCurrentBoolPropertyDelegate getFocus = GetVtableDelegate<ComGetCurrentBoolPropertyDelegate>(curr, 26);
                        ComGetCurrentBoolPropertyDelegate getFocusable = GetVtableDelegate<ComGetCurrentBoolPropertyDelegate>(curr, 27);
                        ComGetCurrentBoundingRectDelegate getRect = GetVtableDelegate<ComGetCurrentBoundingRectDelegate>(curr, 43);
                        ComGetCurrentPatternDelegate getPat = GetVtableDelegate<ComGetCurrentPatternDelegate>(curr, 16);

                        getCt(curr, out int ct);
                        getFocus(curr, out int hasFocus);
                        getFocusable(curr, out int focusable);
                        getRect(curr, out Win32Rect er);

                        bool hasTp = false;
                        if (getPat(curr, UiaTextPatternId, out IntPtr tp) == 0 && tp != IntPtr.Zero)
                        {
                            hasTp = true;
                            Marshal.Release(tp);
                        }
                        bool hasVp = false;
                        if (getPat(curr, UiaValuePatternId, out IntPtr vp) == 0 && vp != IntPtr.Zero)
                        {
                            hasVp = true;
                            Marshal.Release(vp);
                        }

                        // Log elements from the bottom-right AND any element with focus/focusable/non-Button/non-Text
                        bool interesting = total <= 16 ||
                                           hasTp ||
                                           hasVp ||
                                           hasFocus != 0 ||
                                           focusable != 0 ||
                                           (ct != 50000 && ct != 50020);
                        if (interesting && logged < 36)
                        {
                            logged++;
                            sb.Append("n").Append(total).Append("(ct=").Append(ct)
                              .Append(",f=").Append(hasFocus != 0 ? 1 : 0)
                              .Append(",fa=").Append(focusable != 0 ? 1 : 0)
                              .Append(",tp=").Append(hasTp ? 1 : 0)
                              .Append(",vp=").Append(hasVp ? 1 : 0)
                              .Append(",r=").Append(er.Left).Append(",").Append(er.Top)
                              .Append(",").Append(er.Right - er.Left).Append("x").Append(er.Bottom - er.Top).Append(");");
                        }
                    }
                    finally
                    {
                        Marshal.Release(curr);
                    }
                }
                while (stack.Count > 0)
                {
                    Marshal.Release(stack.Pop());
                }
                sb.Insert(0, "total=" + total + ";");
            }
            catch (Exception ex)
            {
                sb.Append("err=").Append(ex.GetType().Name);
            }
            finally
            {
                if (rootEle != IntPtr.Zero) Marshal.Release(rootEle);
                if (walker != IntPtr.Zero) Marshal.Release(walker);
                if (uia != IntPtr.Zero) Marshal.Release(uia);
            }
            return sb.ToString();
        }

        private static void WarmUpSingleWindowAccessibility(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                // 1) Trigger Chromium's kIdScreenReaderHoneyPot (object_id = 1) on LegacyRenderWidgetHostWin::OnGetObject
                //    so Chromium enables AXMode::kWebContents without requiring --force-renderer-accessibility!
                SendMessageTimeout(
                    hwnd,
                    WmGetObject,
                    IntPtr.Zero,
                    new IntPtr(1),
                    SmtoAbortIfHung,
                    20,
                    out _);

                // 2) Trigger UIA UiaRootObjectId (-25) so native UIA lazy providers (such as MyGo / MyGoSurface / JBR)
                //    enable frame accessibility descriptions before the next frame!
                SendMessageTimeout(
                    hwnd,
                    WmGetObject,
                    IntPtr.Zero,
                    new IntPtr(-25),
                    SmtoAbortIfHung,
                    20,
                    out _);

                IntPtr okClient = SendMessageTimeout(
                    hwnd,
                    WmGetObject,
                    IntPtr.Zero,
                    new IntPtr(unchecked((int)ObjIdClient)),
                    SmtoAbortIfHung,
                    20,
                    out _);

                if (okClient == IntPtr.Zero)
                {
                    return;
                }

                Guid iid = IidIAccessible;
                if (AccessibleObjectFromWindowPtr(hwnd, ObjIdClient, ref iid, out IntPtr accClient) == 0 &&
                    accClient != IntPtr.Zero)
                {
                    IntPtr varChild = Marshal.AllocHGlobal(24);
                    try
                    {
                        for (int i = 0; i < 24; i++) Marshal.WriteByte(varChild, i, 0);
                        Marshal.WriteInt16(varChild, 0, 3); // VT_I4 = 3, lVal = 0 (CHILDID_SELF)

                        // Calling vtable slot 10 (get_accName) directly works on both STA and MTA threads
                        // and triggers AXPlatformNodeWin::get_accName -> NotifyAccessibilityApiUsage()!
                        ComGetAccNameDelegate getAccName = GetVtableDelegate<ComGetAccNameDelegate>(accClient, 10);
                        if (getAccName(accClient, varChild, out IntPtr bstrName) == 0 && bstrName != IntPtr.Zero)
                        {
                            Marshal.FreeBSTR(bstrName);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(varChild);
                        Marshal.Release(accClient);
                    }
                }
            }
            catch
            {
            }
        }

        private static bool TryGetMsaaCaretFromHierarchy(
            IntPtr focusHwnd,
            IntPtr fg,
            List<IntPtr> childRenderWindows,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx)
        {
            if (TryGetMsaaCaretRect(focusHwnd, ref fgRect, out xPx, out yPx, out heightPx))
            {
                return true;
            }

            if (focusHwnd != fg && TryGetMsaaCaretRect(fg, ref fgRect, out xPx, out yPx, out heightPx))
            {
                return true;
            }

            if (childRenderWindows != null)
            {
                for (int i = 0; i < childRenderWindows.Count; i++)
                {
                    IntPtr child = childRenderWindows[i];
                    if (child != focusHwnd && child != fg &&
                        TryGetMsaaCaretRect(child, ref fgRect, out xPx, out yPx, out heightPx))
                    {
                        return true;
                    }
                }
            }

            xPx = 0;
            yPx = 0;
            heightPx = 20;
            return false;
        }

        private static bool TryGetUiaCaretOnMtaThread(
            IntPtr focusHwnd,
            IntPtr fg,
            List<IntPtr> childRenderWindows,
            Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx,
            out string strategy)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;
            strategy = string.Empty;

            // A. Native COM CUIAutomation8 (GetCaretPosEx algorithm + Native Right-to-Left Subtree Walker for MyGoSurface / Kero)
            if (TryGetNativeUiaCaretRect(focusHwnd, fg, childRenderWindows, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
            {
                return true;
            }

            // B. Managed UI Automation fallback (AutomationElement.FocusedElement)
            if (TryGetManagedUiaCaretRect(focusHwnd, fg, childRenderWindows, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
            {
                return true;
            }

            return false;
        }

        private static bool TryGetNativeUiaCaretRect(
            IntPtr focusHwnd,
            IntPtr fg,
            List<IntPtr> childRenderWindows,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx,
            out string strategy)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;
            strategy = string.Empty;

            IntPtr uia = IntPtr.Zero;
            IntPtr focusedEle = IntPtr.Zero;
            try
            {
                Guid clsid8 = ClsidCUIAutomation8;
                Guid iidUia = IidIUIAutomation;
                int hr = CoCreateInstance(ref clsid8, IntPtr.Zero, ClsctxInprocServer, ref iidUia, out uia);
                if (hr != 0 || uia == IntPtr.Zero)
                {
                    Guid clsidLegacy = ClsidCUIAutomation;
                    hr = CoCreateInstance(ref clsidLegacy, IntPtr.Zero, ClsctxInprocServer, ref iidUia, out uia);
                }

                if (hr != 0 || uia == IntPtr.Zero)
                {
                    return false;
                }

                // 1) Touch child render windows (Chrome_RenderWidgetHostHWND / MyGoSurface) via ElementFromHandle (slot 6)
                //    so lazy UIA providers wake up before GetFocusedElement
                ComElementFromHandleDelegate elemFromHandle = GetVtableDelegate<ComElementFromHandleDelegate>(uia, 6);
                if (childRenderWindows != null && childRenderWindows.Count > 0)
                {
                    for (int i = 0; i < childRenderWindows.Count; i++)
                    {
                        if (elemFromHandle(uia, childRenderWindows[i], out IntPtr childEle) == 0 && childEle != IntPtr.Zero)
                        {
                            Marshal.Release(childEle);
                        }
                    }
                }

                bool isMyGoFg = GetWindowClassNameSafe(fg).IndexOf("MyGo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                GetWindowClassNameSafe(focusHwnd).IndexOf("MyGo", StringComparison.OrdinalIgnoreCase) >= 0;

                // 2) IUIAutomation::GetFocusedElement (slot 8) - primary path for Chrome, Electron, WinUI, Terminal, Qt, Avalonia
                ComGetFocusedElementDelegate getFocused = GetVtableDelegate<ComGetFocusedElementDelegate>(uia, 8);
                if (getFocused(uia, out focusedEle) == 0 && focusedEle != IntPtr.Zero)
                {
                    if (TryExtractCaretFromNativeUiaElement(focusedEle, isSubtreeChild: false, isMyGoSurface: isMyGoFg, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
                    {
                        return true;
                    }
                }

                // 3) If GetFocusedElement returned an outer container pane/window (such as MyGoSurface in Kero),
                //    walk the UIA subtree of focusHwnd and childRenderWindows right-to-left (bottom-up) in native COM!
                if (focusHwnd != IntPtr.Zero &&
                    TryWalkNativeUiaSubtreeForCaret(uia, focusHwnd, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
                {
                    return true;
                }

                if (childRenderWindows != null)
                {
                    for (int i = 0; i < childRenderWindows.Count; i++)
                    {
                        IntPtr child = childRenderWindows[i];
                        if (child != IntPtr.Zero && child != focusHwnd &&
                            TryWalkNativeUiaSubtreeForCaret(uia, child, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
                        {
                            return true;
                        }
                    }
                }

                if (fg != IntPtr.Zero && fg != focusHwnd &&
                    TryWalkNativeUiaSubtreeForCaret(uia, fg, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
                {
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                if (focusedEle != IntPtr.Zero) Marshal.Release(focusedEle);
                if (uia != IntPtr.Zero) Marshal.Release(uia);
            }

            return false;
        }

        private static bool TryWalkNativeUiaSubtreeForCaret(
            IntPtr uia,
            IntPtr targetHwnd,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx,
            out string strategy)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;
            strategy = string.Empty;

            if (uia == IntPtr.Zero || targetHwnd == IntPtr.Zero)
            {
                return false;
            }

            string targetCls = GetWindowClassNameSafe(targetHwnd);
            bool isMyGoSurface = targetCls.IndexOf("MyGo", StringComparison.OrdinalIgnoreCase) >= 0;

            IntPtr rootEle = IntPtr.Zero;
            IntPtr walker = IntPtr.Zero;
            try
            {
                ComElementFromHandleDelegate elemFromHandle = GetVtableDelegate<ComElementFromHandleDelegate>(uia, 6);
                ComGetRawViewWalkerDelegate getRawWalker = GetVtableDelegate<ComGetRawViewWalkerDelegate>(uia, 14);
                if (elemFromHandle(uia, targetHwnd, out rootEle) != 0 || rootEle == IntPtr.Zero ||
                    getRawWalker(uia, out walker) != 0 || walker == IntPtr.Zero)
                {
                    return false;
                }

                // First check rootEle itself for TextPattern / TextPattern2 (e.g., TermControl / Chrome_RenderWidgetHostHWND)
                if (TryExtractCaretFromNativeUiaElement(rootEle, isSubtreeChild: false, isMyGoSurface: isMyGoSurface, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
                {
                    return true;
                }

                ComTreeWalkerNavigateDelegate getLastChild = GetVtableDelegate<ComTreeWalkerNavigateDelegate>(walker, 5);
                ComTreeWalkerNavigateDelegate getPrevSibling = GetVtableDelegate<ComTreeWalkerNavigateDelegate>(walker, 7);

                Stack<IntPtr> stack = new Stack<IntPtr>();
                if (getLastChild(walker, rootEle, out IntPtr lastChild) == 0 && lastChild != IntPtr.Zero)
                {
                    stack.Push(lastChild);
                }

                int bestFallbackX = 0;
                int bestFallbackY = 0;
                int bestFallbackH = 20;
                string bestFallbackStrat = string.Empty;
                bool hasFallback = false;

                int visited = 0;
                while (stack.Count > 0 && visited < 220)
                {
                    IntPtr curr = stack.Pop();
                    visited++;
                    try
                    {
                        if (getPrevSibling(walker, curr, out IntPtr prev) == 0 && prev != IntPtr.Zero)
                        {
                            stack.Push(prev);
                        }
                        if (getLastChild(walker, curr, out IntPtr chLast) == 0 && chLast != IntPtr.Zero)
                        {
                            stack.Push(chLast);
                        }

                        if (TryExtractCaretFromNativeUiaElement(curr, isSubtreeChild: true, isMyGoSurface: isMyGoSurface, ref fgRect, out int cx, out int cy, out int ch, out string strat))
                        {
                            // Exact text range or focused/Edit/ValuePattern input box wins immediately
                            if (!string.Equals(strat, "MyGoWorkspaceInputBox", StringComparison.Ordinal))
                            {
                                xPx = cx;
                                yPx = cy;
                                heightPx = ch;
                                strategy = strat;
                                return true;
                            }

                            if (!hasFallback)
                            {
                                hasFallback = true;
                                bestFallbackX = cx;
                                bestFallbackY = cy;
                                bestFallbackH = ch;
                                bestFallbackStrat = strat;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(curr);
                    }
                }

                while (stack.Count > 0)
                {
                    Marshal.Release(stack.Pop());
                }

                if (hasFallback)
                {
                    xPx = bestFallbackX;
                    yPx = bestFallbackY;
                    heightPx = bestFallbackH;
                    strategy = bestFallbackStrat;
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                if (rootEle != IntPtr.Zero) Marshal.Release(rootEle);
                if (walker != IntPtr.Zero) Marshal.Release(walker);
            }

            return false;
        }

        private static bool TryExtractCaretFromNativeUiaElement(
            IntPtr uiaEle,
            bool isSubtreeChild,
            bool isMyGoSurface,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx,
            out string strategy)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;
            strategy = string.Empty;

            if (uiaEle == IntPtr.Zero)
            {
                return false;
            }

            int hasFocus = 1;
            if (isSubtreeChild)
            {
                // IUIAutomationElement::get_CurrentHasKeyboardFocus is vtable slot 26
                ComGetCurrentBoolPropertyDelegate getFocus = GetVtableDelegate<ComGetCurrentBoolPropertyDelegate>(uiaEle, 26);
                getFocus(uiaEle, out hasFocus);
            }

            ComGetCurrentPatternDelegate getPattern = GetVtableDelegate<ComGetCurrentPatternDelegate>(uiaEle, 16);
            bool hasTextPattern = false;
            bool hasValuePattern = false;

            // 1) IUIAutomationTextPattern2::GetCaretRange (UIA_TextPattern2Id = 10024, slot 10)
            if (getPattern(uiaEle, UiaTextPattern2Id, out IntPtr textPattern2) == 0 && textPattern2 != IntPtr.Zero)
            {
                hasTextPattern = true;
                try
                {
                    ComGetCaretRangeDelegate getCaretRange = GetVtableDelegate<ComGetCaretRangeDelegate>(textPattern2, 10);
                    if (getCaretRange(textPattern2, out int isActive, out IntPtr caretRange) == 0 && caretRange != IntPtr.Zero)
                    {
                        try
                        {
                            if ((!isSubtreeChild || hasFocus != 0 || isActive != 0 || isMyGoSurface) &&
                                TryExtractCaretFromNativeTextRange(caretRange, ref fgRect, out xPx, out yPx, out heightPx))
                            {
                                strategy = "NativeUia8_TextPattern2";
                                return true;
                            }
                        }
                        finally
                        {
                            Marshal.Release(caretRange);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(textPattern2);
                }
            }

            // 2) IUIAutomationTextPattern::GetSelection (UIA_TextPatternId = 10014, slot 5)
            if (getPattern(uiaEle, UiaTextPatternId, out IntPtr textPattern) == 0 && textPattern != IntPtr.Zero)
            {
                hasTextPattern = true;
                try
                {
                    ComGetSelectionDelegate getSelection = GetVtableDelegate<ComGetSelectionDelegate>(textPattern, 5);
                    if (getSelection(textPattern, out IntPtr rangeArray) == 0 && rangeArray != IntPtr.Zero)
                    {
                        try
                        {
                            ComRangeArrayGetLengthDelegate getLength = GetVtableDelegate<ComRangeArrayGetLengthDelegate>(rangeArray, 3);
                            if (getLength(rangeArray, out int length) == 0 && length > 0)
                            {
                                ComRangeArrayGetElementDelegate getElement = GetVtableDelegate<ComRangeArrayGetElementDelegate>(rangeArray, 4);
                                if (getElement(rangeArray, 0, out IntPtr selectionRange) == 0 && selectionRange != IntPtr.Zero)
                                {
                                    try
                                    {
                                        if ((!isSubtreeChild || hasFocus != 0 || isMyGoSurface) &&
                                            TryExtractCaretFromNativeTextRange(selectionRange, ref fgRect, out xPx, out yPx, out heightPx))
                                        {
                                            strategy = "NativeUia8_TextPattern";
                                            return true;
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.Release(selectionRange);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.Release(rangeArray);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(textPattern);
                }
            }

            // 3) Check ValuePattern (UIA_ValuePatternId = 10002, used by MyGo TextInput/TextArea, Qt, and Avalonia)
            if (getPattern(uiaEle, UiaValuePatternId, out IntPtr valPattern) == 0 && valPattern != IntPtr.Zero)
            {
                hasValuePattern = true;
                Marshal.Release(valPattern);
            }

            // 4) Check element's own CurrentBoundingRectangle (slot 43) for Monaco .inputarea / xterm.js .xterm-helper-textarea / MyGo input boxes
            ComGetCurrentControlTypeDelegate getControlType = GetVtableDelegate<ComGetCurrentControlTypeDelegate>(uiaEle, 21);
            ComGetCurrentBoundingRectDelegate getBoundingRect = GetVtableDelegate<ComGetCurrentBoundingRectDelegate>(uiaEle, 43);
            if (getControlType(uiaEle, out int controlTypeId) == 0 &&
                getBoundingRect(uiaEle, out Win32Rect elemWinRect) == 0)
            {
                Rect elemRect = new Rect(
                    elemWinRect.Left,
                    elemWinRect.Top,
                    Math.Max(0, elemWinRect.Right - elemWinRect.Left),
                    Math.Max(0, elemWinRect.Bottom - elemWinRect.Top));

                // For GetFocusedElement (isSubtreeChild == false), or a focused child in the subtree (hasFocus != 0),
                // or an explicit Edit (50004) / ValuePattern input control inside MyGoSurface:
                bool canCheckInputBounds =
                    (!isSubtreeChild && (hasTextPattern || hasValuePattern || controlTypeId == 50004 || controlTypeId == 50030 || (isMyGoSurface && controlTypeId != 50000 && controlTypeId != 50032))) ||
                    (isSubtreeChild && hasFocus != 0 && (hasTextPattern || hasValuePattern || controlTypeId == 50004 || controlTypeId == 50030 || (isMyGoSurface && controlTypeId != 50000 && controlTypeId != 50032))) ||
                    (isSubtreeChild && isMyGoSurface && (hasValuePattern || controlTypeId == 50004));

                if (canCheckInputBounds &&
                    TryExtractCaretFromElementBounds(elemRect, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
                {
                    return true;
                }

                // 5) Kero / MyGoSurface terminal & chat workspace pane (ct=50026 Group):
                //    In Kero (built on github.com/egoist/mygo), the right-hand workspace pane is a single GPU-rendered
                //    UIA Group (ct=50026). Locate the live terminal block cursor `█`, `>` chat prompt box, or
                //    active terminal command line (e.g. `jimmy@windows:SSYW>`) across the full workspace pane!
                if (isSubtreeChild && isMyGoSurface && controlTypeId == 50026)
                {
                    double fgW = Math.Max(1.0, fgRect.Right - fgRect.Left);
                    double fgH = Math.Max(1.0, fgRect.Bottom - fgRect.Top);
                    if (elemRect.Width > fgW * 0.45 &&
                        elemRect.Height > fgH * 0.45 &&
                        elemRect.Left > fgRect.Left + 80)
                    {
                        if (TryFindMyGoTerminalCursorRect(elemRect, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Locates the active terminal block cursor or active prompt line inside Kero's GPU-rendered
        /// MyGoSurface workspace pane using a top-down 32bpp DIB section across the full pane height.
        /// Supports two distinct workspace modes:
        ///   - Mode A (AI Chat Box with blue `>` chevron and horizontal divider lines, at any vertical height):
        ///     Strictly constrains detection to `[boxTopY .. boxBottomY]` so upper chat history is never matched.
        ///   - Mode B (Standard Terminal / SSH / PowerShell Tab, e.g. `jimmy@windows:SSYW>` at the top of a new tab):
        ///     Scans from the bottom of the terminal canvas upward to locate the lowest active terminal text line,
        ///     matching either its terminal block cursor or the rightmost glyph at the end of that prompt line.
        /// Also masks out our own StatusOsdWindow if currently visible so rapid Shift toggles never self-detect.
        /// </summary>
        private static bool TryFindMyGoTerminalCursorRect(
            Rect elemRect,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx,
            out string strategy)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 32;
            strategy = string.Empty;

            int paneLeft = (int)Math.Round(elemRect.Left);
            int paneTop = (int)Math.Round(elemRect.Top);
            int paneRight = (int)Math.Round(elemRect.Right);
            int paneBottom = (int)Math.Round(elemRect.Bottom);
            int paneW = paneRight - paneLeft;
            int paneH = paneBottom - paneTop;
            if (paneW < 240 || paneH < 180)
            {
                return false;
            }

            // Capture the full vertical height of the workspace pane (from top tab bar margin down to bottom margin)
            // so newly opened terminal/SSH tabs (where `jimmy@windows:SSYW>` is near the top) are detected accurately.
            int scanMarginX = Math.Min(24, paneW / 24);
            int scanLeft = paneLeft + scanMarginX;
            int scanRight = paneRight - Math.Max(scanMarginX, 24); // Exclude right-edge scrollbar track
            int scanW = scanRight - scanLeft;
            int scanTop = paneTop + 16;
            int scanBottom = paneBottom - 16;
            int scanH = scanBottom - scanTop;

            if (scanW <= 64 || scanH <= 48)
            {
                return false;
            }

            // If our own StatusOsdWindow is currently visible on screen, compute its scan-local bounding box
            // (expanded by 18px for shadow/border) so we never mistake our own OSD capsule for terminal text!
            int maskLeft = -1, maskTop = -1, maskRight = -1, maskBottom = -1;
            if (StatusOsdWindow.TryGetVisibleScreenBounds(out int osdL, out int osdT, out int osdR, out int osdB))
            {
                maskLeft = Math.Max(0, osdL - scanLeft - 18);
                maskTop = Math.Max(0, osdT - scanTop - 18);
                maskRight = Math.Min(scanW - 1, osdR - scanLeft + 18);
                maskBottom = Math.Min(scanH - 1, osdB - scanTop + 18);
            }

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr hDib = IntPtr.Zero;
            IntPtr oldObj = IntPtr.Zero;

            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero)
                {
                    return false;
                }

                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero)
                {
                    return false;
                }

                BitmapInfo bmi = new BitmapInfo();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader));
                bmi.bmiHeader.biWidth = scanW;
                bmi.bmiHeader.biHeight = -scanH; // Top-down DIB
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0; // BI_RGB

                hDib = CreateDIBSection(screenDc, ref bmi, 0, out IntPtr pBits, IntPtr.Zero, 0);
                if (hDib == IntPtr.Zero || pBits == IntPtr.Zero)
                {
                    return false;
                }

                oldObj = SelectObject(memDc, hDib);
                const uint SrcCopy = 0x00CC0020;
                if (!BitBlt(memDc, 0, 0, scanW, scanH, screenDc, scanLeft, scanTop, SrcCopy))
                {
                    return false;
                }

                int totalPixels = scanW * scanH;
                int[] pixels = new int[totalPixels];
                Marshal.Copy(pBits, pixels, 0, totalPixels);

                // Sample background RGB from multiple vertical positions along the quiet left margin (x = 4)
                // and pick the darkest background sample so text on a single row never skews bgR/bgG/bgB.
                int bgR = 24, bgG = 28, bgB = 24;
                int bestBgLum = int.MaxValue;
                int[] bgSampleYs = new int[]
                {
                    Math.Max(4, scanH - 20),
                    (scanH * 3) / 4,
                    scanH / 2,
                    scanH / 4,
                    Math.Min(scanH - 4, 24)
                };
                for (int i = 0; i < bgSampleYs.Length; i++)
                {
                    int sy = bgSampleYs[i];
                    if (sy >= maskTop && sy <= maskBottom && 4 >= maskLeft && 4 <= maskRight)
                    {
                        continue;
                    }
                    int sc = pixels[sy * scanW + 4];
                    int sr = (sc >> 16) & 0xFF;
                    int sg = (sc >> 8) & 0xFF;
                    int sb = sc & 0xFF;
                    int lum = sr + sg + sb;
                    if (lum < bestBgLum)
                    {
                        bestBgLum = lum;
                        bgR = sr;
                        bgG = sg;
                        bgB = sb;
                    }
                }

                // Mask out our own visible StatusOsdWindow pixels by filling them with the background color
                if (maskLeft <= maskRight && maskTop <= maskBottom && maskRight >= 0 && maskBottom >= 0)
                {
                    int bgPacked = (bgR << 16) | (bgG << 8) | bgB;
                    for (int my = maskTop; my <= maskBottom; my++)
                    {
                        int rOff = my * scanW;
                        for (int mx = maskLeft; mx <= maskRight; mx++)
                        {
                            pixels[rOff + mx] = bgPacked;
                        }
                    }
                }

                // =========================================================================================
                // MODE A: Check for AI Chat Input Box (Antigravity CLI / Claude Code CLI / Kero TUI)
                // Step 1: Locate the pair of horizontal divider lines (`topDiv` and `botDiv`) in the bottom
                // region of the pane (within 420px of bottom, never up in the chat history at Y=3342/3644!).
                // =========================================================================================
                int aiSearchTopY = Math.Max(12, scanH - 420);
                int foundBotDiv = -1;
                int foundTopDiv = -1;
                int lastDivY = -999;

                for (int y = scanH - 6; y >= aiSearchTopY; y--)
                {
                    if (IsHorizontalDividerRow(pixels, scanW, y, bgR, bgG, bgB))
                    {
                        if (lastDivY > 0 && Math.Abs(lastDivY - y) <= 6)
                        {
                            if (foundTopDiv == lastDivY)
                            {
                                foundTopDiv = y;
                            }
                            lastDivY = y;
                            continue;
                        }

                        lastDivY = y;
                        if (foundBotDiv < 0)
                        {
                            foundBotDiv = y;
                        }
                        else
                        {
                            int gap = foundBotDiv - y;
                            if (gap >= 28 && gap <= 260)
                            {
                                foundTopDiv = y;
                                break;
                            }
                            else
                            {
                                foundBotDiv = y;
                            }
                        }
                    }
                }

                bool isAiChatInputBox = false;
                int boxTopY = 8;
                int boxBottomY = scanH - 8;
                int promptRightX = 36;
                int promptTopY = scanH - 80;

                if (foundTopDiv >= 0 && foundBotDiv > foundTopDiv)
                {
                    isAiChatInputBox = true;
                    boxTopY = Math.Max(4, foundTopDiv + 3);
                    boxBottomY = Math.Min(scanH - 4, foundBotDiv - 3);
                    promptTopY = Math.Max(boxTopY + 4, boxBottomY - 56);

                    // Find the leftmost prompt chevron (`>` or `❯`, any color) near the left margin (x in 8..110),
                    // stopping at the first vertical background gap so we never swallow an adjacent block cursor `█`!
                    int promptMaxX = Math.Min(scanW - 8, 110);
                    int chevronStartX = -1;
                    int chevronEndX = -1;
                    int chevronMinY = boxBottomY;
                    for (int x = 8; x <= promptMaxX; x++)
                    {
                        int colFg = 0;
                        int colMinY = boxBottomY;
                        for (int y = boxTopY + 2; y <= boxBottomY - 2; y++)
                        {
                            int c = pixels[y * scanW + x];
                            int r = (c >> 16) & 0xFF;
                            int g = (c >> 8) & 0xFF;
                            int b = c & 0xFF;
                            if (Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB) >= 85)
                            {
                                colFg++;
                                if (y < colMinY) colMinY = y;
                            }
                        }

                        if (colFg > 0)
                        {
                            if (chevronStartX < 0)
                            {
                                chevronStartX = x;
                            }
                            chevronEndX = x;
                            if (colMinY < chevronMinY) chevronMinY = colMinY;

                            if (chevronEndX - chevronStartX >= 22)
                            {
                                break;
                            }
                        }
                        else if (chevronStartX >= 0 && x - chevronEndX >= 2)
                        {
                            break;
                        }
                    }

                    if (chevronEndX > 0)
                    {
                        promptRightX = chevronEndX;
                        promptTopY = Math.Max(boxTopY + 2, chevronMinY - 10);
                    }
                }

                if (isAiChatInputBox)
                {
                    IntPtr fgNow = GetForegroundWindow();
                    int nowTick = Environment.TickCount;

                    // Mode A Priority 1 (Temporal Blink Diff across 3-frame ring buffer):
                    if (TryFindTemporalBlinkCursorInBand(
                        fgNow, pixels, scanLeft, scanTop, scanW, scanH,
                        Math.Max(8, promptRightX), scanW - 12, boxTopY, boxBottomY,
                        bgR, bgG, bgB, nowTick, ref fgRect,
                        out xPx, out yPx, out heightPx))
                    {
                        strategy = "MyGoTemporalBlinkCursor";
                        _lastMyGoCursorFg = fgNow;
                        _lastMyGoCursorTick = nowTick;
                        _lastMyGoCursorXPx = xPx;
                        _lastMyGoCursorYPx = yPx;
                        _lastMyGoCursorHeightPx = heightPx;
                        PushMyGoBlinkFrame(fgNow, scanLeft, scanTop, scanW, scanH, bgR, bgG, bgB, nowTick, pixels);
                        return true;
                    }

                    // Mode A Priority 2 (Color-Agnostic Solid / Inverted Cursor Block `█` in [boxTopY .. boxBottomY]):
                    // Handles steady black block cursors (`❯ █`), colored blocks, AND double-width CJK cursor blocks
                    // with inverted white Chinese characters inside!
                    int searchStartX = Math.Max(16, promptRightX + 2);
                    for (int y = boxBottomY - 4; y >= boxTopY + 4; y -= 3)
                    {
                        int rowOffset = y * scanW;
                        for (int x = scanW - 16; x >= searchStartX; x -= 3)
                        {
                            int c = pixels[rowOffset + x];
                            int r = (c >> 16) & 0xFF;
                            int g = (c >> 8) & 0xFF;
                            int b = c & 0xFF;

                            int bgDiff = Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB);
                            if (bgDiff >= 90)
                            {
                                if (TryMeasureColorAgnosticCursorBlock(
                                    pixels, scanW, scanH, x, y, r, g, b, bgR, bgG, bgB,
                                    out int blockLeft, out int blockRight, out int blockTop, out int blockBottom) &&
                                    blockTop >= boxTopY - 2 && blockBottom <= boxBottomY + 2)
                                {
                                    int screenX = scanLeft + blockRight;
                                    int screenY = scanTop + blockTop;
                                    int blockH = blockBottom - blockTop + 1;
                                    if (IsReasonableCaretPoint(screenX, screenY, ref fgRect))
                                    {
                                        xPx = screenX;
                                        yPx = screenY;
                                        heightPx = blockH;
                                        strategy = "MyGoColorAgnosticCursorBlock";
                                        _lastMyGoCursorFg = fgNow;
                                        _lastMyGoCursorTick = nowTick;
                                        _lastMyGoCursorXPx = xPx;
                                        _lastMyGoCursorYPx = yPx;
                                        _lastMyGoCursorHeightPx = heightPx;
                                        PushMyGoBlinkFrame(fgNow, scanLeft, scanTop, scanW, scanH, bgR, bgG, bgB, nowTick, pixels);
                                        return true;
                                    }
                                }
                            }
                        }
                    }

                    PushMyGoBlinkFrame(fgNow, scanLeft, scanTop, scanW, scanH, bgR, bgG, bgB, nowTick, pixels);

                    // Mode A Priority 3 (Recent Confirmed Cursor Cache during 500ms Blink-OFF half-cycle):
                    // Only reuse a recently confirmed real cursor block / blink cell within the input box bounds.
                    // Never guess text line ends!
                    long ageMs = unchecked((uint)(nowTick - _lastMyGoCursorTick));
                    int boxScreenTop = scanTop + boxTopY - 6;
                    int boxScreenBottom = scanTop + boxBottomY + 6;
                    if (fgNow != IntPtr.Zero &&
                        fgNow == _lastMyGoCursorFg &&
                        ageMs <= 4500 &&
                        _lastMyGoCursorYPx >= boxScreenTop &&
                        _lastMyGoCursorYPx <= boxScreenBottom &&
                        IsReasonableCaretPoint(_lastMyGoCursorXPx, _lastMyGoCursorYPx, ref fgRect))
                    {
                        xPx = _lastMyGoCursorXPx;
                        yPx = _lastMyGoCursorYPx;
                        heightPx = _lastMyGoCursorHeightPx;
                        strategy = "MyGoTerminalCursorBlinkCached";
                        return true;
                    }
                }

                // =========================================================================================
                // MODE B: Standard Terminal / SSH / PowerShell Tab (e.g. `jimmy@windows:SSYW>` at top/middle)
                // =========================================================================================
                IntPtr termFgNow = GetForegroundWindow();
                int termNowTick = Environment.TickCount;

                // Mode B Priority 1: Check 3-frame Temporal Blink Diff across the full terminal canvas!
                if (TryFindTemporalBlinkCursorInBand(
                    termFgNow, pixels, scanLeft, scanTop, scanW, scanH,
                    8, scanW - 16, 8, scanH - 8,
                    bgR, bgG, bgB, termNowTick, ref fgRect,
                    out xPx, out yPx, out heightPx))
                {
                    strategy = "MyGoTemporalBlinkCursor";
                    _lastMyGoCursorFg = termFgNow;
                    _lastMyGoCursorTick = termNowTick;
                    _lastMyGoCursorXPx = xPx;
                    _lastMyGoCursorYPx = yPx;
                    _lastMyGoCursorHeightPx = heightPx;
                    PushMyGoBlinkFrame(termFgNow, scanLeft, scanTop, scanW, scanH, bgR, bgG, bgB, termNowTick, pixels);
                    return true;
                }

                // Mode B Priority 2: Find the lowest non-empty terminal text line (when not an AI Chat Input Box)
                if (!isAiChatInputBox)
                {
                    int lowestTextRowY = -1;
                    for (int y = scanH - 12; y >= 8; y -= 2)
                    {
                        if (IsHorizontalDividerRow(pixels, scanW, y, bgR, bgG, bgB))
                        {
                            continue;
                        }

                        int rowOffset = y * scanW;
                        int rowFgPixels = 0;
                        for (int x = 8; x < scanW - 24; x += 2)
                        {
                            int c = pixels[rowOffset + x];
                            int r = (c >> 16) & 0xFF;
                            int g = (c >> 8) & 0xFF;
                            int b = c & 0xFF;
                            if (Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB) >= 95)
                            {
                                rowFgPixels++;
                                if (rowFgPixels >= 3)
                                {
                                    lowestTextRowY = y;
                                    break;
                                }
                            }
                        }

                        if (lowestTextRowY >= 0)
                        {
                            break;
                        }
                    }

                    if (lowestTextRowY >= 0)
                    {
                        int lineBottomY = Math.Min(scanH - 4, lowestTextRowY + 8);
                        int lineTopY = Math.Max(4, lowestTextRowY - 38);
                        int blankRows = 0;
                        for (int y = lowestTextRowY - 1; y >= Math.Max(4, lowestTextRowY - 56); y--)
                        {
                            int rowOffset = y * scanW;
                            bool rowHasText = false;
                            for (int x = 8; x < scanW - 24; x += 3)
                            {
                                int c = pixels[rowOffset + x];
                                int r = (c >> 16) & 0xFF;
                                int g = (c >> 8) & 0xFF;
                                int b = c & 0xFF;
                                if (Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB) >= 95)
                                {
                                    rowHasText = true;
                                    break;
                                }
                            }

                            if (!rowHasText)
                            {
                                blankRows++;
                                if (blankRows >= 5)
                                {
                                    lineTopY = y + blankRows;
                                    break;
                                }
                            }
                            else
                            {
                                blankRows = 0;
                                lineTopY = y;
                            }
                        }

                        // Check for a color-agnostic cursor block on this lowest active terminal line
                        for (int y = lineBottomY; y >= lineTopY; y -= 2)
                        {
                            int rowOffset = y * scanW;
                            for (int x = scanW - 16; x >= 8; x -= 3)
                            {
                                int c = pixels[rowOffset + x];
                                int r = (c >> 16) & 0xFF;
                                int g = (c >> 8) & 0xFF;
                                int b = c & 0xFF;
                                if (Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB) >= 90)
                                {
                                    if (TryMeasureColorAgnosticCursorBlock(
                                        pixels, scanW, scanH, x, y, r, g, b, bgR, bgG, bgB,
                                        out int blockLeft, out int blockRight, out int blockTop, out int blockBottom) &&
                                        blockTop >= lineTopY - 8 && blockBottom <= lineBottomY + 8)
                                    {
                                        int screenX = scanLeft + blockRight;
                                        int screenY = scanTop + blockTop;
                                        int blockH = blockBottom - blockTop + 1;
                                        if (IsReasonableCaretPoint(screenX, screenY, ref fgRect))
                                        {
                                            xPx = screenX;
                                            yPx = screenY;
                                            heightPx = blockH;
                                            strategy = "MyGoColorAgnosticCursorBlock";
                                            _lastMyGoCursorFg = termFgNow;
                                            _lastMyGoCursorTick = termNowTick;
                                            _lastMyGoCursorXPx = xPx;
                                            _lastMyGoCursorYPx = yPx;
                                            _lastMyGoCursorHeightPx = heightPx;
                                            PushMyGoBlinkFrame(termFgNow, scanLeft, scanTop, scanW, scanH, bgR, bgG, bgB, termNowTick, pixels);
                                            return true;
                                        }
                                    }
                                }
                            }
                        }

                        PushMyGoBlinkFrame(termFgNow, scanLeft, scanTop, scanW, scanH, bgR, bgG, bgB, termNowTick, pixels);

                        long termAgeMs = unchecked((uint)(termNowTick - _lastMyGoCursorTick));
                        int lineScreenTop = scanTop + lineTopY - 12;
                        int lineScreenBottom = scanTop + lineBottomY + 12;
                        if (termFgNow != IntPtr.Zero &&
                            termFgNow == _lastMyGoCursorFg &&
                            termAgeMs <= 4500 &&
                            _lastMyGoCursorYPx >= lineScreenTop &&
                            _lastMyGoCursorYPx <= lineScreenBottom &&
                            IsReasonableCaretPoint(_lastMyGoCursorXPx, _lastMyGoCursorYPx, ref fgRect))
                        {
                            xPx = _lastMyGoCursorXPx;
                            yPx = _lastMyGoCursorYPx;
                            heightPx = _lastMyGoCursorHeightPx;
                            strategy = "MyGoTerminalCursorBlinkCached";
                            return true;
                        }
                    }
                    else
                    {
                        PushMyGoBlinkFrame(termFgNow, scanLeft, scanTop, scanW, scanH, bgR, bgG, bgB, termNowTick, pixels);
                    }
                }

                // Honest Fallback: If no confirmed cursor block, no temporal blink cell, and no recent confirmed cursor cache
                // could be verified, return false (never guess a fake text-end coordinate) so OsdPlacementService.Calculate
                // cleanly displays the flyout at the Volume OSD position (BottomCenter)!
            }
            catch
            {
            }
            finally
            {
                if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldObj);
                if (hDib != IntPtr.Zero) DeleteObject(hDib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }

            return false;
        }

        private static void PushMyGoBlinkFrame(
            IntPtr fg,
            int scanLeft,
            int scanTop,
            int scanW,
            int scanH,
            int bgR,
            int bgG,
            int bgB,
            int nowTick,
            int[] pixels)
        {
            if (fg == IntPtr.Zero || pixels == null)
            {
                return;
            }

            lock (_myGoBlinkLock)
            {
                int prevIdx = (_myGoBlinkRingWriteIndex + _myGoBlinkRing.Length - 1) % _myGoBlinkRing.Length;
                MyGoBlinkFrame newest = _myGoBlinkRing[prevIdx];
                if (newest != null &&
                    newest.FgHwnd == fg &&
                    newest.ScanW == scanW &&
                    newest.ScanH == scanH &&
                    unchecked((uint)(nowTick - newest.Tick)) < 110)
                {
                    return;
                }

                _myGoBlinkRing[_myGoBlinkRingWriteIndex] = new MyGoBlinkFrame
                {
                    FgHwnd = fg,
                    ScanLeft = scanLeft,
                    ScanTop = scanTop,
                    ScanW = scanW,
                    ScanH = scanH,
                    BgR = bgR,
                    BgG = bgG,
                    BgB = bgB,
                    Tick = nowTick,
                    Pixels = pixels
                };
                _myGoBlinkRingWriteIndex = (_myGoBlinkRingWriteIndex + 1) % _myGoBlinkRing.Length;
            }
        }

        private static bool TryFindTemporalBlinkCursorInBand(
            IntPtr fg,
            int[] currentPixels,
            int scanLeft,
            int scanTop,
            int scanW,
            int scanH,
            int minX,
            int maxX,
            int minY,
            int maxY,
            int bgR,
            int bgG,
            int bgB,
            int nowTick,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 36;

            if (fg == IntPtr.Zero || currentPixels == null)
            {
                return false;
            }

            int clampedMinX = Math.Max(4, minX);
            int clampedMaxX = Math.Min(scanW - 4, maxX);
            int clampedMinY = Math.Max(4, minY);
            int clampedMaxY = Math.Min(scanH - 4, maxY);
            if (clampedMaxX <= clampedMinX + 4 || clampedMaxY <= clampedMinY + 12)
            {
                return false;
            }

            MyGoBlinkFrame[] snapshots = new MyGoBlinkFrame[_myGoBlinkRing.Length];
            lock (_myGoBlinkLock)
            {
                for (int i = 0; i < _myGoBlinkRing.Length; i++)
                {
                    snapshots[i] = _myGoBlinkRing[i];
                }
            }

            for (int s = 0; s < snapshots.Length; s++)
            {
                MyGoBlinkFrame prev = snapshots[s];
                if (prev == null ||
                    prev.FgHwnd != fg ||
                    prev.ScanLeft != scanLeft ||
                    prev.ScanTop != scanTop ||
                    prev.ScanW != scanW ||
                    prev.ScanH != scanH ||
                    prev.Pixels == null ||
                    prev.Pixels.Length != currentPixels.Length)
                {
                    continue;
                }

                uint ageMs = unchecked((uint)(nowTick - prev.Tick));
                if (ageMs < 120 || ageMs > 2500)
                {
                    continue;
                }

                if (Math.Abs(prev.BgR - bgR) + Math.Abs(prev.BgG - bgG) + Math.Abs(prev.BgB - bgB) > 24)
                {
                    continue;
                }

                int diffMinX = int.MaxValue;
                int diffMaxX = int.MinValue;
                int diffMinY = int.MaxValue;
                int diffMaxY = int.MinValue;
                int changedCount = 0;
                bool tooLarge = false;

                for (int y = clampedMinY; y <= clampedMaxY; y += 2)
                {
                    int rowOff = y * scanW;
                    for (int x = clampedMinX; x <= clampedMaxX; x += 2)
                    {
                        int c1 = currentPixels[rowOff + x];
                        int c0 = prev.Pixels[rowOff + x];
                        if (c1 == c0)
                        {
                            continue;
                        }

                        int r1 = (c1 >> 16) & 0xFF, g1 = (c1 >> 8) & 0xFF, b1 = c1 & 0xFF;
                        int r0 = (c0 >> 16) & 0xFF, g0 = (c0 >> 8) & 0xFF, b0 = c0 & 0xFF;
                        if (Math.Abs(r1 - r0) + Math.Abs(g1 - g0) + Math.Abs(b1 - b0) >= 75)
                        {
                            changedCount++;
                            if (x < diffMinX) diffMinX = x;
                            if (x > diffMaxX) diffMaxX = x;
                            if (y < diffMinY) diffMinY = y;
                            if (y > diffMaxY) diffMaxY = y;

                            if (diffMaxX - diffMinX > 56 || diffMaxY - diffMinY > 72 || changedCount > 950)
                            {
                                tooLarge = true;
                                break;
                            }
                        }
                    }
                    if (tooLarge)
                    {
                        break;
                    }
                }

                if (!tooLarge && changedCount >= 10 && diffMaxX >= diffMinX && diffMaxY >= diffMinY)
                {
                    int dw = diffMaxX - diffMinX + 1;
                    int dh = diffMaxY - diffMinY + 1;
                    // Single or CJK double-width character cell blink: width 2..54px, height 18..66px
                    if (dw >= 2 && dw <= 54 && dh >= 18 && dh <= 66)
                    {
                        int screenX = scanLeft + diffMaxX;
                        int screenY = scanTop + diffMinY;
                        int screenH = Math.Max(28, dh);
                        if (IsReasonableCaretPoint(screenX, screenY, ref fgRect))
                        {
                            xPx = screenX;
                            yPx = screenY;
                            heightPx = screenH;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsHorizontalDividerRow(int[] pixels, int width, int y, int bgR, int bgG, int bgB)
        {
            if (y <= 0 || width < 120)
            {
                return false;
            }

            int startX = width / 8;
            int endX = width - (width / 8);
            int step = Math.Max(4, (endX - startX) / 32);
            int rowOffset = y * width;

            int firstPixel = pixels[rowOffset + startX];
            int r0 = (firstPixel >> 16) & 0xFF;
            int g0 = (firstPixel >> 8) & 0xFF;
            int b0 = firstPixel & 0xFF;

            // A horizontal divider line must contrast with the background
            if (Math.Abs(r0 - bgR) + Math.Abs(g0 - bgG) + Math.Abs(b0 - bgB) < 16)
            {
                return false;
            }

            int matchCount = 0;
            int totalSamples = 0;
            for (int x = startX; x <= endX; x += step)
            {
                totalSamples++;
                if (IsSameRgb(pixels[rowOffset + x], r0, g0, b0))
                {
                    matchCount++;
                }
            }

            return totalSamples >= 16 && matchCount * 100 >= totalSamples * 78;
        }

        /// <summary>
        /// Color-agnostic block cursor detector supporting both:
        /// 1. Single-width ASCII cursor blocks (10..26px wide, steady or blinking, any color including solid black `█`),
        /// 2. Double-width CJK cursor blocks (27..54px wide) with inverted white Chinese/English characters inside!
        /// Expands vertically FIRST to locate the solid top/bottom cap rows (`top + 2`, `bottom - 2`) where no character
        /// strokes exist, then measures the full horizontal span `[left .. right]` along those unobstructed cap rows.
        /// </summary>
        private static bool TryMeasureColorAgnosticCursorBlock(
            int[] pixels,
            int width,
            int height,
            int seedX,
            int seedY,
            int targetR,
            int targetG,
            int targetB,
            int bgR,
            int bgG,
            int bgB,
            out int left,
            out int right,
            out int top,
            out int bottom)
        {
            left = seedX;
            right = seedX;
            top = seedY;
            bottom = seedY;

            // Step 1: Anti-aliased 1..2px edge inward nudging.
            // Because the caller scans from right-to-left and bottom-to-top, (seedX, seedY) often lands on the
            // 1px anti-aliased right or bottom border of `█`. Check 2px inward (left/up) for stronger background contrast.
            int baseDiff = Math.Abs(targetR - bgR) + Math.Abs(targetG - bgG) + Math.Abs(targetB - bgB);
            int[] nudgeDx = new int[] { -2, -2, 0, -3 };
            int[] nudgeDy = new int[] { 0, -2, -2, -1 };
            for (int n = 0; n < nudgeDx.Length; n++)
            {
                int nx = seedX + nudgeDx[n];
                int ny = seedY + nudgeDy[n];
                if (nx >= 2 && nx < width - 2 && ny >= 2 && ny < height - 2)
                {
                    int nc = pixels[ny * width + nx];
                    int nr = (nc >> 16) & 0xFF;
                    int ng = (nc >> 8) & 0xFF;
                    int nb = nc & 0xFF;
                    int nDiff = Math.Abs(nr - bgR) + Math.Abs(ng - bgG) + Math.Abs(nb - bgB);
                    if (nDiff > baseDiff + 24)
                    {
                        seedX = nx;
                        seedY = ny;
                        targetR = nr;
                        targetG = ng;
                        targetB = nb;
                        baseDiff = nDiff;
                    }
                }
            }

            // Step 2: Expand vertically FIRST along seedX (and seedX - 2) so we reach the unobstructed
            // top cap row and bottom cap row of the block even if (seedX, seedY) is next to an inner white glyph!
            int bestTop = seedY;
            int bestBottom = seedY;
            int[] initialVerticalCols = new int[] { seedX, Math.Max(1, seedX - 2), Math.Min(width - 2, seedX + 1) };
            for (int i = 0; i < initialVerticalCols.Length; i++)
            {
                int px = initialVerticalCols[i];
                if (!IsSameRgb(pixels[seedY * width + px], targetR, targetG, targetB))
                {
                    continue;
                }
                int t = seedY;
                int b = seedY;
                while (t > 0 && IsSameRgb(pixels[(t - 1) * width + px], targetR, targetG, targetB))
                {
                    t--;
                    if (seedY - t > 72) break;
                }
                while (b < height - 1 && IsSameRgb(pixels[(b + 1) * width + px], targetR, targetG, targetB))
                {
                    b++;
                    if (b - seedY > 72) break;
                }
                if (t < bestTop) bestTop = t;
                if (b > bestBottom) bestBottom = b;
            }

            // Step 3: Expand horizontally along `seedY` AND along the unobstructed top cap (`bestTop + 1`, `bestTop + 2`)
            // and bottom cap (`bestBottom - 1`, `bestBottom - 2`) rows so an inverted white glyph (`A` or Chinese `中`)
            // in the middle of the cursor block NEVER stops the horizontal width measurement!
            int bestLeft = seedX;
            int bestRight = seedX;
            int[] horizProbeYs = new int[]
            {
                seedY,
                Math.Min(height - 2, bestTop + 1),
                Math.Min(height - 2, bestTop + 2),
                Math.Max(1, bestBottom - 1),
                Math.Max(1, bestBottom - 2)
            };
            for (int i = 0; i < horizProbeYs.Length; i++)
            {
                int py = horizProbeYs[i];
                if (!IsSameRgb(pixels[py * width + seedX], targetR, targetG, targetB))
                {
                    continue;
                }
                int l = seedX;
                int r = seedX;
                while (l > 0 && IsSameRgb(pixels[py * width + (l - 1)], targetR, targetG, targetB))
                {
                    l--;
                    if (seedX - l > 60) return false;
                }
                while (r < width - 1 && IsSameRgb(pixels[py * width + (r + 1)], targetR, targetG, targetB))
                {
                    r++;
                    if (r - l > 60) return false;
                }
                if (l < bestLeft) bestLeft = l;
                if (r > bestRight) bestRight = r;
            }

            left = bestLeft;
            right = bestRight;
            int bw = right - left + 1;
            // Support both 1-cell ASCII (10..26px) and 2-cell CJK full-width (27..54px) cursor blocks!
            if (bw < 10 || bw > 54)
            {
                return false;
            }

            // Step 4: Refine vertical span [top .. bottom] along left edge (`left + 2`), right edge (`right - 2`), and center
            int[] probeXs = new int[] { Math.Min(right, left + 2), Math.Max(left, right - 2), (left + right) / 2 };
            for (int i = 0; i < probeXs.Length; i++)
            {
                int px = probeXs[i];
                int startY = IsSameRgb(pixels[seedY * width + px], targetR, targetG, targetB)
                    ? seedY
                    : Math.Min(height - 2, bestTop + 2);
                if (!IsSameRgb(pixels[startY * width + px], targetR, targetG, targetB))
                {
                    continue;
                }
                int t = startY;
                int b = startY;
                while (t > 0 && IsSameRgb(pixels[(t - 1) * width + px], targetR, targetG, targetB))
                {
                    t--;
                    if (startY - t > 72) break;
                }
                while (b < height - 1 && IsSameRgb(pixels[(b + 1) * width + px], targetR, targetG, targetB))
                {
                    b++;
                    if (b - startY > 72) break;
                }
                if (t < bestTop) bestTop = t;
                if (b > bestBottom) bestBottom = b;
            }

            top = bestTop;
            bottom = bestBottom;
            int bh = bottom - top + 1;

            if (bh < 22 || bh > 68)
            {
                return false;
            }

            // Aspect ratio check:
            // - Single-width cell (bw <= 26): tall rectangle (bh >= bw * 1.35)
            // - Double-width CJK cell (bw 27..54): near-square 2-column cell (bh >= bw * 0.78 && bh <= bw * 1.65)
            if (bw <= 26)
            {
                if (bh * 100 < bw * 135)
                {
                    return false;
                }
            }
            else
            {
                if (bh * 100 < bw * 78 || bh * 100 > bw * 165)
                {
                    return false;
                }
            }

            // Step 5: Verify that the inset perimeter (`top + 2`, `bottom - 2`, `left + 2`, `right - 2`)
            // forms a solid rectangular frame of the cursor color (inset by 2px to avoid 1px anti-aliased edge dropoff).
            int perimeterSamples = 0;
            int perimeterMatches = 0;
            int topCapY = Math.Min(bottom - 1, top + 2);
            int botCapY = Math.Max(top + 1, bottom - 2);
            int leftEdgeX = Math.Min(right - 1, left + 2);
            int rightEdgeX = Math.Max(left + 1, right - 2);

            for (int px = leftEdgeX; px <= rightEdgeX; px++)
            {
                perimeterSamples += 2;
                if (IsSameRgb(pixels[topCapY * width + px], targetR, targetG, targetB)) perimeterMatches++;
                if (IsSameRgb(pixels[botCapY * width + px], targetR, targetG, targetB)) perimeterMatches++;
            }

            for (int py = topCapY; py <= botCapY; py += 2)
            {
                int rOff = py * width;
                perimeterSamples += 2;
                if (IsSameRgb(pixels[rOff + leftEdgeX], targetR, targetG, targetB)) perimeterMatches++;
                if (IsSameRgb(pixels[rOff + rightEdgeX], targetR, targetG, targetB)) perimeterMatches++;
            }

            if (perimeterSamples < 16 || perimeterMatches * 100 < perimeterSamples * 72)
            {
                return false;
            }

            // Step 6: Verify interior fill ratio:
            // - An empty 1-cell solid block cursor has ~95%..100% cursor-color fill.
            // - A 1-cell or 2-cell CJK block cursor sitting on top of an inverted character (`A` or Chinese `中`)
            //   has 30%..90% cursor-color fill (the remaining pixels are the inverted glyph strokes).
            int interiorSamples = 0;
            int interiorCursorColorMatches = 0;
            for (int py = topCapY; py <= botCapY; py += 2)
            {
                int rOff = py * width;
                for (int px = leftEdgeX; px <= rightEdgeX; px += 2)
                {
                    interiorSamples++;
                    if (IsSameRgb(pixels[rOff + px], targetR, targetG, targetB))
                    {
                        interiorCursorColorMatches++;
                    }
                }
            }

            if (interiorSamples < 12 || interiorCursorColorMatches * 100 < interiorSamples * 30)
            {
                return false;
            }

            // A 2-cell wide CJK block cursor (bw >= 27) ONLY exists when sitting over a wide CJK glyph,
            // so it must contain inverted glyph strokes inside (<= 94% solid fill), preventing solid square UI icons from matching!
            if (bw >= 27 && interiorCursorColorMatches * 100 > interiorSamples * 94)
            {
                return false;
            }

            // Step 7: Verify exterior horizontally (at top cap, middle, and bottom cap) is NOT the cursor block color
            int midY = (top + bottom) / 2;
            int[] checkYs = new int[] { topCapY, midY, botCapY };
            for (int i = 0; i < checkYs.Length; i++)
            {
                int cy = checkYs[i];
                if (left >= 5 && IsSameRgb(pixels[cy * width + (left - 5)], targetR, targetG, targetB))
                {
                    return false;
                }
                if (right + 5 < width && IsSameRgb(pixels[cy * width + (right + 5)], targetR, targetG, targetB))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSameRgb(int pixel, int r, int g, int b)
        {
            int pr = (pixel >> 16) & 0xFF;
            int pg = (pixel >> 8) & 0xFF;
            int pb = pixel & 0xFF;
            return Math.Abs(pr - r) <= 4 && Math.Abs(pg - g) <= 4 && Math.Abs(pb - b) <= 4;
        }

        private static bool TryExtractCaretFromNativeTextRange(
            IntPtr textRange,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;

            if (textRange == IntPtr.Zero)
            {
                return false;
            }

            // Step A: Direct GetBoundingRectangles (works for Chromium TextPattern2 on empty <textarea> or non-empty selection)
            if (TryGetNativeRangeRect(textRange, useRightEdge: false, ref fgRect, out xPx, out yPx, out heightPx))
            {
                return true;
            }

            ComTextRangeCloneDelegate cloneFn = GetVtableDelegate<ComTextRangeCloneDelegate>(textRange, 3);

            // Step B: ExpandToEnclosingUnit(Character = 0) -> Left edge
            // Required for Windows Terminal (UiaTextRangeBase::GetBoundingRectangles returns empty for 0-length cursor range)
            if (cloneFn(textRange, out IntPtr charRange) == 0 && charRange != IntPtr.Zero)
            {
                try
                {
                    ComTextRangeExpandToEnclosingUnitDelegate expandFn = GetVtableDelegate<ComTextRangeExpandToEnclosingUnitDelegate>(charRange, 6);
                    if (expandFn(charRange, 0) == 0 &&
                        TryGetNativeRangeRect(charRange, useRightEdge: false, ref fgRect, out xPx, out yPx, out heightPx))
                    {
                        return true;
                    }
                }
                finally
                {
                    Marshal.Release(charRange);
                }
            }

            // Step C: MoveEndpointByUnit(Start = 0, Character = 0, -1) -> Right edge of previous character
            if (cloneFn(textRange, out IntPtr prevRange) == 0 && prevRange != IntPtr.Zero)
            {
                try
                {
                    ComTextRangeMoveEndpointByUnitDelegate moveFn = GetVtableDelegate<ComTextRangeMoveEndpointByUnitDelegate>(prevRange, 14);
                    if (moveFn(prevRange, 0, 0, -1, out int moved) == 0 && moved != 0 &&
                        TryGetNativeRangeRect(prevRange, useRightEdge: true, ref fgRect, out xPx, out yPx, out heightPx))
                    {
                        return true;
                    }
                }
                finally
                {
                    Marshal.Release(prevRange);
                }
            }

            // Step D: MoveEndpointByUnit(End = 1, Character = 0, +1) -> Left edge of next character
            if (cloneFn(textRange, out IntPtr nextRange) == 0 && nextRange != IntPtr.Zero)
            {
                try
                {
                    ComTextRangeMoveEndpointByUnitDelegate moveFn = GetVtableDelegate<ComTextRangeMoveEndpointByUnitDelegate>(nextRange, 14);
                    if (moveFn(nextRange, 1, 0, 1, out int moved) == 0 && moved != 0 &&
                        TryGetNativeRangeRect(nextRange, useRightEdge: false, ref fgRect, out xPx, out yPx, out heightPx))
                    {
                        return true;
                    }
                }
                finally
                {
                    Marshal.Release(nextRange);
                }
            }

            // Step E: ExpandToEnclosingUnit(Line = 3) -> Right edge (GetCaretPosEx end-of-line / terminal prompt fallback)
            if (cloneFn(textRange, out IntPtr lineRange) == 0 && lineRange != IntPtr.Zero)
            {
                try
                {
                    ComTextRangeExpandToEnclosingUnitDelegate expandFn = GetVtableDelegate<ComTextRangeExpandToEnclosingUnitDelegate>(lineRange, 6);
                    if (expandFn(lineRange, 3) == 0 &&
                        TryGetNativeRangeRect(lineRange, useRightEdge: true, ref fgRect, out xPx, out yPx, out heightPx))
                    {
                        return true;
                    }
                }
                finally
                {
                    Marshal.Release(lineRange);
                }
            }

            return false;
        }

        private static bool TryGetNativeRangeRect(
            IntPtr textRange,
            bool useRightEdge,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;

            ComTextRangeGetBoundingRectanglesDelegate getRects = GetVtableDelegate<ComTextRangeGetBoundingRectanglesDelegate>(textRange, 10);
            if (getRects(textRange, out IntPtr psa) != 0 || psa == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (SafeArrayGetLBound(psa, 1, out int lBound) != 0 ||
                    SafeArrayGetUBound(psa, 1, out int uBound) != 0)
                {
                    return false;
                }

                int count = uBound - lBound + 1;
                if (count < 4)
                {
                    return false;
                }

                if (SafeArrayAccessData(psa, out IntPtr pData) != 0 || pData == IntPtr.Zero)
                {
                    return false;
                }

                double[] coords = new double[count];
                try
                {
                    Marshal.Copy(pData, coords, 0, count);
                }
                finally
                {
                    SafeArrayUnaccessData(psa);
                }

                int rectCount = count / 4;
                int idx = useRightEdge ? (rectCount - 1) * 4 : 0;
                Rect r = new Rect(
                    coords[idx],
                    coords[idx + 1],
                    Math.Max(0.0, coords[idx + 2]),
                    Math.Max(0.0, coords[idx + 3]));

                return TryExtractCaretFromRect(r, useRightEdge, ref fgRect, out xPx, out yPx, out heightPx);
            }
            finally
            {
                SafeArrayDestroy(psa);
            }
        }

        private static T GetVtableDelegate<T>(IntPtr comObj, int slotIndex) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(comObj);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtable, slotIndex * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(T)) as T;
        }

        private static bool TryGetManagedUiaCaretRect(
            IntPtr focusHwnd,
            IntPtr fg,
            List<IntPtr> childRenderWindows,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx,
            out string strategy)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;
            strategy = string.Empty;

            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused != null &&
                    TryExtractCaretFromManagedAutomationElement(focused, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryExtractCaretFromManagedAutomationElement(
            AutomationElement element,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx,
            out string strategy)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;
            strategy = string.Empty;

            if (element == null)
            {
                return false;
            }

            try
            {
                bool hasTextPattern = false;

                // A. Managed TextPattern selection / insertion caret with full GetCaretPosEx expansion
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj) &&
                    patternObj is TextPattern textPattern)
                {
                    hasTextPattern = true;
                    TextPatternRange[] selections = textPattern.GetSelection();
                    if (selections != null && selections.Length > 0 && selections[0] != null)
                    {
                        TextPatternRange caretRange = selections[0];

                        // 1) Direct caret / selection bounding rectangle
                        Rect[] rects = caretRange.GetBoundingRectangles();
                        if (rects != null && rects.Length > 0 &&
                            TryExtractCaretFromRect(rects[0], useRightEdge: false, ref fgRect, out xPx, out yPx, out heightPx))
                        {
                            strategy = "ManagedUia_Direct";
                            return true;
                        }

                        // 2) ExpandToEnclosingUnit(TextUnit.Character) -> Windows Terminal & UWP / WinUI cursor cell
                        try
                        {
                            TextPatternRange charRange = caretRange.Clone();
                            charRange.ExpandToEnclosingUnit(TextUnit.Character);
                            Rect[] charRects = charRange.GetBoundingRectangles();
                            if (charRects != null && charRects.Length > 0 &&
                                TryExtractCaretFromRect(charRects[0], useRightEdge: false, ref fgRect, out xPx, out yPx, out heightPx))
                            {
                                strategy = "ManagedUia_ExpandChar";
                                return true;
                            }
                        }
                        catch
                        {
                        }

                        // 3) Previous character (location - 1 -> right edge)
                        try
                        {
                            TextPatternRange prevRange = caretRange.Clone();
                            if (prevRange.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) != 0)
                            {
                                Rect[] prevRects = prevRange.GetBoundingRectangles();
                                if (prevRects != null && prevRects.Length > 0 &&
                                    TryExtractCaretFromRect(prevRects[prevRects.Length - 1], useRightEdge: true, ref fgRect, out xPx, out yPx, out heightPx))
                                {
                                    strategy = "ManagedUia_PrevChar";
                                    return true;
                                }
                            }
                        }
                        catch
                        {
                        }

                        // 4) Next character (location + 1 -> left edge)
                        try
                        {
                            TextPatternRange nextRange = caretRange.Clone();
                            if (nextRange.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) != 0)
                            {
                                Rect[] nextRects = nextRange.GetBoundingRectangles();
                                if (nextRects != null && nextRects.Length > 0 &&
                                    TryExtractCaretFromRect(nextRects[0], useRightEdge: false, ref fgRect, out xPx, out yPx, out heightPx))
                                {
                                    strategy = "ManagedUia_NextChar";
                                    return true;
                                }
                            }
                        }
                        catch
                        {
                        }

                        // 5) ExpandToEnclosingUnit(TextUnit.Line) -> Right edge (end of terminal line)
                        try
                        {
                            TextPatternRange lineRange = caretRange.Clone();
                            lineRange.ExpandToEnclosingUnit(TextUnit.Line);
                            Rect[] lineRects = lineRange.GetBoundingRectangles();
                            if (lineRects != null && lineRects.Length > 0 &&
                                TryExtractCaretFromRect(lineRects[lineRects.Length - 1], useRightEdge: true, ref fgRect, out xPx, out yPx, out heightPx))
                            {
                                strategy = "ManagedUia_ExpandLine";
                                return true;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                // B. Element BoundingRectangle:
                //    - Handles Kero / VS Code Monaco (.inputarea) and xterm.js (.xterm-helper-textarea) floating 0..1px IME <textarea>
                //    - Handles standard single-line / multi-line input box container fallback (InputSourcePro CursorRectInfo.container)
                ControlType ct = element.Current.ControlType;
                bool isEditableInput = hasTextPattern ||
                                       ct == ControlType.Edit ||
                                       ct == ControlType.Document ||
                                       element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
                if (isEditableInput)
                {
                    Rect elemRect = element.Current.BoundingRectangle;
                    if (TryExtractCaretFromElementBounds(elemRect, ref fgRect, out xPx, out yPx, out heightPx, out strategy))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryExtractCaretFromElementBounds(
            Rect elemRect,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx,
            out string strategy)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;
            strategy = string.Empty;

            if (elemRect.IsEmpty || double.IsNaN(elemRect.Left) || double.IsInfinity(elemRect.Left))
            {
                return false;
            }

            // 1) Floating IME proxy <textarea> (Monaco .inputarea & xterm.js .xterm-helper-textarea in Kero / VS Code):
            //    Positioned at the exact cursor (left, top) with tiny width (0..48px) and line height (0..64px).
            if (elemRect.Width >= 0 && elemRect.Width <= 48 &&
                elemRect.Height >= 0 && elemRect.Height <= 64)
            {
                int cx = (int)Math.Round(elemRect.Left);
                int cy = (int)Math.Round(elemRect.Top);
                int ch = elemRect.Height >= 8 ? (int)Math.Round(elemRect.Height) : 20;
                if ((cx != 0 || cy != 0) && IsReasonableCaretPoint(cx, cy, ref fgRect))
                {
                    xPx = cx;
                    yPx = cy;
                    heightPx = ch;
                    strategy = "FloatingImeTextarea";
                    return true;
                }
            }

            // 2) InputSourcePro container fallback (CursorRectInfo.container):
            //    If focused element is an input box (e.g., search bar, address bar, or chat prompt box) with height <= 360px,
            //    anchor near its top-left so the indicator sits cleanly 6 DIP above the input box.
            double fgW = Math.Max(1.0, fgRect.Right - fgRect.Left);
            double fgH = Math.Max(1.0, fgRect.Bottom - fgRect.Top);
            if (elemRect.Width > 48 &&
                elemRect.Height >= 14 &&
                elemRect.Height <= 360 &&
                elemRect.Height < fgH * 0.42 &&
                (elemRect.Width * elemRect.Height) / (fgW * fgH) < 0.80)
            {
                int cx = (int)Math.Round(elemRect.Left + 12.0);
                int cy = (int)Math.Round(elemRect.Top + 4.0);
                if (IsReasonableCaretPoint(cx, cy, ref fgRect))
                {
                    xPx = cx;
                    yPx = cy;
                    heightPx = 20;
                    strategy = "InputContainer";
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractCaretFromRect(
            Rect rect,
            bool useRightEdge,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;

            if (rect.IsEmpty || double.IsNaN(rect.Left) || double.IsInfinity(rect.Left))
            {
                return false;
            }

            int cx = (int)Math.Round(useRightEdge ? rect.Right : rect.Left);
            int cy = (int)Math.Round(rect.Top);
            int ch = (int)Math.Round(rect.Height);

            if ((cx == 0 && cy == 0) || ch < 0 || ch > 160 || !IsReasonableCaretPoint(cx, cy, ref fgRect))
            {
                return false;
            }

            xPx = cx;
            yPx = cy;
            heightPx = ch > 4 ? ch : 20;
            return true;
        }

        private static bool TryGetMsaaCaretRect(
            IntPtr hwnd,
            ref Win32Rect fgRect,
            out int xPx,
            out int yPx,
            out int heightPx)
        {
            xPx = 0;
            yPx = 0;
            heightPx = 20;

            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Guid iid = IidIAccessible;
                // GetCaretPosEx.ahk pattern: query raw IAccessible pointer for OBJID_CARET (0xFFFFFFF8)
                // and invoke vtable slot 22 (accLocation) directly so it works on both STA and MTA threads!
                if (AccessibleObjectFromWindowPtr(hwnd, ObjIdCaret, ref iid, out IntPtr accCaret) == 0 &&
                    accCaret != IntPtr.Zero)
                {
                    IntPtr varChild = Marshal.AllocHGlobal(24);
                    try
                    {
                        for (int i = 0; i < 24; i++) Marshal.WriteByte(varChild, i, 0);
                        Marshal.WriteInt16(varChild, 0, 3); // VT_I4 = 3, lVal = 0 (CHILDID_SELF)

                        ComAccLocationDelegate accLoc = GetVtableDelegate<ComAccLocationDelegate>(accCaret, 22);
                        int hr = accLoc(accCaret, out int cx, out int cy, out int cw, out int ch, varChild);
                        if (hr == 0 && (cx != 0 || cy != 0) && ch >= 0 && ch < 160 && IsReasonableCaretPoint(cx, cy, ref fgRect))
                        {
                            xPx = cx;
                            yPx = cy;
                            heightPx = ch > 4 ? ch : 20;
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(varChild);
                        Marshal.Release(accCaret);
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool IsReasonableCaretPoint(int x, int y, ref Win32Rect fgRect)
        {
            if (x < fgRect.Left - 8 || x > fgRect.Right + 8 ||
                y < fgRect.Top - 8 || y > fgRect.Bottom + 8)
            {
                return false;
            }

            // Reject fake (0,0) client origins that sit at the exact top-left window border
            if (Math.Abs(x - fgRect.Left) <= 2 && Math.Abs(y - fgRect.Top) <= 2)
            {
                return false;
            }

            return true;
        }

        private static IntPtr ResolveActiveMonitor()
        {
            IntPtr hMon = IntPtr.Zero;
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                hMon = MonitorFromWindow(fg, MonitorDefaultToNearest);
            }

            if (hMon == IntPtr.Zero)
            {
                if (GetCursorPos(out Win32Point pt))
                {
                    hMon = MonitorFromPoint(pt, MonitorDefaultToNearest);
                }
            }

            return hMon;
        }

        /// <summary>
        /// Inspects the actual live taskbar window rectangle on this monitor so Windhawk's
        /// taskbar auto-hide mod (or native Windows auto-hide taskbar) is respected dynamically:
        /// - When the taskbar is slid off-screen (hidden), returns 0 so the OSD sits 12 DIP from the physical bottom.
        /// - When the taskbar is visible on-screen, returns the visible taskbar overlap height.
        /// </summary>
        public static double GetVisibleBottomTaskbarInsetDip(IntPtr targetMonitor, ref Win32MonitorInfo mi, double dpiY)
        {
            if (dpiY <= 0) dpiY = 1.0;

            double maxBottomInsetPx = 0.0;
            maxBottomInsetPx = Math.Max(maxBottomInsetPx, MeasureTaskbarClassBottomInsetPx("Shell_TrayWnd", targetMonitor, ref mi));
            maxBottomInsetPx = Math.Max(maxBottomInsetPx, MeasureTaskbarClassBottomInsetPx("Shell_SecondaryTrayWnd", targetMonitor, ref mi));

            if (maxBottomInsetPx > 0.0)
            {
                return maxBottomInsetPx / dpiY;
            }

            int workAreaBottomDiffPx = mi.rcMonitor.Bottom - mi.rcWork.Bottom;
            if (workAreaBottomDiffPx > 4 && !AnyTaskbarExistsOnMonitor(targetMonitor))
            {
                return workAreaBottomDiffPx / dpiY;
            }

            return 0.0;
        }

        private static bool AnyTaskbarExistsOnMonitor(IntPtr targetMonitor)
        {
            return ClassHasWindowOnMonitor("Shell_TrayWnd", targetMonitor) ||
                   ClassHasWindowOnMonitor("Shell_SecondaryTrayWnd", targetMonitor);
        }

        private static bool ClassHasWindowOnMonitor(string className, IntPtr targetMonitor)
        {
            IntPtr hwnd = IntPtr.Zero;
            while (true)
            {
                hwnd = FindWindowEx(IntPtr.Zero, hwnd, className, null);
                if (hwnd == IntPtr.Zero)
                {
                    break;
                }
                if (MonitorFromWindow(hwnd, MonitorDefaultToNearest) == targetMonitor)
                {
                    return true;
                }
            }
            return false;
        }

        private static double MeasureTaskbarClassBottomInsetPx(string className, IntPtr targetMonitor, ref Win32MonitorInfo mi)
        {
            double bestInsetPx = 0.0;
            IntPtr hwnd = IntPtr.Zero;
            int monWidth = mi.rcMonitor.Right - mi.rcMonitor.Left;
            int monHeight = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

            while (true)
            {
                hwnd = FindWindowEx(IntPtr.Zero, hwnd, className, null);
                if (hwnd == IntPtr.Zero)
                {
                    break;
                }

                if (!IsWindowVisible(hwnd))
                {
                    continue;
                }

                if (MonitorFromWindow(hwnd, MonitorDefaultToNearest) != targetMonitor)
                {
                    continue;
                }

                if (!GetWindowRect(hwnd, out Win32Rect tbRect))
                {
                    continue;
                }

                int tbWidth = tbRect.Right - tbRect.Left;
                int tbHeight = tbRect.Bottom - tbRect.Top;
                if (tbWidth < monWidth / 3 || tbHeight <= 0 || tbHeight > monHeight / 3)
                {
                    continue;
                }

                int interTop = Math.Max(tbRect.Top, mi.rcMonitor.Top);
                int interBottom = Math.Min(tbRect.Bottom, mi.rcMonitor.Bottom);
                int visibleOverlapHeight = interBottom - interTop;

                int distFromBottom = Math.Abs(mi.rcMonitor.Bottom - interBottom);
                if (visibleOverlapHeight > 6 && distFromBottom <= 16)
                {
                    double insetFromMonitorBottom = mi.rcMonitor.Bottom - interTop;
                    if (insetFromMonitorBottom > bestInsetPx)
                    {
                        bestInsetPx = insetFromMonitorBottom;
                    }
                }
            }

            return bestInsetPx;
        }
    }
}
