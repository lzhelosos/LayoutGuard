namespace Punto;

/// <summary>
/// Точка входа WinForms-приложения.
/// 
/// Программа не показывает окно — она живёт в трее и реагирует на:
/// - глобальный хук клавиатуры (отпускание выбранного chord из модификаторов),
/// - клики по иконке в трее,
/// - обновление иконки трея по активной раскладке.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Mutex для режима single-instance.
    /// 
    /// Важно: это имя должно быть стабильно между запусками одной и той же утилиты,
    /// чтобы второй запуск корректно определял уже работающий экземпляр.
    /// </summary>
    private const string MutexName = "LayoutGuard_SingleInstance_8c2f1a9e";

    /// <summary>
    /// Главный поток WinForms должен быть STA:
    /// часть WinAPI/COM и некоторые WinForms-компоненты требуют STA-контекст.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        // Single-instance: создаём mutex и проверяем, был ли он создан именно сейчас.
        // Если mutex уже существует — значит утилита уже запущена.
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            _ = MessageBox.Show(
                "LayoutGuard уже запущен.",
                "LayoutGuard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // Инициализация WinForms (шрифты, high DPI, default settings).
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // Конфиг хранится "portable"-способом: рядом с .exe.
        // Это удобно для single-file publish: скачал — запустил — настроил — всё рядом.
        var exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? Environment.CurrentDirectory;
        var iniPath = Path.Combine(exeDir, "LayoutGuard.ini");
        var config = AppConfig.Load(iniPath);

        try
        {
            // Запускаем без формы: ApplicationContext управляет циклом сообщений,
            // ресурсами NotifyIcon и глобальными подписками/таймерами.
            Application.Run(new TrayApplicationContext(iniPath, config));
        }
        catch (HookException)
        {
            // Ошибка установки глобального keyboard hook.
            // Пользователю уже показано сообщение внутри TrayApplicationContext.
        }
    }
}
