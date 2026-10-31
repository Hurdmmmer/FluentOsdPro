using System;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;

namespace FluentOsdPro
{
    internal static class ThemeService
    {
        private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static bool _cachedIsDarkAppTheme;
        private static bool _cachedIsDarkSystemTheme = true;
        private static bool _cachedEnableTransparency = true;
        private static bool _themeCacheInitialized;

        public static void RefreshThemeCache()
        {
            bool appDark = false;
            bool sysDark = true;
            bool transparency = !SystemParameters.HighContrast;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath, false))
                {
                    if (key != null)
                    {
                        object appLightVal = key.GetValue("AppsUseLightTheme");
                        if (appLightVal is int appLightInt)
                        {
                            appDark = appLightInt == 0;
                        }

                        object sysLightVal = key.GetValue("SystemUsesLightTheme");
                        if (sysLightVal is int sysLightInt)
                        {
                            sysDark = sysLightInt == 0;
                        }
                        else
                        {
                            sysDark = appDark;
                        }

                        object transVal = key.GetValue("EnableTransparency");
                        if (transVal is int transInt)
                        {
                            transparency = transparency && (transInt != 0);
                        }
                    }
                }
            }
            catch
            {
            }

            _cachedIsDarkAppTheme = appDark;
            _cachedIsDarkSystemTheme = sysDark;
            _cachedEnableTransparency = transparency;
            _themeCacheInitialized = true;
        }

        public static bool IsSystemChinese()
        {
            try
            {
                if (string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(CultureInfo.CurrentCulture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(CultureInfo.InstalledUICulture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string tzId = TimeZoneInfo.Local.Id ?? string.Empty;
                if (tzId.IndexOf("China", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tzId.IndexOf("Beijing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tzId.IndexOf("Taipei", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        public static System.Windows.Media.Color GetAccentColor()
        {
            try
            {
                return SystemParameters.WindowGlassColor;
            }
            catch
            {
                return System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4);
            }
        }

        public static bool IsSystemDarkTheme()
        {
            if (!_themeCacheInitialized)
            {
                RefreshThemeCache();
            }
            return _cachedIsDarkAppTheme;
        }

        public static bool IsTaskbarDarkTheme()
        {
            if (!_themeCacheInitialized)
            {
                RefreshThemeCache();
            }
            return _cachedIsDarkAppTheme && _cachedIsDarkSystemTheme;
        }

        public static bool IsSystemTransparencyEnabled()
        {
            if (!_themeCacheInitialized)
            {
                RefreshThemeCache();
            }
            return _cachedEnableTransparency && !SystemParameters.HighContrast;
        }
    }
}
