using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Логический журнал: непрерывная последовательность записей, разложенная по физическим сегментам.
/// </summary>
/// <remarks>
/// <para>
/// Контракт этапа S1 (<c>plan.md</c>, раздел контрактов). Отличается от <see cref="SegmentWriter"/>
/// и <see cref="SegmentReader"/> тем, что оперирует индексом записи, а не файлом: границы сегментов,
/// rollover, чтение из нескольких сегментов, усечение конфликтующего хвоста и хранение
/// (retention по снапшоту) - забота реализации.
/// </para>
/// <para>
/// Логики Raft здесь нет: хранилище не знает о ролях, выборах и rule коммита. Точку коммита оно
/// лишь запоминает (<see cref="MarkCommittedAsync"/>), потому что обязано отказаться усекать
/// закоммиченную историю (чек-лист R1, пункт 4).
/// </para>
/// <para>
/// Конкурентность (<c>S1-T08</c>, ADR <c>0011-log-store-concurrency.md</c>): мутации
/// (<see cref="AppendAsync()"/>, <see cref="FlushToDiskAsync"/>, <see cref="TruncateSuffixAsync"/>,
/// <see cref="MarkCommittedAsync"/>, <see cref="CompactAsync"/>, <see cref="DisposeAsync"/>)
/// сериализуются внутри реализации, а чтения воротца не берут и видят опубликованный снимок
/// каталога. Порядок вызовов по-прежнему на вызывающем: гонка двух append-ов за один индекс -
/// его ошибка, а не очередь внутри журнала.
/// </para>
/// <para>
/// Числа логического журнала для пустого журнала: <see cref="LastIndex"/> = 0,
/// <see cref="FirstIndex"/> = 1, <see cref="LastTerm"/> = 0 - ровно то, что
/// <see cref="LogIndex.Empty"/> называет "журнал пуст" (<c>LogIndex</c> нумерует записи с единицы).
/// </para>
/// </remarks>
public interface ILogStore : IAsyncDisposable
{
    /// <summary>
    /// Начало доступной истории: индекс первой записи, которую можно прочитать.
    /// </summary>
    /// <remarks>
    /// Учитывает снапшот: после компакции записи ниже снапшота логически недоступны, даже если
    /// физически ещё лежат в последнем оставшемся сегменте. Для пустого журнала - 1.
    /// </remarks>
    LogIndex FirstIndex { get; }

    /// <summary>Индекс последней записи журнала; 0, если журнал пуст.</summary>
    LogIndex LastIndex { get; }

    /// <summary>Терм последней записи журнала; 0, если журнал пуст.</summary>
    Term LastTerm { get; }

    /// <summary>Точка коммита: записи с индексом не выше неизменяемы.</summary>
    LogIndex CommittedIndex { get; }

    /// <summary>Дописывает записи с режимом долговечности по умолчанию.</summary>
    /// <param name="records">Записи в порядке возрастания индекса без дыр.</param>
    /// <param name="cancellationToken">Отмена между записями и до fsync.</param>
    ValueTask AppendAsync(IReadOnlyList<LogRecord> records, CancellationToken cancellationToken);

    /// <summary>Дописывает записи в журнал с указанным режимом долговечности.</summary>
    /// <param name="records">Записи в порядке возрастания индекса без дыр.</param>
    /// <param name="mode">Политика fsync.</param>
    /// <param name="cancellationToken">Отмена между записями и до fsync.</param>
    ValueTask AppendAsync(IReadOnlyList<LogRecord> records, DurabilityMode mode, CancellationToken cancellationToken);

    /// <summary>Отправляет на устройство всё, что записано в последний сегмент (fsync).</summary>
    /// <remarks>
    /// Нужен режиму <see cref="DurabilityMode.Periodic"/>, который не вызывает fsync сам: без этого
    /// метода такой журнал было бы невозможно сделать долговечным.
    /// </remarks>
    /// <param name="cancellationToken">Отмена до вызова fsync.</param>
    ValueTask FlushToDiskAsync(CancellationToken cancellationToken);

