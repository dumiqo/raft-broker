using System.Diagnostics.CodeAnalysis;

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
    /// Проверяет идентификатор узла или клиента: непустой, без пробелов и управляющих
    /// символов, не длиннее <see cref="MaxIdentifierLength"/>.
    /// </summary>
    /// <remarks>
    /// Пробелы и управляющие символы отклоняются, а не обрезаются: опечатка в конфиге
    /// должна падать с понятной ошибкой, а не превращаться в другой идентификатор молча.
    /// Внутренние пробелы запрещены вместе с крайними: <c>node 1</c> и <c>node-1</c> в логе
    /// неразличимы глазом, а идентификатор ещё попадает в пути и метаданные gRPC.
    /// </remarks>
    internal static string Identifier(string value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (!IsValidIdentifier(value, out var reason))
        {
            throw new ArgumentException(reason, parameterName);
        }

        return value;
    }

    /// <summary>Не бросающая версия <see cref="Identifier"/> - для разбора внешних данных.</summary>
    internal static bool IsValidIdentifier([NotNullWhen(true)] string? value, out string reason)
    {
        if (string.IsNullOrEmpty(value))
        {
            reason = "Идентификатор не может быть пустым.";
            return false;
        }

        if (value.Length > MaxIdentifierLength)
        {
            reason = $"Идентификатор длиннее {MaxIdentifierLength} символов.";
            return false;
        }

        foreach (var symbol in value)
        {
            if (char.IsWhiteSpace(symbol) || char.IsControl(symbol))
            {
                reason = "Идентификатор не может содержать пробелы и управляющие символы.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }
}
