using System.Text.Json.Serialization;

namespace RaftBroker.Core.Primitives;

/// <summary>
/// Идентификатор узла кластера. Строка, а не число: имена вида <c>node-1</c> читаемы
/// в логе и в конфиге, а порядок узлов в Raft всё равно задаётся не идентификатором,
/// а сравнением термов.
/// </summary>
/// <remarks>
/// Собственный тип вместо <see cref="string"/> нужен, чтобы узел нельзя было перепутать
/// с идентификатором клиента или с адресом: компилятор не даст подставить одно вместо другого.
/// </remarks>
[JsonConverter(typeof(NodeIdJsonConverter))]
public readonly record struct NodeId : IComparable<NodeId>
{
    /// <summary>Создаёт идентификатор узла, проверяя его пригодность.</summary>
    /// <exception cref="ArgumentException">Пустая строка, пробелы, управляющие символы или длина больше 128.</exception>
    public NodeId(string value) => _value = Guard.Identifier(value, nameof(value));

    private readonly string? _value;

    /// <summary>Строковое значение: не пустое, без пробелов.</summary>
    /// <exception cref="InvalidOperationException">Значение не задано: <c>default(NodeId)</c> не является допустимым идентификатором.</exception>
    public string Value => _value ?? throw new InvalidOperationException(
        "default(NodeId) не является допустимым идентификатором: значение не задано.");

    /// <summary>Порядковое сравнение: нужно для детерминированной сортировки узлов в тестах и логах.</summary>
    public int CompareTo(NodeId other) => string.CompareOrdinal(_value, other._value);

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator <(NodeId left, NodeId right) => left.CompareTo(right) < 0;

    public static bool operator >(NodeId left, NodeId right) => left.CompareTo(right) > 0;

    public static bool operator <=(NodeId left, NodeId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(NodeId left, NodeId right) => left.CompareTo(right) >= 0;
}

/// <summary>JSON-представление <see cref="NodeId"/> - строка.</summary>
internal sealed class NodeIdJsonConverter : StringPrimitiveJsonConverter<NodeId>
{
    protected override string ToValue(NodeId value) => value.Value;

    protected override NodeId FromValue(string value) => new(value);
}
