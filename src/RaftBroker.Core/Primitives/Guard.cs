namespace RaftBroker.Core.Primitives;

/// <summary>
/// Проверки доменных значений в одном месте: примитивы S0-T05 не должны дублировать
/// правила валидации, иначе они разъедутся.
/// </summary>
internal static class Guard
{
    /// <summary>Предел длины идентификатора: он попадает в конфиг, лог и gRPC-метаданные.</summary>
    internal const int MaxIdentifierLength = 128;

    /// <summary>Проверяет, что счётчик не отрицателен.</summary>
    internal static long NonNegative(long value, string parameterName) => value >= 0
        ? value
        : throw new ArgumentOutOfRangeException(parameterName, value, "Значение не может быть отрицательным.");

    /// <summary>
    /// Проверяет идентификатор узла или клиента: непустой, без пробелов по краям,
    /// без управляющих символов, не длиннее <see cref="MaxIdentifierLength"/>.
    /// </summary>
    /// <remarks>
    /// Пробелы по краям и управляющие символы отклоняются, а не обрезаются: опечатка в конфиге
    /// должна падать с понятной ошибкой, а не превращаться в другой идентификатор молча.
    /// </remarks>
    internal static string Identifier(string value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (value.Length == 0)
        {
            throw new ArgumentException("Идентификатор не может быть пустым.", parameterName);
        }

        if (value != value.Trim())
        {
            throw new ArgumentException("Идентификатор не может начинаться или заканчиваться пробелами.", parameterName);
        }

        if (value.Length > MaxIdentifierLength)
        {
            throw new ArgumentException($"Идентификатор длиннее {MaxIdentifierLength} символов.", parameterName);
        }

        foreach (var symbol in value)
        {
            if (char.IsControl(symbol))
            {
                throw new ArgumentException("Идентификатор не может содержать управляющие символы.", parameterName);
            }
        }

        return value;
    }
}