    /// <summary>Ищет запись по индексу.</summary>
    /// <param name="index">Индекс записи.</param>
    /// <param name="cancellationToken">Отмена до начала чтения.</param>
    /// <returns>
    /// Запись, «ещё нет» или «уже в снапшоте» (<see cref="LogLookupStatus"/>). Различать эти
    /// два отсутствия обязан контракт: по ним Raft решает, продолжать репликацию или ставить снапшот.
    /// </returns>
    ValueTask<LogLookupResult> GetAsync(LogIndex index, CancellationToken cancellationToken);

    /// <summary>Читает не больше <paramref name="maxCount"/> записей подряд, начиная с индекса.</summary>
    /// <param name="from">Индекс первой записи; начало доступной истории или больше.</param>
    /// <param name="maxCount">Предел числа записей; больше нуля.</param>
    /// <param name="cancellationToken">Отмена между записями.</param>
    /// <returns>Записи по возрастанию индекса; пустой список, если записей начиная с <paramref name="from"/> нет.</returns>
    ValueTask<IReadOnlyList<LogRecord>> ReadRangeAsync(LogIndex from, int maxCount, CancellationToken cancellationToken);

    /// <summary>Удаляет все записи с индексом не ниже указанного.</summary>
    /// <param name="from">Индекс первой удаляемой записи; строго выше <see cref="CommittedIndex"/>.</param>
    /// <param name="cancellationToken">Отмена до первого разрушающего действия.</param>
    /// <remarks>
    /// <para>
    /// Хвост обрезается физически: сегменты целиком выше точки обрезки удаляются, сегмент,
    /// содержащий <paramref name="from"/>, укорачивается до границы фрейма. Записи ниже
    /// <paramref name="from"/> остаются, журнал продолжается с этого же места
    /// (<c>plan.md</c>, конфликт Raft разрешается именно так).
    /// </para>
    /// <para>
    /// Обрезка закоммиченной истории - программная ошибка вызывающего, а не тихое усечение:
    /// <see cref="InvalidOperationException"/>. После первого разрушающего действия отмена
    /// не проверяется: журнал не должен остаться обрезанным наполовину.
    /// </para>
    /// </remarks>
    ValueTask TruncateSuffixAsync(LogIndex from, CancellationToken cancellationToken);

    /// <summary>Отмечает точку коммита: записи с индексом не выше неизменяемы.</summary>
    /// <param name="index">Новая точка коммита; не меньше текущей и не больше <see cref="LastIndex"/>.</param>
    /// <param name="cancellationToken">Отмена до изменения.</param>
    /// <remarks>
    /// Точка коммита живёт в памяти процесса: в формате сегмента её нет (ADR 0003), а выводить
    /// её из диска нельзя. Значит после рестарта <see cref="CommittedIndex"/> равен нулю, и запрет
    /// обрезать закоммиченное защищает только внутри одного запуска. Это осознанно: настоящая
    /// точка коммита после рестарта восстанавливается из AppendEntries лидера, а значение здесь -
    /// ловушка для ошибки вызывающего, а не хранилище гарантии R4 (ADR 0007).
    /// </remarks>
    ValueTask MarkCommittedAsync(LogIndex index, CancellationToken cancellationToken);

    /// <summary>Удаляет сегменты, целиком покрытые снапшотом.</summary>
    /// <param name="snapshotIndex">Индекс последней записи, вошедшей в снапшот.</param>
    /// <param name="cancellationToken">Отмена до первого удаления.</param>
    /// <remarks>
    /// Хранение (retention): снапшот уже содержит состояние на этот индекс, поэтому записи
    /// до него в журнале больше не нужны (инвариант R4 - "снапшот плюс хвост лога"). Последний
    /// сегмент не удаляется никогда: он продолжает журнал, и его записи нужны Raft для проверки
    /// <c>prevLogTerm</c>.
    /// </remarks>
    ValueTask CompactAsync(LogIndex snapshotIndex, CancellationToken cancellationToken);
}
