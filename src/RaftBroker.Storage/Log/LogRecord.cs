using RaftBroker.Core.Primitives;

namespace RaftBroker.Storage.Log;

/// <summary>
/// Запись журнала Raft в том виде, в каком её хранит и возвращает хранилище.
/// </summary>
/// <remarks>
/// Payload непрозрачен: хранилище не разбирает его ни для одного типа, включая
/// <see cref="LogRecordType.Config"/>, и обязано вернуть байты ровно такими, какими они были
/// записаны. Проверки конструктора - только те правила, которые знает формат
/// (<c>docs/formats/log-segment.md</c>, §4 и §6): известный тип, лимит payload,
/// пустой payload у <see cref="LogRecordType.Noop"/>. Семантика команд - забота слоёв выше.
/// </remarks>
public sealed record LogRecord
{
    /// <summary>Создаёт запись журнала.</summary>
    /// <param name="index">Индекс записи; для записей журнала он не меньше единицы.</param>
    /// <param name="term">Терм, в котором запись предложена.</param>
    /// <param name="type">Тип записи.</param>
    /// <param name="payload">Непрозрачные байты записи.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Индекс меньше единицы, неизвестный тип записи или payload длиннее лимита.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Тип <see cref="LogRecordType.Noop"/>, но payload непуст.
    /// </exception>
    public LogRecord(LogIndex index, Term term, LogRecordType type, ReadOnlyMemory<byte> payload = default)
    {
        if (index.Value < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index.Value,
                "Индекс записи журнала начинается с единицы.");
        }

        if (!IsKnown(type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Неизвестный тип записи: таблица типов закрыта.");
        }

        if (payload.Length > LogSegmentFormat.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"Payload длиннее {LogSegmentFormat.MaxPayloadLength} байт: лимит формата.");
        }

        if (type == LogRecordType.Noop && !payload.IsEmpty)
        {
            throw new ArgumentException(
                "Запись noop обязана быть без payload.",
                nameof(payload));
        }

        Index = index;
        Term = term;
        Type = type;
        Payload = payload;
    }

    /// <summary>Индекс записи в журнале.</summary>
    public LogIndex Index { get; }

    /// <summary>Терм, в котором запись предложена.</summary>
    public Term Term { get; }

    /// <summary>Тип записи.</summary>
    public LogRecordType Type { get; }

    /// <summary>Непрозрачные байты записи.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Команда state machine.</summary>
    /// <param name="index">Индекс записи.</param>
    /// <param name="term">Терм записи.</param>
    /// <param name="payload">Байты команды.</param>
    public static LogRecord Command(LogIndex index, Term term, ReadOnlyMemory<byte> payload)
        => new(index, term, LogRecordType.Command, payload);

    /// <summary>Пустая запись лидера, которой открывается новый терм.</summary>
    /// <param name="index">Индекс записи.</param>
    /// <param name="term">Терм записи.</param>
    public static LogRecord Noop(LogIndex index, Term term)
        => new(index, term, LogRecordType.Noop);

    /// <summary>Команда изменения состава кластера.</summary>
    /// <param name="index">Индекс записи.</param>
    /// <param name="term">Терм записи.</param>
    /// <param name="payload">Байты команды.</param>
    public static LogRecord Config(LogIndex index, Term term, ReadOnlyMemory<byte> payload)
        => new(index, term, LogRecordType.Config, payload);

    /// <summary>Проверяет, что значение типа записи описано таблицей формата.</summary>
    /// <param name="type">Значение байта поля <c>recordType</c>.</param>
    /// <returns><c>true</c>, если тип определён.</returns>
    public static bool IsKnown(LogRecordType type)
        => type is LogRecordType.Command or LogRecordType.Noop or LogRecordType.Config;

    /// <summary>Сравнивает записи по полям, а payload - по содержимому.</summary>
    /// <param name="other">Запись для сравнения.</param>
    /// <returns><c>true</c>, если записи совпадают.</returns>
    /// <remarks>
    /// Синтезированное равенство записей сравнивало бы <see cref="Payload"/> по ссылке
    /// на массив, и две одинаковые записи, прочитанные из журнала дважды, оказались бы
    /// неравны. Сравнение содержимого стоит O(длины payload), но избавляет от такого
    /// тихого расхождения; решение зафиксировано тестом.
    /// </remarks>
    public bool Equals(LogRecord? other)
        => other is not null
            && Index == other.Index
            && Term == other.Term
            && Type == other.Type
            && Payload.Span.SequenceEqual(other.Payload.Span);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Index);
        hash.Add(Term);
        hash.Add(Type);
        hash.AddBytes(Payload.Span);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
        => $"запись {Index}, терм {Term}, {Type}, {Payload.Length} байт payload";
}
