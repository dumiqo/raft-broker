using System.Text.Json.Serialization;

namespace RaftBroker.Core.Primitives;

/// <summary>
/// Позиция записи в потоке состояния (state machine). В отличие от <see cref="LogIndex"/>,
/// это позиция не в журнале Raft, а в выходном потоке приложения, и она монотонна по нему.
/// </summary>
[JsonConverter(typeof(OffsetJsonConverter))]
public readonly record struct Offset : IComparable<Offset>
{
    /// <summary>Начало потока: смещение до первого элемента.</summary>
    public static Offset Beginning => default;

    /// <summary>Создаёт смещение.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Отрицательное значение.</exception>
    public Offset(long value) => Value = Guard.NonNegative(value, nameof(value));

    /// <summary>Значение смещения.</summary>
    public long Value { get; }

    /// <summary>Смещение следующей записи.</summary>
    /// <exception cref="OverflowException">Текущее смещение равно <see cref="long.MaxValue"/>.</exception>
    public Offset Next() => new(checked(Value + 1));

    /// <inheritdoc />
    public int CompareTo(Offset other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => PrimitiveFormat.Number(Value);

    public static bool operator <(Offset left, Offset right) => left.Value < right.Value;

    public static bool operator >(Offset left, Offset right) => left.Value > right.Value;

    public static bool operator <=(Offset left, Offset right) => left.Value <= right.Value;

    public static bool operator >=(Offset left, Offset right) => left.Value >= right.Value;
}

/// <summary>JSON-представление <see cref="Offset"/> - число.</summary>
internal sealed class OffsetJsonConverter : LongPrimitiveJsonConverter<Offset>
{
    protected override long ToValue(Offset value) => value.Value;

    protected override Offset FromValue(long value) => new(value);
}
