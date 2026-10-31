using System;

namespace FluentOsdPro
{
    internal enum InputLanguageFamily
    {
        Other = 0,
        English = 1,
        Chinese = 2,
        Japanese = 3
    }

    internal enum InputConversionModeKind
    {
        Unknown = 0,
        DirectInput = 1,
        EnglishMode = 2,
        ChineseMode = 3,
        Hiragana = 4,
        Katakana = 5
    }

    internal sealed class InputMethodSnapshot
    {
        public InputMethodSnapshot(
            IntPtr windowHandle,
            int langId,
            InputLanguageFamily languageFamily,
            InputConversionModeKind conversionMode)
        {
            WindowHandle = windowHandle;
            LangId = langId;
            LanguageFamily = languageFamily;
            ConversionMode = conversionMode;
        }

        public IntPtr WindowHandle { get; }
        public int LangId { get; }
        public InputLanguageFamily LanguageFamily { get; }
        public InputConversionModeKind ConversionMode { get; }

        public bool Known =>
            LanguageFamily != InputLanguageFamily.Other &&
            ConversionMode != InputConversionModeKind.Unknown;

        public string DisplayText
        {
            get
            {
                bool zh = ThemeService.IsSystemChinese();
                string languageText;
                switch (LanguageFamily)
                {
                    case InputLanguageFamily.Chinese:
                        languageText = zh ? "汉语" : "Chinese";
                        break;
                    case InputLanguageFamily.English:
                        languageText = zh ? "英语" : "English";
                        break;
                    case InputLanguageFamily.Japanese:
                        languageText = zh ? "日语" : "Japanese";
                        break;
                    default:
                        languageText = string.Empty;
                        break;
                }

                string modeText;
                switch (ConversionMode)
                {
                    case InputConversionModeKind.ChineseMode:
                        modeText = zh ? "中文模式" : "Chinese Mode";
                        break;
                    case InputConversionModeKind.EnglishMode:
                        modeText = zh ? "英语模式" : "English Mode";
                        break;
                    case InputConversionModeKind.Hiragana:
                        modeText = zh ? "平假名" : "Hiragana";
                        break;
                    case InputConversionModeKind.Katakana:
                        modeText = zh ? "片假名" : "Katakana";
                        break;
                    case InputConversionModeKind.DirectInput:
                        modeText = zh ? "直接输入" : "Direct Input";
                        break;
                    default:
                        modeText = string.Empty;
                        break;
                }

                if (string.IsNullOrEmpty(languageText) || string.IsNullOrEmpty(modeText))
                {
                    return string.Empty;
                }

                return languageText + " - " + modeText;
            }
        }

        public bool SameDisplayState(InputMethodSnapshot other)
        {
            if (other == null)
            {
                return false;
            }

            return LangId == other.LangId &&
                   LanguageFamily == other.LanguageFamily &&
                   ConversionMode == other.ConversionMode;
        }
    }
}
