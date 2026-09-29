using System.Text.Json.Serialization;

namespace RaftBroker.Core.Primitives;

/// <summary>
/// Идентификатор запроса клиента, по которому работает дедупликация повторных доставок:
/// exactly-once на уровне приложения сводится к "эту пару (клиент, запрос) уже видели".
/// </summary>
/// <remarks>
/// Тип беззнаковый и без ограничения снизу: значение приходит от клиента и сравнивается
/// только на равенство, поэтому арифметика над ним не определена.
/// </remarks>
[JsonConverter(typeof(RequestIdJsonConverter))]
public readonly record struct RequestId : IComparable<RequestId>
{
    /// <summary>Создаёт идентификатор запроса.</summary>
    public RequestId(ulong value) => Value = value;

    /// <summary>Значение идентификатора.</summary>
    public ulong Value { get; }

    /// <inheritdoc />
    public int CompareTo(RequestId other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => PrimitiveFormat.Number(Value);

    public static bool operator <(RequestId left, RequestId right) => left.Value < right.Value;

    public static bool operator >(RequestId left, RequestId right) => left.Value > right.Value;

    public static bool operator <=(RequestId left, RequestId right) => left.Value <= right.Value;

    public static bool operator >=(RequestId left, RequestId right) => left.Value >= right.Value;
}

/// <summary>JSON-представление <see cref="RequestId"/> - число.</summary>
internal sealed class RequestIdJsonConverter : ULongPrimitiveJsonConverter<RequestId>
{
    protected override ulong ToValue(RequestId value) => value.Value;

    protected override RequestId FromValue(ulong value) => new(value);
}
