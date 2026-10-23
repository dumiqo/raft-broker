namespace RaftBroker.Storage.HardState;

/// <summary>Запись файла hard state: порядковый номер и состояние (S2-T02).</summary>
/// <param name="Sequence">Номер записи: растёт на единицу, нужен диагностике, а не логике.</param>
/// <param name="State">Состояние на момент записи.</param>
public readonly record struct HardStateRecord(long Sequence, HardState State);
