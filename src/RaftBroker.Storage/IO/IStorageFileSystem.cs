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
}
