using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T02: правила записи журнала, которые знает формат (<c>docs/formats/log-segment.md</c>, §4 и §6).
/// Всё остальное - семантика Raft и state machine, и в хранилище ей места нет.
/// </summary>
public sealed class LogRecordTests
{
    [Fact]
    public void IndexStartsAtOne()
    {
        var act = () => new LogRecord(new LogIndex(0), new Term(1), LogRecordType.Command, new byte[] { 1 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UnknownRecordType_IsRejectedOnConstruction()
    {
        // 0x00 не занят специально: так обнулённый байт типа ловится как ошибка, а не как "какая-то запись".
        var act = () => new LogRecord(new LogIndex(1), new Term(1), (LogRecordType)0x00);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Noop_RequiresAnEmptyPayload()
    {
        LogRecord.Noop(new LogIndex(1), new Term(1)).Payload.IsEmpty.Should().BeTrue();

        var act = () => new LogRecord(new LogIndex(1), new Term(1), LogRecordType.Noop, new byte[] { 0x01 });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PayloadLimit_IsOneMebibyte()
    {
        var atLimit = () => new LogRecord(new LogIndex(1), new Term(1), LogRecordType.Command, new byte[LogSegmentFormat.MaxPayloadLength]);
        var aboveLimit = () => new LogRecord(new LogIndex(1), new Term(1), LogRecordType.Command, new byte[LogSegmentFormat.MaxPayloadLength + 1]);

        atLimit.Should().NotThrow();
        aboveLimit.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void KnownTypes_AreTheDocumentedOnes()
    {
        LogRecord.IsKnown(LogRecordType.Command).Should().BeTrue();
        LogRecord.IsKnown(LogRecordType.Noop).Should().BeTrue();
        LogRecord.IsKnown(LogRecordType.Config).Should().BeTrue();
        LogRecord.IsKnown((LogRecordType)0x04).Should().BeFalse();

        ((byte)LogRecordType.Command).Should().Be(0x01);
        ((byte)LogRecordType.Noop).Should().Be(0x02);
        ((byte)LogRecordType.Config).Should().Be(0x03);
    }

    /// <summary>
    /// Записи сравниваются по содержимому payload, а не по ссылке на массив: иначе две
    /// одинаковые записи, прочитанные из журнала дважды, оказались бы неравны, и тесты
    /// верхних слоёв ловили бы это как "журнал вернул не то".
    /// </summary>
    [Fact]
    public void Equality_ComparesPayloadContents_NotArrays()
    {
        var first = LogRecord.Command(new LogIndex(7), new Term(3), new byte[] { 1, 2, 3 });
        var sameContents = LogRecord.Command(new LogIndex(7), new Term(3), new byte[] { 1, 2, 3 });
        var otherContents = LogRecord.Command(new LogIndex(7), new Term(3), new byte[] { 1, 2, 4 });
        var otherIndex = LogRecord.Command(new LogIndex(8), new Term(3), new byte[] { 1, 2, 3 });
        var otherTerm = LogRecord.Command(new LogIndex(7), new Term(4), new byte[] { 1, 2, 3 });

        first.Should().Be(sameContents);
        first.GetHashCode().Should().Be(sameContents.GetHashCode());
        first.Should().NotBe(otherContents);
        first.Should().NotBe(otherIndex);
        first.Should().NotBe(otherTerm);
    }

    [Fact]
    public void ToString_NamesTheFieldsWithoutDumpingThePayload()
    {
        LogRecord.Noop(new LogIndex(12), new Term(5)).ToString()
            .Should().Be("запись 12, терм 5, Noop, 0 байт payload");
    }
}
