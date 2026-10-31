using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FluentOsdPro
{
    internal enum OsdPosition
    {
        SameAsVolume = 0, // BottomCenter (stacks above VolumeOsdWindow if both are visible)
        TopLeft = 1,
        TopRight = 2,
        BottomLeft = 3,
        BottomRight = 4,
        TopCenter = 5,
        FollowCursor = 6  // Follow text caret / mouse cursor (InputSourcePro style)
    }

    internal enum OsdAnimationStyle
    {
        SpringPop = 0,   // 灵动回弹 (InputSourcePro / macOS Sonoma style: badge center pop + horizontal text ease)
        SmoothFade = 1,  // 丝滑柔光 (Fluent 2 style: pure optical cross-dissolve, zero positional shift)
        CrispSlide = 2,  // 极速微推 (Linear / GitHub 120Hz style: ultra-fast horizontal micro-slide)
        PulseMorph = 3,  // 呼吸脉冲 (Dynamic Island style: gentle outward expansion pulse on switch)
        None = 4         // 极速直出 (0ms instant switch, no animation)
    }

    internal sealed class AppSettings
    {
        private static readonly object SyncLock = new object();
        private static AppSettings _current;

        public bool GuardEnabled { get; set; } = true;
        public bool ImeFlyoutEnabled { get; set; } = true;
        public bool CapsLockFlyoutEnabled { get; set; } = true;
        public OsdAnimationStyle StatusOsdAnimationStyle { get; set; } = OsdAnimationStyle.SpringPop;

        public bool OsdAnimationEnabled
        {
            get => StatusOsdAnimationStyle != OsdAnimationStyle.None;
            set
            {
                if (!value)
                {
                    StatusOsdAnimationStyle = OsdAnimationStyle.None;
                }
                else if (StatusOsdAnimationStyle == OsdAnimationStyle.None)
                {
                    StatusOsdAnimationStyle = OsdAnimationStyle.SpringPop;
                }
            }
        }

        public OsdPosition StatusOsdPosition { get; set; } = OsdPosition.FollowCursor;

        public static AppSettings Current
        {
            get
            {
                if (_current == null)
                {
                    lock (SyncLock)
                    {
                        if (_current == null)
                        {
                            _current = Load();
                        }
                    }
                }
                return _current;
            }
        }

        public static string GetSettingsFilePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(baseDir, "Data");
            return Path.Combine(dataDir, "settings.json");
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            try
            {
                string filePath = GetSettingsFilePath();
                if (!File.Exists(filePath))
                {
                    return settings;
                }

                string json = File.ReadAllText(filePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return settings;
                }

                settings.GuardEnabled = ReadBool(json, "GuardEnabled", true);
                settings.ImeFlyoutEnabled = ReadBool(json, "ImeFlyoutEnabled", true);
                settings.CapsLockFlyoutEnabled = ReadBool(json, "CapsLockFlyoutEnabled", true);
                bool animEnabled = ReadBool(json, "OsdAnimationEnabled", true);
                settings.StatusOsdAnimationStyle = animEnabled ? OsdAnimationStyle.SpringPop : OsdAnimationStyle.None;

                string animStyleStr = ReadString(json, "StatusOsdAnimationStyle", string.Empty);
                if (!string.IsNullOrEmpty(animStyleStr))
                {
                    if (Enum.TryParse(animStyleStr, true, out OsdAnimationStyle parsedStyle) &&
                        Enum.IsDefined(typeof(OsdAnimationStyle), parsedStyle))
                    {
                        settings.StatusOsdAnimationStyle = parsedStyle;
                    }
                    else if (int.TryParse(animStyleStr, out int styleInt) &&
                             Enum.IsDefined(typeof(OsdAnimationStyle), styleInt))
                    {
                        settings.StatusOsdAnimationStyle = (OsdAnimationStyle)styleInt;
                    }
                }

                string posStr = ReadString(json, "StatusOsdPosition", "FollowCursor");
                if (Enum.TryParse(posStr, true, out OsdPosition parsedPos) && Enum.IsDefined(typeof(OsdPosition), parsedPos))
                {
                    settings.StatusOsdPosition = parsedPos;
                }
                else if (int.TryParse(posStr, out int posInt) && Enum.IsDefined(typeof(OsdPosition), posInt))
                {
                    settings.StatusOsdPosition = (OsdPosition)posInt;
                }
            }
            catch (Exception ex)
            {
                Program.LogError("Failed to load Data/settings.json, using defaults", ex);
            }

            return settings;
        }

        public void Save()
        {
            lock (SyncLock)
            {
                try
                {
                    string filePath = GetSettingsFilePath();
                    string dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("{");
                    sb.AppendLine("  \"GuardEnabled\": " + (GuardEnabled ? "true" : "false") + ",");
                    sb.AppendLine("  \"ImeFlyoutEnabled\": " + (ImeFlyoutEnabled ? "true" : "false") + ",");
                    sb.AppendLine("  \"CapsLockFlyoutEnabled\": " + (CapsLockFlyoutEnabled ? "true" : "false") + ",");
                    sb.AppendLine("  \"OsdAnimationEnabled\": " + (OsdAnimationEnabled ? "true" : "false") + ",");
                    sb.AppendLine("  \"StatusOsdAnimationStyle\": \"" + StatusOsdAnimationStyle.ToString() + "\",");
                    sb.AppendLine("  \"StatusOsdPosition\": \"" + StatusOsdPosition.ToString() + "\"");
                    sb.AppendLine("}");

                    string tempPath = filePath + ".tmp";
                    File.WriteAllText(tempPath, sb.ToString(), Encoding.UTF8);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    File.Move(tempPath, filePath);
                }
                catch (Exception ex)
                {
                    Program.LogError("Failed to save Data/settings.json", ex);
                }
            }
        }

        private static bool ReadBool(string json, string key, bool defaultValue)
        {
            Match m = Regex.Match(
                json,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            }
            return defaultValue;
        }

        private static string ReadString(string json, string key, string defaultValue)
        {
            Match m = Regex.Match(
                json,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"?([A-Za-z0-9_]+)\"?",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
            return defaultValue;
        }
    }
}
