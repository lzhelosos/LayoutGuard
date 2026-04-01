namespace Punto;

/// <summary>
/// "Сердце" приложения без главного окна: держит NotifyIcon (трей),
/// глобальные подписки, таймеры и бизнес-логику проверки/исправления раскладки.
/// 
/// Почему ApplicationContext:
/// - WinForms требует message loop; обычно его держит форма, но здесь формы нет.
/// - ApplicationContext даёт управляемую точку жизни: ExitThread() завершает приложение.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    /// <summary>Путь к INI (portable — рядом с .exe).</summary>
    private readonly string _iniPath;
    /// <summary>Текущие настройки (могут перезагружаться из INI или формы).</summary>
    private AppConfig _config;
    /// <summary>
    /// Нормализованный список модификаторов, которые считаются "chord" (напр. Shift+Alt).
    /// Хранится отдельно, чтобы не парсить строку на каждый keyboard event.
    /// </summary>
    private IReadOnlyList<ChordKey> _chordKeys;

    /// <summary>
    /// Иконка в трее + тултип + контекстное меню.
    /// Через неё пользователь управляет настройками и кликом может переключать раскладку.
    /// </summary>
    private readonly NotifyIcon _tray;

    /// <summary>
    /// Таймер "отложенной проверки" после отпускания chord.
    /// Логика такая: отпускание chord → подождать CheckDelayMs → проверить, изменилась ли раскладка.
    /// </summary>
    private readonly System.Windows.Forms.Timer _checkTimer;

    /// <summary>
    /// Таймер обновления иконки трея по текущей раскладке активного окна.
    /// Это отдельный цикл, чтобы UI всегда отражал состояние, даже если раскладка менялась не нашей утилитой.
    /// </summary>
    private readonly System.Windows.Forms.Timer _trayIconTimer;

    /// <summary>
    /// Глобальный low-level hook клавиатуры (WH_KEYBOARD_LL).
    /// Он не "съедает" события, а лишь сигнализирует: состояние клавиш могло измениться.
    /// </summary>
    private readonly LowLevelKeyboardHook _hook;

    /// <summary>Иконки RU/EN для трея (могут отсутствовать — тогда будет SystemIcons.Application).</summary>
    private readonly Icon? _iconRu;
    private readonly Icon? _iconEn;

    /// <summary>
    /// Состояние chord "в предыдущий тик": нужно для детекта фронта/спада (нажали → отпустили).
    /// </summary>
    private bool _chordWasActive;

    /// <summary>
    /// Раскладка (HKL), которую мы увидели в момент начала chord.
    /// После отпускания chord мы сравниваем её с текущей — и если не изменилась, считаем "переключение не сработало".
    /// </summary>
    private IntPtr _hklAtChordStart;

    /// <summary>
    /// До какого момента подавлять проверки (cooldown), чтобы не:
    /// - зациклиться на переключениях,
    /// - вмешаться сразу после ручного/нашего переключения.
    /// </summary>
    private DateTime _suppressChecksUntilUtc;

    /// <summary>Кеш последней HKL, отражённой в трее (чтобы не трогать Icon/Text без необходимости).</summary>
    private IntPtr _lastTrayHkl = unchecked((IntPtr)(-1));

    public TrayApplicationContext(string iniPath, AppConfig config)
    {
        _iniPath = iniPath;
        _config = config;
        _chordKeys = ChordKeyHelper.ParseChordOrDefault(_config.HotkeyChord);

        // Иконки трея под RU/EN лежат рядом с .exe (или в Assets при запуске из исходников).
        // В single-file publish они могут быть извлечены во временную папку — поэтому поиск идёт через AppPaths.
        _iconRu = LoadTrayIcon("tray-ru.ico");
        _iconEn = LoadTrayIcon("tray-en.ico");

        // Таймер проверки стартует только когда chord отпущен.
        _checkTimer = new System.Windows.Forms.Timer { Enabled = false };
        _checkTimer.Tick += OnCheckTimerTick;

        // Таймер иконки работает постоянно: UI должен соответствовать раскладке активного окна.
        _trayIconTimer = new System.Windows.Forms.Timer { Interval = 250, Enabled = true };
        _trayIconTimer.Tick += (_, _) => UpdateTrayIconFromForegroundLayout();

        // Поднимаем глобальный хук. Если хук не удалось поставить (например, нет доступа к интерактивному рабочему столу),
        // приложение не сможет выполнять основную функцию, поэтому завершаем запуск понятной ошибкой.
        _hook = new LowLevelKeyboardHook();
        _hook.KeyStateChanged += OnKeyStateChanged;
        try
        {
            _hook.Install();
        }
        catch (Exception ex)
        {
            _ = MessageBox.Show(
                ex.Message + Environment.NewLine + Environment.NewLine +
                "Запустите утилиту от обычного пользователя с доступом к рабочему столу.",
                "LayoutGuard",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            throw new HookException("Не удалось установить хук клавиатуры.", ex);
        }

        // NotifyIcon — единственный "видимый" UI приложения.
        // Текст (tooltip) ограничен Windows по длине; поэтому делаем его коротким, но информативным.
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = $"LayoutGuard — {_config.HotkeyChord}: проверка раскладки",
        };

        // Контекстное меню — основной способ управления:
        // - открыть форму настроек,
        // - перечитать INI (если правили руками),
        // - выйти.
        var menu = new ContextMenuStrip();
        menu.Items.Add("Настройки…", null, (_, _) => OpenSettings());
        menu.Items.Add("Перезагрузить настройки", null, (_, _) => ReloadConfig());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += OnTrayMouseClick;

        // Применяем настройки, которые влияют на runtime-объекты (таймеры и chord).
        ApplyRuntimeSettings();
        // При старте сразу вычисляем иконку/tooltip, чтобы не ждать первого тика таймера.
        UpdateTrayIconFromForegroundLayout(force: true);

        // Корректная утилизация ресурсов при завершении приложения.
        Application.ApplicationExit += (_, _) => DisposeTray();
    }

    /// <summary>
    /// Пытается загрузить иконку из "loose file" (рядом с .exe / в Assets).
    /// Возвращает null при отсутствии/битой иконке — это не ошибка: просто будет SystemIcons.Application.
    /// </summary>
    private static Icon? LoadTrayIcon(string fileName)
    {
        var path = AppPaths.FindLooseFile(fileName);
        if (path == null)
            return null;
        try
        {
            return new Icon(path);
        }
        catch
        {
            return null;
        }
    }

    private void ReloadConfig()
    {
        // Перечитываем INI и обновляем runtime-состояние.
        _config = AppConfig.Load(_iniPath);
        ApplyRuntimeSettings();
        UpdateTrayIconFromForegroundLayout(force: true);

        // Явный сигнал пользователю, что подхватились изменения из файла.
        _tray.ShowBalloonTip(
            2000,
            "LayoutGuard",
            "Настройки перечитаны из .ini",
            ToolTipIcon.Info);
    }

    private void OpenSettings()
    {
        // Модальный диалог: если пользователь нажал OK, берём сохранённую конфигурацию.
        using var f = new SettingsForm(_config, _iniPath);
        if (f.ShowDialog() == DialogResult.OK && f.SavedConfig != null)
        {
            _config = f.SavedConfig;
            ApplyRuntimeSettings();
            UpdateTrayIconFromForegroundLayout(force: true);
        }
    }

    private void ApplyRuntimeSettings()
    {
        // Пересобираем chord из строки. Даже если строка некорректна, получим безопасный default.
        _chordKeys = ChordKeyHelper.ParseChordOrDefault(_config.HotkeyChord);
        // Ограничиваем интервал, чтобы не спамить UI и не уходить в слишком редкое обновление.
        _trayIconTimer.Interval = Math.Clamp(_config.TrayIconRefreshMs, 50, 5000);
    }

    private void DisposeTray()
    {
        // Отписки/Dispose важны: NotifyIcon и Icon — это нативные ресурсы, которые могут "утечь",
        // если не освободить их корректно при выходе.
        _tray.MouseClick -= OnTrayMouseClick;
        _trayIconTimer.Stop();
        _trayIconTimer.Dispose();
        _checkTimer.Dispose();
        _hook.Dispose();
        _tray.Visible = false;
        _tray.Icon = SystemIcons.Application;
        _tray.Dispose();
        _iconRu?.Dispose();
        _iconEn?.Dispose();
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        // По ЛКМ пользователь ожидает быстрое действие.
        // Правая кнопка занята системным открытием контекстного меню NotifyIcon.
        if (e.Button != MouseButtons.Left)
            return;

        // Переключаем раскладку активного окна.
        // Если раскладок 0/1 или окно недоступно — метод вернёт false.
        if (!KeyboardLayoutHelper.TryActivateNextLayout())
            return;

        // После ручного переключения через трей подавляем "проверку по chord",
        // чтобы приложение не вмешалось сразу же (особенно если пользователь кликал во время работы хоткея).
        _suppressChecksUntilUtc = DateTime.UtcNow.AddMilliseconds(
            Math.Clamp(_config.CooldownAfterSwitchMs, 50, 5000));

        // Немедленно отражаем изменение в UI.
        UpdateTrayIconFromForegroundLayout(force: true);
    }

    /// <summary>
    /// Подтягивает раскладку активного окна и выставляет:
    /// - иконку RU/EN (если есть),
    /// - текст тултипа с подсказкой hotkey.
    /// </summary>
    private void UpdateTrayIconFromForegroundLayout(bool force = false)
    {
        var hkl = KeyboardLayoutHelper.GetActiveLayout();
        if (!force && hkl == _lastTrayHkl)
            return;
        _lastTrayHkl = hkl;

        Icon icon;
        string tip;

        var hk = _config.HotkeyChord;
        if (InputLanguageHelper.IsRussian(hkl))
        {
            icon = _iconRu ?? SystemIcons.Application;
            tip = $"LayoutGuard — раскладка: RU · {hk}";
        }
        else if (InputLanguageHelper.IsEnglish(hkl))
        {
            icon = _iconEn ?? SystemIcons.Application;
            tip = $"LayoutGuard — раскладка: EN · {hk}";
        }
        else
        {
            icon = SystemIcons.Application;
            tip = $"LayoutGuard — другая раскладка · {hk}";
        }

        _tray.Icon = icon;
        _tray.Text = tip;
    }

    private void OnKeyStateChanged()
    {
        // Хук говорит лишь "что-то нажали/отпустили". Точное состояние модификаторов читаем через GetAsyncKeyState.
        var chord = ChordKeyHelper.AllChordModifiersHeld(_chordKeys);

        // Фронт chord: запоминаем раскладку на старте.
        if (!_chordWasActive && chord)
            _hklAtChordStart = KeyboardLayoutHelper.GetActiveLayout();
        // Спад chord: пользователь отпустил сочетание — запускаем отложенную проверку.
        else if (_chordWasActive && !chord)
        {
            var captured = _hklAtChordStart;
            _checkTimer.Stop();
            _checkTimer.Interval = Math.Clamp(_config.CheckDelayMs, 10, 2000);
            // Tag используем как "передачу контекста" в tick, чтобы не держать отдельное поле/очередь.
            _checkTimer.Tag = captured;
            _checkTimer.Start();
        }

        _chordWasActive = chord;
    }

    private void OnCheckTimerTick(object? sender, EventArgs e)
    {
        // Таймер одноразовый: остановили в начале, чтобы tick не повторялся.
        _checkTimer.Stop();
        if (_checkTimer.Tag is not IntPtr start)
            return;

        // Cooldown: после переключения (наш/пользовательский) некоторое время не вмешиваемся.
        if (DateTime.UtcNow < _suppressChecksUntilUtc)
            return;

        // Если с момента старта chord раскладка уже изменилась — всё хорошо, ничего не делаем.
        var now = KeyboardLayoutHelper.GetActiveLayout();
        if (start != now)
            return;

        // Раскладка не изменилась → пробуем переключить "следующую" раскладку.
        if (!KeyboardLayoutHelper.TryActivateNextLayout())
            return;

        // После принудительного переключения ставим cooldown, иначе можно попасть в "пинг-понг",
        // если внешняя программа/система тут же вернёт раскладку назад.
        _suppressChecksUntilUtc = DateTime.UtcNow.AddMilliseconds(
            Math.Clamp(_config.CooldownAfterSwitchMs, 50, 5000));

        if (_config.ShowBalloonOnFix)
        {
            // Не спамим уведомлениями по умолчанию — это управляется настройкой.
            _tray.ShowBalloonTip(
                1500,
                "LayoutGuard",
                "Раскладка не сменилась — выполнено переключение.",
                ToolTipIcon.Info);
        }

        UpdateTrayIconFromForegroundLayout(force: true);
    }
}
