using FsCheck;
using FsCheck.Fluent;
using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// Генераторы для property-тестов S1-T02.
/// </summary>
/// <remarks>
/// Вывод значений "как есть" здесь не годится: он с равным успехом сделает запись noop
/// с payload, индекс 0 и отрицательный терм - то есть мусор, который формат запрещает
/// по построению, а проверять тогда будет нечего. Генераторы ниже выдают только корректные
/// записи, и сокращение (shrinking) ведёт себя так же: уменьшает запись, не ломая инварианты.
/// </remarks>
public static class StorageArbitraries
{
    private const int MaxGeneratedPayload = 4096;

    private static readonly Gen<byte> AnyByte = Gen.Choose(0, 255).Select(value => (byte)value);

    private static readonly Gen<RecordShape> Shape =
        from index in Gen.Choose(1, 1000)
        from term in Gen.Choose(0, 50)
        from type in Gen.Elements(LogRecordType.Command, LogRecordType.Noop, LogRecordType.Config)
        from size in Gen.Choose(0, MaxGeneratedPayload)
        from payload in Gen.ArrayOf(AnyByte, size)
        select new RecordShape(index, term, type, payload);

    private static readonly Gen<SegmentHeader> Header =
        from index in Gen.Choose(1, 1000)
        from createdAtUnixMs in Gen.Choose(int.MinValue, int.MaxValue).Select(value => (long)value * 1000)
        select new SegmentHeader(new LogIndex(index), createdAtUnixMs);

    /// <summary>Непустые байты: годятся там, где нужно перевернуть конкретный бит.</summary>
    public static Arbitrary<byte[]> AnyBytes { get; } = Arb.From(
        Gen.Choose(1, MaxGeneratedPayload).SelectMany(size => Gen.ArrayOf(AnyByte, size)),
        ShrinkBytes);

    /// <summary>Корректные записи журнала: индекс от 1, терм от 0, известный тип.</summary>
    public static Arbitrary<LogRecord> AnyRecord { get; } = Arb.Convert(
        Arb.From(Shape, ShrinkShape),
        ToRecord,
        ToShape);

    /// <summary>Корректные заголовки сегментов: baseIndex от 1.</summary>
    public static Arbitrary<SegmentHeader> AnySegmentHeader { get; } = Arb.From(Header);

    private static LogRecord ToRecord(RecordShape shape)
        => shape.Type == LogRecordType.Noop
            ? LogRecord.Noop(new LogIndex(shape.Index), new Term(shape.Term))
            : new LogRecord(new LogIndex(shape.Index), new Term(shape.Term), shape.Type, shape.Payload);

    private static RecordShape ToShape(LogRecord record)
        => new((int)record.Index.Value, record.Term.Value, record.Type, record.Payload.ToArray());

    private static IEnumerable<RecordShape> ShrinkShape(RecordShape shape)
    {
        for (long index = shape.Index / 2; index >= 1; index /= 2)
        {
            yield return shape with { Index = (int)index };
        }

        for (long term = shape.Term; term > 0; term /= 2)
        {
            yield return shape with { Term = term - 1 };
        }

        for (int size = shape.Payload.Length / 2; size > 0; size /= 2)
        {
            yield return shape with { Payload = shape.Payload[..size] };
        }

        if (shape.Payload.Length > 0)
        {
            yield return shape with { Payload = [] };
        }

        if (shape.Type != LogRecordType.Command)
        {
            yield return shape with { Type = LogRecordType.Command };
        }
    }

    private static IEnumerable<byte[]> ShrinkBytes(byte[] bytes)
    {
        for (int size = bytes.Length / 2; size > 0; size /= 2)
        {
            yield return bytes[..size];
        }
    }

    /// <summary>
    /// Форма записи до проверок конструктора: так генератор и сокращение описаны одним
    /// набором полей, а инварианты формата восстанавливаются в одном месте - <see cref="ToRecord"/>.
    /// </summary>
    private readonly record struct RecordShape(int Index, long Term, LogRecordType Type, byte[] Payload);
}
