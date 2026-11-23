using System;
using System.Runtime.InteropServices;

namespace FluentOsdPro
{
    internal static class NativeWindowStyler
    {
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExTransparent = 0x00000020;

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const uint SwpNoSendChanging = 0x0400;

        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaSystemBackdropType = 38;
        private const int DwmwcpRound = 2;
        private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
        private const int WcaAccentPolicy = 19;
        private const uint AcrylicOpacity = 175;
        private const uint DarkTintColor = 0x2C2C2C;
        private const uint LightTintColor = 0xFCFCFC;

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        private enum AccentState
        {
            Disabled = 0,
            EnableAcrylicBlurBehind = 4
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins pMarInset);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        public static void ApplyOsdWindowStyle(
            IntPtr hwnd,
            bool isDarkTheme,
            bool enableAcrylic,
            bool clickThrough = true,
            bool forceResetAccent = false)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                int exStyle = GetWindowLong(hwnd, GwlExStyle);
                int desiredFlags = WsExToolWindow | WsExNoActivate | (clickThrough ? WsExTransparent : 0);
                SetWindowLong(hwnd, GwlExStyle, exStyle | desiredFlags);

                int round = DwmwcpRound;
                int noSystemBackdrop = 1; // DWMSBT_NONE
                int defaultDwmBorder = DwmColorDefault;
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref noSystemBackdrop, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref defaultDwmBorder, sizeof(int));

                // Extend frame -1 into client area so DWM composes native rounded border & shadow without white flash
                Margins margins = new Margins
                {
                    cxLeftWidth = -1,
                    cxRightWidth = -1,
                    cyTopHeight = -1,
                    cyBottomHeight = -1
                };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                int dark = isDarkTheme ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

                ApplyAcrylicBlur(hwnd, isDarkTheme, enableAcrylic, forceResetAccent);

                if (forceResetAccent)
                {
                    SetWindowPos(
                        hwnd,
                        IntPtr.Zero,
                        0,
                        0,
                        0,
                        0,
                        SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoOwnerZOrder | SwpNoActivate | SwpFrameChanged | SwpNoSendChanging);
                }
            }
            catch
            {
            }
        }

        public static void ApplyAcrylicBlur(IntPtr hwnd, bool isDarkTheme, bool enableAcrylic, bool forceReset = false)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // GradientColor layout is 0xAABBGGRR (NOT RGB). Use FluentOsdPro's own
            // standalone wallpaper Mica color + user-configured OsdTransparency (0..100).
            uint tint = isDarkTheme ? DarkTintColor : LightTintColor;
            int transPct = Math.Max(0, Math.Min(100, AppSettings.Current.OsdTransparency));
            uint alpha = (uint)Math.Max(1, Math.Min(255, (int)Math.Round(255.0 * (1.0 - transPct / 100.0))));
            System.Windows.Media.Color mica;
            if (TaskbarMicaSync.TryGetForTheme(isDarkTheme, out mica))
            {
                tint = ((uint)mica.B << 16) | ((uint)mica.G << 8) | mica.R;
            }

            if (forceReset && enableAcrylic)
            {
                // Toggle Disabled (0) -> EnableAcrylicBlurBehind (4) so uDWM never skips recreating
                // an invalidated CAcrylicSheet visual after display rotation / mode switches.
                SetAccentPolicyCore(hwnd, new AccentPolicy
                {
                    AccentState = (int)AccentState.Disabled,
                    AccentFlags = 0,
                    GradientColor = 0
                });
            }

            AccentPolicy accent = new AccentPolicy
            {
                AccentState = enableAcrylic
                    ? (int)AccentState.EnableAcrylicBlurBehind
                    : (int)AccentState.Disabled,
                AccentFlags = 0,
                GradientColor = unchecked((int)((alpha << 24) | tint))
            };

            SetAccentPolicyCore(hwnd, accent);
        }

        private static void SetAccentPolicyCore(IntPtr hwnd, AccentPolicy accent)
        {
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                WindowCompositionAttributeData data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    SizeOfData = size,
                    Data = ptr
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            catch
            {
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }
}
