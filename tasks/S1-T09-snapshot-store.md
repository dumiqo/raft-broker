# S1-T09 - SnapshotStore: формат файла снапшота и атомарная запись

- Этап: S1 - Долговечный лог и снапшоты
- Статус: open
- Зависит от: `S0-T05` (примитивы `LogIndex`, `Term`)
- Спецификация этапа: `docs/stages/01 - Durable log and snapshots.md`
- Гейт: `pwsh scripts/verify.ps1`

## Что сделать

Формат файла снапшота (заголовок, метаданные, состояние, CRC) и запись, которая либо доводит файл до целого, либо
не оставляет его вовсе: temp + fsync файла + rename + fsync каталога. Промежуточных состояний, читаемых как валидный
снапшот, быть не должно.

## DoD (из плана)

Тест: снапшот читается после процесса, убитого между каждой парой шагов записи.

## Контракт

```csharp
public interface ISnapshotStore
{
    ValueTask<SnapshotMetadata?> GetLatestAsync(CancellationToken ct);
    ValueTask WriteAsync(SnapshotMetadata metadata, ReadOnlyMemory<byte> state, CancellationToken ct);
    ValueTask<ReadOnlyMemory<byte>> ReadStateAsync(SnapshotMetadata metadata, CancellationToken ct);
    ValueTask DeleteOlderThanAsync(LogIndex index, CancellationToken ct);
}
```

## Что уже есть под руками

- Шов файловой системы разделён на пишущий и читающий (`IO/IStorageFile.cs`, `IO/IStorageReadFile.cs`,
  `IO/IStorageFileSystem.cs`); дубль умеет падать на заданной операции, поэтому «процесс убит между шагами»
  воспроизводится детерминированно.
- Реальное убийство процесса уже отработано в `S1-T05`: проект-жертва `tests/RaftBroker.CrashHarness` (14-й проект,
  ранг 98 в таблице слоёв). Для этой задачи спека требует, чтобы хотя бы один сценарий шёл через него, а не только
  через дубль.
- `Integrity/Crc32C.cs` - своя табличная CRC-32C, годится и для снапшота.
- Инвариант Q7 (`docs/invariants.md`): restore(снапшот) плюс реплей остатка равен полному реплею.

## Как проверять

- Crash-тест на каждой паре шагов записи: снапшот после краха читается либо старым, либо новым, но никогда
  «частично новым» (пункт 5 чек-листа `R1`).
- Тест `GetLatestAsync` при двух снапшотах в каталоге.
- Тест `DeleteOlderThanAsync`: старые снапшоты удаляются, последний остаётся.
- `pwsh scripts/verify.ps1`.

## Открытые вопросы

- Формат снапшота (раскладка заголовка и метаданных) описывается до кода. Если он отличается от формата сегмента по
  принципам (например, нет непрерывности индексов внутри файла), это решение идёт в `docs/formats/` и в ADR, как это
  было сделано для журнала (`docs/formats/log-segment.md`, `docs/adr/0003-log-segment-format.md`).
