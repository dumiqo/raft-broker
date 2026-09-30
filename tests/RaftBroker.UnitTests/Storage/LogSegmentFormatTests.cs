using RaftBroker.Storage.Log;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T02: числа формата. Это не настройки, а нормативные значения из
/// <c>docs/formats/log-segment.md</c>: если они разойдутся с документом, разойдутся
/// и байты на диске, поэтому здесь они закреплены отдельно от кодека.
/// </summary>
public sealed class LogSegmentFormatTests
{
    [Fact]
    public void Magic_IsTheDocumentedEightBytes()
    {
        LogSegmentFormat.MagicSize.Should().Be(8);
        LogSegmentFormat.Magic.ToArray().Should().Equal("RBSEGLOG"u8.ToArray());
    }

    [Fact]
    public void HeaderSize_CoversAllHeaderFields()
    {
        // магия 8 + версия 2 + baseIndex 8 + createdAt 8 + CRC 4
        LogSegmentFormat.HeaderSize.Should().Be(8 + 2 + 8 + 8 + 4);
    }

    [Fact]
    public void FrameSizes_AddUpTheDocumentedFields()
    {
        // frameLength 4 + тип 1 + терм 8 + индекс 8 = 21, плюс CRC 4
        LogSegmentFormat.FrameHeaderSize.Should().Be(4 + 1 + 8 + 8);
        LogSegmentFormat.FrameCrcSize.Should().Be(4);
        LogSegmentFormat.FrameOverhead.Should().Be(LogSegmentFormat.FrameHeaderSize + LogSegmentFormat.FrameCrcSize);
        LogSegmentFormat.MinFrameLength.Should().Be(1 + 8 + 8);
        LogSegmentFormat.MaxFrameLength.Should().Be(LogSegmentFormat.MinFrameLength + LogSegmentFormat.MaxPayloadLength);
    }

    [Fact]
    public void Limits_AreTheDocumentedOneMebibyteAndSixtyFourMebibytes()
    {
        LogSegmentFormat.MaxPayloadLength.Should().Be(1024 * 1024);
        LogSegmentFormat.MaxSegmentSize.Should().Be(64L * 1024 * 1024);
    }
}
