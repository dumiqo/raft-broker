namespace RaftBroker.Core.Time;

/// <summary>
/// Одноразовый таймер, поставленный через <see cref="IClock.Schedule"/>.
/// </summary>
/// <remarks>
/// <see cref="Cancel"/> и <see cref="Dispose"/> делают одно и то же; <see cref="Dispose"/>
/// существует, чтобы таймер можно было держать в <c>using</c>. Отмена уже сработавшего
/// таймера - не ошибка.
/// </remarks>
public interface ITimer : IDisposable
{
    /// <summary>Ждёт ли таймер срабатывания. После срабатывания или отмены - <c>false</c>.</summary>
    bool IsActive { get; }

    /// <summary>Отменяет таймер: колбэк не будет вызван, если ещё не был.</summary>
    void Cancel();
}
