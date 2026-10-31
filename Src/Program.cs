using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FluentOsdPro
{
    internal static partial class Program
    {
        private const string MutexName = @"Local\FluentOsdPro_SingleInstance_Mutex";

        // Supported remote desktop / VDI target processes (UU Remote + ZTE VDI uSmartView suite)
        private static readonly string[] TargetProcessNames = new[]
        {
            "GameViewer",
            "uSmartView_VDI_Client",
            "uSmartView",
            "uSmartView_vdi_mc",
            "uSmartView_VStart",
            "VirtualAppVDI"
        };

        // Primary remote desktop session viewers to heal and auto-focus across Virtual Desktops
        private static readonly string[] SessionTargetProcessNames = new[]
        {
            "GameViewer",
            "uSmartView_VDI_Client",
            "VirtualAppVDI"
        };

        private const int WH_KEYBOARD_LL = 13;
        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_ACTIVATEAPP = 0x001C;
        private const uint WM_NCACTIVATE = 0x0086;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int DWMWA_CLOAKED = 14;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_APPCOMMAND = 0x0319;

        private const uint LLKHF_INJECTED = 0x00000010;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const uint VK_CONTROL = 0x11;
        private const uint VK_CAPITAL = 0x14;
        private const uint VK_LEFT = 0x25;
        private const uint VK_RIGHT = 0x27;
        private const uint VK_LWIN = 0x5B;
        private const uint VK_RWIN = 0x5C;
        private const uint VK_F24 = 0x87;
        private const uint VK_LCONTROL = 0xA2;
        private const uint VK_RCONTROL = 0xA3;

        private const uint VK_VOLUME_MUTE = 0xAD;
        private const uint VK_VOLUME_DOWN = 0xAE;
        private const uint VK_VOLUME_UP = 0xAF;

        private const int PrimaryJapaneseLangId = 0x11;

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_OBJECT_CLOAKED = 0x8017;
        private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
        private const int OBJID_WINDOW = 0;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const uint WS_POPUP = 0x80000000u;
        private const uint WS_EX_LAYERED = 0x00080000u;
        private const uint WS_EX_TRANSPARENT = 0x00000020u;
        private const uint LWA_ALPHA = 0x00000002u;
        private const int SW_HIDE = 0;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static IntPtr _kbHookId = IntPtr.Zero;
        private static IntPtr _winEventHookFg = IntPtr.Zero;
        private static IntPtr _winEventHookCloak = IntPtr.Zero;
        private static IntPtr _focusParkingHwnd = IntPtr.Zero;
        private static IntPtr _lastForegroundHwnd = IntPtr.Zero;
        private static IntPtr _lastExternalForegroundHwnd = IntPtr.Zero;
        private static IntPtr _volumeSessionTargetHwnd = IntPtr.Zero;
        private static IntPtr _desktopSwitchSourceHwnd = IntPtr.Zero;
        private static IntPtr _lastShellTrayHwnd = IntPtr.Zero;
        private static readonly List<IntPtr> _cloakedTargetHwnds = new List<IntPtr>();

        private static readonly LowLevelKeyboardProc _kbProc = KeyboardHookCallback;
        private static readonly WinEventDelegate _winEventProc = WinEventCallback;

        private static DispatcherTimer _rehookTimer1;
        private static DispatcherTimer _rehookTimer2;
        private static DispatcherTimer _restoreFocusTimer;
        private static DispatcherTimer _desktopSwitchEndTimer;
        private static DispatcherTimer _desktopSwitchSafetyTimer;
        private static DispatcherTimer _virtualDesktopWatchdogTimer;

        private static Forms.NotifyIcon _trayIcon;
        private static Drawing.Icon _appTrayIcon;
        private static bool _lastTrayIconTaskbarDark = true;
        private static TrayMenuPopupWindow _trayMenuWindow;
        private static SettingsWindow _settingsWindow;
        private static VolumeOsdWindow _volumeOsdWindow;
        private static StatusOsdWindow _statusOsdWindow;
        private static InputMethodMonitor _imeMonitor;
        private static Dispatcher _uiDispatcher;

        private static string _logFilePath;
        private static uint _ownPid;
        private static IntPtr _ownModuleHandle = IntPtr.Zero;
        private static uint _lastCachedPid;
        private static bool _lastCachedIsTarget;
        private static string _lastCachedProcName = string.Empty;
        private static bool _isHookActive;
        private static bool _isGuardEnabled = true;

        private static readonly IntPtr OurInjectedExtraInfo = new IntPtr(0x55554C56);
        private static readonly UIntPtr OurInjectedExtraInfoU = new UIntPtr(0x55554C56u);

        private static bool _physWinDown;
        private static bool _physCtrlDown;
        private static bool _hookShiftDown;
        private static bool _hookShiftTapCandidate;
        private static bool _inDesktopSwitchSession;
        private static long _lastDesktopSwitchTick;
        private static int _watchdogRetopCounter;
        private static bool _volumeSessionLogged;
        private static IntPtr _lastLoggedTargetHwnd = IntPtr.Zero;

        public static bool IsInTransientFocusSession =>
            _inDesktopSwitchSession ||
            _volumeSessionTargetHwnd != IntPtr.Zero ||
            (_focusParkingHwnd != IntPtr.Zero && GetForegroundWindow() == _focusParkingHwnd);

        public static bool IsProcessElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool IsGuardEnabled
        {
            get => _isGuardEnabled;
            set
            {
                _isGuardEnabled = value;
                AppSettings.Current.GuardEnabled = value;
                AppSettings.Current.Save();

                if (!_isGuardEnabled)
                {
                    _inDesktopSwitchSession = false;
                    _lastCheckedTaskbarRemoteSuppressed = false;
                    EnsureTaskbarsTopmost(true);
                    EnsureKeyboardHookState("UserPausedGuard");
                }
                else
                {
                    IntPtr checkHwnd = IsValidExternalWindow(_lastExternalForegroundHwnd)
                        ? _lastExternalForegroundHwnd
                        : GetForegroundWindow();
                    EvaluateForegroundState(checkHwnd, "UserResumedGuard");
                }

                if (_trayMenuWindow != null)
                {
                    _trayMenuWindow.RefreshMenuState();
                }
                if (_settingsWindow != null && _settingsWindow.IsVisible)
                {
                    _settingsWindow.SyncFromCurrentSettings();
                }
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(appDir, "Data");
            try
            {
                if (!Directory.Exists(dataDir))
                {
                    Directory.CreateDirectory(dataDir);
                }
            }
            catch
            {
            }

            _logFilePath = Path.Combine(dataDir, "FluentOsdPro.log");
            _ownPid = (uint)Process.GetCurrentProcess().Id;
            _ownModuleHandle = GetModuleHandle(null);
            ThemeService.RefreshThemeCache();

            // CLI utilities
            if (args != null && args.Length > 0)
            {
                string cmd = args[0];
                if (string.Equals(cmd, "--generate-icon", StringComparison.OrdinalIgnoreCase))
                {
                    string outAssets = args.Length > 1 ? args[1] : Path.Combine(appDir, "Assets");
                    TrayIconGenerator.GenerateIconFiles(outAssets);
                    Console.WriteLine("GENERATE_ICONS_OK");
                    return 0;
                }

                if (string.Equals(cmd, "--enable-autostart", StringComparison.OrdinalIgnoreCase))
                {
                    AutoStartManager.SetAutoStartImmediate(true);
                    return 0;
                }

                if (string.Equals(cmd, "--disable-autostart", StringComparison.OrdinalIgnoreCase))
                {
                    AutoStartManager.SetAutoStartImmediate(false);
                    return 0;
                }

                if (string.Equals(cmd, "--migrate-autostart", StringComparison.OrdinalIgnoreCase))
                {
                    AutoStartManager.MigrateAutoStartToTask();
                    return 0;
                }

                if (string.Equals(cmd, "--verify", StringComparison.OrdinalIgnoreCase))
                {
                    LogInfo("FluentOsdPro v2.0 self-verification started.");
                    using (Drawing.Icon darkIcon = TrayIconGenerator.CreateTrayIcon(true))
                    using (Drawing.Icon lightIcon = TrayIconGenerator.CreateTrayIcon(false))
                    {
                        bool fgCheck = IsWindowTargetProcess(GetForegroundWindow());
                        LogInfo("Self-verification passed. DarkIcon=" + (darkIcon.Handle != IntPtr.Zero) +
                                ", LightIcon=" + (lightIcon.Handle != IntPtr.Zero) +
                                ", FgIsTarget=" + fgCheck +
                                ", Elevated=" + IsProcessElevated());
                    }
                    Console.WriteLine("VERIFY_FLUENTOSDPRO_V2_OK");
                    return 0;
                }
            }

            bool isElevated = IsProcessElevated();
            bool skipElevate = false;
            bool showSettingsOnStartup = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--elevated-retry", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(args[i], "--no-elevate", StringComparison.OrdinalIgnoreCase))
                    {
                        skipElevate = true;
                    }
                    else if (string.Equals(args[i], "--show-settings", StringComparison.OrdinalIgnoreCase))
                    {
                        showSettingsOnStartup = true;
                    }
                }
            }

            // Stop any running scheduled task instance first so we can cleanly replace it
            StopExistingInstances();

            if (!isElevated && !skipElevate)
            {
                try
                {
                    string exePath = Assembly.GetExecutingAssembly().Location;
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--elevated-retry --replace",
                        WorkingDirectory = appDir,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(psi);
                    LogInfo("Relaunching FluentOsdPro with Administrator privileges (runas) for UIPI compatibility.");
                    return 0;
                }
                catch (Exception ex)
                {
                    LogError("Self-elevation failed, continuing at current integrity level", ex);
                }
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    StopExistingInstances();
                    try
                    {
                        createdNew = mutex.WaitOne(2500, false);
                    }
                    catch (AbandonedMutexException)
                    {
                        createdNew = true;
                    }
                    catch
                    {
                        createdNew = false;
                    }

                    if (!createdNew)
                    {
                        LogInfo("Another instance is still holding the mutex; exiting.");
                        return 0;
                    }
                }

                try
                {
                    AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    {
                        LogError("Unhandled AppDomain exception", e.ExceptionObject as Exception);
                    };

                    Application app = new Application
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown
                    };
                    app.DispatcherUnhandledException += (s, e) =>
                    {
                        LogError("Unhandled Dispatcher exception", e.Exception);
                        e.Handled = true;
                    };

                    bool isFirstRun = !File.Exists(AppSettings.GetSettingsFilePath());
                    _isGuardEnabled = AppSettings.Current.GuardEnabled;
                    _lastTrayIconTaskbarDark = ThemeService.IsTaskbarDarkTheme();
                    _appTrayIcon = TrayIconGenerator.CreateTrayIcon(_lastTrayIconTaskbarDark);
                    AutoStartManager.RefreshCache();

                    IntPtr initialFg = GetForegroundWindow();
                    if (IsValidExternalWindow(initialFg))
                    {
                        _lastExternalForegroundHwnd = initialFg;
                    }

                    InitializeTimers();
                    InitializeTrayAndPopup();
                    InitializeOsdAndImeFeatures();
                    InstallWinEventHook();
                    SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                    HealTargetWindowsOnStartup();
                    EnsureKeyboardHookState("InitialStartup");
                    EvaluateForegroundState(GetForegroundWindow(), "InitialStartup");

                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        AutoStartManager.MigrateAutoStartToTask();
                    });

                    LogInfo("FluentOsdPro v2.0 started (Caret IME OSD + Fluent Volume & CapsLock Flyout + VDI Guard, Elevated=" + isElevated + ", TrayDarkIcon=" + _lastTrayIconTaskbarDark + ").");
                    if (showSettingsOnStartup || isFirstRun)
                    {
                        AppSettings.Current.Save();
                        OpenSettingsWindow();
                    }
                    app.Run();
                }
                catch (Exception ex)
                {
                    LogError("Fatal exception in Main loop", ex);
                    return 1;
                }
                finally
                {
                    SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                    Cleanup();
                }
            }

            return 0;
        }

        private static void StopExistingInstances()
        {
            try
            {
                List<Process> existingList = new List<Process>();
                existingList.AddRange(Process.GetProcessesByName("FluentOsdPro"));
                existingList.AddRange(Process.GetProcessesByName("FluentOsdPro.old"));
                existingList.AddRange(Process.GetProcessesByName("UULocalVolumeGuard"));
                existingList.AddRange(Process.GetProcessesByName("UULocalVolumeGuard.old"));
                Process[] existing = existingList.ToArray();
                bool hasOther = false;
                for (int i = 0; i < existing.Length; i++)
                {
                    if ((uint)existing[i].Id != _ownPid)
                    {
                        hasOther = true;
                        break;
                    }
                }

                if (!hasOther)
                {
                    for (int i = 0; i < existing.Length; i++) existing[i].Dispose();
                    return;
                }

                // Try stopping via Task Scheduler first in case the previous instance was launched by the logon task
                string[] taskNames = new[] { "FluentOsdPro", "UULocalVolumeGuard" };
                for (int t = 0; t < taskNames.Length; t++)
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "schtasks.exe",
                            Arguments = "/End /TN \"" + taskNames[t] + "\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using (Process sp = Process.Start(psi))
                        {
                            sp?.WaitForExit(1500);
                        }
                    }
                    catch
                    {
                    }
                }

                for (int i = 0; i < existing.Length; i++)
                {
                    using (Process p = existing[i])
                    {
                        if ((uint)p.Id != _ownPid)
                        {
                            try
                            {
                                if (!p.HasExited)
                                {
                                    p.Kill();
                                    p.WaitForExit(2000);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            ThemeService.RefreshThemeCache();

            if (_uiDispatcher != null)
            {
                _uiDispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshTrayIconThemeIfNeeded();
                    _trayMenuWindow?.RefreshMenu();
                    _volumeOsdWindow?.RefreshThemeIfNeeded();
                    _statusOsdWindow?.RefreshThemeIfNeeded();
                }));
            }
        }

        private static void RefreshTrayIconThemeIfNeeded()
        {
            bool isTaskbarDark = ThemeService.IsTaskbarDarkTheme();
            if (isTaskbarDark == _lastTrayIconTaskbarDark && _appTrayIcon != null)
            {
                return;
            }

            _lastTrayIconTaskbarDark = isTaskbarDark;
            try
            {
                Drawing.Icon newIcon = TrayIconGenerator.CreateTrayIcon(isTaskbarDark);
                Drawing.Icon oldIcon = _appTrayIcon;
                _appTrayIcon = newIcon;
                if (_trayIcon != null)
                {
                    _trayIcon.Icon = newIcon;
                }
                oldIcon?.Dispose();
            }
            catch (Exception ex)
            {
                LogError("Failed to refresh tray icon for taskbar theme change", ex);
            }
        }

        private static void InitializeTrayAndPopup()
        {
            _trayMenuWindow = new TrayMenuPopupWindow();
            _lastShellTrayHwnd = FindWindow("Shell_TrayWnd", null);

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _appTrayIcon,
                Visible = true,
                Text = ThemeService.IsSystemChinese()
                    ? "Fluent OSD Pro · 光标输入法浮窗 & 音量守护"
                    : "Fluent OSD Pro · Caret IME & Volume Flyout"
            };

            _trayIcon.MouseUp += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left || e.Button == Forms.MouseButtons.Right)
                {
                    IntPtr fg = GetForegroundWindow();
                    if (IsValidExternalWindow(fg))
                    {
                        _lastExternalForegroundHwnd = fg;
                    }
                    _trayMenuWindow.ShowAtCursor();
                }
            };

            _trayIcon.DoubleClick += (s, e) =>
            {
                _trayMenuWindow?.Hide();
                OpenSettingsWindow();
            };
        }

        public static void OpenSettingsWindow()
        {
            if (_uiDispatcher != null && !_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.BeginInvoke(new Action(OpenSettingsWindow));
                return;
            }

            try
            {
                if (_settingsWindow == null || !_settingsWindow.IsLoaded)
                {
                    _settingsWindow = new SettingsWindow();
                    _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                    _settingsWindow.Show();
                    _settingsWindow.Activate();
                }
                else
                {
                    _settingsWindow.SyncFromCurrentSettings();
                    if (_settingsWindow.WindowState == WindowState.Minimized)
                    {
                        _settingsWindow.WindowState = WindowState.Normal;
                    }
                    _settingsWindow.Show();
                    _settingsWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                LogError("Failed to open SettingsWindow", ex);
            }
        }

        private static void InitializeOsdAndImeFeatures()
        {
            try
            {
                _uiDispatcher = Dispatcher.CurrentDispatcher;

                // Synchronously pre-create and pre-warm both OSD windows off-screen (-32000, -32000)
                // so DWM Acrylic composition is ready before the first show — zero white flash!
                _volumeOsdWindow = new VolumeOsdWindow();
                _volumeOsdWindow.Prewarm();

                _statusOsdWindow = new StatusOsdWindow();
                _statusOsdWindow.Prewarm();

                AudioDeviceSwitcher.Init(delegate(AudioVolumeController.VolumeState state)
                {
                    if (_uiDispatcher != null)
                    {
                        _uiDispatcher.BeginInvoke(new Action(delegate
                        {
                            ShowVolumeOsd(state);
                        }));
                    }
                });

                AudioVolumeController.Init(delegate(AudioVolumeController.VolumeState state)
                {
                    if (_uiDispatcher != null)
                    {
                        _uiDispatcher.BeginInvoke(new Action(delegate
                        {
                            ShowVolumeOsd(state);
                        }));
                    }
                });

                _imeMonitor = new InputMethodMonitor();
                _imeMonitor.InputMethodChanged += (s, e) =>
                {
                    if (e?.Snapshot != null && AppSettings.Current.ImeFlyoutEnabled)
                    {
                        _statusOsdWindow?.ShowInputMethod(e.Snapshot);
                    }
                };
                _imeMonitor.Start();
                OsdPlacementService.WarmUpAllVisibleWindows();

                LogInfo("Dual OSD (VolumeOsdWindow + StatusOsdWindow) and InputMethodMonitor initialized.");
                AudioDeviceSwitcher.LogAvailableDevices();
            }
            catch (Exception ex)
            {
                LogError("Failed to initialize OSD & IME features", ex);
            }
        }

        public static void ShowVolumeOsd(AudioVolumeController.VolumeState state)
        {
            try
            {
                _volumeOsdWindow?.ShowState(state);
            }
            catch (Exception ex)
            {
                LogError("Failed to show VolumeOsdWindow", ex);
            }
        }

        public static bool TryGetVisibleVolumeOsdDipBounds(out Rect bounds)
        {
            if (_volumeOsdWindow != null && _volumeOsdWindow.TryGetVisibleDipBounds(out bounds))
            {
                return true;
            }
            bounds = Rect.Empty;
            return false;
        }

        public static void NotifyVolumeOsdVisibilityChanged()
        {
            _statusOsdWindow?.RepositionIfStackedAboveVolume();
        }

        public static void ShowImePreview(int cycleIndex = 0)
        {
            if (_statusOsdWindow == null)
            {
                return;
            }

            InputMethodSnapshot sample;
            switch (cycleIndex % 3)
            {
                case 1:
                    sample = new InputMethodSnapshot(IntPtr.Zero, 0x0804, InputLanguageFamily.Chinese, InputConversionModeKind.EnglishMode);
                    break;
                case 2:
                    sample = new InputMethodSnapshot(IntPtr.Zero, 0x0411, InputLanguageFamily.Japanese, InputConversionModeKind.Hiragana);
                    break;
                default:
                    sample = new InputMethodSnapshot(IntPtr.Zero, 0x0804, InputLanguageFamily.Chinese, InputConversionModeKind.ChineseMode);
                    break;
            }

            _statusOsdWindow.ShowInputMethod(sample);
        }

        public static void ShowCapsLockPreview(bool isOn)
        {
            _statusOsdWindow?.ShowCapsLock(isOn);
        }

        public static void ShowVolumePreview()
        {
            AudioVolumeController.VolumeState state = AudioVolumeController.GetCurrentState();
            if (state == null)
            {
                state = new AudioVolumeController.VolumeState
                {
                    DeviceName = ThemeService.IsSystemChinese() ? "扬声器 (Realtek Audio)" : "Speakers (Realtek Audio)",
                    Level = 0.56f,
                    Muted = false
                };
            }
            ShowVolumeOsd(state);
        }

        private static void AddCloakedTargetWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            _cloakedTargetHwnds.Remove(hwnd);
            _cloakedTargetHwnds.Insert(0, hwnd);
        }

        private static void RemoveCloakedTargetWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            _cloakedTargetHwnds.Remove(hwnd);
        }

        private static void InitializeTimers()
        {
            _rehookTimer1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _rehookTimer1.Tick += (s, e) =>
            {
                _rehookTimer1.Stop();
                if (_isGuardEnabled && (IsWindowTargetProcess(GetForegroundWindow()) || _inDesktopSwitchSession))
                {
                    ReinstallKeyboardHookToTop("DelayedRehook_180ms");
                }
            };

            _rehookTimer2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            _rehookTimer2.Tick += (s, e) =>
            {
                _rehookTimer2.Stop();
                if (_isGuardEnabled && (IsWindowTargetProcess(GetForegroundWindow()) || _inDesktopSwitchSession))
                {
                    ReinstallKeyboardHookToTop("DelayedRehook_550ms");
                }
            };

            _restoreFocusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _restoreFocusTimer.Tick += (s, e) =>
            {
                _restoreFocusTimer.Stop();
                _volumeSessionLogged = false;
                IntPtr target = _volumeSessionTargetHwnd;
                _volumeSessionTargetHwnd = IntPtr.Zero;

                if (target == IntPtr.Zero || !IsValidExternalWindow(target))
                {
                    target = _lastExternalForegroundHwnd;
                }

                if (target != IntPtr.Zero && IsValidExternalWindow(target))
                {
                    IntPtr currentFg = GetForegroundWindow();
                    if (currentFg != target && (_trayMenuWindow == null || !_trayMenuWindow.IsVisible))
                    {
                        RestoreForegroundWindow(target, "VolumeAdjustEnded");
                    }
                    else if (IsWindowTargetProcess(currentFg))
                    {
                        ReinstallKeyboardHookToTop("VolumeAdjustEnded_KeepTop");
                    }
                }
            };

            _desktopSwitchEndTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _desktopSwitchEndTimer.Tick += (s, e) =>
            {
                _desktopSwitchEndTimer.Stop();
                if (_inDesktopSwitchSession)
                {
                    if (_physWinDown || _physCtrlDown || IsAnyDesktopSwitchModifierPhysicallyDown())
                    {
                        return;
                    }
                    FinalizeDesktopSwitchSession("DesktopSwitchModifiersReleased");
                }
            };

            _desktopSwitchSafetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _desktopSwitchSafetyTimer.Tick += (s, e) =>
            {
                if (!_inDesktopSwitchSession)
                {
                    _desktopSwitchSafetyTimer.Stop();
                    return;
                }
                if (IsAnyDesktopSwitchModifierPhysicallyDown())
                {
                    return;
                }
                _desktopSwitchSafetyTimer.Stop();
                FinalizeDesktopSwitchSession("DesktopSwitchSafetyTimeout");
            };

            _virtualDesktopWatchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _virtualDesktopWatchdogTimer.Tick += (s, e) =>
            {
                IntPtr currentTray = FindWindow("Shell_TrayWnd", null);
                if (currentTray != IntPtr.Zero && currentTray != _lastShellTrayHwnd)
                {
                    _lastShellTrayHwnd = currentTray;
                    if (_trayIcon != null)
                    {
                        try
                        {
                            _trayIcon.Visible = false;
                            _trayIcon.Visible = true;
                        }
                        catch
                        {
                        }
                    }
                }

                bool needHook = _isGuardEnabled || AppSettings.Current.CapsLockFlyoutEnabled;
                if (needHook && (!_isHookActive || _kbHookId == IntPtr.Zero))
                {
                    ReinstallKeyboardHookToTop("Watchdog_EnsureHookActive");
                }

                if (!_isGuardEnabled)
                {
                    return;
                }

                if (_inDesktopSwitchSession || IsAnyDesktopSwitchModifierPhysicallyDown())
                {
                    return;
                }

                if (_cloakedTargetHwnds.Count > 0)
                {
                    for (int i = _cloakedTargetHwnds.Count - 1; i >= 0; i--)
                    {
                        IntPtr candidate = _cloakedTargetHwnds[i];
                        if (candidate == IntPtr.Zero || !IsWindow(candidate) || !IsWindowVisible(candidate))
                        {
                            _cloakedTargetHwnds.RemoveAt(i);
                            continue;
                        }

                        if (!IsWindowCloaked(candidate))
                        {
                            RestoreForegroundWindow(candidate, "ReturnedToTargetDesktop");
                            ReinstallKeyboardHookToTop("ReturnedToTargetDesktop");
                            ScheduleDelayedRehooks();
                            if (GetForegroundWindow() == candidate)
                            {
                                _cloakedTargetHwnds.RemoveAt(i);
                            }
                            return;
                        }
                    }
                }

                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    EnsureTaskbarHoverWakeupForWindow(fg, "Watchdog_Tick");
                }
                if (fg != IntPtr.Zero && IsWindowTargetProcess(fg) && !IsWindowCloaked(fg))
                {
                    if (_volumeSessionTargetHwnd == IntPtr.Zero)
                    {
                        _watchdogRetopCounter++;
                        if (_watchdogRetopCounter >= 2)
                        {
                            _watchdogRetopCounter = 0;
                            ReinstallKeyboardHookToTop("Watchdog_PeriodicKeepTop");
                        }
                    }
                }
                else
                {
                    _watchdogRetopCounter = 0;
                }
            };
            _virtualDesktopWatchdogTimer.Start();
        }

        private static void ScheduleDelayedRehooks()
        {
            if (_rehookTimer1 != null)
            {
                _rehookTimer1.Stop();
                _rehookTimer1.Start();
            }
            if (_rehookTimer2 != null)
            {
                _rehookTimer2.Stop();
                _rehookTimer2.Start();
            }
        }

        private static bool IsWindowCloaked(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
            try
            {
                int cloaked = 0;
                int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
                return hr == 0 && cloaked != 0;
            }
            catch
            {
                return false;
            }
        }

        public static void RestorePreviousActiveApp(string reason)
        {
            IntPtr target = _lastExternalForegroundHwnd;
            if (target == IntPtr.Zero || !IsValidExternalWindow(target))
            {
                return;
            }

            DispatcherTimer delayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            delayTimer.Tick += (s, e) =>
            {
                delayTimer.Stop();
                RestoreForegroundWindow(target, reason);
            };
            delayTimer.Start();
        }

        private static void RestoreForegroundWindow(IntPtr targetHwnd, string reason)
        {
            if (targetHwnd == IntPtr.Zero || !IsWindow(targetHwnd) || !IsWindowVisible(targetHwnd) || IsIconic(targetHwnd))
            {
                return;
            }

            IntPtr currentFg = GetForegroundWindow();
            if (currentFg == targetHwnd)
            {
                EvaluateForegroundState(currentFg, "FocusAlreadyAtTarget:" + reason);
                return;
            }

            try
            {
                uint fgPid;
                uint fgThreadId = currentFg != IntPtr.Zero ? GetWindowThreadProcessId(currentFg, out fgPid) : 0;
                uint targetPid;
                uint targetThreadId = GetWindowThreadProcessId(targetHwnd, out targetPid);
                uint currentThreadId = GetCurrentThreadId();

                bool attachedFg = false;
                bool attachedTarget = false;

                if (fgThreadId != 0 && fgThreadId != currentThreadId)
                {
                    attachedFg = AttachThreadInput(currentThreadId, fgThreadId, true);
                }
                if (targetThreadId != 0 && targetThreadId != currentThreadId && targetThreadId != fgThreadId)
                {
                    attachedTarget = AttachThreadInput(currentThreadId, targetThreadId, true);
                }

                try
                {
                    SetForegroundWindow(targetHwnd);
                    BringWindowToTop(targetHwnd);
                    SwitchToThisWindow(targetHwnd, true);

                    if (GetForegroundWindow() != targetHwnd)
                    {
                        keybd_event((byte)VK_F24, 0, KEYEVENTF_KEYUP, OurInjectedExtraInfoU);
                        SetForegroundWindow(targetHwnd);
                        BringWindowToTop(targetHwnd);
                        SwitchToThisWindow(targetHwnd, true);
                    }
                }
                finally
                {
                    if (attachedTarget)
                    {
                        AttachThreadInput(currentThreadId, targetThreadId, false);
                    }
                    if (attachedFg)
                    {
                        AttachThreadInput(currentThreadId, fgThreadId, false);
                    }
                }

                EvaluateForegroundState(GetForegroundWindow(), "FocusRestored:" + reason);
            }
            catch (Exception ex)
            {
                LogError("Failed to restore foreground window (" + reason + ")", ex);
            }
        }

        private static IntPtr EnsureFocusParkingWindow(IntPtr referenceHwnd)
        {
            int x = 0, y = 0, w = 1920, h = 1080;
            IntPtr hMon = referenceHwnd != IntPtr.Zero
                ? MonitorFromWindow(referenceHwnd, MONITOR_DEFAULTTONEAREST)
                : IntPtr.Zero;
            if (hMon == IntPtr.Zero)
            {
                Win32Point pt = new Win32Point { X = 0, Y = 0 };
                hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            }
            if (hMon != IntPtr.Zero)
            {
                Win32MonitorInfo mi = new Win32MonitorInfo { cbSize = Marshal.SizeOf(typeof(Win32MonitorInfo)) };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    x = mi.rcMonitor.Left;
                    y = mi.rcMonitor.Top;
                    w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                }
            }

            if (_focusParkingHwnd == IntPtr.Zero || !IsWindow(_focusParkingHwnd))
            {
                _focusParkingHwnd = CreateWindowEx(
                    (uint)WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT,
                    "Static",
                    string.Empty,
                    WS_POPUP,
                    x, y, w, h,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    _ownModuleHandle,
                    IntPtr.Zero);

                if (_focusParkingHwnd != IntPtr.Zero)
                {
                    SetLayeredWindowAttributes(_focusParkingHwnd, 0, 0, LWA_ALPHA);
                }
            }

            if (_focusParkingHwnd != IntPtr.Zero)
            {
                SetWindowPos(
                    _focusParkingHwnd,
                    IntPtr.Zero,
                    x, y, w, h,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOSENDCHANGING);
            }

            return _focusParkingHwnd;
        }

        private static void HideFocusParkingWindow()
        {
            if (_focusParkingHwnd != IntPtr.Zero && IsWindow(_focusParkingHwnd) && IsWindowVisible(_focusParkingHwnd))
            {
                ShowWindow(_focusParkingHwnd, SW_HIDE);
            }
        }

        private static IntPtr FindTopmostVisibleWindowOnCurrentDesktop(IntPtr preferredMonitor)
        {
            IntPtr bestOnPreferredMonitor = IntPtr.Zero;
            IntPtr bestAnyMonitor = IntPtr.Zero;

            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (hWnd == IntPtr.Zero || hWnd == _focusParkingHwnd)
                {
                    return true;
                }

                if (!IsValidExternalWindow(hWnd) || IsIconic(hWnd) || IsWindowCloaked(hWnd))
                {
                    return true;
                }

                int style = GetWindowLong(hWnd, GWL_STYLE);
                if ((style & WS_CHILD) != 0)
                {
                    return true;
                }

                int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                if ((exStyle & (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)) != 0)
                {
                    return true;
                }

                Win32Rect rect;
                if (!GetWindowRect(hWnd, out rect))
                {
                    return true;
                }

                if ((rect.Right - rect.Left) < 80 || (rect.Bottom - rect.Top) < 80)
                {
                    return true;
                }

                if (bestAnyMonitor == IntPtr.Zero)
                {
                    bestAnyMonitor = hWnd;
                }

                if (preferredMonitor != IntPtr.Zero)
                {
                    IntPtr hMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
                    if (hMon == preferredMonitor && bestOnPreferredMonitor == IntPtr.Zero)
                    {
                        bestOnPreferredMonitor = hWnd;
                        return false;
                    }
                }
                else
                {
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return bestOnPreferredMonitor != IntPtr.Zero ? bestOnPreferredMonitor : bestAnyMonitor;
        }

        private static IntPtr FocusLocalTrayWindow()
        {
            IntPtr currentFg = GetForegroundWindow();
            IntPtr hPark = EnsureFocusParkingWindow(currentFg);
            if (hPark == IntPtr.Zero)
            {
                // Use desktop shell window (Progman) instead of Shell_TrayWnd so auto-hide taskbars
                // (especially 4-edge / right-side taskbars) are never forced to pop open on virtual desktop switch.
                hPark = GetShellWindow();
            }

            if (hPark != IntPtr.Zero && currentFg != hPark)
            {
                uint fgPid;
                uint fgThreadId = currentFg != IntPtr.Zero ? GetWindowThreadProcessId(currentFg, out fgPid) : 0;
                uint parkPid;
                uint parkThreadId = GetWindowThreadProcessId(hPark, out parkPid);
                uint currentThreadId = GetCurrentThreadId();

                bool attachedFg = false;
                bool attachedPark = false;

                if (fgThreadId != 0 && fgThreadId != currentThreadId)
                {
                    attachedFg = AttachThreadInput(currentThreadId, fgThreadId, true);
                }
                if (parkThreadId != 0 && parkThreadId != currentThreadId && parkThreadId != fgThreadId)
                {
                    attachedPark = AttachThreadInput(currentThreadId, parkThreadId, true);
                }

                try
                {
                    SetForegroundWindow(hPark);
                    SwitchToThisWindow(hPark, true);

                    if (IsWindowTargetProcess(GetForegroundWindow()))
                    {
                        keybd_event((byte)VK_F24, 0, KEYEVENTF_KEYUP, OurInjectedExtraInfoU);
                        SetForegroundWindow(hPark);
                        SwitchToThisWindow(hPark, true);
                    }
                }
                finally
                {
                    if (attachedPark)
                    {
                        AttachThreadInput(currentThreadId, parkThreadId, false);
                    }
                    if (attachedFg)
                    {
                        AttachThreadInput(currentThreadId, fgThreadId, false);
                    }
                }
            }

            return GetForegroundWindow();
        }

        private static bool IsValidExternalWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
            {
                return false;
            }

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0 || pid == _ownPid)
            {
                return false;
            }

            StringBuilder sb = new StringBuilder(64);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0)
            {
                string cls = sb.ToString();
                if (string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "#32768", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static void InstallWinEventHook()
        {
            _winEventHookFg = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            _winEventHookCloak = SetWinEventHook(
                EVENT_OBJECT_CLOAKED,
                EVENT_OBJECT_UNCLOAKED,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }

        private static void WinEventCallback(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime)
        {
            if (eventType == EVENT_OBJECT_CLOAKED)
            {
                if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
                if (IsWindowTargetProcess(hwnd) && IsWindowCloaked(hwnd))
                {
                    AddCloakedTargetWindow(hwnd);
                    IntPtr prefMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                    IntPtr topLocal = FindTopmostVisibleWindowOnCurrentDesktop(prefMon);
                    if (topLocal != IntPtr.Zero)
                    {
                        RestoreForegroundWindow(topLocal, "WinEvent_TargetCloaked_FocusLocal");
                    }
                    HideFocusParkingWindow();
                }
                return;
            }

            if (eventType == EVENT_OBJECT_UNCLOAKED)
            {
                if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
                if ((_cloakedTargetHwnds.Contains(hwnd) || IsWindowTargetProcess(hwnd)) &&
                    IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsWindowCloaked(hwnd))
                {
                    RemoveCloakedTargetWindow(hwnd);
                    RestoreForegroundWindow(hwnd, "WinEvent_TargetUncloaked");
                    HideFocusParkingWindow();
                    ReinstallKeyboardHookToTop("WinEvent_TargetUncloaked");
                    ScheduleDelayedRehooks();
                }
                return;
            }

            IntPtr actualFg = GetForegroundWindow();
            IntPtr effectiveHwnd = actualFg != IntPtr.Zero ? actualFg : hwnd;
            if (effectiveHwnd == IntPtr.Zero)
            {
                return;
            }

            bool isTarget = IsWindowTargetProcess(effectiveHwnd);
            if (isTarget && effectiveHwnd != _lastLoggedTargetHwnd)
            {
                _lastLoggedTargetHwnd = effectiveHwnd;
                LogInfo("Target window active: " + _lastCachedProcName + " (PID=" + _lastCachedPid + ", HWND=" + effectiveHwnd + ")");
            }
            else if (!isTarget && !_inDesktopSwitchSession)
            {
                _lastLoggedTargetHwnd = IntPtr.Zero;
            }

            if (effectiveHwnd == _lastForegroundHwnd && _isHookActive == isTarget && !_inDesktopSwitchSession)
            {
                if (isTarget)
                {
                    ReinstallKeyboardHookToTop("WinEvent_SameTargetRehook");
                    ScheduleDelayedRehooks();
                }
                return;
            }

            if (_lastForegroundHwnd != IntPtr.Zero &&
                _lastForegroundHwnd != effectiveHwnd &&
                IsWindowTargetProcess(_lastForegroundHwnd) &&
                IsWindowCloaked(_lastForegroundHwnd))
            {
                AddCloakedTargetWindow(_lastForegroundHwnd);
            }

            _lastForegroundHwnd = effectiveHwnd;

            if (isTarget)
            {
                RemoveCloakedTargetWindow(effectiveHwnd);
            }

            if (IsValidExternalWindow(effectiveHwnd))
            {
                _lastExternalForegroundHwnd = effectiveHwnd;
            }

            _imeMonitor?.NotifyForegroundChanged(effectiveHwnd);
            EvaluateForegroundState(effectiveHwnd, "WinEvent_ForegroundChanged");
        }

        public static void EnsureKeyboardHookState(string reason)
        {
            bool shouldBeActive = _isGuardEnabled ||
                                  AppSettings.Current.CapsLockFlyoutEnabled ||
                                  AppSettings.Current.ImeFlyoutEnabled;
            if (!shouldBeActive)
            {
                UninstallKeyboardHook(reason);
            }
            else
            {
                ReinstallKeyboardHookToTop(reason);
            }
        }

        private static void EvaluateForegroundState(IntPtr hwnd, string reason)
        {
            if (hwnd != IntPtr.Zero)
            {
                EnsureTaskbarHoverWakeupForWindow(hwnd, reason);
                OsdPlacementService.WarmUpForegroundWindow(hwnd);
            }

            EnsureKeyboardHookState(reason);
            if (_isGuardEnabled && (IsWindowTargetProcess(hwnd) || _inDesktopSwitchSession))
            {
                ScheduleDelayedRehooks();
            }
        }

        private static bool IsTargetProcessName(string procName)
        {
            if (string.IsNullOrEmpty(procName)) return false;

            for (int i = 0; i < TargetProcessNames.Length; i++)
            {
                if (string.Equals(procName, TargetProcessNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return procName.StartsWith("uSmartView", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWindowTargetProcess(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return false;

            if (pid == _lastCachedPid)
            {
                return _lastCachedIsTarget;
            }

            bool match = false;
            string pName = string.Empty;
            try
            {
                using (Process proc = Process.GetProcessById((int)pid))
                {
                    pName = proc.ProcessName ?? string.Empty;
                    match = IsTargetProcessName(pName);
                }
            }
            catch
            {
                match = false;
            }

            _lastCachedPid = pid;
            _lastCachedIsTarget = match;
            _lastCachedProcName = pName;
            return match;
        }

        private static void ReinstallKeyboardHookToTop(string reason)
        {
            IntPtr oldHook = _kbHookId;
            IntPtr newHook = IntPtr.Zero;

            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                IntPtr hMod = GetModuleHandle(curModule.ModuleName);
                newHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
            }

            if (newHook != IntPtr.Zero)
            {
                _kbHookId = newHook;
                if (oldHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(oldHook);
                }

                if (!_isHookActive)
                {
                    _isHookActive = true;
                    LogInfo("Activated WH_KEYBOARD_LL hook. Trigger: " + reason);
                }
            }
            else if (oldHook == IntPtr.Zero)
            {
                _isHookActive = false;
                int err = Marshal.GetLastWin32Error();
                LogError("SetWindowsHookEx(WH_KEYBOARD_LL) failed with Win32 error " + err, null);
            }
        }

        private static void UninstallKeyboardHook(string reason)
        {
            if (_kbHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_kbHookId);
                _kbHookId = IntPtr.Zero;
            }

            _physWinDown = false;
            _physCtrlDown = false;
            _inDesktopSwitchSession = false;
            _desktopSwitchEndTimer?.Stop();
            _desktopSwitchSafetyTimer?.Stop();

            if (_isHookActive)
            {
                _isHookActive = false;
                LogInfo("Uninstalled WH_KEYBOARD_LL hook. Trigger: " + reason);
            }
        }

        private static bool IsKeyPhysicallyDown(uint vk)
        {
            return (GetAsyncKeyState((int)vk) & 0x8000) != 0;
        }

        private static bool IsCapsLockToggledOn()
        {
            return (GetKeyState((int)VK_CAPITAL) & 0x0001) != 0;
        }

        private static bool IsPotentialImeSwitchKey(uint vkCode)
        {
            // 1. Shift (Left/Right/Generic) - primary Chinese/English mode toggle and Alt+Shift / Ctrl+Shift
            if (vkCode == 0x10 || vkCode == 0xA0 || vkCode == 0xA1)
            {
                return true;
            }

            // 2. Space when Ctrl or Win is held (Ctrl+Space or Win+Space)
            if (vkCode == 0x20)
            {
                return IsKeyPhysicallyDown(VK_LCONTROL) ||
                       IsKeyPhysicallyDown(VK_RCONTROL) ||
                       IsKeyPhysicallyDown(VK_CONTROL) ||
                       IsKeyPhysicallyDown(VK_LWIN) ||
                       IsKeyPhysicallyDown(VK_RWIN);
            }

            // 3. Ctrl or Alt released while Shift was involved (Ctrl+Shift / Alt+Shift)
            if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL || vkCode == VK_CONTROL ||
                vkCode == 0xA4 || vkCode == 0xA5 || vkCode == 0x12)
            {
                return IsKeyPhysicallyDown(0x10) || IsKeyPhysicallyDown(0xA0) || IsKeyPhysicallyDown(0xA1);
            }

            // 4. Dedicated IME / Japanese / CapsLock keys
            if (vkCode == VK_CAPITAL ||
                vkCode == 0x15 || // VK_KANA / VK_HANGUL
                vkCode == 0x19 || // VK_KANJI / VK_HANJA
                vkCode == 0x1C || // VK_CONVERT
                vkCode == 0x1D || // VK_NONCONVERT
                vkCode == 0xF3 || // VK_OEM_AUTO
                vkCode == 0xF4)   // VK_OEM_ENLW
            {
                return true;
            }

            return false;
        }

        private static bool IsForegroundJapaneseKeyboardLayout()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                return false;
            }

            uint pid;
            uint threadId = GetWindowThreadProcessId(fg, out pid);
            if (threadId == 0)
            {
                return false;
            }

            IntPtr hkl = GetKeyboardLayout(threadId);
            int langId = unchecked((ushort)(long)hkl);
            return (langId & 0x3FF) == PrimaryJapaneseLangId;
        }

        private static bool IsAnyDesktopSwitchModifierPhysicallyDown()
        {
            return IsKeyPhysicallyDown(VK_LWIN) ||
                   IsKeyPhysicallyDown(VK_RWIN) ||
                   IsKeyPhysicallyDown(VK_LCONTROL) ||
                   IsKeyPhysicallyDown(VK_RCONTROL) ||
                   IsKeyPhysicallyDown(VK_CONTROL);
        }

        private static void HealTargetWindowsOnStartup()
        {
            try
            {
                for (int t = 0; t < SessionTargetProcessNames.Length; t++)
                {
                    string targetName = SessionTargetProcessNames[t];
                    Process[] procs = Process.GetProcessesByName(targetName);
                    for (int i = 0; i < procs.Length; i++)
                    {
                        using (Process proc = procs[i])
                        {
                            IntPtr hwnd = proc.MainWindowHandle;
                            if (hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd))
                            {
                                if (GetProp(hwnd, "NonRudeHWND") != IntPtr.Zero)
                                {
                                    RemoveProp(hwnd, "NonRudeHWND");
                                }
                                IntPtr res;
                                SendMessageTimeout(hwnd, WM_ACTIVATEAPP, (IntPtr)1, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out res);
                                SendMessageTimeout(hwnd, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out res);
                                SendMessageTimeout(hwnd, WM_ACTIVATE, (IntPtr)1, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out res);
                                bool cloaked = IsWindowCloaked(hwnd);
                                if (cloaked)
                                {
                                    AddCloakedTargetWindow(hwnd);
                                }
                                LogInfo("Healed " + targetName + " window activation state on startup (HWND=" + hwnd + ", Cloaked=" + cloaked + ").");
                            }
                        }
                    }
                }

                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (IsValidExternalWindow(hWnd) && !IsIconic(hWnd) && !IsWindowCloaked(hWnd) && !IsRemoteDesktopOrVdiWindow(hWnd))
                    {
                        EnsureTaskbarHoverWakeupForWindow(hWnd, "StartupSweep");
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                LogError("HealTargetWindowsOnStartup failed", ex);
            }
        }

        private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                uint vkCode = (uint)Marshal.ReadInt32(lParam);
                uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                IntPtr extraInfo = Marshal.ReadIntPtr(lParam, 16);

                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                // Observe CapsLock key-up non-intrusively (when CapsLock Flyout is enabled and not in Japanese IME)
                if (vkCode == VK_CAPITAL && isUp && AppSettings.Current.CapsLockFlyoutEnabled)
                {
                    if (!IsForegroundJapaneseKeyboardLayout() && _uiDispatcher != null)
                    {
                        _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                        {
                            bool isOn = IsCapsLockToggledOn();
                            _statusOsdWindow?.ShowCapsLock(isOn);
                        }));
                    }
                }

                // Gate IME OSD notifications on genuine physical IME-related keystrokes so mouse clicks
                // or internal app focus changes never cause false-positive IME OSD popups
                if ((flags & LLKHF_INJECTED) == 0 && (isDown || isUp))
                {
                    bool isShiftVk = vkCode == 0x10 || vkCode == 0xA0 || vkCode == 0xA1;
                    bool wasStandaloneShiftTap = false;
                    bool isInitialShiftDown = false;

                    if (isDown)
                    {
                        if (isShiftVk)
                        {
                            if (!_hookShiftDown)
                            {
                                _hookShiftDown = true;
                                _hookShiftTapCandidate = true;
                                isInitialShiftDown = true;
                            }
                        }
                        else
                        {
                            _hookShiftTapCandidate = false;
                        }
                    }
                    else if (isUp)
                    {
                        if (isShiftVk)
                        {
                            wasStandaloneShiftTap = _hookShiftTapCandidate;
                            _hookShiftDown = false;
                            _hookShiftTapCandidate = false;
                        }
                        else
                        {
                            _hookShiftTapCandidate = false;
                        }
                    }

                    if (AppSettings.Current.ImeFlyoutEnabled && IsPotentialImeSwitchKey(vkCode))
                    {
                        if (isShiftVk)
                        {
                            bool hasCtrlOrAlt = IsKeyPhysicallyDown(VK_LCONTROL) ||
                                                IsKeyPhysicallyDown(VK_RCONTROL) ||
                                                IsKeyPhysicallyDown(VK_CONTROL) ||
                                                IsKeyPhysicallyDown(0xA4) ||
                                                IsKeyPhysicallyDown(0xA5) ||
                                                IsKeyPhysicallyDown(0x12);
                            if (isInitialShiftDown && !hasCtrlOrAlt)
                            {
                                OsdPlacementService.PrefetchCaretAsync();
                                // Capture the pre-Shift baseline before Shift is released so subsequent change detection is 100% accurate
                                _uiDispatcher?.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                                {
                                    _imeMonitor?.SyncBaselineSilently();
                                }));
                            }
                            else if ((isUp && wasStandaloneShiftTap) || hasCtrlOrAlt)
                            {
                                if (isDown)
                                {
                                    OsdPlacementService.PrefetchCaretAsync();
                                }
                                bool triggerQuickCheck = isUp;
                                _uiDispatcher?.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                                {
                                    _imeMonitor?.NotifyUserImeKeyActivity(triggerQuickCheck);
                                }));
                            }
                        }
                        else
                        {
                            if (isDown)
                            {
                                OsdPlacementService.PrefetchCaretAsync();
                            }
                            bool triggerQuickCheck = isUp;
                            _uiDispatcher?.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                            {
                                _imeMonitor?.NotifyUserImeKeyActivity(triggerQuickCheck);
                            }));
                        }
                    }
                }

                if (_isGuardEnabled)
                {
                    // 1. Intercept Volume Keys (VK_VOLUME_UP / DOWN / MUTE) -> Local Audio
                    if (vkCode == VK_VOLUME_UP || vkCode == VK_VOLUME_DOWN || vkCode == VK_VOLUME_MUTE)
                    {
                        // 1a. Ctrl + Volume Up/Down -> cycle default audio output device
                        if (isDown && vkCode != VK_VOLUME_MUTE && !_inDesktopSwitchSession)
                        {
                            bool ctrlHeld = IsKeyPhysicallyDown(VK_LCONTROL) || IsKeyPhysicallyDown(VK_RCONTROL) || IsKeyPhysicallyDown(VK_CONTROL);
                            if (!ctrlHeld)
                            {
                                _physCtrlDown = false;
                            }
                            if (ctrlHeld)
                            {
                                AudioDeviceSwitcher.TriggerSwitch(vkCode == VK_VOLUME_UP);
                                return (IntPtr)1;
                            }
                        }

                        if (isDown)
                        {
                            IntPtr currentFg = GetForegroundWindow();
                            if (_volumeSessionTargetHwnd == IntPtr.Zero)
                            {
                                _volumeSessionTargetHwnd = IsValidExternalWindow(currentFg)
                                    ? currentFg
                                    : _lastExternalForegroundHwnd;
                            }

                            if (!_volumeSessionLogged)
                            {
                                _volumeSessionLogged = true;
                                string keyName = vkCode == VK_VOLUME_UP ? "VolumeUp" : (vkCode == VK_VOLUME_DOWN ? "VolumeDown" : "VolumeMute");
                                string targetDesc = (currentFg != IntPtr.Zero && IsWindowTargetProcess(currentFg))
                                    ? (_lastCachedProcName + ":" + currentFg)
                                    : ("HWND:" + currentFg);
                                LogInfo("Intercepted local volume key (" + keyName + ", flags=0x" + flags.ToString("X") + ", Active=" + targetDesc + ") -> local volume (Core Audio) + OSD.");
                            }

                            if (vkCode == VK_VOLUME_UP)
                            {
                                AudioVolumeController.TriggerStepVolume(true);
                            }
                            else if (vkCode == VK_VOLUME_DOWN)
                            {
                                AudioVolumeController.TriggerStepVolume(false);
                            }
                            else
                            {
                                AudioVolumeController.TriggerToggleMute();
                            }

                            if (_restoreFocusTimer != null)
                            {
                                _restoreFocusTimer.Stop();
                                _restoreFocusTimer.Start();
                            }
                        }

                        return (IntPtr)1;
                    }

                    // Never intercept synthetic key events injected by our own keybd_event
                    if ((flags & LLKHF_INJECTED) != 0 && extraInfo == OurInjectedExtraInfo)
                    {
                        return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
                    }

                    // Track physical Win & Ctrl modifier states
                    if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                    {
                        if (isDown) _physWinDown = true;
                        else if (isUp)
                        {
                            _physWinDown = false;
                            CheckDesktopSwitchSessionEnd();
                        }
                    }
                    else if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL || vkCode == VK_CONTROL)
                    {
                        if (isDown) _physCtrlDown = true;
                        else if (isUp)
                        {
                            _physCtrlDown = false;
                            CheckDesktopSwitchSessionEnd();
                        }
                    }

                    // 2. Intercept Win + Ctrl + Left/Right Arrow -> Switch Local Virtual Desktop when UU or ZTE VDI is active
                    if (vkCode == VK_LEFT || vkCode == VK_RIGHT)
                    {
                        bool winHeld = _physWinDown || IsKeyPhysicallyDown(VK_LWIN) || IsKeyPhysicallyDown(VK_RWIN);
                        bool ctrlHeld = _physCtrlDown || IsKeyPhysicallyDown(VK_LCONTROL) || IsKeyPhysicallyDown(VK_RCONTROL) || IsKeyPhysicallyDown(VK_CONTROL);

                        if (winHeld && ctrlHeld)
                        {
                            IntPtr currentFg = GetForegroundWindow();
                            bool targetActive = (currentFg != IntPtr.Zero && IsWindowTargetProcess(currentFg)) || _inDesktopSwitchSession;

                            if (targetActive)
                            {
                                if (isDown)
                                {
                                    _physWinDown = true;
                                    _physCtrlDown = true;
                                    _inDesktopSwitchSession = true;
                                    _desktopSwitchEndTimer?.Stop();
                                    if (_desktopSwitchSafetyTimer != null)
                                    {
                                        _desktopSwitchSafetyTimer.Stop();
                                        _desktopSwitchSafetyTimer.Start();
                                    }

                                    byte arrowByte = (byte)vkCode;
                                    if (currentFg != IntPtr.Zero && IsWindowTargetProcess(currentFg))
                                    {
                                        _desktopSwitchSourceHwnd = currentFg;
                                    }
                                    else if (_desktopSwitchSourceHwnd == IntPtr.Zero && IsWindowTargetProcess(_lastExternalForegroundHwnd))
                                    {
                                        _desktopSwitchSourceHwnd = _lastExternalForegroundHwnd;
                                    }

                                    FocusLocalTrayWindow();

                                    if (_trayMenuWindow != null)
                                    {
                                        _trayMenuWindow.Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            TriggerLocalVirtualDesktopSwitch(arrowByte);
                                        }));
                                    }
                                }

                                return (IntPtr)1;
                            }
                        }
                    }
                }
            }

            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        private static void CheckDesktopSwitchSessionEnd()
        {
            if (_inDesktopSwitchSession && !_physWinDown && !_physCtrlDown)
            {
                if (_desktopSwitchEndTimer != null)
                {
                    _desktopSwitchEndTimer.Stop();
                    _desktopSwitchEndTimer.Start();
                }
                else
                {
                    FinalizeDesktopSwitchSession("DesktopSwitchModifiersReleased");
                }
            }
        }

        private static void FinalizeDesktopSwitchSession(string reason)
        {
            _inDesktopSwitchSession = false;
            _physWinDown = false;
            _physCtrlDown = false;
            _desktopSwitchEndTimer?.Stop();
            _desktopSwitchSafetyTimer?.Stop();

            IntPtr srcHwnd = _desktopSwitchSourceHwnd;
            _desktopSwitchSourceHwnd = IntPtr.Zero;

            if (srcHwnd != IntPtr.Zero && IsWindow(srcHwnd) && IsWindowVisible(srcHwnd) && !IsIconic(srcHwnd))
            {
                bool cloaked = IsWindowCloaked(srcHwnd);
                if (!cloaked)
                {
                    RemoveCloakedTargetWindow(srcHwnd);
                    RestoreForegroundWindow(srcHwnd, reason + "_SameDesktop");
                    HideFocusParkingWindow();
                    ReinstallKeyboardHookToTop(reason + "_SameDesktop");
                    ScheduleDelayedRehooks();
                    return;
                }
                else
                {
                    AddCloakedTargetWindow(srcHwnd);
                    IntPtr currentFg = GetForegroundWindow();
                    if (currentFg == IntPtr.Zero || currentFg == _focusParkingHwnd || !IsValidExternalWindow(currentFg) || IsWindowCloaked(currentFg))
                    {
                        IntPtr prefMon = MonitorFromWindow(srcHwnd, MONITOR_DEFAULTTONEAREST);
                        IntPtr topLocal = FindTopmostVisibleWindowOnCurrentDesktop(prefMon);
                        if (topLocal != IntPtr.Zero)
                        {
                            RestoreForegroundWindow(topLocal, reason + "_FocusLocalTop");
                        }
                    }
                }
            }

            HideFocusParkingWindow();
            EnsureTaskbarHoverWakeupForWindow(GetForegroundWindow(), reason + "_PostSwitch");
            EnsureKeyboardHookState(reason + "_KeepHookActive");
            ScheduleDelayedRehooks();
        }

        private static void TriggerLocalVirtualDesktopSwitch(byte arrowVk)
        {
            long now = Environment.TickCount & Int32.MaxValue;
            if (now - _lastDesktopSwitchTick < 90)
            {
                return;
            }
            _lastDesktopSwitchTick = now;

            try
            {
                FocusLocalTrayWindow();

                DispatcherTimer injectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                injectTimer.Tick += (s, e) =>
                {
                    injectTimer.Stop();
                    try
                    {
                        IntPtr fgAfter = FocusLocalTrayWindow();
                        if (fgAfter != IntPtr.Zero && IsWindowTargetProcess(fgAfter))
                        {
                            fgAfter = FocusLocalTrayWindow();
                        }

                        keybd_event((byte)VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY, OurInjectedExtraInfoU);
                        keybd_event((byte)VK_LCONTROL, 0, 0, OurInjectedExtraInfoU);
                        keybd_event(arrowVk, 0, KEYEVENTF_EXTENDEDKEY, OurInjectedExtraInfoU);
                        keybd_event(arrowVk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, OurInjectedExtraInfoU);
                        keybd_event((byte)VK_LCONTROL, 0, KEYEVENTF_KEYUP, OurInjectedExtraInfoU);
                        keybd_event((byte)VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, OurInjectedExtraInfoU);

                        ReinstallKeyboardHookToTop("PostDesktopSwitchInject");
                        ScheduleDelayedRehooks();

                        if (!_physWinDown && !_physCtrlDown && _desktopSwitchEndTimer != null)
                        {
                            _desktopSwitchEndTimer.Stop();
                            _desktopSwitchEndTimer.Start();
                        }

                        if (_desktopSwitchSafetyTimer != null)
                        {
                            _desktopSwitchSafetyTimer.Stop();
                            _desktopSwitchSafetyTimer.Start();
                        }

                        LogInfo("Switched local virtual desktop via Win+Ctrl+" + (arrowVk == VK_LEFT ? "Left" : "Right") + " (SourceHwnd=" + _desktopSwitchSourceHwnd + ", FgAfterDeactivate=" + fgAfter + ")");
                    }
                    catch (Exception ex)
                    {
                        LogError("Failed to inject Win+Ctrl+Arrow", ex);
                    }
                };
                injectTimer.Start();
            }
            catch (Exception ex)
            {
                LogError("TriggerLocalVirtualDesktopSwitch failed", ex);
            }
        }

        private static void Cleanup()
        {
            _rehookTimer1?.Stop();
            _rehookTimer2?.Stop();
            _restoreFocusTimer?.Stop();
            _desktopSwitchEndTimer?.Stop();
            _desktopSwitchSafetyTimer?.Stop();
            _virtualDesktopWatchdogTimer?.Stop();

            _imeMonitor?.Dispose();
            _imeMonitor = null;

            if (_lastCheckedTaskbarRemoteSuppressed)
            {
                EnsureTaskbarsTopmost(true);
            }
            UninstallKeyboardHook("ProcessExit");

            if (_winEventHookFg != IntPtr.Zero)
            {
                UnhookWinEvent(_winEventHookFg);
                _winEventHookFg = IntPtr.Zero;
            }

            if (_winEventHookCloak != IntPtr.Zero)
            {
                UnhookWinEvent(_winEventHookCloak);
                _winEventHookCloak = IntPtr.Zero;
            }

            if (_focusParkingHwnd != IntPtr.Zero)
            {
                DestroyWindow(_focusParkingHwnd);
                _focusParkingHwnd = IntPtr.Zero;
            }

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            if (_appTrayIcon != null)
            {
                _appTrayIcon.Dispose();
                _appTrayIcon = null;
            }

            if (_volumeOsdWindow != null)
            {
                _volumeOsdWindow.CloseOsd();
                _volumeOsdWindow = null;
            }

            if (_statusOsdWindow != null)
            {
                _statusOsdWindow.CloseOsd();
                _statusOsdWindow = null;
            }

            if (_settingsWindow != null)
            {
                try { _settingsWindow.Close(); } catch { }
                _settingsWindow = null;
            }
        }

        public static void LogInfo(string message)
        {
            WriteLog("INFO", message);
        }

        public static void LogError(string message, Exception ex)
        {
            string full = ex == null ? message : (message + " | " + ex.GetType().Name + ": " + ex.Message);
            WriteLog("ERROR", full);
        }

        private static readonly object LogLock = new object();

        private static void WriteLog(string level, string message)
        {
            try
            {
                if (string.IsNullOrEmpty(_logFilePath)) return;
                string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                    DateTime.Now, level, message, Environment.NewLine);
                lock (LogLock)
                {
                    FileInfo fi = new FileInfo(_logFilePath);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        File.WriteAllText(_logFilePath, line, Encoding.UTF8);
                    }
                    else
                    {
                        File.AppendAllText(_logFilePath, line, Encoding.UTF8);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
