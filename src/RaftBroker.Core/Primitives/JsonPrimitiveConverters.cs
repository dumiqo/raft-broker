using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaftBroker.Core.Primitives;

/// <summary>
/// Числовой примитив домена в JSON пишется голым числом, а не объектом вида <c>{"value":5}</c>:
/// эти типы попадают в конфиг узла (S4-T05) и в файлы, которые человек читает глазами.
/// Ошибка чтения превращается в <see cref="JsonException"/> с именем типа, чтобы
/// сообщение указывало на поле конфига.
/// </summary>
internal abstract class LongPrimitiveJsonConverter<TSelf> : JsonConverter<TSelf>
    where TSelf : struct
{
    public override TSelf Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
        {
            throw new JsonException($"{typeof(TSelf).Name}: ожидается целое число.");
        }

        try
        {
            return FromValue(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException($"{typeof(TSelf).Name}: {exception.Message}", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, TSelf value, JsonSerializerOptions options)
        => writer.WriteNumberValue(ToValue(value));

    protected abstract long ToValue(TSelf value);

    protected abstract TSelf FromValue(long value);
}

/// <summary>То же, что <see cref="LongPrimitiveJsonConverter{TSelf}"/>, но для 64-битных беззнаковых значений.</summary>
internal abstract class ULongPrimitiveJsonConverter<TSelf> : JsonConverter<TSelf>
    where TSelf : struct
{
    public override TSelf Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt64(out var value))
        {
            throw new JsonException($"{typeof(TSelf).Name}: ожидается неотрицательное целое число.");
        }

        try
        {
            return FromValue(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException($"{typeof(TSelf).Name}: {exception.Message}", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, TSelf value, JsonSerializerOptions options)
        => writer.WriteNumberValue(ToValue(value));

    protected abstract ulong ToValue(TSelf value);

    protected abstract TSelf FromValue(ulong value);
}

/// <summary>Строковый примитив домена пишется в JSON голой строкой.</summary>
internal abstract class StringPrimitiveJsonConverter<TSelf> : JsonConverter<TSelf>
    where TSelf : struct
{
    public override TSelf Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"{typeof(TSelf).Name}: ожидается строка.");
        }

        var value = reader.GetString() ?? string.Empty;

        try
        {
            return FromValue(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException($"{typeof(TSelf).Name}: {exception.Message}", exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, TSelf value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToValue(value));

    protected abstract string ToValue(TSelf value);

    protected abstract TSelf FromValue(string value);
}

/// <summary>Форматирование чисел домена без текущей культуры: лог и провод не должны зависеть от локали.</summary>
internal static class PrimitiveFormat
{
    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}
