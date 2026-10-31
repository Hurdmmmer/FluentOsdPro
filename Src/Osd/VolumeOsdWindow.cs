using System;
using System.Collections.Generic;
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
    /// Windows 11 Fluent Acrylic bottom-center OSD flyout for volume stepping/mute and
    /// Ctrl+Volume audio output device switching.
    /// Uses off-screen pre-warming (-32000, -32000) + NativeWindowStyler DWM Acrylic +
    /// 120Hz SetWindowPos slide-in/slide-out so the first show never flashes a white frame.
    /// </summary>
    internal sealed class VolumeOsdWindow : Window
    {
        private enum WindowMotionPhase
        {
            Hidden,
            Entering,
            Visible,
            Exiting
        }

        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;
        private const int SwHide = 0;
        private const int SwShowNoActivate = 4;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint SwpNoSendChanging = 0x0400;
        private const uint SwpAsyncWindowPos = 0x4000;
        private const uint SwpShowWindow = 0x0040;
        private static readonly IntPtr HwndTopMost = new IntPtr(-1);

        private const double MinWindowWidthDip = 220.0;
        private const double MaxWindowWidthDip = 440.0;
        private const double VolumeBarWindowHeightDip = 72.0;
        private const double PaddingHorizontal = 16.0;
        private const double PaddingVertical = 11.0;
        private const double IconGlyphWidth = 18.0;
        private const double IconTextGap = 10.0;
        private const double TrackMinWidth = 136.0;
        private const double TrackRightGap = 10.0;
        private const double ValueBlockWidth = 32.0;
        private const double ResizeDurationSec = 0.22;
        private const double EnterDurationMs = 250.0;
        private const double ExitDurationMs = 83.0;
        private const double VisibleDurationMs = 1750.0;
        private const int MotionFrameIntervalMs = 8;

        private const string GlyphSpeakerMute = "\uE74F";
        private const string GlyphSpeaker0 = "\uE992";
        private const string GlyphSpeaker1 = "\uE993";
        private const string GlyphSpeaker2 = "\uE994";
        private const string GlyphSpeaker3 = "\uE995";
        private const string GlyphHeadphone = "\uE7F6";

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

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        private readonly Border _cardBorder;
        private readonly TextBlock _headerIconGlyph;
        private readonly TextBlock _titleBlock;
        private readonly Grid _volumeRow;
        private readonly Canvas _sliderCanvas;
        private readonly Border _sliderTrackBg;
        private readonly Border _sliderTrackFill;
        private readonly Grid _sliderThumb;
        private readonly Ellipse _sliderThumbOuter;
        private readonly Ellipse _sliderThumbInner;
        private readonly TextBlock _volumeValueBlock;
        private readonly DispatcherTimer _hideTimer;
        private readonly DispatcherTimer _motionTimer;

        private IntPtr _hwnd = IntPtr.Zero;
        private bool _prewarmed;
        private bool _allowClose;
        private bool _highPrecisionTimerEnabled;
        private bool? _lastAppliedDark;
        private bool? _lastAppliedAcrylic;

        private WindowMotionPhase _motionPhase = WindowMotionPhase.Hidden;
        private long _motionStartedAtTick;
        private double _motionDurationMs;
        private double _motionFromTopDip;
        private double _motionToTopDip;
        private double _motionFromOpacity;
        private double _motionToOpacity;

        private double _currentWidthDip = MinWindowWidthDip;
        private double _currentHeightDip = VolumeBarWindowHeightDip;
        private double _currentLeftDip;
        private double _currentTopDip;
        private double _finalTopDip;
        private double _startTopDip;
        private double _dpiX = 1.0;
        private double _dpiY = 1.0;

        private bool _sizeAnimRunning;
        private double _sizeAnimStartW;
        private double _sizeAnimTargetW;
        private double _sizeAnimCenterX;
        private long _sizeAnimStartTick;

        private int _currentVolumePercent = -1;
        private bool _currentMuted;

        public VolumeOsdWindow()
        {
            Title = "UULocalVolumeGuard_VolumeOSD";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Width = MinWindowWidthDip;
            Height = VolumeBarWindowHeightDip;
            Left = -32000;
            Top = -32000;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _cardBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(PaddingHorizontal, PaddingVertical, PaddingHorizontal, PaddingVertical),
                Background = Brushes.Transparent,
                SnapsToDevicePixels = true
            };

            Grid contentStack = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            contentStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid headerRow = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconGlyphWidth) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconTextGap) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _headerIconGlyph = new TextBlock
            {
                Text = GlyphSpeaker2,
                FontFamily = CreateIconFontFamily(),
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            Grid.SetColumn(_headerIconGlyph, 0);
            headerRow.Children.Add(_headerIconGlyph);

            FontFamily uiFont = CreateUiFontFamily();
            _titleBlock = new TextBlock
            {
                Text = string.Empty,
                FontFamily = uiFont,
                FontSize = 13.0,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = MaxWindowWidthDip - (PaddingHorizontal * 2.0) - IconGlyphWidth - IconTextGap
            };
            Grid.SetColumn(_titleBlock, 2);
            headerRow.Children.Add(_titleBlock);

            Grid.SetRow(headerRow, 0);
            contentStack.Children.Add(headerRow);

            _volumeRow = new Grid
            {
                Margin = new Thickness(0, 8, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TrackRightGap) });
            _volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ValueBlockWidth) });

            _sliderCanvas = new Canvas
            {
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = false
            };
            _sliderCanvas.SizeChanged += OnSliderCanvasSizeChanged;

            _sliderTrackBg = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(_sliderTrackBg, 0);
            Canvas.SetTop(_sliderTrackBg, 7);
            _sliderCanvas.Children.Add(_sliderTrackBg);

            _sliderTrackFill = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                SnapsToDevicePixels = true
            };
            Canvas.SetLeft(_sliderTrackFill, 0);
            Canvas.SetTop(_sliderTrackFill, 7);
            _sliderCanvas.Children.Add(_sliderTrackFill);

            _sliderThumb = new Grid
            {
                Width = 16,
                Height = 16
            };
            _sliderThumbOuter = new Ellipse
            {
                Width = 16,
                Height = 16,
                StrokeThickness = 1
            };
            _sliderThumbInner = new Ellipse
            {
                Width = 8,
                Height = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _sliderThumb.Children.Add(_sliderThumbOuter);
            _sliderThumb.Children.Add(_sliderThumbInner);
            Canvas.SetTop(_sliderThumb, 1);
            _sliderCanvas.Children.Add(_sliderThumb);

            Grid.SetColumn(_sliderCanvas, 0);
            _volumeRow.Children.Add(_sliderCanvas);

            _volumeValueBlock = new TextBlock
            {
                Text = "0",
                FontFamily = uiFont,
                FontSize = 13.0,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right
            };
            Grid.SetColumn(_volumeValueBlock, 2);
            _volumeRow.Children.Add(_volumeValueBlock);

            Grid.SetRow(_volumeRow, 1);
            contentStack.Children.Add(_volumeRow);

            _cardBorder.Child = contentStack;
            Content = _cardBorder;

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

        public bool TryGetVisibleDipBounds(out Rect bounds)
        {
            if (!IsCurrentlyVisible)
            {
                bounds = Rect.Empty;
                return false;
            }

            double top = _motionPhase == WindowMotionPhase.Entering ? _finalTopDip : _currentTopDip;
            bounds = new Rect(_currentLeftDip, top, _currentWidthDip, _currentHeightDip);
            return true;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;

            HwndSource source = HwndSource.FromHwnd(_hwnd);
            if (source?.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
            source?.AddHook(WindowMessageHook);

            ApplyTheme(force: true);
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmMouseActivate)
            {
                handled = true;
                return new IntPtr(MaNoActivate);
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
            StopResizeRendering();
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
                Program.LogError("VolumeOsdWindow.Prewarm failed", ex);
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
                ApplyTheme(force: false);
            }
        }

        public void ShowState(AudioVolumeController.VolumeState state)
        {
            if (state == null)
            {
                return;
            }

            if (!_prewarmed || _hwnd == IntPtr.Zero)
            {
                Prewarm();
            }

            ApplyTheme(force: false);

            bool hasVolume = state.VolumePercent >= 0;
            string deviceTitle = state.DeviceName ?? string.Empty;
            _titleBlock.Text = deviceTitle;

            int displayPct = hasVolume ? Math.Max(0, Math.Min(100, state.VolumePercent)) : 0;
            bool muted = state.IsMuted || (hasVolume && displayPct == 0);
            _headerIconGlyph.Text = PickHeaderGlyph(deviceTitle, displayPct, muted, hasVolume);

            if (hasVolume)
            {
                _volumeRow.Visibility = Visibility.Visible;
                _currentVolumePercent = displayPct;
                _currentMuted = muted;
                _volumeValueBlock.Text = displayPct.ToString(CultureInfo.InvariantCulture);
                _sliderTrackFill.Opacity = muted ? 0.42 : 1.0;
            }
            else
            {
                _volumeRow.Visibility = Visibility.Collapsed;
                _currentVolumePercent = -1;
            }

            double desiredWidthDip = ComputeDesiredWidthDip(deviceTitle, hasVolume);
            double desiredHeightDip = VolumeBarWindowHeightDip;

            OsdPlacement placement = OsdPlacementService.Calculate(
                this,
                desiredWidthDip,
                desiredHeightDip,
                OsdPosition.SameAsVolume,
                null);

            _dpiX = placement.DpiScaleX;
            _dpiY = placement.DpiScaleY;
            _finalTopDip = placement.FinalTop;
            _startTopDip = placement.StartTop;

            bool animate = AppSettings.Current.OsdAnimationEnabled;
            _hideTimer.Stop();

            if (!IsCurrentlyVisible)
            {
                StopResizeRendering();
                _currentWidthDip = desiredWidthDip;
                _currentHeightDip = desiredHeightDip;
                _currentLeftDip = placement.Left;
                Width = desiredWidthDip;
                Height = desiredHeightDip;
                UpdateLayout();
                UpdateSliderGeometry();

                if (!animate)
                {
                    StopMotionLoop();
                    ApplyWindowBounds(_currentLeftDip, _finalTopDip, _currentWidthDip, _currentHeightDip, 1.0, ensureShown: true);
                    _motionPhase = WindowMotionPhase.Visible;
                }
                else
                {
                    StartWindowMotion(
                        WindowMotionPhase.Entering,
                        _startTopDip,
                        _finalTopDip,
                        0.0,
                        1.0,
                        EnterDurationMs,
                        ensureShown: true);
                }
            }
            else if (_motionPhase == WindowMotionPhase.Exiting)
            {
                StopResizeRendering();
                _currentWidthDip = desiredWidthDip;
                _currentHeightDip = desiredHeightDip;
                _currentLeftDip = placement.Left;
                Width = desiredWidthDip;
                Height = desiredHeightDip;
                UpdateLayout();
                UpdateSliderGeometry();

                StartWindowMotion(
                    WindowMotionPhase.Entering,
                    _currentTopDip,
                    _finalTopDip,
                    Math.Max(0.05, Opacity),
                    1.0,
                    EnterDurationMs,
                    ensureShown: true);
            }
            else
            {
                // Already visible or entering: animate horizontal width smoothly if device name length changed
                UpdateSliderGeometry();
                double centerDip = placement.Left + (desiredWidthDip / 2.0);
                if (animate && Math.Abs(desiredWidthDip - _currentWidthDip) > 1.5)
                {
                    AnimateWidthTo(desiredWidthDip, centerDip);
                }
                else
                {
                    StopResizeRendering();
                    _currentWidthDip = desiredWidthDip;
                    _currentLeftDip = placement.Left;
                    Width = desiredWidthDip;
                    ApplyWindowBounds(_currentLeftDip, _currentTopDip, _currentWidthDip, _currentHeightDip, Opacity, ensureShown: false);
                }
            }

            Program.NotifyVolumeOsdVisibilityChanged();
            _hideTimer.Start();
        }

        private double ComputeDesiredWidthDip(string deviceTitle, bool hasVolume)
        {
            double textWidth = MeasureTextWidth(_titleBlock, deviceTitle);
            double headerNeeded = (PaddingHorizontal * 2.0) + IconGlyphWidth + IconTextGap + textWidth + 6.0;
            double sliderNeeded = hasVolume
                ? (PaddingHorizontal * 2.0) + TrackMinWidth + TrackRightGap + ValueBlockWidth
                : MinWindowWidthDip;

            double raw = Math.Max(headerNeeded, sliderNeeded);
            if (raw < MinWindowWidthDip) raw = MinWindowWidthDip;
            if (raw > MaxWindowWidthDip) raw = MaxWindowWidthDip;
            return Math.Ceiling(raw);
        }

        private double MeasureTextWidth(TextBlock block, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0.0;
            }

            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                Typeface tf = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
                FormattedText ft = new FormattedText(
                    text,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    tf,
                    block.FontSize,
                    Brushes.White,
                    dpi.PixelsPerDip > 0 ? dpi.PixelsPerDip : 1.0);
                return ft.WidthIncludingTrailingWhitespace;
            }
            catch
            {
                return text.Length * (block.FontSize * 0.62);
            }
        }

        private void OnSliderCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSliderGeometry();
        }

        private void UpdateSliderGeometry()
        {
            if (_currentVolumePercent < 0)
            {
                return;
            }

            double trackWidth = _sliderCanvas.ActualWidth;
            if (trackWidth <= 0 || double.IsNaN(trackWidth))
            {
                double innerW = Math.Max(MinWindowWidthDip, _currentWidthDip) - (PaddingHorizontal * 2.0);
                trackWidth = Math.Max(60.0, innerW - TrackRightGap - ValueBlockWidth);
            }

            double thumbSize = 16.0;
            double usable = Math.Max(0.0, trackWidth - thumbSize);
            double ratio = _currentMuted ? 0.0 : (_currentVolumePercent / 100.0);
            if (ratio < 0.0) ratio = 0.0;
            if (ratio > 1.0) ratio = 1.0;

            double thumbLeft = usable * ratio;
            double fillWidth = thumbLeft + (thumbSize * 0.5);
            if (fillWidth > trackWidth) fillWidth = trackWidth;

            _sliderTrackBg.Width = trackWidth;
            _sliderTrackFill.Width = Math.Max(0.0, fillWidth);
            Canvas.SetLeft(_sliderThumb, thumbLeft);
        }

        private void AnimateWidthTo(double targetWidthDip, double centerDip)
        {
            _sizeAnimStartW = _currentWidthDip;
            _sizeAnimTargetW = targetWidthDip;
            _sizeAnimCenterX = centerDip;
            _sizeAnimStartTick = Stopwatch.GetTimestamp();

            if (!_sizeAnimRunning)
            {
                _sizeAnimRunning = true;
                CompositionTarget.Rendering += OnResizeRendering;
            }
        }

        private void StopResizeRendering()
        {
            if (_sizeAnimRunning)
            {
                _sizeAnimRunning = false;
                CompositionTarget.Rendering -= OnResizeRendering;
            }
        }

        private void OnResizeRendering(object sender, EventArgs e)
        {
            if (!_sizeAnimRunning)
            {
                return;
            }

            double elapsed = (double)(Stopwatch.GetTimestamp() - _sizeAnimStartTick) / Stopwatch.Frequency;
            double t = elapsed / ResizeDurationSec;
            if (t >= 1.0)
            {
                StopResizeRendering();
                _currentWidthDip = _sizeAnimTargetW;
                _currentLeftDip = Math.Round(_sizeAnimCenterX - (_sizeAnimTargetW / 2.0));
                Width = _currentWidthDip;
                UpdateSliderGeometry();
                ApplyWindowBounds(_currentLeftDip, _currentTopDip, _currentWidthDip, _currentHeightDip, Opacity, ensureShown: false);
                return;
            }

            double inv = 1.0 - t;
            double eased = 1.0 - (inv * inv * inv);
            double curW = _sizeAnimStartW + ((_sizeAnimTargetW - _sizeAnimStartW) * eased);
            double curLeft = Math.Round(_sizeAnimCenterX - (curW / 2.0));

            _currentWidthDip = curW;
            _currentLeftDip = curLeft;
            Width = curW;
            UpdateSliderGeometry();
            ApplyWindowBounds(_currentLeftDip, _currentTopDip, _currentWidthDip, _currentHeightDip, Opacity, ensureShown: false);
        }

        private void OnHideTimerTick(object sender, EventArgs e)
        {
            _hideTimer.Stop();

            if (!AppSettings.Current.OsdAnimationEnabled)
            {
                CompleteHide();
                return;
            }

            StartWindowMotion(
                WindowMotionPhase.Exiting,
                _currentTopDip,
                _startTopDip,
                Opacity,
                0.0,
                ExitDurationMs,
                ensureShown: false);
        }

        private void StartWindowMotion(
            WindowMotionPhase phase,
            double fromTopDip,
            double toTopDip,
            double fromOpacity,
            double toOpacity,
            double durationMs,
            bool ensureShown)
        {
            _motionPhase = phase;
            _motionFromTopDip = fromTopDip;
            _motionToTopDip = toTopDip;
            _motionFromOpacity = fromOpacity;
            _motionToOpacity = toOpacity;
            _motionDurationMs = Math.Max(1.0, durationMs);
            _motionStartedAtTick = StopwatchTickMs();

            ApplyWindowBounds(_currentLeftDip, fromTopDip, _currentWidthDip, _currentHeightDip, fromOpacity, ensureShown);
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
            double nextOpacity = _motionFromOpacity + ((_motionToOpacity - _motionFromOpacity) * eased);
            ApplyWindowBounds(_currentLeftDip, nextTop, _currentWidthDip, _currentHeightDip, nextOpacity, ensureShown: false);

            if (progress < 1.0)
            {
                return;
            }

            if (_motionPhase == WindowMotionPhase.Entering)
            {
                _motionPhase = WindowMotionPhase.Visible;
                ApplyWindowBounds(_currentLeftDip, _motionToTopDip, _currentWidthDip, _currentHeightDip, 1.0, ensureShown: false);
                StopMotionLoop();
                return;
            }

            CompleteHide();
        }

        private void CompleteHide()
        {
            StopMotionLoop();
            StopResizeRendering();
            _motionPhase = WindowMotionPhase.Hidden;
            if (_hwnd != IntPtr.Zero)
            {
                ShowWindow(_hwnd, SwHide);
            }
            Program.NotifyVolumeOsdVisibilityChanged();
        }

        private void ApplyWindowBounds(double leftDip, double topDip, double widthDip, double heightDip, double opacity, bool ensureShown)
        {
            _currentLeftDip = leftDip;
            _currentTopDip = topDip;

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            int xPx = (int)Math.Round(leftDip * _dpiX);
            int yPx = (int)Math.Round(topDip * _dpiY);
            int wPx = Math.Max(1, (int)Math.Round(widthDip * _dpiX));
            int hPx = Math.Max(1, (int)Math.Round(heightDip * _dpiY));

            uint flags = SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging | SwpAsyncWindowPos |
                         (ensureShown ? SwpShowWindow : 0u);
            SetWindowPos(
                _hwnd,
                HwndTopMost,
                xPx,
                yPx,
                wPx,
                hPx,
                flags);

            if (ensureShown)
            {
                ShowWindow(_hwnd, SwShowNoActivate);
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

        private void ApplyTheme(bool force)
        {
            bool isDark = ThemeService.IsSystemDarkTheme();
            bool isLight = !isDark;
            bool enableAcrylic = ThemeService.IsSystemTransparencyEnabled();

            if (!force && _lastAppliedDark == isDark && _lastAppliedAcrylic == enableAcrylic)
            {
                return;
            }

            _lastAppliedDark = isDark;
            _lastAppliedAcrylic = enableAcrylic;

            Color primaryColor = isLight ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White;
            Color accentColor = ThemeService.GetAccentColor();
            Color trackRemainColor = isLight
                ? Color.FromArgb(0x22, 0, 0, 0)
                : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
            Color thumbOuterFill = isDark ? Color.FromRgb(0x45, 0x45, 0x45) : Colors.White;
            Color thumbOuterStroke = isDark
                ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x28, 0x00, 0x00, 0x00);

            SolidColorBrush primaryBrush = CreateFrozenBrush(primaryColor);
            SolidColorBrush accentBrush = CreateFrozenBrush(accentColor);

            _cardBorder.Background = enableAcrylic
                ? Brushes.Transparent
                : CreateFrozenBrush(isLight ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x2C, 0x2C, 0x2C));

            _headerIconGlyph.Foreground = primaryBrush;
            _titleBlock.Foreground = primaryBrush;
            _volumeValueBlock.Foreground = primaryBrush;
            _sliderTrackBg.Background = CreateFrozenBrush(trackRemainColor);
            _sliderTrackFill.Background = accentBrush;
            _sliderThumbOuter.Fill = CreateFrozenBrush(thumbOuterFill);
            _sliderThumbOuter.Stroke = CreateFrozenBrush(thumbOuterStroke);
            _sliderThumbInner.Fill = accentBrush;

            if (_hwnd != IntPtr.Zero)
            {
                NativeWindowStyler.ApplyOsdWindowStyle(_hwnd, isDark, enableAcrylic, clickThrough: false);
            }
        }

        private static Color GetAccentColor(bool isDark)
        {
            try
            {
                if (SystemParameters.WindowGlassBrush is SolidColorBrush glass)
                {
                    Color c = glass.Color;
                    if (isDark)
                    {
                        return Color.FromRgb(
                            (byte)Math.Min(255, c.R + 42),
                            (byte)Math.Min(255, c.G + 42),
                            (byte)Math.Min(255, c.B + 42));
                    }
                    return c;
                }
            }
            catch
            {
            }

            return isDark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0);
        }

        private static SolidColorBrush CreateFrozenBrush(Color color)
        {
            SolidColorBrush b = new SolidColorBrush(color);
            b.Freeze();
            return b;
        }

        private static string PickHeaderGlyph(string deviceTitle, int volumePct, bool muted, bool hasVolume)
        {
            if (muted)
            {
                return GlyphSpeakerMute;
            }

            if (!string.IsNullOrEmpty(deviceTitle))
            {
                string lower = deviceTitle.ToLowerInvariant();
                if (lower.Contains("headphone") ||
                    lower.Contains("headset") ||
                    lower.Contains("earphone") ||
                    lower.Contains("earbud") ||
                    lower.Contains("airpods") ||
                    lower.Contains("buds") ||
                    deviceTitle.Contains("耳机") ||
                    deviceTitle.Contains("耳麦") ||
                    deviceTitle.Contains("头戴"))
                {
                    return GlyphHeadphone;
                }
            }

            if (!hasVolume)
            {
                return GlyphSpeaker2;
            }
            if (volumePct <= 0)
            {
                return GlyphSpeakerMute;
            }
            if (volumePct < 33)
            {
                return GlyphSpeaker0;
            }
            if (volumePct < 66)
            {
                return GlyphSpeaker1;
            }
            return GlyphSpeaker2;
        }

        private static FontFamily CreateIconFontFamily()
        {
            try
            {
                foreach (FontFamily ff in Fonts.SystemFontFamilies)
                {
                    string src = ff.Source ?? string.Empty;
                    if (src.IndexOf("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return new FontFamily("Segoe Fluent Icons");
                    }
                }
            }
            catch
            {
            }
            return new FontFamily("Segoe MDL2 Assets");
        }

        private static FontFamily CreateUiFontFamily()
        {
            List<string> available = new List<string>();
            try
            {
                foreach (FontFamily ff in Fonts.SystemFontFamilies)
                {
                    string src = ff.Source;
                    if (!string.IsNullOrEmpty(src))
                    {
                        available.Add(src);
                    }
                }
            }
            catch
            {
            }

            string primaryEn = "Segoe UI";
            for (int i = 0; i < available.Count; i++)
            {
                if (string.Equals(available[i], "Segoe UI Variable Text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(available[i], "Segoe UI Variable Display", StringComparison.OrdinalIgnoreCase))
                {
                    primaryEn = available[i];
                    break;
                }
            }

            string primaryCjk = "Microsoft YaHei UI";
            string[] preferredCjk = new[]
            {
                "HarmonyOS Sans SC",
                "MiSans",
                "Source Han Sans SC",
                "Noto Sans CJK SC",
                "PingFang SC",
                "Microsoft YaHei UI"
            };
            for (int p = 0; p < preferredCjk.Length; p++)
            {
                for (int i = 0; i < available.Count; i++)
                {
                    if (string.Equals(available[i], preferredCjk[p], StringComparison.OrdinalIgnoreCase))
                    {
                        primaryCjk = available[i];
                        p = preferredCjk.Length;
                        break;
                    }
                }
            }

            return new FontFamily(primaryEn + ", " + primaryCjk + ", Segoe UI, Microsoft YaHei");
        }

        private static long StopwatchTickMs()
        {
            return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }

        private static double EvaluateCubicBezier(double x, double x1, double y1, double x2, double y2)
        {
            if (x <= 0.0) return 0.0;
            if (x >= 1.0) return 1.0;

            double t = x;
            for (int i = 0; i < 6; i++)
            {
                double currentX = SampleBezier(t, x1, x2) - x;
                double derivative = SampleBezierDerivative(t, x1, x2);
                if (Math.Abs(derivative) < 1e-6)
                {
                    break;
                }
                t -= currentX / derivative;
                if (t < 0.0) t = 0.0;
                else if (t > 1.0) t = 1.0;
            }

            return SampleBezier(t, y1, y2);
        }

        private static double SampleBezier(double t, double p1, double p2)
        {
            double inv = 1.0 - t;
            return (3.0 * inv * inv * t * p1) +
                   (3.0 * inv * t * t * p2) +
                   (t * t * t);
        }

        private static double SampleBezierDerivative(double t, double p1, double p2)
        {
            double inv = 1.0 - t;
            return (3.0 * inv * inv * p1) +
                   (6.0 * inv * t * (p2 - p1)) +
                   (3.0 * t * t * (1.0 - p2));
        }
    }
}
