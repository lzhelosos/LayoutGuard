using System.Runtime.InteropServices;

namespace Punto;

/// <summary>
/// Минимальная обёртка над низкоуровневым глобальным хуком клавиатуры (WH_KEYBOARD_LL).
/// 
/// Важно:
/// - Мы НЕ пытаемся "обработать" конкретные клавиши здесь и не блокируем ввод.
/// - Хук нужен только как триггер: "состояние клавиш изменилось".
///   Далее точное состояние модификаторов читается через GetAsyncKeyState (см. ChordKeyHelper).
/// 
/// Такой подход проще и надёжнее:
/// - не зависит от раскладок/scan-кодов,
/// - не требует сложной фильтрации,
/// - избегает логики "нажат/отпущен" в двух разных местах.
/// </summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    /// <summary>WH_KEYBOARD_LL — low-level hook клавиатуры.</summary>
    private const int WhKeyboardLl = 13;

    /// <summary>Сообщения клавиатуры, которые считаем "изменением состояния".</summary>
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;

    /// <summary>Дескриптор установленного hook'а (0 — не установлен).</summary>
    private IntPtr _hook = IntPtr.Zero;

    /// <summary>
    /// Делегат нужно держать в поле, иначе GC может собрать его,
    /// и callback начнёт указывать "в никуда" → падение процесса.
    /// </summary>
    private readonly HookProc _proc;

    public LowLevelKeyboardHook()
    {
        _proc = HookCallback;
    }

    /// <summary>
    /// Событие "клавиатурное сообщение пришло".
    /// Это не означает, что модификатор точно изменился, но достаточно, чтобы перепроверить chord.
    /// </summary>
    public event Action? KeyStateChanged;

    /// <summary>
    /// Устанавливает глобальный low-level hook.
    /// 
    /// Для WH_KEYBOARD_LL в managed-приложении обычно передают handle текущего модуля и threadId=0 (глобально).
    /// </summary>
    public void Install()
    {
        if (_hook != IntPtr.Zero)
            return;
        var mod = GetModuleHandle(null);
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, mod, 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Не удалось установить низкоуровневый хук клавиатуры.");
    }

    /// <summary>
    /// Снимает hook. Вызывать обязательно, чтобы не оставлять нативные ресурсы висеть до завершения процесса.
    /// </summary>
    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Callback Windows для low-level hook.
    /// 
    /// Правила:
    /// - если nCode &lt; 0: нельзя обрабатывать, просто передать дальше;
    /// - обязательно вызывать CallNextHookEx, иначе можно ломать цепочку хуков в системе.
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            if (msg is WmKeydown or WmKeyup or WmSyskeydown or WmSyskeyup)
                KeyStateChanged?.Invoke();
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Сигнатура callback'а hook-процедуры.</summary>
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>Ставит hook указанного типа.</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    /// <summary>Снимает ранее установленный hook.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>Передаёт сообщение следующему hook в цепочке.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>Возвращает handle модуля. null = текущий exe-модуль.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName); // null = модуль текущего .exe
}
