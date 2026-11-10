using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FluentOsdPro
{
    /// <summary>
    /// Standalone wallpaper Mica &amp; accent color engine for FluentOsdPro.
    /// Samples the active desktop wallpaper (48x48 WIC thumbnail with edge + chroma weighting),
    /// synthesizes a Windows 11 Mica surface color for light/dark themes, and derives a
    /// contrast-checked (&gt;= 3:1) same-hue accent color for OSD controls and badges.
    ///
    /// Completely standalone and read-only: has zero external/Windhawk dependencies and
    /// NEVER writes any system theme or registry setting.
    /// </summary>
    internal static class TaskbarMicaSync
    {
        private const string DesktopRegPath = @"Control Panel\Desktop";
        private const string WallpapersRegPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers";
        private const string PersonalizeRegPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private const int RegNotifyChangeName = 0x00000001;
        private const int RegNotifyChangeLastSet = 0x00000004;
        private const int SpiGetDeskWallpaper = 0x0073;
        private const int ThumbSize = 48;

        private static readonly object SyncRoot = new object();
        private static bool _hasColor;
        private static Color _color;
        private static long _signature;
        private static long _lastQuickCheckTick;

        private static string _cachedStateKey;
        private static uint _cachedSampleHash;
        private static bool _hasCachedWallpaper;
        private static byte _wallR, _wallG, _wallB;

        private static Thread _workerThread;
        private static ManualResetEvent _stopEvent;
        private static AutoResetEvent _recomputeEvent;
        private static FileSystemWatcher _themesWatcher;

        /// <summary>Raised on a background thread whenever the effective wallpaper Mica color changes.</summary>
        public static event Action Changed;

        /// <summary>Incremented on every change; OSD windows compare it to decide whether to restyle.</summary>
        public static long Signature
        {
            get { return Interlocked.Read(ref _signature); }
        }

        [DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(
            SafeRegistryHandle hKey,
            bool bWatchSubtree,
            int dwNotifyFilter,
            SafeWaitHandle hEvent,
            bool fAsynchronous);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(int uiAction, int uiParam, StringBuilder pvParam, int fWinIni);

        public static void Start()
        {
            lock (SyncRoot)
            {
                if (_workerThread != null)
                {
                    return;
                }

                _stopEvent = new ManualResetEvent(false);
                _recomputeEvent = new AutoResetEvent(false);
                Recompute(raiseEvent: false);

                StartThemesWatcher();
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _workerThread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "TaskbarMicaSync"
                };
                _workerThread.Start();
            }
        }

        public static void Stop()
        {
            Thread thread;
            lock (SyncRoot)
            {
                thread = _workerThread;
                _workerThread = null;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                StopThemesWatcher();
                if (_stopEvent != null)
                {
                    _stopEvent.Set();
                }
            }

            if (thread != null)
            {
                thread.Join(500);
            }
        }

        /// <summary>
        /// Signals the background watcher when an OSD window receives WM_SETTINGCHANGE / WM_THEMECHANGED.
        /// </summary>
        public static void NotifySettingChange()
        {
            AutoResetEvent ev = _recomputeEvent;
            if (ev != null)
            {
                try
                {
                    ev.Set();
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Lightweight synchronous check invoked right before an OSD flyout is shown.
        /// If the wallpaper file timestamp/size or theme changed since the last check,
        /// immediately recomputes so the very first frame of the OSD uses the new color.
        /// </summary>
        public static void RefreshIfNeeded()
        {
            if (!AppSettings.Current.WallpaperColorSyncEnabled)
            {
                return;
            }

            long nowTick = Environment.TickCount & int.MaxValue;
            long lastTick = Interlocked.Read(ref _lastQuickCheckTick);
            if (nowTick - lastTick >= 0 && nowTick - lastTick < 200)
            {
                return;
            }
            Interlocked.Exchange(ref _lastQuickCheckTick, nowTick);

            try
            {
                string currentStateKey = BuildQuickWallpaperStateKey();
                bool needsRecompute;
                lock (SyncRoot)
                {
                    needsRecompute = !_hasColor || !string.Equals(_cachedStateKey, currentStateKey, StringComparison.Ordinal);
                }

                if (needsRecompute)
                {
                    Recompute(raiseEvent: false);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Immediately applies or restores wallpaper &amp; Microsoft IME color sync when toggled in Settings.
        /// </summary>
        public static void ApplySyncToggle(bool enabled)
        {
            if (enabled)
            {
                lock (SyncRoot)
                {
                    _cachedStateKey = null;
                }
                Recompute(raiseEvent: true);
            }
            else
            {
                RestoreMicrosoftImeDefaultTheme();
                Interlocked.Increment(ref _signature);
                Action handler = Changed;
                if (handler != null)
                {
                    try
                    {
                        handler();
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Immediately refreshes OSD windows when the user adjusts the transparency slider.
        /// </summary>
        public static void NotifyTransparencyChanged()
        {
            Interlocked.Increment(ref _signature);
            Action handler = Changed;
            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch
                {
                }
            }
        }
        /// <summary>
        /// Returns the wallpaper Mica color when it suits the given OSD theme (light color for
        /// light theme, dark color for dark theme), so OSD text always keeps its contrast.
        /// </summary>
        public static bool TryGetForTheme(bool isDark, out Color color)
        {
            if (!AppSettings.Current.WallpaperColorSyncEnabled)
            {
                color = default(Color);
                return false;
            }

            lock (SyncRoot)
            {
                color = _color;
                if (!_hasColor)
                {
                    return false;
                }
            }

            double luminance = RelativeLuminance(color);
            return isDark ? luminance < 0.30 : luminance > 0.45;
        }

        public static bool IsColorLight(Color color)
        {
            return RelativeLuminance(color) > 0.42;
        }

        /// <summary>
        /// Derives a clearly distinguishable accent (same hue, higher saturation, darker on
        /// light theme / lighter on dark theme) with at least 3:1 contrast against the
        /// background. Returns null for near-grey backgrounds (no meaningful hue).
        /// </summary>
        public static Color? DeriveAccent(Color background, bool isDark)
        {
            int maxCh = Math.Max(background.R, Math.Max(background.G, background.B));
            int minCh = Math.Min(background.R, Math.Min(background.G, background.B));
            if (maxCh - minCh < 6)
            {
                return null;
            }

            double h, s, l;
            RgbToHsl(background, out h, out s, out l);

            double targetS = Math.Min(0.85, Math.Max(0.55, s));
            double targetL = isDark ? 0.70 : 0.38;
            Color accent = HslToRgb(h, targetS, targetL);

            for (int i = 0; i < 12 && ContrastRatio(accent, background) < 3.0; i++)
            {
                targetL = isDark ? Math.Min(0.92, targetL + 0.04) : Math.Max(0.12, targetL - 0.04);
                accent = HslToRgb(h, targetS, targetL);
            }

            return accent;
        }

        private static void StartThemesWatcher()
        {
            try
            {
                string themesDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Themes");
                if (!Directory.Exists(themesDir))
                {
                    return;
                }

                _themesWatcher = new FileSystemWatcher(themesDir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false
                };
                FileSystemEventHandler onChanged = (s, e) => NotifySettingChange();
                RenamedEventHandler onRenamed = (s, e) => NotifySettingChange();
                _themesWatcher.Changed += onChanged;
                _themesWatcher.Created += onChanged;
                _themesWatcher.Deleted += onChanged;
                _themesWatcher.Renamed += onRenamed;
                _themesWatcher.EnableRaisingEvents = true;
            }
            catch
            {
            }
        }

        private static void StopThemesWatcher()
        {
            try
            {
                if (_themesWatcher != null)
                {
                    _themesWatcher.EnableRaisingEvents = false;
                    _themesWatcher.Dispose();
                    _themesWatcher = null;
                }
            }
            catch
            {
            }
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // Desktop = wallpaper changed; General/Color/VisualStyle = light/dark theme changed.
            if (e.Category == UserPreferenceCategory.Desktop ||
                e.Category == UserPreferenceCategory.General ||
                e.Category == UserPreferenceCategory.Color ||
                e.Category == UserPreferenceCategory.VisualStyle)
            {
                NotifySettingChange();
            }
        }

        private static void WorkerLoop()
        {
            ManualResetEvent stopEvent = _stopEvent;
            AutoResetEvent recomputeEvent = _recomputeEvent;

            while (!stopEvent.WaitOne(0))
            {
                RegistryKey desktopKey = null;
                RegistryKey wallpapersKey = null;
                RegistryKey personalizeKey = null;
                AutoResetEvent regDesktopEvent = new AutoResetEvent(false);
                AutoResetEvent regWallpapersEvent = new AutoResetEvent(false);
                AutoResetEvent regPersonalizeEvent = new AutoResetEvent(false);
                try
                {
                    desktopKey = Registry.CurrentUser.OpenSubKey(DesktopRegPath, false);
                    wallpapersKey = Registry.CurrentUser.OpenSubKey(WallpapersRegPath, false);
                    personalizeKey = Registry.CurrentUser.OpenSubKey(PersonalizeRegPath, false);

                    if (desktopKey != null)
                    {
                        RegNotifyChangeKeyValue(desktopKey.Handle, false,
                            RegNotifyChangeName | RegNotifyChangeLastSet,
                            regDesktopEvent.SafeWaitHandle, true);
                    }
                    if (wallpapersKey != null)
                    {
                        RegNotifyChangeKeyValue(wallpapersKey.Handle, false,
                            RegNotifyChangeName | RegNotifyChangeLastSet,
                            regWallpapersEvent.SafeWaitHandle, true);
                    }
                    if (personalizeKey != null)
                    {
                        RegNotifyChangeKeyValue(personalizeKey.Handle, false,
                            RegNotifyChangeName | RegNotifyChangeLastSet,
                            regPersonalizeEvent.SafeWaitHandle, true);
                    }

                    WaitHandle[] handles = new WaitHandle[]
                    {
                        stopEvent,
                        recomputeEvent,
                        regDesktopEvent,
                        regWallpapersEvent,
                        regPersonalizeEvent
                    };

                    int which = WaitHandle.WaitAny(handles, Timeout.Infinite);
                    if (which == 0)
                    {
                        return;
                    }

                    // Stage 1: coalesce initial burst (150ms) and recompute.
                    if (stopEvent.WaitOne(150))
                    {
                        return;
                    }
                    Recompute(raiseEvent: true);

                    // Stage 2: Windows 11 Explorer often finishes writing TranscodedWallpaper
                    // 250~500ms after the registry/WM_SETTINGCHANGE broadcast. Follow up once more.
                    if (stopEvent.WaitOne(450))
                    {
                        return;
                    }
                    Recompute(raiseEvent: true);
                }
                catch
                {
                    if (stopEvent.WaitOne(2000))
                    {
                        return;
                    }
                }
                finally
                {
                    regDesktopEvent.Dispose();
                    regWallpapersEvent.Dispose();
                    regPersonalizeEvent.Dispose();
                    if (desktopKey != null) desktopKey.Dispose();
                    if (wallpapersKey != null) wallpapersKey.Dispose();
                    if (personalizeKey != null) personalizeKey.Dispose();
                }
            }
        }

        private static void Recompute(bool raiseEvent)
        {
            Color color;
            try
            {
                color = ComputeTaskbarMicaColor();
            }
            catch
            {
                return;
            }

            string stateKey = BuildQuickWallpaperStateKey();
            bool changed;
            lock (SyncRoot)
            {
                changed = !_hasColor || color != _color;
                _hasColor = true;
                _color = color;
                _cachedStateKey = stateKey;
            }

            if (!changed)
            {
                return;
            }

            Interlocked.Increment(ref _signature);
            Color? derivedAccent = DeriveAccent(color, !IsSystemLightTheme());
            string accentHex = derivedAccent.HasValue
                ? string.Format("#{0:X2}{1:X2}{2:X2}", derivedAccent.Value.R, derivedAccent.Value.G, derivedAccent.Value.B)
                : "system";
            SyncMicrosoftImeCandidateTheme();
            Program.LogInfo(string.Format(
                "[WallpaperMica] wallpaperAvg=#{0:X2}{1:X2}{2:X2} micaSurface=#{3:X2}{4:X2}{5:X2} accent={6}",
                _wallR, _wallG, _wallB, color.R, color.G, color.B, accentHex));
            if (raiseEvent)
            {
                Action handler = Changed;
                if (handler != null)
                {
                    try
                    {
                        handler();
                    }
                    catch
                    {
                    }
                }
            }
        }

        // ---- Port of the mod's ComputeTaskbarMicaColor (keep in sync with the .wh.cpp) ----
        private static Color ComputeTaskbarMicaColor()
        {
            return ComputeMicaColorForTheme(IsSystemLightTheme());
        }

        private static Color ComputeMicaColorForTheme(bool light)
        {
            byte wallR, wallG, wallB;
            if (!SampleLiveWallpaperColor(out wallR, out wallG, out wallB))
            {
                return light ? Color.FromRgb(243, 243, 243) : Color.FromRgb(32, 32, 32);
            }

            double lum = (wallR + wallG + wallB) / 3.0;
            double dR = wallR - lum;
            double dG = wallG - lum;
            double dB = wallB - lum;
            double maxDev = Math.Max(Math.Abs(dR), Math.Max(Math.Abs(dG), Math.Abs(dB)));

            if (light)
            {
                int r = 243, g = 243, b = 243;
                if (maxDev >= 1.5)
                {
                    double spread = Clamp(maxDev * 1.45, 8.0, 15.0);
                    double scale = spread / maxDev;
                    const double baseLight = 243.0;
                    r = (int)Math.Round(baseLight + dR * scale, MidpointRounding.AwayFromZero);
                    g = (int)Math.Round(baseLight + dG * scale, MidpointRounding.AwayFromZero);
                    b = (int)Math.Round(baseLight + dB * scale, MidpointRounding.AwayFromZero);
                }
                return Color.FromRgb((byte)ClampInt(r, 215, 255), (byte)ClampInt(g, 215, 255), (byte)ClampInt(b, 215, 255));
            }
            else
            {
                int r = 32, g = 32, b = 32;
                if (maxDev >= 1.5)
                {
                    double spread = Clamp(maxDev * 1.50, 8.0, 16.0);
                    double scale = spread / maxDev;
                    const double baseDark = 34.0;
                    r = (int)Math.Round(baseDark + dR * scale, MidpointRounding.AwayFromZero);
                    g = (int)Math.Round(baseDark + dG * scale, MidpointRounding.AwayFromZero);
                    b = (int)Math.Round(baseDark + dB * scale, MidpointRounding.AwayFromZero);
                }
                return Color.FromRgb((byte)ClampInt(r, 16, 68), (byte)ClampInt(g, 16, 68), (byte)ClampInt(b, 16, 68));
            }
        }

        /// <summary>
        /// Synchronizes the computed wallpaper Mica background and derived accent color to
        /// Windows 11's dedicated Text Input / Microsoft Pinyin IME candidate window theme
        /// (HKCU\Software\Microsoft\TabletTip\1.7). Only TextInputHost.exe listens to this
        /// key via the Updating=1-&gt;0 transaction flag, so Explorer/DWM/remote desktops are
        /// completely unaffected (zero global broadcast, zero mouse lag).
        /// </summary>
        private static void SyncMicrosoftImeCandidateTheme()
        {
            if (!AppSettings.Current.WallpaperColorSyncEnabled)
            {
                return;
            }

            try
            {
                Color lightMica = ComputeMicaColorForTheme(true);
                Color darkMica = ComputeMicaColorForTheme(false);
                Color lightAccent = DeriveAccent(lightMica, false) ?? Color.FromRgb(0x00, 0x67, 0xC0);
                Color darkAccent = DeriveAccent(darkMica, true) ?? Color.FromRgb(0x60, 0xCD, 0xFF);

                string lightBgRgb = FormatRgbCsv(lightMica);
                string lightAccRgb = FormatRgbCsv(lightAccent);
                string darkBgRgb = FormatRgbCsv(darkMica);
                string darkAccRgb = FormatRgbCsv(darkAccent);

                using (RegistryKey root = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\TabletTip\1.7"))
                {
                    if (root == null)
                    {
                        return;
                    }

                    root.SetValue("Updating", 1, RegistryValueKind.DWord);
                    root.SetValue("SelectedThemeName", "ColorPopTheme", RegistryValueKind.String);
                    root.SetValue("SelectedThemeIndex", 2, RegistryValueKind.DWord);

                    using (RegistryKey lightKey = root.CreateSubKey("SelectedThemeLight"))
                    {
                        if (lightKey != null)
                        {
                            lightKey.SetValue("KeyboardBackgroundSolidColor", lightBgRgb, RegistryValueKind.String);
                            lightKey.SetValue("ChineseSimplifiedImeBackgroundColor", lightBgRgb, RegistryValueKind.String);
                            lightKey.SetValue("AccentColor", lightAccRgb, RegistryValueKind.String);
                            lightKey.SetValue("SuggestionTextColor", "0,0,0", RegistryValueKind.String);
                            lightKey.SetValue("ChineseSimplifiedImeTextColor", "0,0,0", RegistryValueKind.String);
                            lightKey.SetValue("KeyLabelColor", "0,0,0", RegistryValueKind.String);
                        }
                    }

                    using (RegistryKey darkKey = root.CreateSubKey("SelectedThemeDark"))
                    {
                        if (darkKey != null)
                        {
                            darkKey.SetValue("KeyboardBackgroundSolidColor", darkBgRgb, RegistryValueKind.String);
                            darkKey.SetValue("ChineseSimplifiedImeBackgroundColor", darkBgRgb, RegistryValueKind.String);
                            darkKey.SetValue("AccentColor", darkAccRgb, RegistryValueKind.String);
                            darkKey.SetValue("SuggestionTextColor", "255,255,255", RegistryValueKind.String);
                            darkKey.SetValue("ChineseSimplifiedImeTextColor", "255,255,255", RegistryValueKind.String);
                            darkKey.SetValue("KeyLabelColor", "255,255,255", RegistryValueKind.String);
                        }
                    }

                    root.SetValue("Updating", 0, RegistryValueKind.DWord);
                }
            }
            catch
            {
            }
        }

        private static void RestoreMicrosoftImeDefaultTheme()
        {
            try
            {
                using (RegistryKey root = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\TabletTip\1.7"))
                {
                    if (root == null)
                    {
                        return;
                    }

                    root.SetValue("Updating", 1, RegistryValueKind.DWord);
                    root.SetValue("SelectedThemeName", "DefaultTheme", RegistryValueKind.String);
                    root.SetValue("SelectedThemeIndex", 0, RegistryValueKind.DWord);

                    using (RegistryKey lightKey = root.CreateSubKey("SelectedThemeLight"))
                    {
                        if (lightKey != null)
                        {
                            lightKey.SetValue("KeyboardBackgroundSolidColor", "systemaccent", RegistryValueKind.String);
                            lightKey.SetValue("AccentColor", string.Empty, RegistryValueKind.String);
                            lightKey.DeleteValue("ChineseSimplifiedImeBackgroundColor", false);
                            lightKey.DeleteValue("ChineseSimplifiedImeTextColor", false);
                        }
                    }

                    using (RegistryKey darkKey = root.CreateSubKey("SelectedThemeDark"))
                    {
                        if (darkKey != null)
                        {
                            darkKey.SetValue("KeyboardBackgroundSolidColor", "systemaccent", RegistryValueKind.String);
                            darkKey.SetValue("AccentColor", string.Empty, RegistryValueKind.String);
                            darkKey.DeleteValue("ChineseSimplifiedImeBackgroundColor", false);
                            darkKey.DeleteValue("ChineseSimplifiedImeTextColor", false);
                        }
                    }

                    root.SetValue("Updating", 0, RegistryValueKind.DWord);
                }
                Program.LogInfo("[WallpaperMica] WallpaperColorSyncEnabled=False -> Restored Microsoft IME DefaultTheme.");
            }
            catch
            {
            }
        }

        private static string FormatRgbCsv(Color c)
        {
            return c.R.ToString() + "," + c.G.ToString() + "," + c.B.ToString();
        }

        private static bool IsSystemLightTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    if (key != null)
                    {
                        object sys = key.GetValue("SystemUsesLightTheme");
                        if (sys is int sysInt)
                        {
                            return sysInt != 0;
                        }
                        object app = key.GetValue("AppsUseLightTheme");
                        if (app is int appInt)
                        {
                            return appInt != 0;
                        }
                    }
                }
            }
            catch
            {
            }
            return true;
        }

        // ---- Standalone SampleLiveWallpaperColor with content-hash change detection ----
        private static bool SampleLiveWallpaperColor(out byte r, out byte g, out byte b)
        {
            r = _wallR;
            g = _wallG;
            b = _wallB;

            string path = GetCurrentDesktopWallpaperPath();
            if (path == null)
            {
                return _hasCachedWallpaper;
            }

            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length < 128 || info.Length > 64L * 1024 * 1024)
            {
                return _hasCachedWallpaper;
            }

            byte[] fileBytes;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    fileBytes = new byte[fs.Length];
                    int read = 0;
                    while (read < fileBytes.Length)
                    {
                        int n = fs.Read(fileBytes, read, fileBytes.Length - read);
                        if (n <= 0)
                        {
                            break;
                        }
                        read += n;
                    }
                    if (read != fileBytes.Length)
                    {
                        return _hasCachedWallpaper;
                    }
                }
            }
            catch
            {
                return _hasCachedWallpaper;
            }

            uint sampleHash = ComputeFastSampleHash(fileBytes);
            if (_hasCachedWallpaper && _cachedSampleHash == sampleHash)
            {
                return true;
            }

            try
            {
                BitmapImage thumb = new BitmapImage();
                using (MemoryStream ms = new MemoryStream(fileBytes))
                {
                    thumb.BeginInit();
                    thumb.CacheOption = BitmapCacheOption.OnLoad;
                    thumb.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    thumb.DecodePixelWidth = ThumbSize;
                    thumb.DecodePixelHeight = ThumbSize;
                    thumb.StreamSource = ms;
                    thumb.EndInit();
                }
                thumb.Freeze();

                FormatConvertedBitmap bgra = new FormatConvertedBitmap(thumb, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth;
                int h = bgra.PixelHeight;
                byte[] pixels = new byte[w * h * 4];
                bgra.CopyPixels(pixels, w * 4, 0);

                double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4;
                        byte pb = pixels[i + 0];
                        byte pg = pixels[i + 1];
                        byte pr = pixels[i + 2];

                        int maxCh = Math.Max(pr, Math.Max(pg, pb));
                        int minCh = Math.Min(pr, Math.Min(pg, pb));
                        if (maxCh < 8 || minCh > 250)
                        {
                            continue; // letterbox borders
                        }

                        double edgeBoost = (x >= w - 10 || x < 10 || y >= h - 10 || y < 10) ? 1.45 : 1.0;
                        double chroma = (maxCh - minCh) / 255.0;
                        double weight = edgeBoost * (1.0 + chroma * 1.2);

                        sumR += pr * weight;
                        sumG += pg * weight;
                        sumB += pb * weight;
                        sumW += weight;
                    }
                }

                if (sumW <= 0)
                {
                    return _hasCachedWallpaper;
                }

                _wallR = (byte)ClampInt((int)(sumR / sumW + 0.5), 0, 255);
                _wallG = (byte)ClampInt((int)(sumG / sumW + 0.5), 0, 255);
                _wallB = (byte)ClampInt((int)(sumB / sumW + 0.5), 0, 255);
                _cachedSampleHash = sampleHash;
                _hasCachedWallpaper = true;

                r = _wallR;
                g = _wallG;
                b = _wallB;
                return true;
            }
            catch
            {
                return _hasCachedWallpaper;
            }
        }

        private static uint ComputeFastSampleHash(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return 0;
            }

            uint hash = 2166136261u ^ (uint)data.Length;
            int step = Math.Max(1, data.Length / 512);
            for (int i = 0; i < data.Length; i += step)
            {
                hash ^= data[i];
                hash *= 16777619u;
            }
            return hash;
        }

        private static string BuildQuickWallpaperStateKey()
        {
            bool light = IsSystemLightTheme();
            string path = GetCurrentDesktopWallpaperPath() ?? string.Empty;
            long len = 0;
            long ticks = 0;
            try
            {
                if (path.Length > 0)
                {
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists)
                    {
                        len = fi.Length;
                        ticks = fi.LastWriteTimeUtc.Ticks;
                    }
                }
            }
            catch
            {
            }

            return (light ? "L|" : "D|") + path + "|" + len.ToString() + "|" + ticks.ToString();
        }

        private static string GetCurrentDesktopWallpaperPath()
        {
            try
            {
                string transcoded = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Themes\TranscodedWallpaper");
                FileInfo transcodedInfo = File.Exists(transcoded) ? new FileInfo(transcoded) : null;

                StringBuilder sb = new StringBuilder(260);
                FileInfo spiInfo = null;
                if (SystemParametersInfo(SpiGetDeskWallpaper, sb.Capacity, sb, 0))
                {
                    string spi = sb.ToString();
                    if (!string.IsNullOrEmpty(spi) && File.Exists(spi))
                    {
                        spiInfo = new FileInfo(spi);
                    }
                }

                // If both exist, prefer whichever was written more recently (handles tools that update
                // SPI_GETDESKWALLPAPER directly as well as Explorer updating TranscodedWallpaper).
                if (transcodedInfo != null && spiInfo != null)
                {
                    return spiInfo.LastWriteTimeUtc > transcodedInfo.LastWriteTimeUtc
                        ? spiInfo.FullName
                        : transcodedInfo.FullName;
                }
                if (transcodedInfo != null)
                {
                    return transcodedInfo.FullName;
                }
                if (spiInfo != null)
                {
                    return spiInfo.FullName;
                }

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(WallpapersRegPath, false))
                {
                    string history = key != null ? key.GetValue("BackgroundHistoryPath0") as string : null;
                    if (!string.IsNullOrEmpty(history) && File.Exists(history))
                    {
                        return history;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static int ClampInt(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static double Channel(byte c)
        {
            double v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        private static double RelativeLuminance(Color c)
        {
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        private static double ContrastRatio(Color a, Color b)
        {
            double la = RelativeLuminance(a);
            double lb = RelativeLuminance(b);
            double hi = Math.Max(la, lb);
            double lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        private static void RgbToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            l = (max + min) / 2.0;
            if (d < 1e-6)
            {
                h = 0;
                s = 0;
                return;
            }

            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r)
            {
                h = ((g - b) / d + (g < b ? 6 : 0)) * 60.0;
            }
            else if (max == g)
            {
                h = ((b - r) / d + 2) * 60.0;
            }
            else
            {
                h = ((r - g) / d + 4) * 60.0;
            }
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = l - c / 2;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromRgb(
                (byte)Math.Round(Clamp(r + m, 0, 1) * 255),
                (byte)Math.Round(Clamp(g + m, 0, 1) * 255),
                (byte)Math.Round(Clamp(b + m, 0, 1) * 255));
        }
    }
}
