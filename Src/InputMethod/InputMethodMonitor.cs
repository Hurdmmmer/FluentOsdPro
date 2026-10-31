using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace FluentOsdPro
{
    internal sealed class InputMethodChangedEventArgs : EventArgs
    {
        public InputMethodChangedEventArgs(InputMethodSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public InputMethodSnapshot Snapshot { get; }
    }

    internal sealed class InputMethodMonitor : IDisposable
    {
        private const uint WmImeControl = 0x0283;
        private const int ImcGetConversionMode = 0x0001;
        private const int ImcSetConversionMode = 0x0002;
        private const int ImcGetOpenStatus = 0x0005;
        private const int ImcSetOpenStatus = 0x0006;
        private const uint SmtoAbortIfHung = 0x0002;
        private const int PrimaryChinese = 0x04;
        private const int PrimaryEnglish = 0x09;
        private const int PrimaryJapanese = 0x11;
        private const int ImeCmodeNative = 0x0001;
        private const int ImeCmodeKatakana = 0x0002;
        private const int PollIntervalMs = 110;
        private const int QuickCheckDelayMs = 60;
        private const int FollowUpCheckDelayMs = 165;

        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _quickCheckTimer;
        private readonly DispatcherTimer _followUpCheckTimer;
        private readonly InputMethodTracker _tracker;
        private readonly uint _ownPid;
        private bool _pendingForceEmit;
        private bool _disposed;

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo lpgui);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

        public InputMethodMonitor()
        {
            _ownPid = (uint)Process.GetCurrentProcess().Id;
            _tracker = new InputMethodTracker(InputMethodTracker.DefaultWindowQuietMilliseconds);
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
            };
            _timer.Tick += OnTick;

            _quickCheckTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(QuickCheckDelayMs)
            };
            _quickCheckTimer.Tick += OnQuickCheckTick;

            _followUpCheckTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(FollowUpCheckDelayMs)
            };
            _followUpCheckTimer.Tick += OnFollowUpCheckTick;
        }

        public event EventHandler<InputMethodChangedEventArgs> InputMethodChanged;

        public void NotifyUserImeKeyActivity(bool scheduleQuickCheck = true)
        {
            if (_disposed)
            {
                return;
            }

            long nowTick = Environment.TickCount & int.MaxValue;
            _tracker.NotifyUserKeyActivity(nowTick);

            if (scheduleQuickCheck)
            {
                _quickCheckTimer.Stop();
                _quickCheckTimer.Start();
                _followUpCheckTimer.Stop();
                _followUpCheckTimer.Start();
            }
        }

        public void SyncBaselineSilently()
        {
            if (_disposed)
            {
                return;
            }

            InputMethodSnapshot snapshot = CaptureForegroundSnapshot();
            if (snapshot != null && snapshot.Known)
            {
                _tracker.SyncBaselineSilently(snapshot);
            }
        }

        public void EnsureOsdShownForCurrentState()
        {
            if (_disposed)
            {
                return;
            }

            _quickCheckTimer.Stop();
            _followUpCheckTimer.Stop();
            _pendingForceEmit = false;
            long nowTick = Environment.TickCount & int.MaxValue;
            _tracker.NotifyUserKeyActivity(nowTick);
            PollOnce(forceEmit: true);
        }

        public void ScheduleGuaranteedOsdRefresh(int delayMs = 50)
        {
            if (_disposed)
            {
                return;
            }

            long nowTick = Environment.TickCount & int.MaxValue;
            _tracker.NotifyUserKeyActivity(nowTick);
            _pendingForceEmit = true;
            _followUpCheckTimer.Stop();
            _quickCheckTimer.Stop();
            _quickCheckTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, delayMs));
            _quickCheckTimer.Start();
        }

        public void NotifyForegroundChanged(IntPtr hwnd)
        {
            if (_disposed || hwnd == IntPtr.Zero || IsTransientOverlayWindow(hwnd))
            {
                return;
            }

            long nowTick = Environment.TickCount & int.MaxValue;
            _tracker.ResetWindowQuietPeriod(hwnd, nowTick);
        }

        private static bool IsTransientOverlayWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return true;
            }

            StringBuilder classSb = new StringBuilder(64);
            if (GetClassName(hwnd, classSb, classSb.Capacity) > 0)
            {
                string cls = classSb.ToString();
                if (string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Shell_InputSwitchTopLevelWindow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Shell_InputSwitchDismissOverlay", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "#32768", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public void Start()
        {
            if (_disposed)
            {
                return;
            }

            PollOnce();
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _quickCheckTimer.Stop();
            _followUpCheckTimer.Stop();
        }

        private void OnQuickCheckTick(object sender, EventArgs e)
        {
            _quickCheckTimer.Stop();
            _quickCheckTimer.Interval = TimeSpan.FromMilliseconds(QuickCheckDelayMs);
            bool force = _pendingForceEmit;
            _pendingForceEmit = false;
            PollOnce(forceEmit: force);
        }

        private void OnFollowUpCheckTick(object sender, EventArgs e)
        {
            _followUpCheckTimer.Stop();
            PollOnce();
        }

        private void OnTick(object sender, EventArgs e)
        {
            PollOnce();
        }

        private void PollOnce(bool forceEmit = false)
        {
            if (!AppSettings.Current.ImeFlyoutEnabled || Program.IsInTransientFocusSession)
            {
                return;
            }

            InputMethodSnapshot snapshot = CaptureForegroundSnapshot();
            if (snapshot == null)
            {
                return;
            }

            long nowTick = Environment.TickCount & int.MaxValue;

            // When the user actively switches keyboard layouts from a non-Chinese layout (e.g. 0x0409 English)
            // to the Chinese IME (0x0804), automatically ensure Chinese native conversion mode ("中") is active.
            if (_tracker.IsUserSwitchFromNonChineseToChineseLayout(snapshot, nowTick) &&
                snapshot.ConversionMode != InputConversionModeKind.ChineseMode)
            {
                ForceChineseNativeModeForWindow(snapshot.WindowHandle);
                snapshot = new InputMethodSnapshot(
                    snapshot.WindowHandle,
                    snapshot.LangId,
                    InputLanguageFamily.Chinese,
                    InputConversionModeKind.ChineseMode);
                Program.LogInfo("[IME] Auto-switched Chinese IME to Native (ChineseMode) on layout switch (HWND=" +
                                snapshot.WindowHandle + ", LangId=0x" + snapshot.LangId.ToString("X4") + ")");
            }

            InputMethodSnapshot changed = _tracker.Observe(snapshot, nowTick, forceEmit);
            if (changed != null)
            {
                Program.LogInfo("[IME] Mode changed -> " + changed.LanguageFamily + "/" + changed.ConversionMode +
                                " (HWND=" + changed.WindowHandle + ", LangId=0x" + changed.LangId.ToString("X4") + ")");
                InputMethodChanged?.Invoke(this, new InputMethodChangedEventArgs(changed));
            }
        }

        private InputMethodSnapshot CaptureForegroundSnapshot()
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                return null;
            }

            uint processId;
            uint threadId = GetWindowThreadProcessId(foregroundWindow, out processId);
            if (threadId == 0 || processId == 0 || processId == _ownPid)
            {
                return null;
            }

            // Skip shell tray / Win+Space input switcher / popup host windows so volume or tray interactions don't disturb IME tracking
            if (IsTransientOverlayWindow(foregroundWindow))
            {
                return null;
            }

            IntPtr targetWindow = ResolveImeTargetWindow(foregroundWindow, threadId);
            IntPtr hkl = GetKeyboardLayout(threadId);
            int langId = unchecked((ushort)(long)hkl);
            InputLanguageFamily languageFamily = ClassifyLanguage(langId);
            InputConversionModeKind conversionMode = QueryConversionMode(targetWindow, foregroundWindow, languageFamily);

            return new InputMethodSnapshot(
                foregroundWindow,
                langId,
                languageFamily,
                conversionMode);
        }

        private static IntPtr ResolveImeTargetWindow(IntPtr foregroundWindow, uint threadId)
        {
            GuiThreadInfo guiInfo = new GuiThreadInfo
            {
                cbSize = Marshal.SizeOf(typeof(GuiThreadInfo))
            };

            if (GetGUIThreadInfo(threadId, ref guiInfo))
            {
                if (guiInfo.hwndFocus != IntPtr.Zero)
                {
                    return guiInfo.hwndFocus;
                }

                if (guiInfo.hwndActive != IntPtr.Zero)
                {
                    return guiInfo.hwndActive;
                }
            }

            return foregroundWindow;
        }

        private static InputLanguageFamily ClassifyLanguage(int langId)
        {
            switch (langId & 0x3FF)
            {
                case PrimaryChinese:
                    return InputLanguageFamily.Chinese;
                case PrimaryEnglish:
                    return InputLanguageFamily.English;
                case PrimaryJapanese:
                    return InputLanguageFamily.Japanese;
                default:
                    return InputLanguageFamily.Other;
            }
        }

        private static InputConversionModeKind QueryConversionMode(
            IntPtr targetWindow,
            IntPtr foregroundWindow,
            InputLanguageFamily languageFamily)
        {
            if (languageFamily == InputLanguageFamily.English)
            {
                return InputConversionModeKind.EnglishMode;
            }

            if (languageFamily == InputLanguageFamily.Other)
            {
                return InputConversionModeKind.Unknown;
            }

            IntPtr imeWindow = ImmGetDefaultIMEWnd(targetWindow);
            if (imeWindow == IntPtr.Zero && targetWindow != foregroundWindow)
            {
                imeWindow = ImmGetDefaultIMEWnd(foregroundWindow);
            }

            if (imeWindow == IntPtr.Zero)
            {
                return InputConversionModeKind.Unknown;
            }

            if (!TrySendImeControl(imeWindow, ImcGetOpenStatus, out int openStatus))
            {
                return InputConversionModeKind.Unknown;
            }

            bool isOpen = openStatus != 0;
            bool hasConversionMode = TrySendImeControl(imeWindow, ImcGetConversionMode, out int conversionMode);

            if (languageFamily == InputLanguageFamily.Chinese)
            {
                if (!isOpen)
                {
                    return InputConversionModeKind.EnglishMode;
                }

                if (!hasConversionMode)
                {
                    return InputConversionModeKind.ChineseMode;
                }

                return (conversionMode & ImeCmodeNative) != 0
                    ? InputConversionModeKind.ChineseMode
                    : InputConversionModeKind.EnglishMode;
            }

            if (languageFamily == InputLanguageFamily.Japanese)
            {
                if (!isOpen)
                {
                    return InputConversionModeKind.DirectInput;
                }

                if (!hasConversionMode || (conversionMode & ImeCmodeNative) == 0)
                {
                    return InputConversionModeKind.DirectInput;
                }

                return (conversionMode & ImeCmodeKatakana) != 0
                    ? InputConversionModeKind.Katakana
                    : InputConversionModeKind.Hiragana;
            }

            return InputConversionModeKind.Unknown;
        }

        private static bool TrySendImeControl(IntPtr imeWindow, int command, out int value)
        {
            value = 0;
            IntPtr result;
            IntPtr ok = SendMessageTimeout(
                imeWindow,
                WmImeControl,
                new IntPtr(command),
                IntPtr.Zero,
                SmtoAbortIfHung,
                60,
                out result);

            if (ok == IntPtr.Zero)
            {
                return false;
            }

            value = result.ToInt32();
            return true;
        }

        private void ForceChineseNativeModeForWindow(IntPtr foregroundWindow)
        {
            if (foregroundWindow == IntPtr.Zero)
            {
                return;
            }

            ApplyChineseNativeModeOnce(foregroundWindow);

            // Re-apply once after 45ms in case Windows TSF initializes the 0x0804 TIP slightly after HKL switch
            DispatcherTimer retryTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(45)
            };
            retryTimer.Tick += (s, e) =>
            {
                retryTimer.Stop();
                if (!_disposed && GetForegroundWindow() == foregroundWindow)
                {
                    ApplyChineseNativeModeOnce(foregroundWindow);
                }
            };
            retryTimer.Start();
        }

        private static void ApplyChineseNativeModeOnce(IntPtr foregroundWindow)
        {
            try
            {
                uint pid;
                uint threadId = GetWindowThreadProcessId(foregroundWindow, out pid);
                IntPtr targetWindow = threadId != 0 ? ResolveImeTargetWindow(foregroundWindow, threadId) : foregroundWindow;

                IntPtr imeWindow = ImmGetDefaultIMEWnd(targetWindow);
                if (imeWindow == IntPtr.Zero && targetWindow != foregroundWindow)
                {
                    imeWindow = ImmGetDefaultIMEWnd(foregroundWindow);
                }

                if (imeWindow == IntPtr.Zero)
                {
                    return;
                }

                SendMessageTimeout(
                    imeWindow,
                    WmImeControl,
                    new IntPtr(ImcSetOpenStatus),
                    new IntPtr(1),
                    SmtoAbortIfHung,
                    50,
                    out _);

                int baseMode = 0x0400;
                if (TrySendImeControl(imeWindow, ImcGetConversionMode, out int currentMode))
                {
                    baseMode = currentMode;
                }

                int nativeMode = baseMode | ImeCmodeNative;
                SendMessageTimeout(
                    imeWindow,
                    WmImeControl,
                    new IntPtr(ImcSetConversionMode),
                    new IntPtr(nativeMode),
                    SmtoAbortIfHung,
                    50,
                    out _);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _quickCheckTimer.Stop();
            _quickCheckTimer.Tick -= OnQuickCheckTick;
            _followUpCheckTimer.Stop();
            _followUpCheckTimer.Tick -= OnFollowUpCheckTick;
        }
    }
}
