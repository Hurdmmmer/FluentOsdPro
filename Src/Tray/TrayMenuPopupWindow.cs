using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Forms = System.Windows.Forms;

namespace FluentOsdPro
{
    /// <summary>
    /// Minimalist 4-row tray popup window:
    /// Row 1: ✓ 已启动 UU / VDI 拦截
    /// Row 2: 打开设置...
    /// Row 3: ✓ 开机自动启动
    /// Row 4: 退出
    /// </summary>
    internal sealed class TrayMenuPopupWindow : Window
    {
        private readonly Border _cardBorder;
        private readonly Border _rowGuard;
        private readonly Border _rowSettings;
        private readonly Border _rowAutoStart;
        private readonly Border _rowExit;

        private readonly TextBlock _checkGuard;
        private readonly TextBlock _textGuard;
        private readonly TextBlock _iconSettings;
        private readonly TextBlock _textSettings;
        private readonly TextBlock _checkAutoStart;
        private readonly TextBlock _textAutoStart;
        private readonly TextBlock _textExit;

        private Brush _rowHoverBrush;
        private Brush _rowPressedBrush;
        private bool _isClosing;

        public TrayMenuPopupWindow()
        {
            Title = "FluentOsdPro_Menu";
            Width = 240;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _cardBorder = new Border
            {
                Margin = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 4,
                    Direction = 270,
                    Opacity = 0.28,
                    Color = Colors.Black
                }
            };

            StackPanel rootStack = new StackPanel
            {
                Orientation = Orientation.Vertical
            };

            _rowGuard = CreateMenuRow(out _checkGuard, out _textGuard);
            _rowGuard.MouseLeftButtonUp += (s, e) =>
            {
                Program.IsGuardEnabled = !Program.IsGuardEnabled;
                RefreshMenuState();
                HideMenuAndRestoreFocus("ToggleGuard");
            };

            _rowSettings = CreateMenuRow(out _iconSettings, out _textSettings);
            _iconSettings.Text = "\uE713";
            _iconSettings.FontSize = 11.5;
            _iconSettings.Opacity = 0.85;
            _rowSettings.MouseLeftButtonUp += (s, e) =>
            {
                Hide();
                Program.OpenSettingsWindow();
            };

            _rowAutoStart = CreateMenuRow(out _checkAutoStart, out _textAutoStart);
            _rowAutoStart.MouseLeftButtonUp += (s, e) =>
            {
                bool nextState = !AutoStartManager.IsAutoStartEnabled();
                AutoStartManager.SetAutoStart(nextState);
                RefreshMenuState();
                HideMenuAndRestoreFocus("ToggleAutoStart");
            };

            _rowExit = CreateMenuRow(out _, out _textExit);
            _rowExit.MouseLeftButtonUp += (s, e) =>
            {
                _isClosing = true;
                Hide();
                Application.Current.Shutdown();
            };

            rootStack.Children.Add(_rowGuard);
            rootStack.Children.Add(_rowSettings);
            rootStack.Children.Add(_rowAutoStart);
            rootStack.Children.Add(_rowExit);

            _cardBorder.Child = rootStack;
            Content = _cardBorder;

