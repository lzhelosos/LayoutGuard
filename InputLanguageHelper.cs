namespace Punto;

/// <summary>
/// Утилиты для определения языка раскладки по HKL (handle keyboard layout).
/// 
/// HKL — это дескриптор раскладки. Его младшие 16 бит содержат LANGID.
/// Из LANGID можно получить:
/// - primary language ID (низшие 10 бит),
/// - sublanguage и региональные варианты.
/// 
/// Нам достаточно primary language:
/// - RU (0x19),
/// - EN (0x09),
/// чтобы выбрать иконку в трее и подпись.
/// </summary>
internal static class InputLanguageHelper
{
    /// <summary>Нейтральный язык (редко встречается как 0 в HKL).</summary>
    private const ushort LangNeutral = 0;
    /// <summary>PrimaryLanguageId для русского.</summary>
    private const ushort LangRussian = 0x19;
    /// <summary>PrimaryLanguageId для английского.</summary>
    private const ushort LangEnglish = 0x09;

    /// <summary>
    /// Возвращает PrimaryLanguageId:
    /// - берём LANGID из младших 16 бит HKL,
    /// - затем берём primary language (low 10 bits).
    /// </summary>
    public static ushort GetPrimaryLanguageId(IntPtr hkl)
    {
        var langId = (ushort)(hkl.ToInt64() & 0xFFFF);
        if (langId == LangNeutral)
            return LangNeutral;
        return (ushort)(langId & 0x03FF);
    }

    /// <summary>True, если primary language — русский.</summary>
    public static bool IsRussian(IntPtr hkl) => GetPrimaryLanguageId(hkl) == LangRussian;

    /// <summary>True, если primary language — английский.</summary>
    public static bool IsEnglish(IntPtr hkl) => GetPrimaryLanguageId(hkl) == LangEnglish;
}
