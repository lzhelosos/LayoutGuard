using System.Runtime.InteropServices;
using System.Threading;

namespace Punto;

/// <summary>
/// Помощник для работы с раскладками клавиатуры (HKL) в Windows.
/// 
/// Заметка о модели Windows:
/// - раскладка привязана к *потоку* GUI (thread input locale), а не "глобально" на процесс;
/// - у активного (foreground) окна может быть своя раскладка/поток.
/// 
/// Поэтому "переключить раскладку" корректнее всего именно в контексте окна с фокусом,
/// через сообщения WM_INPUTLANGCHANGEREQUEST или активацию HKL с учётом thread input.
/// </summary>
internal static class KeyboardLayoutHelper
{
    /// <summary>
    /// Специальное значение HKL_NEXT (1): "следующая раскладка в системном цикле",
    /// по смыслу аналог Alt+Shift / Win+Space (зависит от настроек Windows).
    /// </summary>
    private static readonly IntPtr HklNext = new(1);

    /// <summary>Флаги для ActivateKeyboardLayout.</summary>
    private const uint KlfActivate = 0x00000001;
    private const uint KlfSetForProcess = 0x00000100;

    /// <summary>
    /// WM_INPUTLANGCHANGEREQUEST — просьба к окну переключить раскладку.
    /// lParam: HKL (или HKL_NEXT).
    /// </summary>
    private const uint WmInputlangchangerequest = 0x0050;

