namespace Punto;

/// <summary>
/// Специализированное исключение "не удалось установить глобальный keyboard hook".
/// 
/// Зачем отдельный тип:
/// - удобнее отличать "не стартанули из-за хуков" от прочих исключений,
/// - Program.cs может поймать только это исключение и молча завершиться
///   (сообщение пользователю уже показывается в момент ошибки).
/// </summary>
internal sealed class HookException : Exception
{
    public HookException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
