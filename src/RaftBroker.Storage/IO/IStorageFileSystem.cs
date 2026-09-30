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
/// </remarks>
public interface IStorageFileSystem
{
    /// <summary>Создаёт новый файл и открывает его на запись.</summary>
    /// <param name="path">Полный или относительный путь; родительские каталоги создаются.</param>
    /// <param name="cancellationToken">Отмена до создания файла.</param>
    /// <returns>Открытый на запись файл.</returns>
    /// <exception cref="IOException">Файл уже существует.</exception>
    ValueTask<IStorageFile> CreateAsync(string path, CancellationToken cancellationToken);
}
