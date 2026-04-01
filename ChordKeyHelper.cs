using System.Runtime.InteropServices;

namespace Punto;

/// <summary>
/// Типы модификаторов, из которых собирается "chord".
/// 
/// Мы ограничиваемся модификаторами, чтобы:
/// - не конфликтовать с обычным вводом текста,
/// - не зависеть от раскладки (буквы/символы могут меняться),
/// - проще и надёжнее определять состояние (GetAsyncKeyState).
/// </summary>
internal enum ChordKey
{
    Shift,
    Ctrl,
    Alt,
    Win,
}

/// <summary>
/// Утилиты для:
/// - парсинга строки HotkeyChord (например "Ctrl+Alt"),
/// - нормализации (канонический порядок),
/// - проверки "все ли модификаторы сейчас нажаты".
/// </summary>
internal static class ChordKeyHelper
{
    /// <summary>Chord по умолчанию, если в INI пусто/ошибка.</summary>
    public const string DefaultChordIni = "Shift+Alt";

    /// <summary>
    /// Проверяет: нажаты ли ВСЕ модификаторы из списка одновременно.
    /// 
    /// Требование "минимум два" — сознательное:
    /// один модификатор (например просто Shift) слишком часто используется в обычной работе,
    /// и утилита стала бы вмешиваться в повседневный ввод.
    /// </summary>
    public static bool AllChordModifiersHeld(IReadOnlyList<ChordKey> keys)
    {
        if (keys.Count < 2)
            return false;
        foreach (var k in keys)
        {
            if (!IsModifierHeld(k))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Парсит строку вида "Shift+Alt" в список модификаторов.
    /// 
    /// Особенности:
    /// - допускается любой регистр;
    /// - допускаются синонимы: Control, Windows;
    /// - дубликаты удаляются (HashSet);
    /// - возвращает false, если:
    ///   - есть неизвестный токен,
    ///   - модификаторов меньше двух.
    /// </summary>
    public static bool TryParseChord(string? s, out List<ChordKey> keys)
    {
        keys = new List<ChordKey>();
        if (string.IsNullOrWhiteSpace(s))
            return false;

        var set = new HashSet<ChordKey>();
        foreach (var part in s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseOne(part, out var k))
                return false;
            _ = set.Add(k);
        }

        keys = set.ToList();
        return keys.Count >= 2;
    }

    /// <summary>
    /// Парсит chord, а если не получилось — возвращает дефолтный набор.
    /// </summary>
    public static IReadOnlyList<ChordKey> ParseChordOrDefault(string? s)
    {
        if (TryParseChord(s, out var keys))
            return keys;
        return new[] { ChordKey.Shift, ChordKey.Alt };
    }

    /// <summary>
    /// Приводит chord к канонической строке для INI.
    /// 
    /// Делает 3 вещи:
    /// - убирает повторы,
    /// - сортирует по enum-порядку,
    /// - маппит в стабильные токены "Shift/Ctrl/Alt/Win".
    /// </summary>
    public static string ToCanonicalIniString(IReadOnlyList<ChordKey> keys)
    {
        var ordered = keys.Distinct().OrderBy(k => k).ToArray();
        return string.Join('+', ordered.Select(ChordToIniToken));
    }

    private static string ChordToIniToken(ChordKey k) => k switch
    {
        ChordKey.Shift => "Shift",
        ChordKey.Ctrl => "Ctrl",
        ChordKey.Alt => "Alt",
        ChordKey.Win => "Win",
        _ => "Shift",
    };

    private static bool TryParseOne(string token, out ChordKey key)
    {
        // Токены стараемся принимать максимально "по-человечески" (синонимы, регистр),
        // но при этом держим закрытый набор допустимых модификаторов.
        if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            key = ChordKey.Shift;
            return true;
        }

        if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
            || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
        {
            key = ChordKey.Ctrl;
            return true;
        }

        if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
        {
            key = ChordKey.Alt;
            return true;
        }

        if (token.Equals("Win", StringComparison.OrdinalIgnoreCase)
            || token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
        {
            key = ChordKey.Win;
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>
    /// Проверяет "нажата ли" конкретная модификаторная клавиша через GetAsyncKeyState.
    /// 
    /// Почему GetAsyncKeyState:
    /// - даёт текущее состояние клавиши независимо от того, какое окно в фокусе;
    /// - подходит для нашей модели "хук как триггер, состояние читаем отдельно".
    /// </summary>
    private static bool IsModifierHeld(ChordKey key)
    {
        const short high = unchecked((short)0x8000);
        bool Down(int vk) => (GetAsyncKeyState(vk) & high) != 0;

        // VK коды:
        // - общий (Shift/Ctrl/Alt) + левый/правый варианты,
        // - Win: LWIN/RWIN.
        return key switch
        {
            ChordKey.Shift => Down(0x10) || Down(0xA0) || Down(0xA1),
            ChordKey.Ctrl => Down(0x11) || Down(0xA2) || Down(0xA3),
            ChordKey.Alt => Down(0x12) || Down(0xA4) || Down(0xA5),
            ChordKey.Win => Down(0x5B) || Down(0x5C),
            _ => false,
        };
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
