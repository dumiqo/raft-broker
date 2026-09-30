namespace RaftBroker.Storage.Log;

/// <summary>
/// Что восстановление (<c>S1-T05</c>) сделало с сегментом.
/// </summary>
/// <remarks>
/// Статус описывает действие, а не вид повреждения: <see cref="Intact"/> и <see cref="TruncatedTail"/>
/// - оба успешны для старта, <see cref="Removed"/> тоже (оборванное создание), а <see cref="Refused"/>
/// означает, что узел стартовать не должен. Вид повреждения остаётся в <see cref="SegmentRecoveryResult.Reason"/>:
/// отчёт обязан объяснять решение, а не только называть его.
/// </remarks>
public enum SegmentRecoveryStatus
{
    /// <summary>Сегмент разобран до конца файла: на диске ничего не менялось.</summary>
    Intact,

    /// <summary>Оборванный хвост последнего сегмента усечён до последней валидной записи (§9.2).</summary>
    TruncatedTail,

    /// <summary>Файл короче заголовка у последнего сегмента удалён как оборванное создание (§9.3).</summary>
    Removed,

    /// <summary>Порча: старт запрещён, диск не тронут (§9.3, §9.4).</summary>
    Refused,
}
