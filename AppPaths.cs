namespace Punto;

/// <summary>
/// Поиск файлов, которые лежат "рядом с приложением" (loose files).
/// 
/// Это нужно для portable/single-file сценариев:
/// - при запуске из исходников файлы лежат в папке проекта (Assets\*.ico),
/// - при обычном запуске они лежат рядом с .exe,
/// - при single-file publish .NET может распаковывать контент во временную папку (AppContext.BaseDirectory).
/// 
/// Поэтому мы пробуем несколько директорий-кандидатов.
/// </summary>
internal static class AppPaths
{
    /// <summary>
    /// Возвращает набор директорий, где имеет смысл искать дополнительные файлы.
    /// 
    /// Кандидаты:
    /// - папка .exe (Environment.ProcessPath),
    /// - AppContext.BaseDirectory (важно для single-file/self-extract),
    /// - папка сборки (Assembly.Location),
    /// - текущий каталог (Directory.GetCurrentDirectory()).
    /// 
    /// Возвращаем HashSet, чтобы убрать дубликаты и не зависеть от регистра путей.
    /// </summary>
    public static IEnumerable<string> GetSearchDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Внутренний helper: нормализует путь и добавляет, если каталог существует.
        void TryAdd(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                var full = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (Directory.Exists(full))
                    _ = set.Add(full);
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            // Папка реально запущенного процесса (наиболее ожидаемое место "рядом с .exe").
            var pp = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(pp))
                TryAdd(Path.GetDirectoryName(pp));
        }
        catch
        {
            // ignore
        }

        // BaseDirectory: для single-file/self-extract может отличаться от ProcessPath.
        TryAdd(AppContext.BaseDirectory);

        try
        {
            // Location executing assembly: полезно в нестандартных сценариях запуска/тестов.
            var loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(loc))
                TryAdd(Path.GetDirectoryName(loc));
        }
        catch
        {
            // ignore
        }

        // Текущая директория — fallback (например, запуск из консоли из другой папки).
        TryAdd(Directory.GetCurrentDirectory());

        return set;
    }

    /// <summary>
    /// Ищет файл в "loose" местах:
    /// - сначала `Assets\fileName`,
    /// - затем `fileName` в корне каждой из папок поиска.
    /// 
    /// Возвращает первый найденный путь или null.
    /// </summary>
    public static string? FindLooseFile(string fileName)
    {
        foreach (var dir in GetSearchDirectories())
        {
            var inAssets = Path.Combine(dir, "Assets", fileName);
            if (File.Exists(inAssets))
                return inAssets;

            var root = Path.Combine(dir, fileName);
            if (File.Exists(root))
                return root;
        }

        return null;
    }
}
