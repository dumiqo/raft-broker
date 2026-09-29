using System.Text.Json.Serialization;

namespace RaftBroker.Core.Primitives;

/// <summary>
/// Идентификатор клиента. Правила те же, что у <see cref="NodeId"/>, но это отдельный тип:
/// в коде дедупликации (S5, S6) перепутать узел кластера с клиентом должно быть невозможно.
/// </summary>
[JsonConverter(typeof(ClientIdJsonConverter))]
public readonly record struct ClientId : IComparable<ClientId>
{
    /// <summary>Создаёт идентификатор клиента, проверяя его пригодность.</summary>
    /// <exception cref="ArgumentException">Пустая строка, пробелы, управляющие символы или длина больше 128.</exception>
    public ClientId(string value) => _value = Guard.Identifier(value, nameof(value));

    private readonly string? _value;

    /// <summary>Строковое значение: не пустое, без пробелов.</summary>
    /// <exception cref="InvalidOperationException">Значение не задано: <c>default(ClientId)</c> не является допустимым идентификатором.</exception>
    public string Value => _value ?? throw new InvalidOperationException(
        "default(ClientId) не является допустимым идентификатором: значение не задано.");

    /// <summary>Порядковое сравнение.</summary>
    public int CompareTo(ClientId other) => string.CompareOrdinal(_value, other._value);

    /// <inheritdoc />
    public override string ToString() => Value;

    public static bool operator <(ClientId left, ClientId right) => left.CompareTo(right) < 0;

    public static bool operator >(ClientId left, ClientId right) => left.CompareTo(right) > 0;

    public static bool operator <=(ClientId left, ClientId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ClientId left, ClientId right) => left.CompareTo(right) >= 0;
}

/// <summary>JSON-представление <see cref="ClientId"/> - строка.</summary>
internal sealed class ClientIdJsonConverter : StringPrimitiveJsonConverter<ClientId>
{
    protected override string ToValue(ClientId value) => value.Value;

    protected override ClientId FromValue(string value) => new(value);
}