    /// <summary>
    /// Возвращает HKL (handle keyboard layout) для потока активного окна.
    /// Если foreground-окна нет (редко), возвращаем раскладку текущего потока.
    /// </summary>
    public static IntPtr GetActiveLayout()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return GetKeyboardLayout(0);
        var tid = GetWindowThreadProcessId(hwnd, out _);
        return GetKeyboardLayout(tid);
    }

    /// <summary>
    /// Переключает раскладку в окне с фокусом.
    /// 
    /// Возвращает true только если HKL действительно изменился (то есть переключение "сработало"),
    /// иначе false (например, раскладка одна, нет foreground-окна, или приложение-цель игнорирует запрос).
    /// 
    /// Почему здесь несколько попыток:
    /// - разные приложения по-разному реагируют на WM_INPUTLANGCHANGEREQUEST;
    /// - иногда нужно отправлять сообщение не на hwnd foreground, а на focus-контрол;
    /// - ActivateKeyboardLayout работает только при корректной привязке thread input (AttachThreadInput).
    /// </summary>
    public static bool TryActivateNextLayout()
    {
        // Если раскладка одна — переключать нечего.
        var count = GetKeyboardLayoutList(0, null);
        if (count <= 1)
            return false;

        // Переключаем именно раскладку foreground-окна.
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        var fgThread = GetWindowThreadProcessId(hwnd, out _);
        if (fgThread == 0)
            return false;

        // Поток нашей утилиты (UI thread WinForms).
        var selfThread = GetCurrentThreadId();
        // Что было "до" — по нему определяем факт изменения.
        var before = GetActiveLayout();

        // Явно получаем список раскладок и вычисляем "следующую" относительно текущей.
        // Это нужно как fallback: HKL_NEXT не всегда приводит к смене в конкретном окне.
        var list = new IntPtr[count];
        _ = GetKeyboardLayoutList(count, list);
        var nextExplicit = GetNextLayoutHandle(list, count, before);

        // Попытка 1: попросить окно переключиться на "следующую" в системном цикле.
        RequestNextLayoutViaWindowMessage(hwnd, fgThread, selfThread, HklNext);
        if (WaitUntilLayoutDiffers(before))
            return true;

        // Попытка 2: попросить окно переключиться на конкретный HKL (следующий по списку).
        RequestNextLayoutViaWindowMessage(hwnd, fgThread, selfThread, nextExplicit);
        if (WaitUntilLayoutDiffers(before))
            return true;

        // Попытка 3: активировать HKL напрямую.
        // Для чужого GUI-потока чаще нужно временно "приклеиться" к нему через AttachThreadInput.
        if (fgThread == selfThread)
        {
            _ = ActivateKeyboardLayout(nextExplicit, KlfActivate);
        }
        else if (AttachThreadInput(selfThread, fgThread, true))
        {
            try
            {
                _ = ActivateKeyboardLayout(nextExplicit, KlfActivate | KlfSetForProcess);
            }
            finally
            {
                _ = AttachThreadInput(selfThread, fgThread, false);
            }
        }

        if (WaitUntilLayoutDiffers(before))
            return true;

        // Попытка 4: асинхронная просьба окну (SendNotifyMessage).
        // Иногда приложения реагируют на notify, даже если синхронный SendMessage был проигнорирован.
        _ = SendNotifyMessage(hwnd, WmInputlangchangerequest, IntPtr.Zero, nextExplicit);
        return WaitUntilLayoutDiffers(before);
    }

    /// <summary>
    /// Вычисляет "следующую" раскладку по списку GetKeyboardLayoutList:
    /// - сначала по точному совпадению HKL,
    /// - затем по совпадению младших 16 бит (LANGID), если HKL-значения отличаются "вариантами".
    /// </summary>
    private static IntPtr GetNextLayoutHandle(IntPtr[] list, int count, IntPtr current)
    {
        var idx = -1;
        for (var i = 0; i < count; i++)
        {
            if (list[i] == current)
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            var curLow = (ushort)(current.ToInt64() & 0xFFFF);
            for (var i = 0; i < count; i++)
            {
                var low = (ushort)(list[i].ToInt64() & 0xFFFF);
                if (low == curLow)
                {
                    idx = i;
                    break;
                }
            }
        }

        if (idx < 0)
            idx = 0;

        return list[(idx + 1) % count];
    }

    /// <summary>
    /// Небольшое ожидание, пока Windows/приложение реально применит новую раскладку.
    /// 
    /// Переключение раскладки часто асинхронно, поэтому проверка "сразу после вызова" ненадёжна.
    /// Мы ждём короткое время (несколько десятков миллисекунд) и смотрим, изменился ли HKL.
    /// </summary>
    private static bool WaitUntilLayoutDiffers(IntPtr previous)
    {
        for (var i = 0; i < 8; i++)
        {
            Thread.Sleep(12);
            if (GetActiveLayout() != previous)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Отправляет WM_INPUTLANGCHANGEREQUEST в "правильную" цель:
    /// - если мы в том же GUI-потоке, можно использовать GetFocus();
    /// - если в другом потоке — временно делаем AttachThreadInput, чтобы GetFocus() вернул фокус того потока.
    /// </summary>
    private static void RequestNextLayoutViaWindowMessage(
        IntPtr hwndForeground,
        uint fgThread,
        uint selfThread,
        IntPtr hklOrNext)
    {
        if (fgThread == selfThread)
        {
            var target = GetFocus();
            if (target == IntPtr.Zero)
                target = hwndForeground;
            _ = SendMessage(target, WmInputlangchangerequest, IntPtr.Zero, hklOrNext);
            return;
        }

        if (AttachThreadInput(selfThread, fgThread, true))
        {
            try
            {
                var target = GetFocus();
                if (target == IntPtr.Zero)
                    target = hwndForeground;
                _ = SendMessage(target, WmInputlangchangerequest, IntPtr.Zero, hklOrNext);
            }
            finally
            {
                _ = AttachThreadInput(selfThread, fgThread, false);
            }

            return;
        }

        // Fallback: если AttachThreadInput не удалось (редко), отправляем прямо на foreground hwnd.
        _ = SendMessage(hwndForeground, WmInputlangchangerequest, IntPtr.Zero, hklOrNext);
    }

    // --- WinAPI imports ---
    // Здесь собраны только те вызовы, которые реально используются в алгоритме выше.

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int nBuff, IntPtr[]? lpList);

    [DllImport("user32.dll")]
    private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);
}
