namespace Punto;

/// <summary>
/// Простая форма настроек (WinForms), которая:
/// - показывает текущие значения из AppConfig,
/// - валидирует ввод (особенно HotkeyChord),
/// - сохраняет итог в INI через AppConfig.Save.
/// 
/// Форма сделана полностью кодом (без Designer), чтобы проект оставался компактным и portable.
/// </summary>
internal sealed class SettingsForm : Form
{
    // Поля элементов управления держим в полях класса,
    // чтобы можно было прочитать их значения при нажатии OK.
    private readonly NumericUpDown _numCheckDelay;
    private readonly NumericUpDown _numCooldown;
    private readonly NumericUpDown _numTrayRefresh;
    private readonly CheckBox _chkBalloon;
    private readonly ComboBox _comboHotkey;
    private readonly string _iniPath;

    /// <summary>
    /// Если пользователь нажал OK и сохранение прошло успешно, сюда кладём сохранённый конфиг.
    /// Если Cancel/закрытие — остаётся null.
    /// </summary>
    public AppConfig? SavedConfig { get; private set; }

    public SettingsForm(AppConfig config, string iniPath)
    {
        _iniPath = iniPath;

        // Базовые настройки окна: фиксированный диалог, без max/min, по центру.
        Text = "LayoutGuard — настройки";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(440, 330);
        Padding = new Padding(12);

        // Небольшой "hint"-текст рисуем курсивом и серым — как подсказку, а не как основную информацию.
        var tipFont = new Font(Font, FontStyle.Italic);

        // --- CheckDelayMs ---
        var lblCheck = new Label
        {
            AutoSize = true,
            Location = new Point(0, 8),
            Text = "Задержка после отпускания сочетания перед проверкой (мс):",
        };
        _numCheckDelay = new NumericUpDown
        {
            Location = new Point(0, 28),
            Width = 120,
            Minimum = 10,
            Maximum = 2000,
            Increment = 10,
            Value = Math.Clamp(config.CheckDelayMs, 10, 2000),
        };

        // --- CooldownAfterSwitchMs ---
        var lblCooldown = new Label
        {
            AutoSize = true,
            Location = new Point(0, 60),
            Text = "Пауза после принудительного переключения раскладки (мс):",
        };
        _numCooldown = new NumericUpDown
        {
            Location = new Point(0, 80),
            Width = 120,
            Minimum = 50,
            Maximum = 5000,
            Increment = 50,
            Value = Math.Clamp(config.CooldownAfterSwitchMs, 50, 5000),
        };

        // --- TrayIconRefreshMs ---
        var lblTray = new Label
        {
            AutoSize = true,
            Location = new Point(0, 112),
            Text = "Интервал обновления иконки в трее (мс):",
        };
        _numTrayRefresh = new NumericUpDown
        {
            Location = new Point(0, 132),
            Width = 120,
            Minimum = 50,
            Maximum = 5000,
            Increment = 50,
            Value = Math.Clamp(config.TrayIconRefreshMs, 50, 5000),
        };

        // --- ShowBalloonOnFix ---
        _chkBalloon = new CheckBox
        {
            AutoSize = true,
            Location = new Point(0, 168),
            Text = "Показывать уведомление при исправлении раскладки",
            Checked = config.ShowBalloonOnFix,
        };

        // --- HotkeyChord (chord модификаторов) ---
        var lblHotkey = new Label
        {
            AutoSize = true,
            Location = new Point(0, 198),
            Text = "Сочетание клавиш для отслеживания (все указанные модификаторы вместе):",
        };
        _comboHotkey = new ComboBox
        {
            Location = new Point(0, 218),
            Width = 416,
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
        };

        // Добавляем пресеты, чтобы пользователь мог выбрать "из коробки",
        // но оставляем возможность ручного ввода (DropDownStyle.DropDown).
        foreach (var preset in SettingsFormPresets.HotkeyPresets)
            _ = _comboHotkey.Items.Add(preset);

        // Если текущая строка совпадает с одним из пресетов — выберем его.
        // Иначе покажем пользовательский текст (или дефолт, если пусто).
        var chord = config.HotkeyChord.Trim();
        var match = _comboHotkey.Items.Cast<string>().FirstOrDefault(x => x.Equals(chord, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            _comboHotkey.SelectedItem = match;
        else
            _comboHotkey.Text = string.IsNullOrEmpty(chord) ? ChordKeyHelper.DefaultChordIni : chord;

        // Подсказка по формату хоткея и месту сохранения.
        var lblHint = new Label
        {
            AutoSize = false,
            Location = new Point(0, 246),
            Width = 416,
            Height = 44,
            Font = tipFont,
            ForeColor = SystemColors.GrayText,
            Text = "Допустимые имена: Shift, Ctrl, Alt, Win. Пример: Ctrl+Alt. Настройки сохраняются в LayoutGuard.ini.",
        };

        // Кнопки OK/Cancel — стандартный UX диалога.
        var btnOk = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.None,
            Location = new Point(268, 292),
            Width = 80,
        };
        var btnCancel = new Button
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Location = new Point(356, 292),
            Width = 80,
        };
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        // Добавляем контролы в форму в визуальном порядке сверху вниз.
        Controls.Add(lblCheck);
        Controls.Add(_numCheckDelay);
        Controls.Add(lblCooldown);
        Controls.Add(_numCooldown);
        Controls.Add(lblTray);
        Controls.Add(_numTrayRefresh);
        Controls.Add(_chkBalloon);
        Controls.Add(lblHotkey);
        Controls.Add(_comboHotkey);
        Controls.Add(lblHint);
        Controls.Add(btnOk);
        Controls.Add(btnCancel);

        // На OK делаем явную валидацию и сохранение.
        btnOk.Click += (_, _) =>
        {
            if (TryCommit())
                DialogResult = DialogResult.OK;
        };
    }

