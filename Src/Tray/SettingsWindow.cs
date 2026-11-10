using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FluentOsdPro
{
    /// <summary>
    /// Lightweight Fluent-styled Settings Window for configuring:
    /// - UU Remote / ZTE VDI local volume &amp; virtual desktop guard
    /// - Silent elevated logon auto-start
    /// - IME Status Flyout toggle
    /// - CapsLock Status Flyout toggle
    /// - 120Hz OSD animations toggle
    /// - IME &amp; CapsLock OSD position (SameAsVolume / TopLeft / TopRight / BottomLeft / BottomRight / TopCenter)
    /// - Live preview triggers for IME, CapsLock, and Volume OSDs
    /// </summary>
    internal sealed class SettingsWindow : Window
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        private CheckBox _guardToggle;
        private CheckBox _autoStartToggle;
        private CheckBox _imeToggle;
        private CheckBox _capsToggle;
        private CheckBox _wallpaperColorToggle;
        private Slider _transparencySlider;
        private TextBlock _transparencyValueText;
        private ComboBox _animStyleCombo;
        private ComboBox _positionCombo;
        private ComboBox _boundaryCombo;
        private ComboBox _languageCombo;
        private bool _suppressEvents;
        private bool _previewCapsToggleState = true;
        private int _previewImeCycleIndex;

        public SettingsWindow()
        {
            bool zh = ThemeService.IsSystemChinese();
            Title = zh ? "Fluent OSD Pro - 设置" : "Fluent OSD Pro - Settings";
            Width = 540;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ResizeMode = ResizeMode.CanMinimize;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            BuildUi();
            Loaded += (s, e) =>
            {
                CenterOnActiveMonitorAndBringToFront();
            };
        }

        public void CenterOnActiveMonitorAndBringToFront()
        {
            try
            {
                System.Drawing.Point pt = System.Windows.Forms.Cursor.Position;
                System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromPoint(pt);
                System.Drawing.Rectangle wa = screen.WorkingArea;

                double dpiX = 1.0;
                double dpiY = 1.0;
                PresentationSource source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    dpiX = source.CompositionTarget.TransformToDevice.M11;
                    dpiY = source.CompositionTarget.TransformToDevice.M22;
                }
                else
                {
                    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    {
                        dpiX = g.DpiX / 96.0;
                        dpiY = g.DpiY / 96.0;
                    }
                }

                double waLeft = wa.Left / dpiX;
                double waTop = wa.Top / dpiY;
                double waWidth = wa.Width / dpiX;
                double waHeight = wa.Height / dpiY;

                double w = ActualWidth > 0 ? ActualWidth : Width;
                double h = ActualHeight > 0 ? ActualHeight : 540;

                Left = waLeft + Math.Max(0, (waWidth - w) / 2.0);
                Top = waTop + Math.Max(0, (waHeight - h) / 2.0);

                Topmost = true;
                Activate();
                Focus();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Topmost = false;
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch
            {
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int dark = ThemeService.IsSystemDarkTheme() ? 1 : 0;
                    DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
                    if (Environment.OSVersion.Version.Build >= 22000)
                    {
                        int round = DwmwcpRound;
                        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
                    }
                }
            }
            catch
            {
            }
        }

        public void SyncFromCurrentSettings()
        {
            _suppressEvents = true;
            try
            {
                AppSettings s = AppSettings.Current;
                if (_guardToggle != null) _guardToggle.IsChecked = Program.IsGuardEnabled;
                if (_autoStartToggle != null) _autoStartToggle.IsChecked = AutoStartManager.IsAutoStartEnabled();
                if (_imeToggle != null) _imeToggle.IsChecked = s.ImeFlyoutEnabled;
                if (_capsToggle != null) _capsToggle.IsChecked = s.CapsLockFlyoutEnabled;
                if (_wallpaperColorToggle != null) _wallpaperColorToggle.IsChecked = s.WallpaperColorSyncEnabled;
                if (_transparencySlider != null) _transparencySlider.Value = s.OsdTransparency;
                if (_transparencyValueText != null) _transparencyValueText.Text = s.OsdTransparency.ToString() + "%";

                if (_animStyleCombo != null)
                {
                    for (int i = 0; i < _animStyleCombo.Items.Count; i++)
                    {
                        if (_animStyleCombo.Items[i] is ComboBoxItem item &&
                            item.Tag is OsdAnimationStyle style &&
                            style == s.StatusOsdAnimationStyle)
                        {
                            _animStyleCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }

                if (_positionCombo != null)
                {
                    for (int i = 0; i < _positionCombo.Items.Count; i++)
                    {
                        if (_positionCombo.Items[i] is ComboBoxItem item &&
                            item.Tag is OsdPosition pos &&
                            pos == s.StatusOsdPosition)
                        {
                            _positionCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }

                if (_boundaryCombo != null)
                {
                    for (int i = 0; i < _boundaryCombo.Items.Count; i++)
                    {
                        if (_boundaryCombo.Items[i] is ComboBoxItem item &&
                            item.Tag is DesktopBoundaryFeedbackStyle bStyle &&
                            bStyle == s.BoundaryFeedbackStyle)
                        {
                            _boundaryCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }

                if (_languageCombo != null)
                {
                    for (int i = 0; i < _languageCombo.Items.Count; i++)
                    {
                        if (_languageCombo.Items[i] is ComboBoxItem item &&
                            item.Tag is AppLanguage lang &&
                            lang == s.Language)
                        {
                            _languageCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void BuildUi()
        {
            bool isDark = ThemeService.IsSystemDarkTheme();
            bool zh = ThemeService.IsSystemChinese();

            Color windowBg = isDark ? Color.FromRgb(0x1E, 0x20, 0x24) : Color.FromRgb(0xF4, 0xF6, 0xF9);
            Color cardBg = isDark ? Color.FromRgb(0x28, 0x2B, 0x30) : Color.FromRgb(0xFF, 0xFF, 0xFF);
            Color cardBorder = isDark ? Color.FromRgb(0x38, 0x3C, 0x44) : Color.FromRgb(0xE2, 0xE6, 0xEE);
            Color primaryFg = isDark ? Color.FromRgb(0xF3, 0xF4, 0xF6) : Color.FromRgb(0x1B, 0x1D, 0x21);
            Color secondaryFg = isDark ? Color.FromRgb(0xA6, 0xAD, 0xBA) : Color.FromRgb(0x5E, 0x66, 0x73);
            Color accentColor = isDark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0);

            Background = new SolidColorBrush(windowBg);
            FontFamily uiFont = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
            FontFamily = uiFont;

            StackPanel root = new StackPanel
            {
                Margin = new Thickness(20, 18, 20, 20),
                Orientation = Orientation.Vertical
            };

            // Header section
            StackPanel header = new StackPanel
            {
                Margin = new Thickness(2, 0, 2, 14)
            };
            header.Children.Add(new TextBlock
            {
                Text = zh ? "Fluent OSD Pro 控制台" : "Fluent OSD Pro Settings",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryFg)
            });
            header.Children.Add(new TextBlock
            {
                Text = zh
                    ? "UU 远程 / 中兴 VDI 本地音量与虚拟桌面守护 · Fluent 输入法与大写锁定状态浮窗"
                    : "UU Remote / ZTE VDI Local Volume Guard · Fluent IME & CapsLock OSD Flyout",
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(secondaryFg)
            });
            root.Children.Add(header);

            // Section 1: Core Guard & Startup
            root.Children.Add(CreateSettingRowCard(
                zh ? "启用 UU 远程 / ZTE VDI 守护" : "Enable UU / ZTE VDI Guard",
                zh ? "拦截远程全屏下的音量键、Ctrl+滚轮切换音频输出设备、Win+Ctrl+方向键切本地桌面" : "Intercept volume keys, Ctrl+Volume output switch, and Win+Ctrl+Arrow virtual desktop switch",
                cardBg, cardBorder, primaryFg, secondaryFg,
                out _guardToggle,
                checkedChanged: val =>
                {
                    Program.IsGuardEnabled = val;
                    AppSettings.Current.GuardEnabled = val;
                    AppSettings.Current.Save();
                }));

            root.Children.Add(CreateSettingRowCard(
                zh ? "开机静默自动启动" : "Silent Auto-Start at Logon",
                zh ? "通过 Windows 计划任务以最高权限静默随系统启动（无 UAC 弹窗）" : "Run at logon with highest privileges via Task Scheduler without UAC prompts",
                cardBg, cardBorder, primaryFg, secondaryFg,
                out _autoStartToggle,
                checkedChanged: val =>
                {
                    AutoStartManager.SetAutoStart(val);
                }));

            // Section 2: IME & CapsLock OSD
            root.Children.Add(CreateSettingRowCard(
                zh ? "输入法状态提示浮窗 (IME Flyout)" : "IME Status Flyout",
                zh ? "切换中英文模式、日语平假名/片假名时显示 Windows 11 亚克力胶囊提示（切换窗口静默不打扰）" : "Show Fluent Acrylic capsule when switching Chinese/English/Japanese IME conversion modes",
                cardBg, cardBorder, primaryFg, secondaryFg,
                out _imeToggle,
                checkedChanged: val =>
                {
                    AppSettings.Current.ImeFlyoutEnabled = val;
                    AppSettings.Current.Save();
                    Program.EnsureKeyboardHookState("SettingsToggleImeFlyout");
                }));

            root.Children.Add(CreateSettingRowCard(
                zh ? "大写锁定状态提示浮窗 (CapsLock Flyout)" : "CapsLock Status Flyout",
                zh ? "按下 CapsLock 键时显示动态锁扣矢量动画与底部状态指示条" : "Show animated lock badge and state indicator pill when toggling Caps Lock",
                cardBg, cardBorder, primaryFg, secondaryFg,
                out _capsToggle,
                checkedChanged: val =>
                {
                    AppSettings.Current.CapsLockFlyoutEnabled = val;
                    AppSettings.Current.Save();
                    Program.EnsureKeyboardHookState("SettingsToggleCapsLock");
                }));

            root.Children.Add(CreateSettingRowCard(
                zh ? "壁纸与输入法动态取色同步 (Wallpaper & IME Color Sync)" : "Wallpaper & IME Dynamic Color Sync",
                zh ? "实时采样桌面壁纸云母底色与高对比强调色，同步至浮窗与系统默认微软拼音候选框（关闭即恢复默认）" : "Sync live wallpaper Mica surface and contrast accent to OSD flyouts and Microsoft IME candidate window",
                cardBg, cardBorder, primaryFg, secondaryFg,
                out _wallpaperColorToggle,
                checkedChanged: val =>
                {
                    AppSettings.Current.WallpaperColorSyncEnabled = val;
                    AppSettings.Current.Save();
                    TaskbarMicaSync.ApplySyncToggle(val);
                    Program.ShowImePreview(0);
                }));

            // Section 2.4: OSD Acrylic Transparency Slider (0% ~ 100%)
            Border transCard = new Border
            {
                Background = new SolidColorBrush(cardBg),
                BorderBrush = new SolidColorBrush(cardBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8)
            };

            Grid transGrid = new Grid();
            transGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            transGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel transTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            transTextStack.Children.Add(new TextBlock
            {
                Text = zh ? "浮窗亚克力背景透明度 (OSD Acrylic Transparency)" : "OSD Acrylic Transparency",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryFg)
            });
            transTextStack.Children.Add(new TextBlock
            {
                Text = zh ? "拖动滑块无级调节音量与输入法浮窗的背景通透度（0% 纯色不透明 ~ 100% 全透模糊，拖动时实时预览）" : "Adjust OSD background acrylic transparency (0% opaque ~ 100% full blur) with live preview",
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(secondaryFg)
            });
            Grid.SetColumn(transTextStack, 0);
            transGrid.Children.Add(transTextStack);

            StackPanel sliderRightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            _transparencySlider = CreateFluentSlider(isDark, accentColor, 168);
            _transparencyValueText = new TextBlock
            {
                Width = 42,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryFg),
                Margin = new Thickness(6, 0, 0, 0),
                Text = AppSettings.Current.OsdTransparency.ToString() + "%"
            };

            _transparencySlider.ValueChanged += (s, e) =>
            {
                int val = (int)Math.Round(e.NewValue);
                if (_transparencyValueText != null)
                {
                    _transparencyValueText.Text = val.ToString() + "%";
                }
                if (_suppressEvents) return;
                if (AppSettings.Current.OsdTransparency != val)
                {
                    AppSettings.Current.OsdTransparency = val;
                    AppSettings.Current.Save();
                    TaskbarMicaSync.NotifyTransparencyChanged();
                    Program.ShowImePreview(0);
                }
            };

            sliderRightPanel.Children.Add(_transparencySlider);
            sliderRightPanel.Children.Add(_transparencyValueText);
            Grid.SetColumn(sliderRightPanel, 1);
            transGrid.Children.Add(sliderRightPanel);
            transCard.Child = transGrid;
            root.Children.Add(transCard);

            // Section 2.5: OSD Animation Style Selector (5 presets)
            Border animCard = new Border
            {
                Background = new SolidColorBrush(cardBg),
                BorderBrush = new SolidColorBrush(cardBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };

            Grid animGrid = new Grid();
            animGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            animGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel animTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            animTextStack.Children.Add(new TextBlock
            {
                Text = zh ? "浮窗切换与出入场动画风格" : "OSD Switching & Motion Style",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryFg)
            });
            animTextStack.Children.Add(new TextBlock
            {
                Text = zh ? "支持 5 种微交互预设（灵动回弹 / 丝滑柔光 / 极速微推 / 灵动岛脉冲 / 无动画），选择后自动预览切换效果" : "Choose from 5 micro-interaction presets with instant switching preview",
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(secondaryFg)
            });
            Grid.SetColumn(animTextStack, 0);
            animGrid.Children.Add(animTextStack);

            _animStyleCombo = CreateThemedComboBox(isDark, 216);
            AddAnimStyleOption(_animStyleCombo, OsdAnimationStyle.SpringPop, zh ? "灵动回弹 (InputSourcePro)" : "Spring Pop (InputSourcePro)", isDark);
            AddAnimStyleOption(_animStyleCombo, OsdAnimationStyle.SmoothFade, zh ? "丝滑柔光 (Fluent 2 原地渐变)" : "Smooth Fade (Fluent 2)", isDark);
            AddAnimStyleOption(_animStyleCombo, OsdAnimationStyle.CrispSlide, zh ? "极速微推 (Linear 120Hz)" : "Crisp Slide (Linear 120Hz)", isDark);
            AddAnimStyleOption(_animStyleCombo, OsdAnimationStyle.PulseMorph, zh ? "呼吸脉冲 (灵动岛微张)" : "Pulse Morph (Dynamic Island)", isDark);
            AddAnimStyleOption(_animStyleCombo, OsdAnimationStyle.None, zh ? "极速直出 (无动画)" : "Instant (No Animation)", isDark);

            _animStyleCombo.SelectionChanged += async (s, e) =>
            {
                if (_suppressEvents) return;
                if (_animStyleCombo.SelectedItem is ComboBoxItem item && item.Tag is OsdAnimationStyle style)
                {
                    AppSettings.Current.StatusOsdAnimationStyle = style;
                    AppSettings.Current.Save();
                    // Show a two-step live switch preview (English -> 简体拼音) so the user immediately sees the in-place switching animation!
                    Program.ShowImePreview(1);
                    await System.Threading.Tasks.Task.Delay(260);
                    Program.ShowImePreview(0);
                }
            };

            Grid.SetColumn(_animStyleCombo, 1);
            animGrid.Children.Add(_animStyleCombo);
            animCard.Child = animGrid;
            root.Children.Add(animCard);

            // Section 3: Status OSD Position Selector
            Border posCard = new Border
            {
                Background = new SolidColorBrush(cardBg),
                BorderBrush = new SolidColorBrush(cardBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };

            Grid posGrid = new Grid();
            posGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            posGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel posTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            posTextStack.Children.Add(new TextBlock
            {
                Text = zh ? "输入法 / 大写锁定浮窗位置" : "IME & CapsLock Flyout Position",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryFg)
            });
            posTextStack.Children.Add(new TextBlock
            {
                Text = zh ? "音量浮窗固定在底部居中；当输入法与音量同位置时会自动向上堆叠避免重叠" : "Volume OSD stays bottom-center; Same-As-Volume stacks cleanly above Volume OSD",
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(secondaryFg)
            });
            Grid.SetColumn(posTextStack, 0);
            posGrid.Children.Add(posTextStack);

            _positionCombo = CreateThemedComboBox(isDark, 216);
            AddPositionOption(_positionCombo, OsdPosition.FollowCursor, zh ? "跟随输入光标 / 鼠标 (InputSourcePro)" : "Follow Caret / Cursor (InputSourcePro)", isDark);
            AddPositionOption(_positionCombo, OsdPosition.SameAsVolume, zh ? "与音量位置相同 (底部居中)" : "Same as Volume (Bottom Center)", isDark);
            AddPositionOption(_positionCombo, OsdPosition.TopLeft, zh ? "屏幕左上角 (Top-Left)" : "Top Left", isDark);
            AddPositionOption(_positionCombo, OsdPosition.TopRight, zh ? "屏幕右上角 (Top-Right)" : "Top Right", isDark);
            AddPositionOption(_positionCombo, OsdPosition.BottomLeft, zh ? "屏幕左下角 (Bottom-Left)" : "Bottom Left", isDark);
            AddPositionOption(_positionCombo, OsdPosition.BottomRight, zh ? "屏幕右下角 (Bottom-Right)" : "Bottom Right", isDark);
            AddPositionOption(_positionCombo, OsdPosition.TopCenter, zh ? "屏幕顶部居中 (Top-Center)" : "Top Center", isDark);

            _positionCombo.SelectionChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                if (_positionCombo.SelectedItem is ComboBoxItem item && item.Tag is OsdPosition pos)
                {
                    AppSettings.Current.StatusOsdPosition = pos;
                    AppSettings.Current.Save();
                    Program.ShowImePreview(0);
                }
            };

            Grid.SetColumn(_positionCombo, 1);
            posGrid.Children.Add(_positionCombo);
            posCard.Child = posGrid;
            root.Children.Add(posCard);

            // Section 3.2: Virtual Desktop Boundary Feedback Selector
            Border boundaryCard = new Border
            {
                Background = new SolidColorBrush(cardBg),
                BorderBrush = new SolidColorBrush(cardBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };

            Grid boundaryGrid = new Grid();
            boundaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            boundaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel boundaryTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            boundaryTextStack.Children.Add(new TextBlock
            {
                Text = zh ? "虚拟桌面第一屏 / 末屏边界提示" : "Virtual Desktop Boundary Feedback",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryFg)
            });
            boundaryTextStack.Children.Add(new TextBlock
            {
                Text = zh ? "在第一屏继续向左或末屏继续向右切屏时触发全屏物理回弹提示（选择后自动预览）" : "Full-screen physical bounce or shake when switching past the first or last virtual desktop",
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(secondaryFg)
            });
            Grid.SetColumn(boundaryTextStack, 0);
            boundaryGrid.Children.Add(boundaryTextStack);

            _boundaryCombo = CreateThemedComboBox(isDark, 216);
            _boundaryCombo.Items.Add(CreateThemedComboBoxItem(zh ? "橡皮筋弹性回弹 (macOS)" : "Rubber-Band Bounce (macOS)", DesktopBoundaryFeedbackStyle.RubberBandBounce, isDark));
            _boundaryCombo.Items.Add(CreateThemedComboBoxItem(zh ? "左右阻尼摇头抖动" : "Horizontal Head-Shake", DesktopBoundaryFeedbackStyle.HorizontalShake, isDark));
            _boundaryCombo.Items.Add(CreateThemedComboBoxItem(zh ? "物理碰撞微震" : "Physical Impact Thud", DesktopBoundaryFeedbackStyle.ImpactThud, isDark));
            _boundaryCombo.Items.Add(CreateThemedComboBoxItem(zh ? "关闭边界提示" : "Off (Disabled)", DesktopBoundaryFeedbackStyle.None, isDark));

            _boundaryCombo.SelectionChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                if (_boundaryCombo.SelectedItem is ComboBoxItem item && item.Tag is DesktopBoundaryFeedbackStyle bStyle)
                {
                    AppSettings.Current.BoundaryFeedbackStyle = bStyle;
                    AppSettings.Current.Save();
                    Program.EnsureKeyboardHookState("SettingsChangeBoundaryFeedback");
                    if (bStyle != DesktopBoundaryFeedbackStyle.None)
                    {
                        DesktopBoundaryBounceService.Trigger(isLeftEdge: true, bStyle);
                    }
                }
            };

            Grid.SetColumn(_boundaryCombo, 1);
            boundaryGrid.Children.Add(_boundaryCombo);
            boundaryCard.Child = boundaryGrid;
            root.Children.Add(boundaryCard);

            // Section 3.5: UI & OSD Language Selector (Auto / 简体中文 / English)
            Border langCard = new Border
            {
                Background = new SolidColorBrush(cardBg),
                BorderBrush = new SolidColorBrush(cardBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };

            Grid langGrid = new Grid();
            langGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            langGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel langTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            langTextStack.Children.Add(new TextBlock
            {
                Text = zh ? "界面与提示词语言 (Language)" : "UI & Flyout Language",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryFg)
            });
            langTextStack.Children.Add(new TextBlock
            {
                Text = zh ? "支持跟随系统语言或手动指定中英文（实时生效于设置面板、托盘菜单与状态胶囊）" : "Follow Windows UI culture or manually switch between Chinese and English",
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(secondaryFg)
            });
            Grid.SetColumn(langTextStack, 0);
            langGrid.Children.Add(langTextStack);

            _languageCombo = CreateThemedComboBox(isDark, 216);
            _languageCombo.Items.Add(CreateThemedComboBoxItem(zh ? "跟随系统语言 (Auto)" : "Follow System (Auto)", AppLanguage.Auto, isDark));
            _languageCombo.Items.Add(CreateThemedComboBoxItem("简体中文 (Chinese)", AppLanguage.Chinese, isDark));
            _languageCombo.Items.Add(CreateThemedComboBoxItem("English (英文)", AppLanguage.English, isDark));

            _languageCombo.SelectionChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                if (_languageCombo.SelectedItem is ComboBoxItem item && item.Tag is AppLanguage lang)
                {
                    AppSettings.Current.Language = lang;
                    AppSettings.Current.Save();
                    bool nowZh = ThemeService.IsSystemChinese();
                    Title = nowZh ? "Fluent OSD Pro - 设置" : "Fluent OSD Pro - Settings";
                    BuildUi();
                    Program.ShowImePreview(0);
                }
            };

            Grid.SetColumn(_languageCombo, 1);
            langGrid.Children.Add(_languageCombo);
            langCard.Child = langGrid;
            root.Children.Add(langCard);

            // Section 4: Live Preview Buttons
            Grid previewRow = new Grid
            {
                Margin = new Thickness(0, 6, 0, 0)
            };
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Button btnPreviewIme = CreateActionButton(
                zh ? "预览输入法浮窗" : "Preview IME OSD",
                isDark, cardBg, cardBorder, accentColor,
                () =>
                {
                    _previewImeCycleIndex = (_previewImeCycleIndex + 1) % 3;
                    Program.ShowImePreview(_previewImeCycleIndex);
                });
            Grid.SetColumn(btnPreviewIme, 0);
            previewRow.Children.Add(btnPreviewIme);

            Button btnPreviewCaps = CreateActionButton(
                zh ? "预览大写锁定" : "Preview CapsLock",
                isDark, cardBg, cardBorder, accentColor,
                () =>
                {
                    Program.ShowCapsLockPreview(_previewCapsToggleState);
                    _previewCapsToggleState = !_previewCapsToggleState;
                });
            Grid.SetColumn(btnPreviewCaps, 2);
            previewRow.Children.Add(btnPreviewCaps);

            Button btnPreviewVol = CreateActionButton(
                zh ? "预览音量浮窗" : "Preview Volume OSD",
                isDark, cardBg, cardBorder, accentColor,
                () =>
                {
                    Program.ShowVolumePreview();
                });
            Grid.SetColumn(btnPreviewVol, 4);
            previewRow.Children.Add(btnPreviewVol);

            root.Children.Add(previewRow);
            Content = root;

            SyncFromCurrentSettings();
        }

        private static ComboBox CreateThemedComboBox(bool isDark, double width)
        {
            Color bgNormal = isDark ? Color.FromRgb(0x32, 0x36, 0x40) : Color.FromRgb(0xF8, 0xFA, 0xFC);
            Color bgHover = isDark ? Color.FromRgb(0x3B, 0x40, 0x4C) : Color.FromRgb(0xEE, 0xF2, 0xF7);
            Color borderCol = isDark ? Color.FromRgb(0x47, 0x4D, 0x5B) : Color.FromRgb(0xD0, 0xD7, 0xE2);
            Color fgCol = isDark ? Color.FromRgb(0xF3, 0xF4, 0xF6) : Color.FromRgb(0x1B, 0x1D, 0x21);
            Color arrowCol = isDark ? Color.FromRgb(0xA6, 0xAD, 0xBA) : Color.FromRgb(0x5E, 0x66, 0x73);
            Color popupBg = isDark ? Color.FromRgb(0x2B, 0x2E, 0x36) : Color.FromRgb(0xFF, 0xFF, 0xFF);

            SolidColorBrush bgNormalBrush = new SolidColorBrush(bgNormal);
            SolidColorBrush bgHoverBrush = new SolidColorBrush(bgHover);
            SolidColorBrush borderBrush = new SolidColorBrush(borderCol);
            SolidColorBrush fgBrush = new SolidColorBrush(fgCol);
            SolidColorBrush arrowBrush = new SolidColorBrush(arrowCol);
            SolidColorBrush popupBgBrush = new SolidColorBrush(popupBg);

            ControlTemplate toggleTemplate = new ControlTemplate(typeof(ToggleButton));
            FrameworkElementFactory toggleBorder = new FrameworkElementFactory(typeof(Border), "ToggleBorder");
            toggleBorder.SetValue(Border.BackgroundProperty, bgNormalBrush);
            toggleBorder.SetValue(Border.BorderBrushProperty, borderBrush);
            toggleBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            toggleBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

            FrameworkElementFactory toggleGrid = new FrameworkElementFactory(typeof(Grid));
            FrameworkElementFactory col0 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            FrameworkElementFactory col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col1.SetValue(ColumnDefinition.WidthProperty, new GridLength(28));
            toggleGrid.AppendChild(col0);
            toggleGrid.AppendChild(col1);

            FrameworkElementFactory arrowText = new FrameworkElementFactory(typeof(TextBlock));
            arrowText.SetValue(TextBlock.TextProperty, "\uE70D");
            arrowText.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
            arrowText.SetValue(TextBlock.FontSizeProperty, 10.5);
            arrowText.SetValue(TextBlock.ForegroundProperty, arrowBrush);
            arrowText.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrowText.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            arrowText.SetValue(Grid.ColumnProperty, 1);
            toggleGrid.AppendChild(arrowText);

            toggleBorder.AppendChild(toggleGrid);
            toggleTemplate.VisualTree = toggleBorder;

            Trigger hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, bgHoverBrush, "ToggleBorder"));
            toggleTemplate.Triggers.Add(hoverTrigger);

            ControlTemplate comboTemplate = new ControlTemplate(typeof(ComboBox));
            FrameworkElementFactory rootGrid = new FrameworkElementFactory(typeof(Grid));

            FrameworkElementFactory toggleBtn = new FrameworkElementFactory(typeof(ToggleButton), "ToggleButton");
            toggleBtn.SetValue(Control.TemplateProperty, toggleTemplate);
            toggleBtn.SetValue(UIElement.FocusableProperty, false);
            toggleBtn.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
            toggleBtn.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
            {
                Mode = BindingMode.TwoWay,
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            rootGrid.AppendChild(toggleBtn);

            FrameworkElementFactory contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter), "ContentSite");
            contentPresenter.SetValue(ContentPresenter.IsHitTestVisibleProperty, false);
            contentPresenter.SetBinding(ContentPresenter.ContentProperty, new Binding("SelectionBoxItem")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            contentPresenter.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding("SelectionBoxItemTemplate")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            contentPresenter.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 28, 0));
            contentPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            rootGrid.AppendChild(contentPresenter);

            FrameworkElementFactory popup = new FrameworkElementFactory(typeof(Popup), "Popup");
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.FocusableProperty, false);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
            popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });

            FrameworkElementFactory dropDownBorder = new FrameworkElementFactory(typeof(Border), "DropDownBorder");
            dropDownBorder.SetValue(Border.BackgroundProperty, popupBgBrush);
            dropDownBorder.SetValue(Border.BorderBrushProperty, borderBrush);
            dropDownBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            dropDownBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            dropDownBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
            dropDownBorder.SetValue(Border.PaddingProperty, new Thickness(4));
            dropDownBorder.SetValue(FrameworkElement.MinWidthProperty, width);

            FrameworkElementFactory scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
            scrollViewer.SetValue(ScrollViewer.CanContentScrollProperty, true);
            FrameworkElementFactory itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            scrollViewer.AppendChild(itemsPresenter);
            dropDownBorder.AppendChild(scrollViewer);
            popup.AppendChild(dropDownBorder);
            rootGrid.AppendChild(popup);

            comboTemplate.VisualTree = rootGrid;

            return new ComboBox
            {
                Width = width,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12.2,
                Foreground = fgBrush,
                Background = bgNormalBrush,
                BorderBrush = borderBrush,
                Cursor = Cursors.Hand,
                Template = comboTemplate
            };
        }

        private static ComboBoxItem CreateThemedComboBoxItem(string label, object tag, bool isDark)
        {
            Color fgCol = isDark ? Color.FromRgb(0xF3, 0xF4, 0xF6) : Color.FromRgb(0x1B, 0x1D, 0x21);
            Color hoverBg = isDark ? Color.FromRgb(0x3A, 0x3F, 0x4B) : Color.FromRgb(0xEC, 0xF0, 0xF6);
            Color selBg = isDark ? Color.FromRgb(0x28, 0x52, 0x7A) : Color.FromRgb(0xD8, 0xE8, 0xF8);

            SolidColorBrush fgBrush = new SolidColorBrush(fgCol);
            SolidColorBrush hoverBrush = new SolidColorBrush(hoverBg);
            SolidColorBrush selBrush = new SolidColorBrush(selBg);

            ControlTemplate itemTemplate = new ControlTemplate(typeof(ComboBoxItem));
            FrameworkElementFactory itemBorder = new FrameworkElementFactory(typeof(Border), "ItemBorder");
            itemBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            itemBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            itemBorder.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
            itemBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1));

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            itemBorder.AppendChild(cp);
            itemTemplate.VisualTree = itemBorder;

            Trigger hoverTrig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrig.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush, "ItemBorder"));
            itemTemplate.Triggers.Add(hoverTrig);

            Trigger selTrig = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selTrig.Setters.Add(new Setter(Border.BackgroundProperty, selBrush, "ItemBorder"));
            itemTemplate.Triggers.Add(selTrig);

            return new ComboBoxItem
            {
                Content = label,
                Tag = tag,
                Foreground = fgBrush,
                Cursor = Cursors.Hand,
                Template = itemTemplate
            };
        }

        private static void AddPositionOption(ComboBox combo, OsdPosition pos, string label, bool isDark)
        {
            combo.Items.Add(CreateThemedComboBoxItem(label, pos, isDark));
        }

        private static void AddAnimStyleOption(ComboBox combo, OsdAnimationStyle style, string label, bool isDark)
        {
            combo.Items.Add(CreateThemedComboBoxItem(label, style, isDark));
        }

        private Border CreateSettingRowCard(
            string title,
            string subtitle,
            Color cardBg,
            Color cardBorder,
            Color primaryFg,
            Color secondaryFg,
            out CheckBox toggleBox,
            Action<bool> checkedChanged)
        {
            bool isDark = ThemeService.IsSystemDarkTheme();
            Border card = new Border
            {
                Background = new SolidColorBrush(cardBg),
                BorderBrush = new SolidColorBrush(cardBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8)
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            textStack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryFg)
            });
            textStack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(secondaryFg)
            });
            Grid.SetColumn(textStack, 0);
            grid.Children.Add(textStack);

            CheckBox cb = CreateFluentToggleSwitch(isDark);
            cb.Checked += (s, e) =>
            {
                if (!_suppressEvents) checkedChanged?.Invoke(true);
            };
            cb.Unchecked += (s, e) =>
            {
                if (!_suppressEvents) checkedChanged?.Invoke(false);
            };

            Grid.SetColumn(cb, 1);
            grid.Children.Add(cb);

            toggleBox = cb;
            card.Child = grid;
            return card;
        }

        private static Slider CreateFluentSlider(bool isDark, Color accentColor, double width)
        {
            string accentHex = string.Format("#{0:X2}{1:X2}{2:X2}", accentColor.R, accentColor.G, accentColor.B);
            string trackRemainHex = isDark ? "#45FFFFFF" : "#28000000";
            string thumbOuterHex = isDark ? "#FF383C44" : "#FFFFFFFF";
            string thumbStrokeHex = isDark ? "#55FFFFFF" : "#30000000";

            string xaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>" +
                "<Grid Height='24' Background='Transparent'>" +
                "<Track x:Name='PART_Track'>" +
                "<Track.DecreaseRepeatButton>" +
                "<RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>" +
                "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
                "<Border Height='4' CornerRadius='2' VerticalAlignment='Center' Background='" + accentHex + "'/>" +
                "</ControlTemplate></RepeatButton.Template>" +
                "</RepeatButton>" +
                "</Track.DecreaseRepeatButton>" +
                "<Track.IncreaseRepeatButton>" +
                "<RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>" +
                "<RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>" +
                "<Border Height='4' CornerRadius='2' VerticalAlignment='Center' Background='" + trackRemainHex + "'/>" +
                "</ControlTemplate></RepeatButton.Template>" +
                "</RepeatButton>" +
                "</Track.IncreaseRepeatButton>" +
                "<Track.Thumb>" +
                "<Thumb Focusable='False'>" +
                "<Thumb.Template><ControlTemplate TargetType='Thumb'>" +
                "<Grid Width='16' Height='16'>" +
                "<Ellipse Fill='" + thumbOuterHex + "' Stroke='" + thumbStrokeHex + "' StrokeThickness='1'/>" +
                "<Ellipse x:Name='InnerDot' Width='8.5' Height='8.5' Fill='" + accentHex + "' HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "</Grid>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'>" +
                "<Setter TargetName='InnerDot' Property='Width' Value='10.5'/>" +
                "<Setter TargetName='InnerDot' Property='Height' Value='10.5'/>" +
                "</Trigger>" +
                "</ControlTemplate.Triggers>" +
                "</ControlTemplate></Thumb.Template>" +
                "</Thumb>" +
                "</Track.Thumb>" +
                "</Track>" +
                "</Grid>" +
                "</ControlTemplate>";

            ControlTemplate sliderTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);

            return new Slider
            {
                Width = width,
                Minimum = 0,
                Maximum = 100,
                SmallChange = 1,
                LargeChange = 10,
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Template = sliderTemplate
            };
        }

        private static CheckBox CreateFluentToggleSwitch(bool isDark)
        {
            SolidColorBrush offTrack = new SolidColorBrush(isDark ? Color.FromRgb(0x32, 0x36, 0x40) : Color.FromRgb(0xEC, 0xF0, 0xF5));
            SolidColorBrush offBorder = new SolidColorBrush(isDark ? Color.FromRgb(0x66, 0x6E, 0x7E) : Color.FromRgb(0xA0, 0xAA, 0xB8));
            SolidColorBrush offThumb = new SolidColorBrush(isDark ? Color.FromRgb(0xC6, 0xCD, 0xDA) : Color.FromRgb(0x5A, 0x62, 0x70));

            SolidColorBrush onTrack = new SolidColorBrush(isDark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0));
            SolidColorBrush onThumb = new SolidColorBrush(isDark ? Color.FromRgb(0x14, 0x18, 0x1E) : Color.FromRgb(0xFF, 0xFF, 0xFF));

            ControlTemplate switchTemplate = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory trackBorder = new FrameworkElementFactory(typeof(Border), "Track");
            trackBorder.SetValue(FrameworkElement.WidthProperty, 40.0);
            trackBorder.SetValue(FrameworkElement.HeightProperty, 20.0);
            trackBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            trackBorder.SetValue(Border.BackgroundProperty, offTrack);
            trackBorder.SetValue(Border.BorderBrushProperty, offBorder);
            trackBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));

            FrameworkElementFactory thumb = new FrameworkElementFactory(typeof(Ellipse), "Thumb");
            thumb.SetValue(FrameworkElement.WidthProperty, 12.0);
            thumb.SetValue(FrameworkElement.HeightProperty, 12.0);
            thumb.SetValue(Shape.FillProperty, offThumb);
            thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            thumb.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 0, 0));

            trackBorder.AppendChild(thumb);
            switchTemplate.VisualTree = trackBorder;

            Trigger checkedTrig = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedTrig.Setters.Add(new Setter(Border.BackgroundProperty, onTrack, "Track"));
            checkedTrig.Setters.Add(new Setter(Border.BorderBrushProperty, onTrack, "Track"));
            checkedTrig.Setters.Add(new Setter(Shape.FillProperty, onThumb, "Thumb"));
            checkedTrig.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "Thumb"));
            checkedTrig.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 3, 0), "Thumb"));
            switchTemplate.Triggers.Add(checkedTrig);

            return new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Template = switchTemplate
            };
        }

        private static Button CreateActionButton(
            string text,
            bool isDark,
            Color cardBg,
            Color cardBorder,
            Color accentFg,
            Action onClick)
        {
            SolidColorBrush normalBg = new SolidColorBrush(cardBg);
            SolidColorBrush hoverBg = new SolidColorBrush(isDark ? Color.FromRgb(0x34, 0x38, 0x42) : Color.FromRgb(0xEC, 0xF0, 0xF6));
            SolidColorBrush pressBg = new SolidColorBrush(isDark ? Color.FromRgb(0x23, 0x26, 0x2C) : Color.FromRgb(0xE0, 0xE6, 0xF0));
            SolidColorBrush borderBrush = new SolidColorBrush(cardBorder);
            SolidColorBrush fgBrush = new SolidColorBrush(accentFg);

            ControlTemplate btnTemplate = new ControlTemplate(typeof(Button));
            FrameworkElementFactory btnBorder = new FrameworkElementFactory(typeof(Border), "BtnBorder");
            btnBorder.SetValue(Border.BackgroundProperty, normalBg);
            btnBorder.SetValue(Border.BorderBrushProperty, borderBrush);
            btnBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            btnBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            btnBorder.AppendChild(cp);
            btnTemplate.VisualTree = btnBorder;

            Trigger hoverTrig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrig.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "BtnBorder"));
            btnTemplate.Triggers.Add(hoverTrig);

            Trigger pressTrig = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressTrig.Setters.Add(new Setter(Border.BackgroundProperty, pressBg, "BtnBorder"));
            btnTemplate.Triggers.Add(pressTrig);

            Button btn = new Button
            {
                Content = text,
                Height = 34,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = fgBrush,
                Cursor = Cursors.Hand,
                Template = btnTemplate
            };
            btn.Click += (s, e) => onClick?.Invoke();
            return btn;
        }
    }
}
