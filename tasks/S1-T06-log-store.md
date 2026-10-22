# S1-T06 - LogStore: индекс сегментов, Append/Get/TruncateSuffix, retention по снапшоту

- Этап: S1 - Долговечный лог и снапшоты
- Статус: in-progress (в рабочем дереве, без коммита на 2026-10-22)
- Зависит от: `S1-T03` (пишущая половина), `S1-T05` (восстановление сегмента)
- Спецификация этапа: `docs/stages/01 - Durable log and snapshots.md`
- Формат сегмента: `docs/formats/log-segment.md`
- Гейт: `pwsh scripts/verify.ps1`

## Что сделать

Логический журнал из физических сегментов: индекс сегментов в памяти, `Append`, `Get`, `ReadRange`,
`TruncateSuffix`, диапазонное чтение и retention по снапшоту. `LogStore` не знает про роли, term-переходы и
правило коммита: на этом этапе он умеет хранить записи и отдавать их по индексу.

## DoD (из плана)

Тесты: обрезка хвоста, чтение после обрезки, удаление сегментов ниже снапшота.

Дополнительно из спецификации этапа: попытка обрезать закоммиченную историю - программная ошибка, а не тихое
усечение, и это проверяется тестом (инвариант R4).

## Контракт

```csharp
public interface ILogStore
{
    LogIndex FirstIndex { get; }          // с учётом снапшота: начало доступной истории
    LogIndex LastIndex { get; }
    Term LastTerm { get; }
    LogIndex CommittedIndex { get; }      // ниже этого индекса записи неизменяемы

    ValueTask AppendAsync(IReadOnlyList<LogRecord> records, DurabilityMode mode, CancellationToken ct);
    ValueTask<LogRecord?> GetAsync(LogIndex index, CancellationToken ct);
    ValueTask<IReadOnlyList<LogRecord>> ReadRangeAsync(LogIndex from, int maxCount, CancellationToken ct);
    ValueTask TruncateSuffixAsync(LogIndex from, CancellationToken ct);   // удаляет index >= from
    ValueTask CompactAsync(SnapshotMetadata snapshot, CancellationToken ct);
}
```

Правило: `TruncateSuffixAsync(from)` разрешён только при `from > CommittedIndex`.

## Что уже есть под руками

- `Log/SegmentWriter.cs` - append, три политики `DurabilityMode`, обязательный порядок rollover, проверка
  последовательности до первого байта на диске.
- `Log/SegmentReader.cs` - заголовок, скан сегмента, индекс смещений в памяти, итерация вперёд и назад, доступ по
  абсолютному индексу; вердикт о хвосте - `SegmentScanStatus`/`TailProblem`, а не решение.
- `Log/SegmentRecovery.cs` - `Intact`, `TruncatedTail`, `Removed`, `Refused`; вид сегмента (`Sealed`/`Last`) - вход.
- `IO/IStorageFileSystem.cs`, `IO/IStorageFile.cs`, `IO/IStorageReadFile.cs` - шов ввода-вывода; дубль
  `tests/RaftBroker.TestKit/IO/FakeStorageFileSystem.cs` считает fsync и умеет падать на заданной операции.
- `Log/LogSegmentFormat.cs` - имя файла `{baseIndex:D20}.seg` и строгий разбор имени.
- В рабочем дереве уже лежат `Log/ILogStore.cs`, `Log/LogStore.cs`, `Log/LogStoreOpenResult.cs`,
  `Log/SegmentResumeState.cs` (без коммита на 2026-10-22).

## Как проверять

- Модульные тесты на обрезку хвоста, на чтение после обрезки, на удаление сегментов ниже снапшота.
- Тест на отказ при `TruncateSuffixAsync(from <= CommittedIndex)`.
- Тест на чтение через границу сегментов (rollover уже проверен в `S1-T03`, здесь - чтение объединённого журнала).
- `pwsh scripts/verify.ps1` целиком: сборка, тесты, формат, анализаторы, границы слоёв.

## Открытые вопросы

Новых решений по этой задаче на 2026-10-22 не зафиксировано: открытые решения этапа 1-7 закрыты ADR `0003`-`0006`
и перечислены в спецификации. Если по ходу работы появляется новое необратимое решение (например, про кэш индекса
сегментов на диске), оно идёт в `docs/adr/` до кода, а не в комментарий.
