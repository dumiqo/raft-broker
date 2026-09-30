using RaftBroker.Core.Primitives;
using RaftBroker.Storage.Log;

namespace RaftBroker.UnitTests.Storage;

/// <summary>
/// S1-T03: имя файла сегмента - часть формата (§7), от него зависит порядок чтения каталога.
/// </summary>
public sealed class SegmentFileNameTests
{
    [Fact]
    public void GetFileName_PadsTheIndexToTwentyDigits()
    {
        LogSegmentFormat.GetFileName(new LogIndex(1)).Should().Be("00000000000000000001.seg");
    }

    [Fact]
    public void GetFileName_KeepsTheFixedWidthForTheLargestIndex()
    {
        // long.MaxValue занимает 19 цифр: двадцатая - ведущий ноль.
        LogSegmentFormat.GetFileName(new LogIndex(long.MaxValue)).Should().Be("09223372036854775807.seg");
    }

    [Fact]
    public void GetFileName_HasConstantLength()
    {
        long[] indices = [1, 2, 9, 10, 99, 100, 1_000_000, long.MaxValue];

        foreach (var index in indices)
        {
            var name = LogSegmentFormat.GetFileName(new LogIndex(index));

            name.Should().MatchRegex(@"^\d{20}\.seg$");
            name.Length.Should().Be(LogSegmentFormat.FileNameDigits + LogSegmentFormat.FileNameSuffix.Length);
        }
    }

    [Fact]
    public void GetFileName_SortsLexicographicallyInNumericOrder()
    {
        long[] indices = [1, 2, 9, 10, 11, 99, 100, 1_000, long.MaxValue];

        var names = indices.Select(index => LogSegmentFormat.GetFileName(new LogIndex(index))).ToList();
        var sorted = names.Order(StringComparer.Ordinal).ToList();

        // Лексикографический порядок имён обязан совпадать с числовым: по нему восстановление
        // обходит каталог без разбора чисел.
        sorted.Should().Equal(names);
    }

    [Fact]
    public void TryParseFileName_ReadsBackWhatGetFileNameWrote()
    {
        long[] indices = [1, 2, 9, 10, 99, 100, 1_000_000, long.MaxValue];

        foreach (var index in indices)
        {
            var name = LogSegmentFormat.GetFileName(new LogIndex(index));

            LogSegmentFormat.TryParseFileName(name, out var parsed).Should().BeTrue();
            parsed.Value.Should().Be(index);
        }
    }

    [Fact]
    public void TryParseFileName_AcceptsFullPathAndUsesOnlyTheFileName()
    {
        var path = Path.Combine("data", "log", LogSegmentFormat.GetFileName(new LogIndex(42)));

        LogSegmentFormat.TryParseFileName(path, out var parsed).Should().BeTrue();
        parsed.Value.Should().Be(42);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.seg")]
    [InlineData("00000000000000000001.log")]
    [InlineData("00000000000000000001.seg.bak")]
    [InlineData("+0000000000000000001.seg")]
    [InlineData("0000000000000000000a.seg")]
    [InlineData("00000000000000000000.seg")]
    [InlineData("99999999999999999999.seg")]
    [InlineData("00000000000000000001")]
    public void TryParseFileName_RejectsAnythingElse(string name)
    {
        // Посторонний файл в каталоге лога - ошибка (§7), а не повод выдать ему индекс.
        LogSegmentFormat.TryParseFileName(name, out var parsed).Should().BeFalse();
        parsed.Should().Be(default(LogIndex));
    }
}
