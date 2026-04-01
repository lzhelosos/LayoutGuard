using System.Text;

namespace Punto;

/// <summary>
/// Настройки приложения, хранимые в простом INI-файле рядом с .exe (portable-подход).
/// 
/// Почему INI, а не json:
/// - легко открыть/поправить вручную;
/// - не нужны дополнительные зависимости;
/// - формат "дружелюбен" к single-file publish.
/// 
/// Файл по умолчанию: LayoutGuard.ini (создаётся рядом с .exe при первом запуске).
/// Секция: [General]
/// </summary>
internal sealed class AppConfig
{
    /// <summary>
    /// Задержка (мс) после отпускания chord перед проверкой "сменилась ли раскладка".
    /// Нужна потому, что смена раскладки может применяться асинхронно спустя несколько миллисекунд.
    /// </summary>
    public int CheckDelayMs { get; init; } = 120;

    /// <summary>
    /// Cooldown (мс) после принудительного переключения:
    /// защищает от повторных вмешательств и "пинг-понга" в случаях,
    /// когда внешняя программа/система тут же возвращает раскладку обратно.
    /// </summary>
    public int CooldownAfterSwitchMs { get; init; } = 400;

    /// <summary>
    /// Как часто обновлять иконку/tooltip в трее по текущей раскладке активного окна (мс).
    /// Нужен, чтобы UI отражал состояние даже при переключении раскладки "не нами".
    /// </summary>
    public int TrayIconRefreshMs { get; init; } = 250;

    /// <summary>
    /// Показывать ли balloon-уведомление, когда утилита сама исправила ситуацию (раскладка не сменилась → переключили).
    /// </summary>
    public bool ShowBalloonOnFix { get; init; }

    /// <summary>
    /// Сочетание модификаторов через «+», например Shift+Alt.
    /// 
    /// Требования:
    /// - минимум два разных модификатора;
    /// - допустимые токены: Shift, Ctrl (Control), Alt, Win (Windows).
    /// 
    /// Важно: мы отслеживаем именно модификаторы (без обычных клавиш),
    /// чтобы chord был максимально "универсален" и не зависел от раскладки/scan-кодов.
    /// </summary>
    public string HotkeyChord { get; init; } = ChordKeyHelper.DefaultChordIni;

    /// <summary>
    /// Загружает конфиг из INI.
    /// 
    /// Поведение:
    /// - если файла нет — создаёт файл с дефолтами и возвращает дефолтный AppConfig;
    /// - если HotkeyChord некорректен — заменяет его на дефолтный и нормализует формат (канонический порядок).
    /// </summary>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            WriteDefault(path);
            return new AppConfig();
        }

        // Внутреннее представление INI: section -> (key -> value), без учёта регистра.
        var dict = ReadIni(path);

        // Хоткей нормализуем:
        // - принимаем разные регистры/синонимы (Control/Windows),
        // - упорядочиваем модификаторы, чтобы строка была стабильной при сохранении.
        var hotkeyRaw = GetString(dict, "General", "HotkeyChord", ChordKeyHelper.DefaultChordIni);
        var hotkey = ChordKeyHelper.TryParseChord(hotkeyRaw, out var chordKeys)
            ? ChordKeyHelper.ToCanonicalIniString(chordKeys)
            : ChordKeyHelper.DefaultChordIni;

        return new AppConfig
        {
            // Числовые значения мы здесь читаем "как есть"; диапазоны жёстко применяются при сохранении.
            CheckDelayMs = GetInt(dict, "General", "CheckDelayMs", 120),
            CooldownAfterSwitchMs = GetInt(dict, "General", "CooldownAfterSwitchMs", 400),
            TrayIconRefreshMs = GetInt(dict, "General", "TrayIconRefreshMs", 250),
            ShowBalloonOnFix = GetBool(dict, "General", "ShowBalloonOnFix", false),
            HotkeyChord = hotkey,
        };
    }

    /// <summary>
    /// Сохраняет конфиг в INI, добавляя поясняющие комментарии.
    /// 
    /// Здесь же применяется clamp диапазонов, чтобы файл всегда оставался валидным.
    /// </summary>
    public static void Save(string path, AppConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; LayoutGuard — настройки рядом с .exe");
        sb.AppendLine("[General]");
        sb.AppendLine("; Задержка после отпускания сочетания клавиш перед проверкой раскладки (мс), 10–2000");
        sb.AppendLine($"CheckDelayMs={Math.Clamp(cfg.CheckDelayMs, 10, 2000)}");
        sb.AppendLine("; Пауза после принудительного переключения, чтобы не зациклиться (мс), 50–5000");
        sb.AppendLine($"CooldownAfterSwitchMs={Math.Clamp(cfg.CooldownAfterSwitchMs, 50, 5000)}");
        sb.AppendLine("; Как часто обновлять иконку в трее по текущей раскладке (мс), 50–5000");
        sb.AppendLine($"TrayIconRefreshMs={Math.Clamp(cfg.TrayIconRefreshMs, 50, 5000)}");
        sb.AppendLine("; Показывать всплывающее уведомление при исправлении (0/1)");
        sb.AppendLine($"ShowBalloonOnFix={(cfg.ShowBalloonOnFix ? 1 : 0)}");
        sb.AppendLine("; Сочетание модификаторов для отслеживания: минимум два из Shift, Ctrl, Alt, Win через +");
        sb.AppendLine($"HotkeyChord={cfg.HotkeyChord}");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Создаёт дефолтный конфиг на диске.
    /// Отдельный метод, чтобы подчёркнуть: отсутствие файла — нормальная ситуация при первом запуске.
    /// </summary>
    private static void WriteDefault(string path)
    {
        Save(path, new AppConfig());
    }

    /// <summary>
    /// Примитивный INI-парсер:
    /// - секции: [Section]
    /// - ключи: Key=Value
    /// - комментарии: строки, начинающиеся с ';' или '#'
    /// - пробелы вокруг ключа/значения обрезаются
    /// 
    /// Особенности:
    /// - неизвестные секции/ключи разрешены (про запас для будущих версий);
    /// - порядок не сохраняется (мы всегда перезаписываем файл в Save).
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = "General";
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (!result.ContainsKey(section))
                    result[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (!result.ContainsKey(section))
                result[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result[section][key] = val;
        }

        return result;
    }

    /// <summary>Читает int или возвращает fallback.</summary>
    private static int GetInt(Dictionary<string, Dictionary<string, string>> ini, string section, string key, int fallback)
    {
        if (!ini.TryGetValue(section, out var sec) || !sec.TryGetValue(key, out var s))
            return fallback;
        return int.TryParse(s, out var v) ? v : fallback;
    }

    /// <summary>
    /// Читает bool в "дружелюбном" формате:
    /// - true: 1/true/yes/on
    /// - false: 0/false/no/off
    /// иначе fallback.
    /// </summary>
    private static bool GetBool(Dictionary<string, Dictionary<string, string>> ini, string section, string key, bool fallback)
    {
        if (!ini.TryGetValue(section, out var sec) || !sec.TryGetValue(key, out var s))
            return fallback;
        s = s.Trim();
        if (s is "1" or "true" or "yes" or "on")
            return true;
        if (s is "0" or "false" or "no" or "off")
            return false;
        return fallback;
    }

    /// <summary>Читает строку, отсекая пустые/пробельные значения.</summary>
    private static string GetString(Dictionary<string, Dictionary<string, string>> ini, string section, string key, string fallback)
    {
        if (!ini.TryGetValue(section, out var sec) || !sec.TryGetValue(key, out var s))
            return fallback;
        return string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
    }
}
