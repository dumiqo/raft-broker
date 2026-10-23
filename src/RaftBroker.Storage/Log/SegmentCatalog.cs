using RaftBroker.Core.Primitives;
using RaftBroker.Storage.IO;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Каталог сегментов журнала: перечисление, порядок и проверка того, что файлы образуют
/// один непрерывный журнал (§9.4, §9.5).
/// </summary>
/// <remarks>
/// <para>
/// Правила «что такое корректный каталог» живут здесь, а не в <see cref="LogStore"/>:
/// их два потребителя - открытие хранилища (<c>S1-T06</c>) и восстановление при старте
/// (<c>S1-T07</c>), и две копии одной нормы разъехались бы на первом же уточнении.
/// Каталог ничего не чинит и ничего не решает о судьбе файла: он лишь сообщает, что нашёл,
/// или на каком файле журнал перестал быть непрерывным.
/// </para>
/// <para>
/// Читателей каталог не держит: каждый сегмент открывается, сканируется и закрывается.
/// Это единственное место, где видно весь каталог, поэтому именно здесь выясняется,
/// какой сегмент последний (<see cref="SegmentKind.Last"/>) и можно ли его хвост усекать.
/// </para>
/// <para>
/// Логика Raft сюда не входит: каталог не знает ни про термы записей, ни про точку коммита,
/// ни про снапшот.
/// </para>
/// </remarks>
public static class SegmentCatalog
{
    /// <summary>Читает каталог и проверяет, что сегменты образуют непрерывный журнал.</summary>
    /// <param name="fileSystem">Файловая система: рантайм-реализация или тестовый дубль.</param>
    /// <param name="directory">Каталог журнала; отсутствующий каталог - пустой журнал.</param>
    /// <param name="cancellationToken">Отмена между сегментами.</param>
    /// <returns>Сегменты по порядку либо причина, по которой каталог не является журналом.</returns>
    /// <remarks>
    /// Проверки - из §9.5 и §9.4: имя файла обязано быть именем сегмента, каждый сегмент обязан
    /// разбираться до конца файла, диапазоны обязаны быть непрерывными, а пустой сегмент -
    /// только последним (§8). Первый сегмент может начинаться не с единицы: историю ниже могла
    /// убрать компакция, и сверять её обязан тот, кто знает снапшот.
    /// </remarks>
    public static async ValueTask<SegmentCatalogResult> ReadAsync(
        IStorageFileSystem fileSystem,
        string directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var files = await fileSystem.ListFilesAsync(directory, cancellationToken).ConfigureAwait(false);
        var parsed = new List<(LogIndex BaseIndex, string Path)>(files.Count);

        foreach (var name in files)
        {
            if (!LogSegmentFormat.TryParseFileName(name, out var fileBaseIndex))
            {
                return SegmentCatalogResult.Corrupt(
                    Path.Combine(directory, name),
                    "имя файла не является именем сегмента журнала (§7): посторонний файл в каталоге журнала (§9.5 шаг 1).");
            }

            parsed.Add((fileBaseIndex, Path.Combine(directory, name)));
        }

        parsed.Sort((left, right) => left.BaseIndex.CompareTo(right.BaseIndex));

        var entries = new List<SegmentCatalogEntry>(parsed.Count);

        for (var position = 0; position < parsed.Count; position++)
        {
            var (baseIndex, path) = parsed[position];
            var kind = position == parsed.Count - 1 ? SegmentKind.Last : SegmentKind.Sealed;
            var open = await SegmentReader.OpenAsync(fileSystem, path, cancellationToken).ConfigureAwait(false);

            // Короткий последний файл - оборванное создание сегмента, его удалит восстановление (§9.3).
            // Нечитаемый заголовок и всё, что не сходится в запечатанном сегменте, - отказ (§9.3, §9.4).
            if (!open.IsSuccess)
            {
                return open.Status == LogReadStatus.Truncated && kind == SegmentKind.Last
                    ? SegmentCatalogResult.Truncated(path, open.Reason)
                    : SegmentCatalogResult.Corrupt(path, open.Reason);
            }

            var reader = open.Reader!;

            try
            {
                var scan = await reader.ScanAsync(cancellationToken).ConfigureAwait(false);

                if (!scan.IsComplete)
                {
                    return kind == SegmentKind.Last
                        ? SegmentCatalogResult.Truncated(path, scan.Reason)
                        : SegmentCatalogResult.Corrupt(path, scan.Reason);
                }

                if (entries.Count > 0)
                {
                    var expected = entries[^1].LastIndex!.Value.Next();

                    if (baseIndex != expected)
                    {
                        return SegmentCatalogResult.Corrupt(
                            path,
                            $"базовый индекс {baseIndex} не продолжает предыдущий сегмент: ожидался {expected} (непрерывность диапазона сегментов, §9.4).");
                    }
                }

                // Предыдущий сегмент не последний, значит он не пуст: LastIndex у него есть.
                if (scan.FrameCount == 0 && kind != SegmentKind.Last)
                {
                    return SegmentCatalogResult.Corrupt(
                        path,
                        "пустой сегмент (только заголовок) не является последним: §8 разрешает такой файл только последним.");
                }

                entries.Add(new SegmentCatalogEntry
                {
                    BaseIndex = baseIndex,
                    LastIndex = scan.LastIndex,
                    LastTerm = scan.LastTerm,
                    Length = reader.Length,
                    Path = path,
                    Kind = kind,
                });
            }
            finally
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }

        return SegmentCatalogResult.Success(entries);
    }
}