            Deactivated += (s, e) =>
            {
                if (!_isClosing && IsVisible)
                {
                    HideMenuAndRestoreFocus("MenuDeactivated");
                }
            };

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    HideMenuAndRestoreFocus("MenuEscape");
                }
            };

            RefreshMenu();
        }

        private Border CreateMenuRow(out TextBlock checkGlyph, out TextBlock labelText)
        {
            Border row = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 12, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            checkGlyph = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetColumn(checkGlyph, 0);
            grid.Children.Add(checkGlyph);

            labelText = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI"),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(labelText, 1);
            grid.Children.Add(labelText);

            row.Child = grid;

            row.MouseEnter += (s, e) =>
            {
                if (_rowHoverBrush != null) row.Background = _rowHoverBrush;
            };
            row.MouseLeave += (s, e) =>
            {
                row.Background = Brushes.Transparent;
            };
            row.MouseLeftButtonDown += (s, e) =>
            {
                if (_rowPressedBrush != null) row.Background = _rowPressedBrush;
            };

            return row;
        }

        public void ApplyThemeAndLanguage(bool isDark, bool isChinese)
        {
            if (isDark)
            {
                _cardBorder.Background = new SolidColorBrush(Color.FromRgb(40, 42, 46));
                _cardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(62, 65, 72));
                _rowHoverBrush = new SolidColorBrush(Color.FromRgb(54, 57, 63));
                _rowPressedBrush = new SolidColorBrush(Color.FromRgb(48, 51, 57));

                Brush primaryText = new SolidColorBrush(Color.FromRgb(243, 244, 246));
                Brush checkBrush = new SolidColorBrush(Color.FromRgb(96, 205, 255));
                _textGuard.Foreground = primaryText;
                _textSettings.Foreground = primaryText;
                _iconSettings.Foreground = primaryText;
                _textAutoStart.Foreground = primaryText;
                _textExit.Foreground = primaryText;
                _checkGuard.Foreground = checkBrush;
                _checkAutoStart.Foreground = checkBrush;
            }
            else
            {
                _cardBorder.Background = new SolidColorBrush(Color.FromRgb(251, 252, 254));
                _cardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(222, 226, 232));
                _rowHoverBrush = new SolidColorBrush(Color.FromRgb(237, 240, 245));
                _rowPressedBrush = new SolidColorBrush(Color.FromRgb(228, 232, 238));

                Brush primaryText = new SolidColorBrush(Color.FromRgb(28, 30, 33));
                Brush checkBrush = new SolidColorBrush(Color.FromRgb(0, 103, 192));
                _textGuard.Foreground = primaryText;
                _textSettings.Foreground = primaryText;
                _iconSettings.Foreground = primaryText;
                _textAutoStart.Foreground = primaryText;
                _textExit.Foreground = primaryText;
                _checkGuard.Foreground = checkBrush;
                _checkAutoStart.Foreground = checkBrush;
            }

            if (isChinese)
            {
                _textGuard.Text = "已启动 UU / VDI 拦截";
                _textSettings.Text = "设置与浮窗位置...";
                _textAutoStart.Text = "开机自动启动";
                _textExit.Text = "退出";
            }
            else
            {
                _textGuard.Text = "Enable UU / VDI Guard";
                _textSettings.Text = "Settings & OSD...";
                _textAutoStart.Text = "Start with Windows";
                _textExit.Text = "Exit";
            }

            RefreshMenuState();
        }

        public void RefreshMenu()
        {
            ThemeService.RefreshThemeCache();
            ApplyThemeAndLanguage(ThemeService.IsSystemDarkTheme(), ThemeService.IsSystemChinese());
        }

        public void RefreshMenuState()
        {
            _checkGuard.Visibility = Program.IsGuardEnabled ? Visibility.Visible : Visibility.Hidden;
            _checkAutoStart.Visibility = AutoStartManager.IsAutoStartEnabled() ? Visibility.Visible : Visibility.Hidden;
        }

        private void HideMenuAndRestoreFocus(string reason)
        {
            Hide();
            Program.RestorePreviousActiveApp(reason);
        }

        public void ShowAtCursor()
        {
            RefreshMenu();

            Forms.Cursor.Position.Deconstruct(out int cursorX, out int cursorY);
            Forms.Screen screen = Forms.Screen.FromPoint(new System.Drawing.Point(cursorX, cursorY));
            System.Drawing.Rectangle workArea = screen.WorkingArea;

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

            Show();
            UpdateLayout();

            double menuW = ActualWidth > 0 ? ActualWidth : Width;
            double menuH = ActualHeight > 0 ? ActualHeight : 165;

            double curDipX = cursorX / dpiX;
            double curDipY = cursorY / dpiY;
            double waLeft = workArea.Left / dpiX;
            double waTop = workArea.Top / dpiY;
            double waRight = workArea.Right / dpiX;
            double waBottom = workArea.Bottom / dpiY;

            double targetLeft = curDipX - menuW + 18;
            double targetTop = curDipY - menuH - 6;

            if (targetLeft < waLeft + 4) targetLeft = waLeft + 4;
            if (targetLeft + menuW > waRight - 4) targetLeft = waRight - menuW - 4;
            if (targetTop < waTop + 4) targetTop = curDipY + 8;
            if (targetTop + menuH > waBottom - 4) targetTop = waBottom - menuH - 4;

            Left = targetLeft;
            Top = targetTop;
            Activate();
        }
    }

    internal static class PointExtensions
    {
        public static void Deconstruct(this System.Drawing.Point pt, out int x, out int y)
        {
            x = pt.X;
            y = pt.Y;
        }
    }
}
