namespace RaftBroker.Storage.Integrity;

/// <summary>
/// CRC-32C (Castagnoli) - контрольная сумма сегментов журнала и их заголовков.
/// </summary>
/// <remarks>
/// Параметры и контрольные векторы зафиксированы в <c>docs/formats/log-segment.md</c> (§5):
/// полином 0x1EDC6F41 (отражённый 0x82F63B78), <c>init</c> и <c>xorout</c> 0xFFFFFFFF,
/// <c>refin</c>/<c>refout</c> - да. Реализация табличная и намеренно простая: формат
/// определяет значения CRC, а не способ их вычисления, поэтому аппаратное ускорение
/// (<c>Sse42.Crc32</c>) допустимо позже, но только с тестом на совпадение с этой реализацией.
/// </remarks>
public static class Crc32C
{
    /// <summary>Отражённый полином Castagnoli.</summary>
    private const uint Polynomial = 0x82F63B78u;

    private static readonly uint[] Table = CreateTable();

    /// <summary>Считает CRC-32C от переданных байтов.</summary>
    /// <param name="data">Байты, по которым считается CRC.</param>
    /// <returns>Контрольная сумма: пустой ввод даёт <c>0</c>.</returns>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;

        foreach (byte value in data)
        {
            crc = (crc >> 8) ^ Table[(int)((crc ^ value) & 0xFF)];
        }

        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];

        for (int index = 0; index < table.Length; index++)
        {
            uint crc = (uint)index;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }

            table[index] = crc;
        }

        return table;
    }
}
