using System;
using System.Collections.Generic;

namespace FluentOsdPro
{
    internal sealed class InputMethodTracker
    {
        public const int DefaultWindowQuietMilliseconds = 350;
        public const int DefaultKeyGateMilliseconds = 950;

        private readonly int _windowQuietMilliseconds;
        private readonly Dictionary<IntPtr, int> _lastLangIdByWindow = new Dictionary<IntPtr, int>();
        private InputMethodSnapshot _baseline;
        private InputMethodSnapshot _lastEmittedSnapshot;
        private long _lastEmittedTick;
        private IntPtr _quietWindowHandle;
        private long _quietUntilTick;
        private long _keyGateUntilTick;

        public InputMethodTracker(int windowQuietMilliseconds = DefaultWindowQuietMilliseconds)
        {
            _windowQuietMilliseconds = Math.Max(0, windowQuietMilliseconds);
        }

        public void NotifyUserKeyActivity(long nowTick)
        {
            _keyGateUntilTick = nowTick + DefaultKeyGateMilliseconds;
        }

        public void SyncBaselineSilently(InputMethodSnapshot snapshot)
        {
            if (snapshot != null && snapshot.Known)
            {
                if (_baseline == null || _quietWindowHandle != snapshot.WindowHandle)
                {
                    _quietWindowHandle = snapshot.WindowHandle;
                    _baseline = snapshot;
                }
                _lastLangIdByWindow[snapshot.WindowHandle] = snapshot.LangId;
            }
        }

        public void ResetWindowQuietPeriod(IntPtr hwnd, long nowTick)
        {
            if (hwnd != _quietWindowHandle)
            {
                _quietWindowHandle = hwnd;
                _baseline = null;
            }
            _quietUntilTick = nowTick + _windowQuietMilliseconds;
        }

        /// <summary>
        /// Returns true when the user actively pressed an IME/layout switch hotkey to switch
        /// from a non-Chinese keyboard layout (e.g. 0x0409 English) to a Chinese IME layout (0x0804).
        /// </summary>
        public bool IsUserSwitchFromNonChineseToChineseLayout(InputMethodSnapshot snapshot, long nowTick)
        {
            if (snapshot == null || !snapshot.Known || snapshot.LanguageFamily != InputLanguageFamily.Chinese)
            {
                return false;
            }

            if (nowTick > _keyGateUntilTick)
            {
                return false;
            }

            if (_baseline != null &&
                snapshot.WindowHandle == _quietWindowHandle &&
                _baseline.LanguageFamily != InputLanguageFamily.Chinese)
            {
                return true;
            }

            if (_lastLangIdByWindow.TryGetValue(snapshot.WindowHandle, out int prevLangId) &&
                (prevLangId & 0x3FF) != 0x04)
            {
                return true;
            }

            return false;
        }

        public InputMethodSnapshot Observe(InputMethodSnapshot snapshot, long nowTick, bool forceEmit = false)
        {
            if (snapshot == null)
            {
                return null;
            }

            if (snapshot.Known && snapshot.WindowHandle != IntPtr.Zero)
            {
                _lastLangIdByWindow[snapshot.WindowHandle] = snapshot.LangId;
            }

            if (forceEmit && snapshot.Known)
            {
                _quietWindowHandle = snapshot.WindowHandle;
                _baseline = snapshot;
                if (_lastEmittedSnapshot != null &&
                    _lastEmittedSnapshot.SameDisplayState(snapshot) &&
                    nowTick >= _lastEmittedTick &&
                    (nowTick - _lastEmittedTick) < 180)
                {
                    return null;
                }

                _lastEmittedSnapshot = snapshot;
                _lastEmittedTick = nowTick;
                return snapshot;
            }

            // Foreground window switched: clear old baseline and start quiet period so automatic
            // per-window IME restoration or delayed initialization never triggers a false OSD popup
            if (snapshot.WindowHandle != _quietWindowHandle)
            {
                _quietWindowHandle = snapshot.WindowHandle;
                _quietUntilTick = nowTick + _windowQuietMilliseconds;
                _baseline = snapshot.Known ? snapshot : null;
                return null;
            }

            if (!snapshot.Known)
            {
                return null;
            }

            // First valid snapshot for this window establishes baseline silently
            if (_baseline == null)
            {
                _baseline = snapshot;
                return null;
            }

            // Within quiet period after window switch (when no IME key was pressed): update baseline silently
            if (nowTick < _quietUntilTick && nowTick > _keyGateUntilTick)
            {
                _baseline = snapshot;
                return null;
            }

            // Same state as baseline: no notification
            if (_baseline.SameDisplayState(snapshot))
            {
                _baseline = snapshot;
                return null;
            }

            // State changed, but if the user did not press any IME-switching key recently
            // (e.g. app changed focus between an edit box and non-edit area on mouse click),
            // silently update baseline without popping up a false-positive OSD.
            if (nowTick > _keyGateUntilTick)
            {
                _baseline = snapshot;
                return null;
            }

            // Genuine user-initiated IME switch inside the active window
            _baseline = snapshot;
            _lastEmittedSnapshot = snapshot;
            _lastEmittedTick = nowTick;
            return snapshot;
        }
    }
}
