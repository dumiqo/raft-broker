namespace RaftBroker.Server;

/// <summary>
/// Конфигурация узла не принята. Отдельный тип, а не <see cref="ArgumentException"/>,
/// потому что это единственная ошибка, которую хост обязан встретить до старта
/// и объяснить человеку понятным текстом: без такогого типа сообщение растаскивалось
/// бы по <c>catch</c>-фильтрам в точке входа.
/// </summary>
public sealed class NodeConfigurationException : Exception
{
    /// <summary>Создаёт исключение с готовым для чтения сообщением.</summary>
    public NodeConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение, сохраняя исходную причину для диагностики.</summary>
    public NodeConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
