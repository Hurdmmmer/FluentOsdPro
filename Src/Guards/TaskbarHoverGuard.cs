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

        private static void EnsureTaskbarHoverWakeupForWindow(IntPtr hwnd, string reason)
        {
            if (_inDesktopSwitchSession)
            {
                return;
            }

            if (!IsValidExternalWindow(hwnd) || IsIconic(hwnd) || IsWindowCloaked(hwnd))
            {
                if (_lastCheckedTaskbarRemoteSuppressed)
                {
                    _lastCheckedTaskbarRemoteSuppressed = false;
                    _lastCheckedTaskbarWakeupHwnd = IntPtr.Zero;
                    _lastCheckedTaskbarWakeupFullOrMax = false;
                    EnsureTaskbarsTopmost(true);
                    LogInfo("Restored local taskbar topmost after leaving fullscreen remote/VDI window (Reason=" + reason + ").");
                }
                else
                {
                    EnsureTaskbarsTopmost(false);
                }
                return;
            }

            try
            {
                int style = GetWindowLong(hwnd, GWL_STYLE);
                if ((style & WS_CHILD) != 0) return;

                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((exStyle & (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)) != 0) return;

                bool isRemote = IsRemoteDesktopOrVdiWindow(hwnd);
                bool isFullOrMax = IsWindowMaximizedOrCoveringMonitor(hwnd);

                if (isRemote)
                {
                    bool propRemoved = false;
                    if (GetProp(hwnd, "NonRudeHWND") != IntPtr.Zero)
                    {
                        RemoveProp(hwnd, "NonRudeHWND");
                        propRemoved = true;
                    }

                    IntPtr remoteCoreWnd = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                    if (remoteCoreWnd != IntPtr.Zero && GetProp(remoteCoreWnd, "NonRudeHWND") != IntPtr.Zero)
                    {
                        RemoveProp(remoteCoreWnd, "NonRudeHWND");
                        propRemoved = true;
                    }

                    bool remoteStateChanged = (hwnd != _lastCheckedTaskbarWakeupHwnd) ||
                                              (isFullOrMax != _lastCheckedTaskbarWakeupFullOrMax) ||
                                              (_lastCheckedTaskbarRemoteSuppressed != isFullOrMax) ||
                                              propRemoved;

                    _lastCheckedTaskbarWakeupHwnd = hwnd;
                    _lastCheckedTaskbarWakeupFullOrMax = isFullOrMax;
                    _lastCheckedTaskbarRemoteSuppressed = isFullOrMax;

                    if (isFullOrMax)
                    {
                        IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                        if (remoteStateChanged)
                        {
                            MarkWindowFullscreen(hwnd, true);
                            if (propRemoved)
                            {
                                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOSENDCHANGING);
                            }
                            SuppressTaskbarOnMonitor(hMon, true);
                            LogInfo("Suppressed local taskbar hover wakeup for fullscreen remote/VDI window (HWND=" + hwnd + ", Reason=" + reason + ").");
                        }
                        else
                        {
                            SuppressTaskbarOnMonitor(hMon, false);
                        }
                    }
                    else
                    {
                        if (remoteStateChanged)
                        {
                            MarkWindowFullscreen(hwnd, false);
                            EnsureTaskbarsTopmost(true);
                            LogInfo("Restored local taskbar wakeup for windowed remote/VDI window (HWND=" + hwnd + ", Reason=" + reason + ").");
                        }
                        else
                        {
                            EnsureTaskbarsTopmost(false);
                        }
                    }
                    return;
                }

                bool propAdded = false;
                if (GetProp(hwnd, "NonRudeHWND") == IntPtr.Zero)
                {
                    SetProp(hwnd, "NonRudeHWND", NonRudePropValue);
                    propAdded = true;
                }

                IntPtr coreWnd = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (coreWnd != IntPtr.Zero && GetProp(coreWnd, "NonRudeHWND") == IntPtr.Zero)
                {
                    SetProp(coreWnd, "NonRudeHWND", NonRudePropValue);
                    propAdded = true;
                }

                bool wasRemoteSuppressed = _lastCheckedTaskbarRemoteSuppressed;
                bool stateChanged = (hwnd != _lastCheckedTaskbarWakeupHwnd) ||
                                    (isFullOrMax != _lastCheckedTaskbarWakeupFullOrMax) ||
                                    wasRemoteSuppressed ||
                                    propAdded;

                _lastCheckedTaskbarWakeupHwnd = hwnd;
                _lastCheckedTaskbarWakeupFullOrMax = isFullOrMax;
                _lastCheckedTaskbarRemoteSuppressed = false;

                if (isFullOrMax && stateChanged)
                {
                    MarkWindowNotFullscreen(hwnd);
                    if (coreWnd != IntPtr.Zero)
                    {
                        MarkWindowNotFullscreen(coreWnd);
                    }
                    if (propAdded)
                    {
                        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOSENDCHANGING);
                    }
                    EnsureTaskbarsTopmost(true);
                    LogInfo("Applied NonRudeHWND & taskbar topmost guard for fullscreen/maximized window (HWND=" + hwnd + ", Reason=" + reason + ").");
                }
                else
                {
                    EnsureTaskbarsTopmost(wasRemoteSuppressed);
                }

                if (isFullOrMax)
                {
                    Win32Point pt;
                    if (GetCursorPos(out pt))
                    {
                        IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                        if (hMon != IntPtr.Zero)
                        {
                            Win32MonitorInfo mi = new Win32MonitorInfo { cbSize = Marshal.SizeOf(typeof(Win32MonitorInfo)) };
                            if (GetMonitorInfo(hMon, ref mi) && pt.Y >= mi.rcMonitor.Bottom - 2 && pt.Y <= mi.rcMonitor.Bottom)
                            {
                                EnsureTaskbarsTopmost(true);
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}
