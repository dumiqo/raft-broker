namespace RaftBroker.Storage.IO;

/// <summary>
/// Открытый на запись файл хранилища - шов между логикой долговечности и файловой системой.
/// </summary>
/// <remarks>
/// <para>
/// Абстракция существует ради двух вещей, которые через <see cref="System.IO.FileStream"/>
/// напрямую не наблюдаются, а чек-лист ревью R1 требует их проверки (S1-T03):
/// </para>
/// <list type="number">
/// <item>счётчик вызовов fsync - <c>DurabilityMode</c> обязан влиять на реальные вызовы,
/// а не только на комментарий;</item>
/// <item>инъекция отказа на конкретной операции - crash-тесты S1-T05 и S1-T09 должны падать
/// в заданной точке записи, а не только "иногда".</item>
/// </list>
/// <para>
/// Реализации: <c>SystemStorageFile</c> в рантайме и <c>FakeStorageFile</c> из
/// <c>RaftBroker.TestKit</c> (ADR 0002) в тестах. Метод <see cref="FlushToDiskAsync"/>
/// отделён от <see cref="WriteAsync"/> намеренно: запись может попасть в буфер процесса,
/// в буфер ядра или на устройство - это три разные границы отказов, и политики долговечности
/// различают их.
/// </para>
/// </remarks>
public interface IStorageFile : IAsyncDisposable
{
    /// <summary>Путь файла: попадает в диагностику и в отчёт о восстановлении.</summary>
    string Path { get; }

    /// <summary>Дописывает байты в конец файла.</summary>
    /// <param name="buffer">Байты для записи.</param>
    /// <param name="cancellationToken">Отмена до начала записи.</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// Сбрасывает буферы процесса и ядра на устройство (fsync/fdatasync).
    /// </summary>
    /// <remarks>
    /// После возврата из этого метода данные обязаны пережить падение процесса и машины.
    /// Операция неотменяема на полпути: отмена проверяется до вызова fsync, а не во время него.
    /// </remarks>
    /// <param name="cancellationToken">Отмена до начала fsync.</param>
    ValueTask FlushToDiskAsync(CancellationToken cancellationToken);

    /// <summary>Укорачивает открытый файл до указанной длины и сбрасывает изменение на устройство.</summary>
    /// <param name="length">Новая длина файла; не больше текущей.</param>
    /// <param name="cancellationToken">Отмена до изменения файла.</param>
    /// <remarks>
    /// Добавлено в <c>S1-T06</c>: незавершённая запись фрейма (ошибка ввода-вывода, отмена
    /// посреди записи) оставляет в конце сегмента мусор, и писатель обязан вернуть файл к последней
    /// целой границе. Иначе следующая запись допишется после мусора и сегмент перестанет читаться
    /// (§9.4), а незаметный мусор в середине журнала - это молчаливая потеря данных.
    /// Усечение идёт через собственный дескриптор, а не через <c>IStorageFileSystem.TruncateAsync</c>:
    /// файл уже открыт писателем, и второй дескриптор на запись система не даст.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> больше текущей длины файла.</exception>
    ValueTask TruncateAsync(long length, CancellationToken cancellationToken);
}
