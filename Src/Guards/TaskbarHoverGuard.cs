using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FluentOsdPro
{
    /// <summary>
    /// Universal Frameless/Maximized Taskbar Hover Wakeup Guard:
    /// - For local apps (ChatGPT, WeChat, Feishu, Electron/CEF/UWP): prevents borderless/frameless
    ///   maximized windows from triggering Windows Shell RudeApp/fullscreen suppression of auto-hidden taskbars.
    /// - For remote desktop / VDI apps (UU GameViewer, ZTE VDI uSmartView, mstsc): suppresses local taskbar
    ///   bottom-edge hover popup while fullscreen in the foreground so the remote taskbar at the bottom of
    ///   the remote screen can be clicked without interference, and immediately restores local taskbar wakeup
    ///   when switching to any local window/desktop or exiting fullscreen.
    /// </summary>
    internal static partial class Program
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out Win32Rect pvAttribute, int cbAttribute);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Win32Point pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref Win32MonitorInfo lpmi);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Win32Point lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        private const uint ABM_GETTASKBARPOS = 0x00000005;
        private const uint ABM_GETSTATE = 0x00000004;
        private const int ABE_LEFT = 0;
        private const int ABE_TOP = 1;
        private const int ABE_RIGHT = 2;
        private const int ABE_BOTTOM = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public Win32Rect rc;
            public IntPtr lParam;
        }

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

        [ComImport]
        [Guid("602D4995-B13A-429B-A66E-1935E44F4317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList2
        {
            [PreserveSig] int HrInit();
            [PreserveSig] int AddTab(IntPtr hwnd);
            [PreserveSig] int DeleteTab(IntPtr hwnd);
            [PreserveSig] int ActivateTab(IntPtr hwnd);
            [PreserveSig] int SetActiveAlt(IntPtr hwnd);
            [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        }

        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class TaskbarListComObject
        {
        }

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOSENDCHANGING = 0x0400;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private static readonly IntPtr NonRudePropValue = new IntPtr(1);
        private static ITaskbarList2 _taskbarList2;
        private static IntPtr _lastCheckedTaskbarWakeupHwnd = IntPtr.Zero;
        private static bool _lastCheckedTaskbarWakeupFullOrMax;
        private static bool _lastCheckedTaskbarRemoteSuppressed;

        private static bool IsRemoteDesktopOrVdiWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (IsWindowTargetProcess(hwnd))
            {
                return true;
            }

            string procName = _lastCachedProcName;
            if (!string.IsNullOrEmpty(procName))
            {
                if (string.Equals(procName, "mstsc", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(procName, "msrdc", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            StringBuilder sb = new StringBuilder(64);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0)
            {
                string cls = sb.ToString();
                if (string.Equals(cls, "TscShellContainerClass", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "IHWindowClass", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void MarkWindowFullscreen(IntPtr hwnd, bool isFullscreen)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (_taskbarList2 == null)
                {
                    ITaskbarList2 list = (ITaskbarList2)new TaskbarListComObject();
                    if (list.HrInit() == 0)
                    {
                        _taskbarList2 = list;
                    }
                }
                if (_taskbarList2 != null)
                {
                    _taskbarList2.MarkFullscreenWindow(hwnd, isFullscreen);
                }
            }
            catch
            {
            }
        }

        private static void MarkWindowNotFullscreen(IntPtr hwnd)
        {
            MarkWindowFullscreen(hwnd, false);
        }

        private static void SuppressTaskbarOnMonitor(IntPtr targetMonitor, bool forceSuppress)
        {
            if (targetMonitor == IntPtr.Zero) return;
            try
            {
                IntPtr primaryTray = FindWindow("Shell_TrayWnd", null);
                if (primaryTray != IntPtr.Zero && IsWindowVisible(primaryTray))
                {
                    IntPtr trayMon = MonitorFromWindow(primaryTray, MONITOR_DEFAULTTONEAREST);
                    if (trayMon == targetMonitor)
                    {
                        if (forceSuppress || (GetWindowLong(primaryTray, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
                        {
                            SetWindowPos(primaryTray, HWND_BOTTOM, 0, 0, 0, 0,
                                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
                        }
                    }
                }

                IntPtr secondaryTray = IntPtr.Zero;
                while ((secondaryTray = FindWindowEx(IntPtr.Zero, secondaryTray, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                {
                    if (IsWindowVisible(secondaryTray))
                    {
                        IntPtr trayMon = MonitorFromWindow(secondaryTray, MONITOR_DEFAULTTONEAREST);
                        if (trayMon == targetMonitor)
                        {
                            if (forceSuppress || (GetWindowLong(secondaryTray, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
                            {
                                SetWindowPos(secondaryTray, HWND_BOTTOM, 0, 0, 0, 0,
                                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void EnsureTaskbarsTopmost(bool forceReassert)
        {
            try
            {
                IntPtr primaryTray = FindWindow("Shell_TrayWnd", null);
                if (primaryTray != IntPtr.Zero && IsWindowVisible(primaryTray))
                {
                    if (forceReassert || (GetWindowLong(primaryTray, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
                    {
                        SetWindowPos(primaryTray, HWND_TOPMOST, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
                    }
                }

                IntPtr secondaryTray = IntPtr.Zero;
                while ((secondaryTray = FindWindowEx(IntPtr.Zero, secondaryTray, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                {
                    if (IsWindowVisible(secondaryTray))
                    {
                        if (forceReassert || (GetWindowLong(secondaryTray, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
                        {
                            SetWindowPos(secondaryTray, HWND_TOPMOST, 0, 0, 0, 0,
                                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsWindowMaximizedOrCoveringMonitor(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                return false;
            }

            if (IsZoomed(hwnd))
            {
                return true;
            }

            IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMon == IntPtr.Zero)
            {
                return false;
            }

            Win32MonitorInfo mi = new Win32MonitorInfo { cbSize = Marshal.SizeOf(typeof(Win32MonitorInfo)) };
            if (!GetMonitorInfo(hMon, ref mi))
            {
                return false;
            }

            Win32Rect winRect;
            if (!GetWindowRect(hwnd, out winRect))
            {
                return false;
            }

            if (winRect.Left <= mi.rcMonitor.Left + 2 &&
                winRect.Top <= mi.rcMonitor.Top + 2 &&
                winRect.Right >= mi.rcMonitor.Right - 2 &&
                winRect.Bottom >= mi.rcMonitor.Bottom - 2)
            {
                return true;
            }

            Win32Rect dwmRect;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out dwmRect, Marshal.SizeOf(typeof(Win32Rect))) == 0)
            {
                if (dwmRect.Left <= mi.rcMonitor.Left + 2 &&
                    dwmRect.Top <= mi.rcMonitor.Top + 2 &&
                    dwmRect.Right >= mi.rcMonitor.Right - 2 &&
                    dwmRect.Bottom >= mi.rcMonitor.Bottom - 2)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Detects the docked edge (ABE_LEFT=0, ABE_TOP=1, ABE_RIGHT=2, ABE_BOTTOM=3) and screen rectangle
        /// of the taskbar on the specified monitor (supports Windows 11 4-edge taskbars / Windhawk vertical taskbars).
        /// </summary>
        private static int GetTaskbarEdgeAndRect(IntPtr hMonitor, out Win32Rect trayRect)
        {
            trayRect = new Win32Rect();
            try
            {
                IntPtr primaryTray = FindWindow("Shell_TrayWnd", null);
                IntPtr targetTray = primaryTray;

                if (hMonitor != IntPtr.Zero && primaryTray != IntPtr.Zero)
                {
                    IntPtr primaryMon = MonitorFromWindow(primaryTray, MONITOR_DEFAULTTONEAREST);
                    if (primaryMon != hMonitor)
                    {
                        IntPtr sec = IntPtr.Zero;
                        while ((sec = FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                        {
                            if (MonitorFromWindow(sec, MONITOR_DEFAULTTONEAREST) == hMonitor)
                            {
                                targetTray = sec;
                                break;
                            }
                        }
                    }
                }

                if (targetTray != IntPtr.Zero && GetWindowRect(targetTray, out trayRect))
                {
                    IntPtr mon = hMonitor != IntPtr.Zero ? hMonitor : MonitorFromWindow(targetTray, MONITOR_DEFAULTTONEAREST);
                    if (mon != IntPtr.Zero)
                    {
                        Win32MonitorInfo mi = new Win32MonitorInfo { cbSize = Marshal.SizeOf(typeof(Win32MonitorInfo)) };
                        if (GetMonitorInfo(mon, ref mi))
                        {
                            int trayW = Math.Max(1, trayRect.Right - trayRect.Left);
                            int trayH = Math.Max(1, trayRect.Bottom - trayRect.Top);
                            int monMidX = (mi.rcMonitor.Left + mi.rcMonitor.Right) / 2;
                            int monMidY = (mi.rcMonitor.Top + mi.rcMonitor.Bottom) / 2;
                            int trayMidX = (trayRect.Left + trayRect.Right) / 2;
                            int trayMidY = (trayRect.Top + trayRect.Bottom) / 2;

                            // Vertical taskbar (Left or Right edge)
                            if (trayH > trayW)
                            {
                                return trayMidX >= monMidX ? ABE_RIGHT : ABE_LEFT;
                            }
                            // Horizontal taskbar (Top or Bottom edge)
                            return trayMidY <= monMidY ? ABE_TOP : ABE_BOTTOM;
                        }
                    }
                }

                APPBARDATA abd = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)) };
                if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
                {
                    trayRect = abd.rc;
                    if (abd.uEdge <= ABE_BOTTOM)
                    {
                        return (int)abd.uEdge;
                    }
                }
            }
            catch
            {
            }

            return ABE_BOTTOM;
        }

        private static bool IsCursorAtTaskbarMonitorEdge(Win32Point pt, IntPtr hMon, Win32MonitorInfo mi)
        {
            Win32Rect trayRect;
            int edge = GetTaskbarEdgeAndRect(hMon, out trayRect);
            switch (edge)
            {
                case ABE_LEFT:
                    return pt.X >= mi.rcMonitor.Left && pt.X <= mi.rcMonitor.Left + 2;
                case ABE_RIGHT:
                    return pt.X >= mi.rcMonitor.Right - 2 && pt.X <= mi.rcMonitor.Right;
                case ABE_TOP:
                    return pt.Y >= mi.rcMonitor.Top && pt.Y <= mi.rcMonitor.Top + 2;
                default:
                    return pt.Y >= mi.rcMonitor.Bottom - 2 && pt.Y <= mi.rcMonitor.Bottom;
            }
        }

        private static void EnsureTaskbarHoverWakeupForWindow(IntPtr hwnd, string reason)
        {
            // Clean up any legacy NonRudeHWND property that was previously attached to windows,
            // and never manipulate Shell_TrayWnd Z-order or ITaskbarList2::MarkFullscreenWindow.
            // Touching Shell_TrayWnd or NonRudeHWND causes Windows 11's auto-hide taskbar
            // (especially 4-edge / right-side taskbars) to pop out and stay stuck visible
            // when switching virtual desktops via Win+Ctrl+Left/Right.
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                return;
            }

            try
            {
                if (GetProp(hwnd, "NonRudeHWND") != IntPtr.Zero)
                {
                    RemoveProp(hwnd, "NonRudeHWND");
                }

                IntPtr coreWnd = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (coreWnd != IntPtr.Zero && GetProp(coreWnd, "NonRudeHWND") != IntPtr.Zero)
                {
                    RemoveProp(coreWnd, "NonRudeHWND");
                }
            }
            catch
            {
            }
        }
    }
}
