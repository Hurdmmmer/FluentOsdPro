using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FluentOsdPro
{
    /// <summary>
    /// Windows 11 HardwareConfirmator / CapsLockFlyout style OSD window for CapsLock and Input Method (IME) switches.
    /// Uses native DWM Acrylic (AllowsTransparency = false + WCA_ACCENT_POLICY + DWMWCP_ROUND) and
    /// SystemParameters.WindowGlassColor for the bottom state indicator pill.
    /// </summary>
    internal sealed class StatusOsdWindow : Window
    {
        private enum OsdVisualMode
        {
            None,
            CapsLock,
            InputMethod
        }

        private enum WindowMotionPhase
        {
            Hidden,
            Entering,
            Visible,
            Exiting
        }

        private const int WmMouseActivate = 0x0021;
        private const int WmEraseBkgnd = 0x0014;
        private const int WmDisplayChange = 0x007E;
        private const int WmPowerBroadcast = 0x0218;
        private const int WmSettingChange = 0x001A;
        private const int WmThemeChanged = 0x031A;
        private const int WmDwmCompositionChanged = 0x031E;
        private const int WmDwmColorizationColorChanged = 0x0320;
        private const int GclpHbrBackground = -10;
        private const int MaNoActivate = 3;
        private const int SwHide = 0;
        private const int SwShowNoActivate = 4;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint SwpNoSendChanging = 0x0400;
        private const uint SwpAsyncWindowPos = 0x4000;
        private const uint SwpShowWindow = 0x0040;
        private static readonly IntPtr HwndTopMost = new IntPtr(-1);

        private const double InitialFlyoutWidthDip = 84.0;
        private const double MinimumFlyoutWidthDip = 64.0;
        private const double MaximumFlyoutWidthDip = 210.0;
        private const double HorizontalChromeDip = 33.0;
        private const double WindowHeightDip = 26.0;
        private const double EnterDurationMs = 130.0;
        private const double ExitDurationMs = 65.0;
        private const double VisibleDurationMs = 1100.0;
        private const int MotionFrameIntervalMs = 8;

        private static readonly Duration StateAnimationDuration =
            new Duration(TimeSpan.FromMilliseconds(167));

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
        private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
        private static extern uint SetClassLong32(IntPtr hWnd, int nIndex, uint dwNewLong);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private static void ClearWindowClassBackgroundBrush(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                if (IntPtr.Size == 8)
                {
                    SetClassLongPtr64(hwnd, GclpHbrBackground, IntPtr.Zero);
                }
                else
                {
                    SetClassLong32(hwnd, GclpHbrBackground, 0);
                }
            }
            catch
            {
            }
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        private readonly DispatcherTimer _hideTimer;
        private readonly DispatcherTimer _motionTimer;

        private IntPtr _hwnd = IntPtr.Zero;
        private HwndSource _hwndSource;
        private bool _prewarmed;
        private bool _allowClose;
        private bool _highPrecisionTimerEnabled;

        private OsdVisualMode _lastVisualMode = OsdVisualMode.None;
        private bool? _lastStateEnabled;
        private string _lastBadgeGlyph;
        private string _lastTitleText;
        private string _lastDetailText;
        private double _flyoutWidthDip = InitialFlyoutWidthDip;
        private bool? _lastAppliedDark;
        private bool? _lastAppliedAcrylic;
        private SolidColorBrush _activeForegroundBrush;
        private SolidColorBrush _dimmedForegroundBrush;
        private SolidColorBrush _activeAccentBrush;
        private SolidColorBrush _neutralBadgeBgBrush;
        private SolidColorBrush _activeBadgeFgBrush;

        private WindowMotionPhase _motionPhase = WindowMotionPhase.Hidden;
        private long _motionStartedAtTick;
        private double _motionDurationMs;
        private double _motionFromTopDip;
        private double _motionToTopDip;
        private double _currentLeftDip;
        private double _currentTopDip;
        private double _currentDpiX = 1.0;
        private double _currentDpiY = 1.0;
        private OsdPlacement _activePlacement;

        private Border _surface;
        private Grid _outerGrid;
        private ScaleTransform _contentScale;
        private Border _iconBadge;
        private TextBlock _iconText;
        private ScaleTransform _genericIconScale;
        private TranslateTransform _genericIconBounce;

        private Viewbox _lockSymbol;
        private TranslateTransform _shackleBounce;
        private Path _lockBodyPath;
        private Path _lockDotPath;
        private Path _lockShacklePath;
        private RotateTransform _shackleRotation;

        private TextBlock _titleText;
        private TranslateTransform _titleMotion;
        private static StatusOsdWindow _activeInstance;

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32BoundsRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out Win32BoundsRect lpRect);

        public static bool TryGetVisibleScreenBounds(out int left, out int top, out int right, out int bottom)
        {
            left = 0;
            top = 0;
            right = 0;
            bottom = 0;
            StatusOsdWindow inst = _activeInstance;
            if (inst == null || !inst.IsCurrentlyVisible || inst._hwnd == IntPtr.Zero)
            {
                return false;
            }

            if (GetWindowRect(inst._hwnd, out Win32BoundsRect rc) && rc.Right > rc.Left && rc.Bottom > rc.Top)
            {
                left = rc.Left;
                top = rc.Top;
                right = rc.Right;
                bottom = rc.Bottom;
                return true;
            }

            return false;
        }

        public StatusOsdWindow()
        {
            _activeInstance = this;
            Title = "FluentOsdPro_StatusOSD";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Width = InitialFlyoutWidthDip;
            Height = WindowHeightDip;
            Left = -32000;
            Top = -32000;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            BuildVisualTree();

            _hideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(VisibleDurationMs)
            };
            _hideTimer.Tick += OnHideTimerTick;

            _motionTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(MotionFrameIntervalMs)
            };
            _motionTimer.Tick += OnMotionTimerTick;
        }

        public bool IsCurrentlyVisible =>
            _motionPhase == WindowMotionPhase.Entering ||
            _motionPhase == WindowMotionPhase.Visible ||
            _motionPhase == WindowMotionPhase.Exiting;

        private void BuildVisualTree()
        {
            _surface = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(5, 3, 7, 3),
                SnapsToDevicePixels = true
            };

            _outerGrid = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            _contentScale = new ScaleTransform(1.0, 1.0);
            _outerGrid.RenderTransform = _contentScale;
            _outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            FontFamily cjkFont = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI, Microsoft YaHei");

            // Column 0: InputSourcePro IndicatorViewConfig.swift (.medium):
            // 16x16 badge, cornerRadius = 3, spacing = 5, filled with textColor and glyph in bgColor
            _iconBadge = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 5, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            _genericIconScale = new ScaleTransform(1.0, 1.0);
            _genericIconBounce = new TranslateTransform(0.0, 0.0);
            TransformGroup iconTransformGroup = new TransformGroup();
            iconTransformGroup.Children.Add(_genericIconScale);
            iconTransformGroup.Children.Add(_genericIconBounce);
            _iconBadge.RenderTransform = iconTransformGroup;

            Grid badgeInner = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _iconText = new TextBlock
            {
                Text = "中",
                FontFamily = cjkFont,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, -0.5, 0, 0)
            };

            _lockSymbol = new Viewbox
            {
                Width = 13.0,
                Height = 13.0,
                Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _shackleBounce = new TranslateTransform(0.0, 0.0);
            TransformGroup lockSymbolTransformGroup = new TransformGroup();
            lockSymbolTransformGroup.Children.Add(_shackleBounce);
            _lockSymbol.RenderTransform = lockSymbolTransformGroup;

            Canvas lockCanvas = new Canvas
            {
                Width = 24,
                Height = 24
            };
            _lockBodyPath = new Path
            {
                Data = Geometry.Parse("M7.25 7C5.45507 7 4 8.45507 4 10.25V17.75C4 19.5449 5.45507 21 7.25 21H16.75C18.5449 21 20 19.5449 20 17.75V10.25C20 8.45507 18.5449 7 16.75 7H7.25ZM5.5 10.25C5.5 9.2835 6.2835 8.5 7.25 8.5H16.75C17.7165 8.5 18.5 9.2835 18.5 10.25V17.75C18.5 18.7165 17.7165 19.5 16.75 19.5H7.25C6.2835 19.5 5.5 18.7165 5.5 17.75V10.25Z")
            };
            _lockDotPath = new Path
            {
                Data = Geometry.Parse("M12 15.5C12.8284 15.5 13.5 14.8284 13.5 14C13.5 13.1716 12.8284 12.5 12 12.5C11.1716 12.5 10.5 13.1716 10.5 14C10.5 14.8284 11.1716 15.5 12 15.5Z")
            };
            _lockShacklePath = new Path
            {
                Data = Geometry.Parse("M8 7V5C8 2.79086 9.79086 1 12 1C14.2091 1 16 2.79086 16 5V7.75H14.5V5C14.5 3.61929 13.3807 2.5 12 2.5C10.6193 2.5 9.5 3.61929 9.5 5V7H8Z")
            };
            _shackleRotation = new RotateTransform
            {
                CenterX = 16,
                CenterY = 7,
                Angle = 0
            };
            _lockShacklePath.RenderTransform = _shackleRotation;

            lockCanvas.Children.Add(_lockBodyPath);
            lockCanvas.Children.Add(_lockDotPath);
            lockCanvas.Children.Add(_lockShacklePath);
            _lockSymbol.Child = lockCanvas;

            badgeInner.Children.Add(_iconText);
            badgeInner.Children.Add(_lockSymbol);
            _iconBadge.Child = badgeInner;
            Grid.SetColumn(_iconBadge, 0);
            _outerGrid.Children.Add(_iconBadge);

            // Column 1: InputSourcePro IndicatorViewConfig.swift (.medium): fontSize = 12.6
            _titleText = new TextBlock
            {
                FontFamily = cjkFont,
                FontSize = 12.5,
                FontWeight = FontWeights.Normal,
                MaxWidth = 180,
                TextTrimming = TextTrimming.None,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            _titleMotion = new TranslateTransform(0, 0);
            _titleText.RenderTransform = _titleMotion;
            Grid.SetColumn(_titleText, 1);
            _outerGrid.Children.Add(_titleText);

            _surface.Child = _outerGrid;
            Content = _surface;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            ClearWindowClassBackgroundBrush(_hwnd);

            _hwndSource = HwndSource.FromHwnd(_hwnd);
            if (_hwndSource?.CompositionTarget != null)
            {
                _hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
            _hwndSource?.AddHook(WindowMessageHook);

            ApplyCurrentTheme(force: true);
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmMouseActivate)
            {
                handled = true;
                return new IntPtr(MaNoActivate);
            }

            if (msg == WmEraseBkgnd)
            {
                handled = true;
                return new IntPtr(1);
            }

            if (msg == WmDisplayChange ||
                msg == WmPowerBroadcast ||
                msg == WmSettingChange ||
                msg == WmThemeChanged ||
                msg == WmDwmCompositionChanged ||
                msg == WmDwmColorizationColorChanged)
            {
                _lastAppliedDark = null;
                _lastAppliedAcrylic = null;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_hwnd != IntPtr.Zero)
                    {
                        ApplyCurrentTheme(force: true);
                        InvalidateVisual();
                    }
                }), DispatcherPriority.Send);
            }

            return IntPtr.Zero;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                return;
            }

            _hideTimer.Stop();
            StopMotionLoop();
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WindowMessageHook);
                _hwndSource = null;
            }
            base.OnClosing(e);
        }

        public void Prewarm()
        {
            if (_prewarmed)
            {
                return;
            }

            try
            {
                Left = -32000;
                Top = -32000;
                Show();
                UpdateLayout();
                if (_hwnd != IntPtr.Zero)
                {
                    ShowWindow(_hwnd, SwHide);
                }
                _prewarmed = true;
            }
            catch (Exception ex)
            {
                Program.LogError("StatusOsdWindow.Prewarm failed", ex);
            }
        }

        public void CloseOsd()
        {
            _allowClose = true;
            try
            {
                Close();
            }
            catch
            {
            }
        }

        public void RefreshThemeIfNeeded()
        {
            if (_hwnd != IntPtr.Zero)
            {
                ApplyCurrentTheme();
            }
        }

        public void ShowCapsLock(bool isOn)
        {
            bool zh = ThemeService.IsSystemChinese();
            string label = isOn
                ? (zh ? "大写锁定 · 开启" : "Caps Lock · On")
                : (zh ? "大写锁定 · 关闭" : "Caps Lock · Off");
            string refLabel = zh ? "大写锁定 · 开启" : "Caps Lock · Off";

            PresentMessage(
                visualMode: OsdVisualMode.CapsLock,
                badgeGlyph: string.Empty,
                title: label,
                isEnabled: isOn,
                fixedWidthReference: refLabel);
        }

        public void ShowInputMethod(InputMethodSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            bool zh = ThemeService.IsSystemChinese();
            string badgeGlyph;
            string labelText;
            bool isNativeActive;

            if (snapshot.LanguageFamily == InputLanguageFamily.Japanese)
            {
                if (snapshot.ConversionMode == InputConversionModeKind.Katakana)
                {
                    badgeGlyph = "ア";
                    labelText = zh ? "日语片假名" : "Katakana";
                    isNativeActive = true;
                }
                else if (snapshot.ConversionMode == InputConversionModeKind.Hiragana)
                {
                    badgeGlyph = "あ";
                    labelText = zh ? "日语平假名" : "Hiragana";
                    isNativeActive = true;
                }
                else
                {
                    badgeGlyph = "A";
                    labelText = "English";
                    isNativeActive = false;
                }
            }
            else if (snapshot.LanguageFamily == InputLanguageFamily.Chinese &&
                     snapshot.ConversionMode == InputConversionModeKind.ChineseMode)
            {
                badgeGlyph = "中";
                labelText = zh ? "简体拼音" : "Chinese";
                isNativeActive = true;
            }
            else
            {
                badgeGlyph = "A";
                labelText = "English";
                isNativeActive = false;
            }

            string fixedRef = zh ? "简体拼音" : "Chinese";

            PresentMessage(
                visualMode: OsdVisualMode.InputMethod,
                badgeGlyph: badgeGlyph,
                title: labelText,
                isEnabled: isNativeActive,
                fixedWidthReference: fixedRef);
        }

        private void PresentMessage(
            OsdVisualMode visualMode,
            string badgeGlyph,
            string title,
            bool isEnabled,
            string fixedWidthReference)
        {
            EnsurePrewarmed();

            OsdAnimationStyle animStyle = AppSettings.Current.StatusOsdAnimationStyle;
            bool animate = animStyle != OsdAnimationStyle.None;
            bool wasVisible = IsCurrentlyVisible;

            _iconText.Text = badgeGlyph;
            bool isLock = visualMode == OsdVisualMode.CapsLock;
            _lockSymbol.Visibility = isLock ? Visibility.Visible : Visibility.Collapsed;
            _iconText.Visibility = isLock ? Visibility.Collapsed : Visibility.Visible;

            // Evaluate state & label transitions BEFORE overwriting _last* fields so in-place switching animations fire properly
            AnimateState(visualMode, isEnabled, badgeGlyph, animStyle, animate && wasVisible);
            _titleText.Text = title;
            AnimateLabels(visualMode, title, animStyle, animate && wasVisible);

            _lastVisualMode = visualMode;
            _lastStateEnabled = isEnabled;
            _lastBadgeGlyph = badgeGlyph;
            _lastTitleText = title;
            _lastDetailText = string.Empty;

            ApplyCurrentTheme(force: !wasVisible);
            UpdateFlyoutWidth(title, fixedWidthReference);

            Rect? stackAboveVolume = null;
            if (AppSettings.Current.StatusOsdPosition == OsdPosition.SameAsVolume &&
                Program.TryGetVisibleVolumeOsdDipBounds(out Rect volBounds))
            {
                stackAboveVolume = volBounds;
            }

            // Compute placement ONCE per PresentMessage call so pre-move and PresentOsdWindow share identical coordinates
            OsdPlacement placement = OsdPlacementService.Calculate(
                this,
                _flyoutWidthDip,
                WindowHeightDip,
                AppSettings.Current.StatusOsdPosition,
                stackAboveVolume);
            if (!wasVisible && _hwnd != IntPtr.Zero)
            {
                bool monitorChanged = Math.Abs(_currentDpiX - placement.DpiScaleX) > 0.01 ||
                                      Math.Abs(_currentLeftDip - placement.Left) > 40.0;
                _currentDpiX = placement.DpiScaleX;
                _currentDpiY = placement.DpiScaleY;
                if (monitorChanged)
                {
                    int preW = Math.Max(1, (int)Math.Round(_flyoutWidthDip * _currentDpiX));
                    int preH = Math.Max(1, (int)Math.Round(WindowHeightDip * _currentDpiY));
                    SetWindowPos(
                        _hwnd,
                        HwndTopMost,
                        placement.LeftPx,
                        placement.FinalTopPx,
                        preW,
                        preH,
                        SwpNoActivate | SwpNoOwnerZOrder);
                    UpdateLayout();
                }
            }

            PresentOsdWindow(placement, animate, animStyle);
        }

        private void EnsurePrewarmed()
        {
            if (!_prewarmed || _hwnd == IntPtr.Zero)
            {
                Prewarm();
            }
        }

        private void ApplyCurrentTheme(bool force = false)
        {
            bool isDark = ThemeService.IsSystemDarkTheme();
            bool isLight = !isDark;
            bool enableAcrylic = ThemeService.IsSystemTransparencyEnabled();

            // InputSourcePro IndicatorColor.swift + IndicatorViewConfig.swift:
            // Dark mode: bgColor = #141416, textColor = #FFFFFF
            // Light mode: bgColor = #FFFFFF (0.95), textColor = #141416
            // Leading 16x16 badge is filled with textColor, and the glyph inside uses bgColor!
            Color textColor = isLight ? Color.FromArgb(0xF5, 0x14, 0x14, 0x16) : Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF);
            Color dimmedTextColor = isLight ? Color.FromArgb(0xB8, 0x14, 0x14, 0x16) : Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF);
            Color badgeFillColor = isLight ? Color.FromArgb(0xEB, 0x18, 0x18, 0x1B) : Color.FromArgb(0xF2, 0xF5, 0xF6, 0xF8);
            Color badgeGlyphContrast = isLight ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x14, 0x14, 0x16);

            _activeForegroundBrush = CreateFrozenBrush(textColor);
            _dimmedForegroundBrush = CreateFrozenBrush(dimmedTextColor);
            _activeAccentBrush = CreateFrozenBrush(badgeFillColor);
            _neutralBadgeBgBrush = CreateFrozenBrush(badgeFillColor);
            _activeBadgeFgBrush = CreateFrozenBrush(badgeGlyphContrast);

            Foreground = _activeForegroundBrush;
            _titleText.Foreground = _activeForegroundBrush;

            bool currentEnabled = _lastStateEnabled ?? true;
            ApplyBadgeColors(currentEnabled);

            _surface.Background = enableAcrylic
                ? Brushes.Transparent
                : CreateFrozenBrush(isLight ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x16, 0x17, 0x1A));

            if (_hwnd != IntPtr.Zero &&
                (force ||
                 !_lastAppliedDark.HasValue || _lastAppliedDark.Value != isDark ||
                 !_lastAppliedAcrylic.HasValue || _lastAppliedAcrylic.Value != enableAcrylic))
            {
                _lastAppliedDark = isDark;
                _lastAppliedAcrylic = enableAcrylic;
                NativeWindowStyler.ApplyOsdWindowStyle(_hwnd, isDark, enableAcrylic, clickThrough: false);
            }
        }

        private void ApplyBadgeColors(bool isEnabled)
        {
            bool isLock = _lastVisualMode == OsdVisualMode.CapsLock;
            if (isLock)
            {
                _iconBadge.Background = Brushes.Transparent;
                _iconBadge.BorderThickness = new Thickness(0);
                SolidColorBrush lockBrush = isEnabled ? _activeForegroundBrush : _dimmedForegroundBrush;
                _lockBodyPath.Fill = lockBrush;
                _lockDotPath.Fill = lockBrush;
                _lockShacklePath.Fill = lockBrush;
            }
            else
            {
                // InputSourcePro IndicatorViewConfig.swift (getGlyphBadgeView / getTextImageView):
                // Solid textColor 16x16 rounded square (cornerRadius = 3) with inverted bgColor glyph inside
                _iconBadge.Background = _activeAccentBrush;
                _iconBadge.BorderThickness = new Thickness(0);
                _iconText.Foreground = _activeBadgeFgBrush;
            }
        }

        private void AnimateLabels(OsdVisualMode visualMode, string title, OsdAnimationStyle style, bool animate)
        {
            _titleMotion.BeginAnimation(TranslateTransform.XProperty, null);
            _titleMotion.BeginAnimation(TranslateTransform.YProperty, null);
            _titleText.BeginAnimation(UIElement.OpacityProperty, null);
            _titleMotion.X = 0.0;
            _titleMotion.Y = 0.0;
            _titleText.Opacity = 1.0;

            if (!animate || style == OsdAnimationStyle.None)
            {
                return;
            }

            bool changed = _lastVisualMode != visualMode ||
                           !string.Equals(_lastTitleText, title, StringComparison.Ordinal);
            if (!changed)
            {
                return;
            }

            switch (style)
            {
                case OsdAnimationStyle.SpringPop:
                    {
                        // InputSourcePro / macOS Sonoma style: horizontal ease-out from the badge (+0 Y, never dropping down!)
                        _titleMotion.BeginAnimation(
                            TranslateTransform.XProperty,
                            new DoubleAnimation(-2.2, 0.0, TimeSpan.FromMilliseconds(130))
                            {
                                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                            });
                        _titleText.BeginAnimation(
                            UIElement.OpacityProperty,
                            new DoubleAnimation(0.32, 1.0, TimeSpan.FromMilliseconds(110))
                            {
                                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                            });
                        break;
                    }

                case OsdAnimationStyle.SmoothFade:
                    {
                        // Fluent 2 optical cross-dissolve: zero X/Y positional movement
                        _titleText.BeginAnimation(
                            UIElement.OpacityProperty,
                            new DoubleAnimation(0.22, 1.0, TimeSpan.FromMilliseconds(120))
                            {
                                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                            });
                        break;
                    }

                case OsdAnimationStyle.CrispSlide:
                    {
                        // Linear / GitHub 120Hz style: ultra-fast horizontal micro-slide
                        _titleMotion.BeginAnimation(
                            TranslateTransform.XProperty,
                            new DoubleAnimation(-4.5, 0.0, TimeSpan.FromMilliseconds(100))
                            {
                                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
                            });
                        _titleText.BeginAnimation(
                            UIElement.OpacityProperty,
                            new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(80)));
                        break;
                    }

                case OsdAnimationStyle.PulseMorph:
                    {
                        // Dynamic Island style: subtle optical brighten while the outer capsule pulses outward
                        _titleText.BeginAnimation(
                            UIElement.OpacityProperty,
                            new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(115))
                            {
                                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                            });
                        break;
                    }
            }
        }

        private void AnimateState(
            OsdVisualMode visualMode,
            bool isEnabled,
            string badgeGlyph,
            OsdAnimationStyle style,
            bool animate)
        {
            ApplyBadgeColors(isEnabled);

            bool stateUnchanged = _lastVisualMode == visualMode &&
                                  _lastStateEnabled == isEnabled &&
                                  string.Equals(_lastBadgeGlyph, badgeGlyph, StringComparison.Ordinal);

            if (!animate || style == OsdAnimationStyle.None || stateUnchanged)
            {
                ResetStateAnimations();
                SetStateBaseValues(visualMode, isEnabled);
                return;
            }

            bool newVisual = _lastVisualMode != visualMode || !_lastStateEnabled.HasValue;
            if (newVisual)
            {
                ResetStateAnimations();
                SetStateBaseValues(visualMode, !isEnabled);
            }

            // Optional whole-capsule outward pulse for Dynamic Island style (expands outward 1.0 -> 1.045 -> 1.0, never shrinks!)
            _contentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _contentScale.ScaleX = 1.0;
            _contentScale.ScaleY = 1.0;

            if (style == OsdAnimationStyle.PulseMorph)
            {
                DoubleAnimationUsingKeyFrames pulseX = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(155)
                };
                pulseX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
                pulseX.KeyFrames.Add(new EasingDoubleKeyFrame(1.045, KeyTime.FromPercent(0.28), new SineEase { EasingMode = EasingMode.EaseOut }));
                pulseX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), new SineEase { EasingMode = EasingMode.EaseInOut }));

                DoubleAnimationUsingKeyFrames pulseY = new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(155)
                };
                pulseY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
                pulseY.KeyFrames.Add(new EasingDoubleKeyFrame(1.035, KeyTime.FromPercent(0.28), new SineEase { EasingMode = EasingMode.EaseOut }));
                pulseY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), new SineEase { EasingMode = EasingMode.EaseInOut }));

                _contentScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulseX, HandoffBehavior.SnapshotAndReplace);
                _contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulseY, HandoffBehavior.SnapshotAndReplace);
            }

            if (visualMode == OsdVisualMode.CapsLock)
            {
                IEasingFunction shackleEase = style == OsdAnimationStyle.SpringPop || style == OsdAnimationStyle.PulseMorph
                    ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.30 }
                    : new CubicEase { EasingMode = EasingMode.EaseOut };

                _shackleRotation.BeginAnimation(
                    RotateTransform.AngleProperty,
                    new DoubleAnimation
                    {
                        To = isEnabled ? 0.0 : 25.0,
                        Duration = TimeSpan.FromMilliseconds(145),
                        EasingFunction = shackleEase
                    },
                    HandoffBehavior.SnapshotAndReplace);

                // Subtle UPWARD lift (-1.0 -> 0.0), never downward
                _shackleBounce.BeginAnimation(
                    TranslateTransform.YProperty,
                    new DoubleAnimation(-1.0, 0.0, TimeSpan.FromMilliseconds(135))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    },
                    HandoffBehavior.SnapshotAndReplace);
                return;
            }

            // InputMethod badge micro-animations (strictly center-anchored, Y = 0, no downward drop)
            _genericIconBounce.BeginAnimation(TranslateTransform.XProperty, null);
            _genericIconBounce.BeginAnimation(TranslateTransform.YProperty, null);
            _genericIconBounce.X = 0.0;
            _genericIconBounce.Y = 0.0;
            _iconBadge.BeginAnimation(UIElement.OpacityProperty, null);
            _iconBadge.Opacity = 1.0;

            switch (style)
            {
                case OsdAnimationStyle.SpringPop:
                    {
                        BackEase springEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.42 };
                        TimeSpan dur = TimeSpan.FromMilliseconds(145);
                        _genericIconScale.BeginAnimation(
                            ScaleTransform.ScaleXProperty,
                            new DoubleAnimation(0.84, 1.0, dur) { EasingFunction = springEase },
                            HandoffBehavior.SnapshotAndReplace);
                        _genericIconScale.BeginAnimation(
                            ScaleTransform.ScaleYProperty,
                            new DoubleAnimation(0.84, 1.0, dur) { EasingFunction = springEase },
                            HandoffBehavior.SnapshotAndReplace);
                        break;
                    }

                case OsdAnimationStyle.SmoothFade:
                    {
                        SineEase sineOut = new SineEase { EasingMode = EasingMode.EaseOut };
                        TimeSpan dur = TimeSpan.FromMilliseconds(120);
                        _genericIconScale.BeginAnimation(
                            ScaleTransform.ScaleXProperty,
                            new DoubleAnimation(0.94, 1.0, dur) { EasingFunction = sineOut },
                            HandoffBehavior.SnapshotAndReplace);
                        _genericIconScale.BeginAnimation(
                            ScaleTransform.ScaleYProperty,
                            new DoubleAnimation(0.94, 1.0, dur) { EasingFunction = sineOut },
                            HandoffBehavior.SnapshotAndReplace);
                        _iconBadge.BeginAnimation(
                            UIElement.OpacityProperty,
                            new DoubleAnimation(0.45, 1.0, dur) { EasingFunction = sineOut },
                            HandoffBehavior.SnapshotAndReplace);
                        break;
                    }

                case OsdAnimationStyle.CrispSlide:
                    {
                        QuarticEase quartOut = new QuarticEase { EasingMode = EasingMode.EaseOut };
                        TimeSpan dur = TimeSpan.FromMilliseconds(95);
                        _genericIconScale.BeginAnimation(
                            ScaleTransform.ScaleXProperty,
                            new DoubleAnimation(0.86, 1.0, dur) { EasingFunction = quartOut },
                            HandoffBehavior.SnapshotAndReplace);
                        _genericIconScale.BeginAnimation(
                            ScaleTransform.ScaleYProperty,
                            new DoubleAnimation(0.92, 1.0, dur) { EasingFunction = quartOut },
                            HandoffBehavior.SnapshotAndReplace);
                        _genericIconBounce.BeginAnimation(
                            TranslateTransform.XProperty,
                            new DoubleAnimation(-1.5, 0.0, dur) { EasingFunction = quartOut },
                            HandoffBehavior.SnapshotAndReplace);
                        break;
                    }

                case OsdAnimationStyle.PulseMorph:
                    {
                        BackEase pulseEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.52 };
                        TimeSpan dur = TimeSpan.FromMilliseconds(155);
                        _genericIconScale.BeginAnimation(
                            ScaleTransform.ScaleXProperty,
                            new DoubleAnimation(0.80, 1.0, dur) { EasingFunction = pulseEase },
                            HandoffBehavior.SnapshotAndReplace);
                        _genericIconScale.BeginAnimation(
                            ScaleTransform.ScaleYProperty,
                            new DoubleAnimation(0.80, 1.0, dur) { EasingFunction = pulseEase },
                            HandoffBehavior.SnapshotAndReplace);
                        break;
                    }
            }
        }

        private void SetStateBaseValues(OsdVisualMode visualMode, bool isEnabled)
        {
            ApplyBadgeColors(isEnabled);
            if (visualMode == OsdVisualMode.CapsLock)
            {
                _shackleRotation.Angle = isEnabled ? 0.0 : 25.0;
                _shackleBounce.Y = 0.0;
            }
            else
            {
                _genericIconScale.ScaleX = 1.0;
                _genericIconScale.ScaleY = 1.0;
                _genericIconBounce.X = 0.0;
                _genericIconBounce.Y = 0.0;
                _iconBadge.Opacity = 1.0;
            }
        }

        private void ResetStateAnimations()
        {
            _shackleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _shackleBounce.BeginAnimation(TranslateTransform.YProperty, null);
            _genericIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _genericIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _genericIconBounce.BeginAnimation(TranslateTransform.XProperty, null);
            _genericIconBounce.BeginAnimation(TranslateTransform.YProperty, null);
            _iconBadge.BeginAnimation(UIElement.OpacityProperty, null);
            _contentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            _shackleRotation.Angle = 0.0;
            _shackleBounce.Y = 0.0;
            _genericIconScale.ScaleX = 1.0;
            _genericIconScale.ScaleY = 1.0;
            _genericIconBounce.X = 0.0;
            _genericIconBounce.Y = 0.0;
            _iconBadge.Opacity = 1.0;
            _contentScale.ScaleX = 1.0;
            _contentScale.ScaleY = 1.0;
        }

        private void UpdateFlyoutWidth(string title, string fixedWidthReference)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double pixelsPerDip = dpi.PixelsPerDip > 0 ? dpi.PixelsPerDip : 1.0;

            double titleWidth = MeasureTextWidth(_titleText, title, pixelsPerDip);
            // Lock capsule width using fixedWidthReference for BOTH InputMethod ("简体拼音" vs "English") and CapsLock,
            // preventing the capsule from shrinking horizontally ("一缩") when switching between Chinese and English!
            if (!string.IsNullOrEmpty(fixedWidthReference))
            {
                titleWidth = Math.Max(titleWidth, MeasureTextWidth(_titleText, fixedWidthReference, pixelsPerDip));
            }

            _flyoutWidthDip = Clamp(
                Math.Ceiling(HorizontalChromeDip + titleWidth + 3.0),
                MinimumFlyoutWidthDip,
                MaximumFlyoutWidthDip);
            Width = _flyoutWidthDip;
            Height = WindowHeightDip;
            UpdateLayout();
        }

        private static double MeasureTextWidth(TextBlock textBlock, string text, double pixelsPerDip)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0.0;
            }

            Typeface typeface = new Typeface(
                textBlock.FontFamily,
                textBlock.FontStyle,
                textBlock.FontWeight,
                textBlock.FontStretch);
            FormattedText formattedText = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                textBlock.FlowDirection,
                typeface,
                textBlock.FontSize,
                Brushes.Black,
                pixelsPerDip);

            TextBlock probe = new TextBlock
            {
                Text = text,
                FontFamily = textBlock.FontFamily,
                FontStyle = textBlock.FontStyle,
                FontWeight = textBlock.FontWeight,
                FontStretch = textBlock.FontStretch,
                FontSize = textBlock.FontSize,
                FlowDirection = textBlock.FlowDirection
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            return Math.Max(formattedText.WidthIncludingTrailingWhitespace, probe.DesiredSize.Width);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static SolidColorBrush CreateFrozenBrush(Color color)
        {
            SolidColorBrush b = new SolidColorBrush(color);
            b.Freeze();
            return b;
        }

        private static void SplitImeDisplayText(string displayText, out string languageText, out string detailText)
        {
            string[] parts = displayText.Split(new[] { " - " }, 2, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                languageText = parts[0];
                detailText = parts[1];
            }
            else
            {
                languageText = ThemeService.IsSystemChinese() ? "汉语" : "IME";
                detailText = displayText;
            }
        }

        private void PresentOsdWindow(OsdPlacement placement, bool animate, OsdAnimationStyle style)
        {
            _activePlacement = placement;

            _currentDpiX = _activePlacement.DpiScaleX;
            _currentDpiY = _activePlacement.DpiScaleY;
            _currentLeftDip = _activePlacement.Left;

            _hideTimer.Stop();

            if (!animate || style == OsdAnimationStyle.None)
            {
                StopMotionLoop();
                _outerGrid.BeginAnimation(UIElement.OpacityProperty, null);
                _outerGrid.Opacity = 1.0;
                ApplyWindowBounds(_currentLeftDip, _activePlacement.FinalTop, ensureShown: true);
                _motionPhase = WindowMotionPhase.Visible;
                _hideTimer.Start();
                return;
            }

            double enterMs = EnterDurationMs;
            double startTop = _activePlacement.StartTop;
            switch (style)
            {
                case OsdAnimationStyle.SpringPop:
                    enterMs = 130.0;
                    break;
                case OsdAnimationStyle.SmoothFade:
                    enterMs = 115.0;
                    startTop = _activePlacement.FinalTop + ((_activePlacement.StartTop - _activePlacement.FinalTop) * 0.35);
                    break;
                case OsdAnimationStyle.CrispSlide:
                    enterMs = 95.0;
                    break;
                case OsdAnimationStyle.PulseMorph:
                    enterMs = 145.0;
                    break;
            }

            if (_motionPhase == WindowMotionPhase.Hidden)
            {
                PlayEntranceContentAnimation(style, enterMs);
                StartWindowMotion(
                    WindowMotionPhase.Entering,
                    startTop,
                    _activePlacement.FinalTop,
                    enterMs,
                    ensureShown: true);
            }
            else if (_motionPhase == WindowMotionPhase.Exiting)
            {
                PlayEntranceContentAnimation(style, enterMs);
                StartWindowMotion(
                    WindowMotionPhase.Entering,
                    _currentTopDip,
                    _activePlacement.FinalTop,
                    enterMs,
                    ensureShown: true);
            }
            else
            {
                _outerGrid.BeginAnimation(UIElement.OpacityProperty, null);
                _outerGrid.Opacity = 1.0;
                _motionToTopDip = _activePlacement.FinalTop;
                if (_motionPhase == WindowMotionPhase.Visible)
                {
                    ApplyWindowBounds(_currentLeftDip, _activePlacement.FinalTop, ensureShown: false);
                }
            }

            _hideTimer.Start();
        }

        private void PlayEntranceContentAnimation(OsdAnimationStyle style, double enterMs)
        {
            _outerGrid.BeginAnimation(UIElement.OpacityProperty, null);
            _contentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            double fadeMs = Math.Max(65.0, enterMs * 0.78);
            _outerGrid.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(fadeMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });

            double fromScale = 0.94;
            IEasingFunction scaleEase = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (style == OsdAnimationStyle.SpringPop)
            {
                fromScale = 0.93;
                scaleEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
            }
            else if (style == OsdAnimationStyle.SmoothFade)
            {
                fromScale = 0.97;
                scaleEase = new SineEase { EasingMode = EasingMode.EaseOut };
            }
            else if (style == OsdAnimationStyle.PulseMorph)
            {
                fromScale = 0.89;
                scaleEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 };
            }

            TimeSpan dur = TimeSpan.FromMilliseconds(enterMs);
            _contentScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(fromScale, 1.0, dur) { EasingFunction = scaleEase });
            _contentScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(fromScale, 1.0, dur) { EasingFunction = scaleEase });
        }

        public void RepositionIfStackedAboveVolume()
        {
            if (!IsCurrentlyVisible || _motionPhase == WindowMotionPhase.Exiting)
            {
                return;
            }

            Rect? stackAboveVolume = null;
            if (AppSettings.Current.StatusOsdPosition == OsdPosition.SameAsVolume &&
                Program.TryGetVisibleVolumeOsdDipBounds(out Rect volBounds))
            {
                stackAboveVolume = volBounds;
            }

            _activePlacement = OsdPlacementService.Calculate(
                this,
                _flyoutWidthDip,
                WindowHeightDip,
                AppSettings.Current.StatusOsdPosition,
                stackAboveVolume);

            _currentDpiX = _activePlacement.DpiScaleX;
            _currentDpiY = _activePlacement.DpiScaleY;
            _currentLeftDip = _activePlacement.Left;
            _motionToTopDip = _activePlacement.FinalTop;

            if (_motionPhase == WindowMotionPhase.Visible)
            {
                ApplyWindowBounds(_currentLeftDip, _activePlacement.FinalTop, ensureShown: false);
            }
        }

        private void OnHideTimerTick(object sender, EventArgs e)
        {
            _hideTimer.Stop();

            OsdAnimationStyle style = AppSettings.Current.StatusOsdAnimationStyle;
            if (style == OsdAnimationStyle.None)
            {
                CompleteHide();
                return;
            }

            double exitMs = style == OsdAnimationStyle.CrispSlide ? 55.0 : ExitDurationMs;
            _outerGrid.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(exitMs))
                {
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn }
                });

            StartWindowMotion(
                WindowMotionPhase.Exiting,
                _currentTopDip,
                _activePlacement.StartTop,
                exitMs,
                ensureShown: false);
        }

        private void StartWindowMotion(
            WindowMotionPhase phase,
            double fromTopDip,
            double toTopDip,
            double durationMs,
            bool ensureShown)
        {
            _motionPhase = phase;
            _motionFromTopDip = fromTopDip;
            _motionToTopDip = toTopDip;
            _motionDurationMs = Math.Max(1.0, durationMs);
            _motionStartedAtTick = StopwatchTickMs();

            ApplyWindowBounds(_currentLeftDip, fromTopDip, ensureShown);
            StartMotionLoop();
        }

        private void OnMotionTimerTick(object sender, EventArgs e)
        {
            if (_motionPhase != WindowMotionPhase.Entering && _motionPhase != WindowMotionPhase.Exiting)
            {
                StopMotionLoop();
                return;
            }

            double elapsed = Math.Max(0.0, StopwatchTickMs() - _motionStartedAtTick);
            double progress = Math.Min(1.0, elapsed / _motionDurationMs);
            double eased = _motionPhase == WindowMotionPhase.Entering
                ? EvaluateCubicBezier(progress, 0.0, 0.0, 0.0, 1.0)
                : EvaluateCubicBezier(progress, 1.0, 0.0, 1.0, 1.0);

            double nextTop = _motionFromTopDip + ((_motionToTopDip - _motionFromTopDip) * eased);
            ApplyWindowBounds(_currentLeftDip, nextTop, ensureShown: false);

            if (progress < 1.0)
            {
                return;
            }

            if (_motionPhase == WindowMotionPhase.Entering)
            {
                _motionPhase = WindowMotionPhase.Visible;
                ApplyWindowBounds(_currentLeftDip, _motionToTopDip, ensureShown: false);
                StopMotionLoop();
                return;
            }

            CompleteHide();
        }

        private void CompleteHide()
        {
            StopMotionLoop();
            _motionPhase = WindowMotionPhase.Hidden;
            if (_hwnd != IntPtr.Zero)
            {
                ShowWindow(_hwnd, SwHide);
            }
        }

        private void ApplyWindowBounds(double leftDip, double topDip, bool ensureShown)
        {
            _currentLeftDip = leftDip;
            _currentTopDip = topDip;

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            Width = _flyoutWidthDip;
            Height = WindowHeightDip;

            int xPx = (int)Math.Round(leftDip * _currentDpiX);
            int yPx = (int)Math.Round(topDip * _currentDpiY);
            int wPx = Math.Max(1, (int)Math.Round(_flyoutWidthDip * _currentDpiX));
            int hPx = Math.Max(1, (int)Math.Round(WindowHeightDip * _currentDpiY));

            if (ensureShown)
            {
                bool wasHwndVisible = IsWindowVisible(_hwnd);
                if (!wasHwndVisible)
                {
                    SetWindowPos(_hwnd, HwndTopMost, -32000, -32000, wPx, hPx, SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
                    ShowWindow(_hwnd, SwShowNoActivate);
                    ApplyCurrentTheme(force: true);
                    InvalidateVisual();
                    UpdateLayout();
                    try
                    {
                        DwmFlush();
                    }
                    catch
                    {
                    }
                }
                else
                {
                    UpdateLayout();
                }

                SetWindowPos(_hwnd, HwndTopMost, xPx, yPx, wPx, hPx, SwpNoActivate | SwpNoOwnerZOrder);
            }
            else
            {
                SetWindowPos(_hwnd, HwndTopMost, xPx, yPx, wPx, hPx, SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
            }
        }

        private void StartMotionLoop()
        {
            if (!_highPrecisionTimerEnabled)
            {
                timeBeginPeriod(1);
                _highPrecisionTimerEnabled = true;
            }

            if (!_motionTimer.IsEnabled)
            {
                _motionTimer.Start();
            }
        }

        private void StopMotionLoop()
        {
            if (_motionTimer.IsEnabled)
            {
                _motionTimer.Stop();
            }

            if (_highPrecisionTimerEnabled)
            {
                timeEndPeriod(1);
                _highPrecisionTimerEnabled = false;
            }
        }

        private static long StopwatchTickMs()
        {
            return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }

        private static double EvaluateCubicBezier(double x, double x1, double y1, double x2, double y2)
        {
            if (x <= 0.0) return 0.0;
            if (x >= 1.0) return 1.0;

            double low = 0.0;
            double high = 1.0;
            for (int i = 0; i < 18; i++)
            {
                double parameter = (low + high) / 2.0;
                double curveX = BezierCoordinate(parameter, x1, x2);
                if (curveX < x)
                    low = parameter;
                else
                    high = parameter;
            }

            return BezierCoordinate((low + high) / 2.0, y1, y2);
        }

        private static double BezierCoordinate(double t, double control1, double control2)
        {
            double inv = 1.0 - t;
            return (3.0 * inv * inv * t * control1) +
                   (3.0 * inv * t * t * control2) +
                   (t * t * t);
        }
    }
}
