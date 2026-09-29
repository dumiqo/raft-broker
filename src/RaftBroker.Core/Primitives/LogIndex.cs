using System.Text.Json.Serialization;

namespace RaftBroker.Core.Primitives;

/// <summary>
/// Индекс записи в журнале Raft. Нумерация с единицы: ноль означает "журнал пуст"
/// и является корректным значением для пустого журнала.
/// </summary>
/// <remarks>
/// Отдельный тип, а не <see cref="long"/>, потому что индекс и терм постоянно ходят рядом
/// в одном выражении (<c>AppendEntries(term, prevLogIndex, ...)</c>), и перестановка
/// аргументов должна ловиться компилятором.
/// </remarks>
[JsonConverter(typeof(LogIndexJsonConverter))]
public readonly record struct LogIndex : IComparable<LogIndex>
{
    /// <summary>Индекс пустого журнала: записи нумеруются с единицы.</summary>
    public static LogIndex Empty => default;

    /// <summary>Создаёт индекс журнала.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Отрицательное значение.</exception>
    public LogIndex(long value) => Value = Guard.NonNegative(value, nameof(value));

    /// <summary>Значение индекса. 0 - пустой журнал, записи нумеруются с 1.</summary>
    public long Value { get; }

    /// <summary>Индекс следующей записи.</summary>
    /// <exception cref="OverflowException">Текущий индекс равен <see cref="long.MaxValue"/>.</exception>
    public LogIndex Next() => new(checked(Value + 1));

    /// <inheritdoc />
    public int CompareTo(LogIndex other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => PrimitiveFormat.Number(Value);

    public static bool operator <(LogIndex left, LogIndex right) => left.Value < right.Value;

    public static bool operator >(LogIndex left, LogIndex right) => left.Value > right.Value;

    public static bool operator <=(LogIndex left, LogIndex right) => left.Value <= right.Value;

    public static bool operator >=(LogIndex left, LogIndex right) => left.Value >= right.Value;
}

/// <summary>JSON-представление <see cref="LogIndex"/> - число.</summary>
internal sealed class LogIndexJsonConverter : LongPrimitiveJsonConverter<LogIndex>
{
    protected override long ToValue(LogIndex value) => value.Value;

    protected override LogIndex FromValue(long value) => new(value);
}
