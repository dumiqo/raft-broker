namespace RaftBroker.SimulationTests.Fuzzer;

/// <summary>
/// Падение фаззера выборов: нарушен инвариант протокола (<c>S2-T10</c>).
/// </summary>
/// <remarks>
/// Отдельный тип, а не <see cref="Xunit.Sdk.XunitException"/>, чтобы падение мутационного прогона
/// можно было отличить от падения самой проверки: тест ловит именно этот тип и требует, чтобы в
/// сообщении были сид и шаг.
/// </remarks>
public sealed class ElectionSafetyViolationException : Exception
{
    /// <summary>Создаёт падение с готовым текстом: сид, шаг, нарушение, история, команда повтора.</summary>
    /// <param name="message">Что нарушено и как это воспроизвести.</param>
    public ElectionSafetyViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт падение с готовым текстом и вложенным исключением.</summary>
    /// <param name="message">Что нарушено и как это воспроизвести.</param>
    /// <param name="innerException">Исключение, из-за которого прогон сломался.</param>
    public ElectionSafetyViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Создаёт падение без текста: требование анализаторов к исключениям.</summary>
    public ElectionSafetyViolationException()
    {
    }
}
