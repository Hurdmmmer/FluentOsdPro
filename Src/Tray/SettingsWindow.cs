using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

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
        private ComboBox _animStyleCombo;
        private ComboBox _positionCombo;
        private bool _suppressEvents;
        private bool _previewCapsToggleState = true;
        private int _previewImeCycleIndex;

        public SettingsWindow()
        {
            bool zh = ThemeService.IsSystemChinese();
            Title = zh ? "Fluent OSD Pro - 设置" : "Fluent OSD Pro - Settings";
            Width = 500;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanMinimize;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            BuildUi();
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

            _animStyleCombo = new ComboBox
            {
                Width = 178,
                Height = 30,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12.5
            };
            AddAnimStyleOption(_animStyleCombo, OsdAnimationStyle.SpringPop, zh ? "灵动回弹 (InputSourcePro)" : "Spring Pop (InputSourcePro)");
            AddAnimStyleOption(_animStyleCombo, OsdAnimationStyle.SmoothFade, zh ? "丝滑柔光 (Fluent 2 原地渐变)" : "Smooth Fade (Fluent 2)");
            AddAnimStyleOption(_animStyleCombo, OsdAnimationStyle.CrispSlide, zh ? "极速微推 (Linear 120Hz)" : "Crisp Slide (Linear 120Hz)");
            AddAnimStyleOption(_animStyleCombo, OsdAnimationStyle.PulseMorph, zh ? "呼吸脉冲 (灵动岛微张)" : "Pulse Morph (Dynamic Island)");
            AddAnimStyleOption(_animStyleCombo, OsdAnimationStyle.None, zh ? "极速直出 (无动画)" : "Instant (No Animation)");

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

            _positionCombo = new ComboBox
            {
                Width = 178,
                Height = 30,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 12.5
            };
            AddPositionOption(_positionCombo, OsdPosition.FollowCursor, zh ? "跟随输入光标 / 鼠标 (InputSourcePro)" : "Follow Caret / Cursor (InputSourcePro)");
            AddPositionOption(_positionCombo, OsdPosition.SameAsVolume, zh ? "与音量位置相同 (底部居中)" : "Same as Volume (Bottom Center)");
            AddPositionOption(_positionCombo, OsdPosition.TopLeft, zh ? "屏幕左上角 (Top-Left)" : "Top Left");
            AddPositionOption(_positionCombo, OsdPosition.TopRight, zh ? "屏幕右上角 (Top-Right)" : "Top Right");
            AddPositionOption(_positionCombo, OsdPosition.BottomLeft, zh ? "屏幕左下角 (Bottom-Left)" : "Bottom Left");
            AddPositionOption(_positionCombo, OsdPosition.BottomRight, zh ? "屏幕右下角 (Bottom-Right)" : "Bottom Right");
            AddPositionOption(_positionCombo, OsdPosition.TopCenter, zh ? "屏幕顶部居中 (Top-Center)" : "Top Center");

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
                cardBg, cardBorder, accentColor,
                () =>
                {
                    _previewImeCycleIndex = (_previewImeCycleIndex + 1) % 3;
                    Program.ShowImePreview(_previewImeCycleIndex);
                });
            Grid.SetColumn(btnPreviewIme, 0);
            previewRow.Children.Add(btnPreviewIme);

            Button btnPreviewCaps = CreateActionButton(
                zh ? "预览大写锁定" : "Preview CapsLock",
                cardBg, cardBorder, accentColor,
                () =>
                {
                    Program.ShowCapsLockPreview(_previewCapsToggleState);
                    _previewCapsToggleState = !_previewCapsToggleState;
                });
            Grid.SetColumn(btnPreviewCaps, 2);
            previewRow.Children.Add(btnPreviewCaps);

            Button btnPreviewVol = CreateActionButton(
                zh ? "预览音量浮窗" : "Preview Volume OSD",
                cardBg, cardBorder, accentColor,
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

        private static void AddPositionOption(ComboBox combo, OsdPosition pos, string label)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = pos
            });
        }

        private static void AddAnimStyleOption(ComboBox combo, OsdAnimationStyle style, string label)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = style
            });
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

            CheckBox cb = new CheckBox
            {
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                LayoutTransform = new ScaleTransform(1.18, 1.18)
            };
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

        private static Button CreateActionButton(
            string text,
            Color cardBg,
            Color cardBorder,
            Color accentFg,
            Action onClick)
        {
            Button btn = new Button
            {
                Content = text,
                Height = 34,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(cardBg),
                BorderBrush = new SolidColorBrush(cardBorder),
                Foreground = new SolidColorBrush(accentFg),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) => onClick?.Invoke();
            return btn;
        }
    }
}
