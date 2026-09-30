namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// Байты, записанные шестнадцатеричными парами, - для эталонных векторов формата.
/// Пробелы и вертикальные черты игнорируются: так вектор можно перенести в тест по полям,
/// не склеивая байты глазами. Любой другой посторонний символ остаётся ошибкой.
/// </summary>
internal static class Hex
{
    public static byte[] Bytes(string hex)
        => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("|", string.Empty, StringComparison.Ordinal));
}