    /// <summary>
    /// Валидация и фиксация настроек:
    /// - проверяем chord (минимум 2 модификатора),
    /// - нормализуем строку (канонический порядок),
    /// - сохраняем в INI и отдаём SavedConfig.
    /// </summary>
    private bool TryCommit()
    {
        var hotkeyText = _comboHotkey.Text.Trim();
        if (!ChordKeyHelper.TryParseChord(hotkeyText, out var parsed))
        {
            _ = MessageBox.Show(
                "Укажите сочетание из минимум двух модификаторов: Shift, Ctrl, Alt, Win.\nПример: Shift+Alt или Ctrl+Alt",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        var normalized = ChordKeyHelper.ToCanonicalIniString(parsed);

        // Собираем новый конфиг из значений формы.
        var cfg = new AppConfig
        {
            CheckDelayMs = (int)_numCheckDelay.Value,
            CooldownAfterSwitchMs = (int)_numCooldown.Value,
            TrayIconRefreshMs = (int)_numTrayRefresh.Value,
            ShowBalloonOnFix = _chkBalloon.Checked,
            HotkeyChord = normalized,
        };

        // Пишем в файл сразу при OK, чтобы приложение могло подхватить изменения "на месте".
        AppConfig.Save(_iniPath, cfg);
        SavedConfig = cfg;
        return true;
    }
}

/// <summary>
/// Набор пресетов для ComboBox хоткея.
/// 
/// Здесь есть и "Shift+Alt", и "Alt+Shift": парсер нормализует и приведёт к каноническому виду.
/// </summary>
internal static class SettingsFormPresets
{
    internal static readonly string[] HotkeyPresets =
    {
        "Shift+Alt",
        "Shift+Ctrl",
        "Shift+Win",
        "Alt+Shift",
        "Alt+Ctrl",
        "Alt+Win",
        "Ctrl+Shift",
        "Ctrl+Alt",
        "Ctrl+Win",
        "Win+Shift",
        "Win+Alt",
        "Win+Ctrl",
    };
}
