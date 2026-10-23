namespace RaftBroker.Storage.IO;

/// <summary>
/// Фабрика файлов хранилища: единственная точка, где хранилище обращается к файловой системе.
/// </summary>
/// <remarks>
/// Отдельный интерфейс от <see cref="IStorageFile"/> (а не статические методы) нужен тестам:
/// дубль из <c>RaftBroker.TestKit</c> считает операции и умеет падать на заданной. Создание
/// файла - семантика "только новый" (<c>CreateNew</c>): перезапись существующего сегмента
/// форматом запрещена (сегмент не переименовывается и не переписывается), поэтому потеря
/// существующего файла обязана быть видна как ошибка, а не как тихая замена.
/// <para>
/// Читающая половина (<see cref="OpenReadAsync"/>) добавлена в <c>S1-T04</c>: она нужна
/// не только ради наблюдаемости, но и потому, что писатель держит файл открытым
/// (<see cref="IStorageFile"/>), и без общего шва чтение сегмента в тесте потребовало бы
/// второго, независимого доступа к тем же байтам.
/// </para>
/// </remarks>
public interface IStorageFileSystem
{
    /// <summary>Создаёт новый файл и открывает его на запись.</summary>
    /// <param name="path">Полный или относительный путь; родительские каталоги создаются.</param>
    /// <param name="cancellationToken">Отмена до создания файла.</param>
    /// <returns>Открытый на запись файл.</returns>
    /// <exception cref="IOException">Файл уже существует.</exception>
    ValueTask<IStorageFile> CreateAsync(string path, CancellationToken cancellationToken);

    /// <summary>Открывает существующий файл на чтение.</summary>
    /// <param name="path">Полный или относительный путь.</param>
    /// <param name="cancellationToken">Отмена до открытия файла.</param>
    /// <returns>Открытый на чтение файл со снимком длины на момент открытия.</returns>
    /// <exception cref="FileNotFoundException">Файла нет.</exception>
    ValueTask<IStorageReadFile> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Открывает существующий файл на дозапись: указатель в конце файла.</summary>
    /// <param name="path">Полный или относительный путь.</param>
    /// <param name="cancellationToken">Отмена до открытия файла.</param>
    /// <returns>Открытый на запись файл; существующее содержимое сохраняется.</returns>
    /// <remarks>
    /// Добавлено в <c>S1-T06</c>: журнал продолжает последний сегмент, а не начинает новый файл,
    /// после усечения конфликтующего хвоста (<c>TruncateSuffixAsync</c>) и после перезапуска.
    /// Начать новый сегмент на том же месте нельзя: сегмент с базовым индексом
    /// <c>lastIndex + 1</c> уже существует, а <see cref="CreateAsync"/> имеет семантику
    /// "только новый". Дозапись при этом не ослабляет §7 формата: имя сегмента по-прежнему
    /// не меняется, меняется только его длина.
    /// </remarks>
    /// <exception cref="FileNotFoundException">Файла нет.</exception>
    ValueTask<IStorageFile> OpenAppendAsync(string path, CancellationToken cancellationToken);

    /// <summary>Перечисляет файлы каталога.</summary>
    /// <param name="directory">Каталог; отсутствующий каталог - пустой список, а не ошибка.</param>
    /// <param name="cancellationToken">Отмена до перечисления.</param>
    /// <returns>
    /// Имена файлов (без пути), отсортированные по возрастанию порядковых значений: порядок
    /// результата не зависит от файловой системы и потому пригоден для проверок в тестах.
    /// </returns>
    /// <remarks>
    /// Добавлено в <c>S1-T06</c>: индекс сегментов строится перечислением каталога
    /// (§9.5 шаг 1), а не доверенным файлом индекса (открытое решение 2 этапа). Каталог, которого
    /// ещё нет, - это пустой журнал: узел создаёт его при первой записи. Подкаталоги
    /// не возвращаются: §9.5 говорит о файлах.
    /// </remarks>
    ValueTask<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken);

    /// <summary>Укорачивает существующий файл до указанной длины и сбрасывает изменение на устройство.</summary>
    /// <param name="path">Полный или относительный путь.</param>
    /// <param name="length">Новая длина файла; не больше текущей.</param>
    /// <param name="cancellationToken">Отмена до изменения файла.</param>
    /// <remarks>
    /// Добавлено в <c>S1-T05</c>: восстановление обязано физически убрать оборванный хвост
    /// последнего сегмента (§9.2), иначе писатель допишет фрейм после мусора и сегмент перестанет
    /// читаться. Операция только укорачивает: удлинение означало бы запись байтов, которых никто
    /// не проверял, поэтому <paramref name="length"/> больше текущей длины - ошибка вызывающего.
    /// </remarks>
    /// <exception cref="FileNotFoundException">Файла нет.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> больше текущей длины файла.</exception>
    ValueTask TruncateAsync(string path, long length, CancellationToken cancellationToken);

    /// <summary>Удаляет файл.</summary>
    /// <param name="path">Полный или относительный путь.</param>
    /// <param name="cancellationToken">Отмена до удаления файла.</param>
    /// <remarks>
    /// Добавлено в <c>S1-T05</c>: оборванное создание сегмента (§9.3) чинится удалением файла,
    /// а не игнорированием - иначе посторонний файл в каталоге ломает непрерывность индексов
    /// при каждом старте. Отсутствие файла не ошибка: повторный старт после падения между
    /// решением и удалением обязан просто повторить операцию.
    /// </remarks>
    ValueTask DeleteAsync(string path, CancellationToken cancellationToken);
}
