using System.Text.Json.Serialization;

namespace RaftBroker.Core.Primitives;

/// <summary>
/// Терм Raft: номер эпохи, в которой выбирается лидер. Только растёт, никогда не уменьшается
/// внутри жизни узла, и сравнивается на каждом RPC.
/// </summary>
/// <remarks>
/// Три типа-счётчика (<see cref="Term"/>, <see cref="LogIndex"/>, <see cref="Offset"/>) устроены
/// одинаково, и это осознанное дублирование: если свести их к одному типу или разрешить
/// неявные преобразования, компилятор перестанет ловить путаницу "терм вместо индекса",
/// а именно она даёт самые неприятные ошибки в реализациях Raft.
/// </remarks>
[JsonConverter(typeof(TermJsonConverter))]
public readonly record struct Term : IComparable<Term>
{
    /// <summary>Создаёт терм.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Отрицательное значение.</exception>
    public Term(long value) => Value = Guard.NonNegative(value, nameof(value));

    /// <summary>Значение терма. Начинается с нуля.</summary>
    public long Value { get; }

    /// <summary>
    /// Следующий терм. Переполнение - это ошибка, а не переход в отрицательный терм:
    /// <see cref="long.MaxValue"/> термов кластер не проживёт.
    /// </summary>
    /// <exception cref="OverflowException">Текущий терм равен <see cref="long.MaxValue"/>.</exception>
    public Term Next() => new(checked(Value + 1));

    /// <inheritdoc />
    public int CompareTo(Term other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => PrimitiveFormat.Number(Value);

    public static bool operator <(Term left, Term right) => left.Value < right.Value;

    public static bool operator >(Term left, Term right) => left.Value > right.Value;

    public static bool operator <=(Term left, Term right) => left.Value <= right.Value;

    public static bool operator >=(Term left, Term right) => left.Value >= right.Value;
}

/// <summary>JSON-представление <see cref="Term"/> - число.</summary>
internal sealed class TermJsonConverter : LongPrimitiveJsonConverter<Term>
{
    protected override long ToValue(Term value) => value.Value;

    protected override Term FromValue(long value) => new(value);
}
