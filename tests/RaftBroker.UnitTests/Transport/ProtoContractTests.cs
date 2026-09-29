using Google.Protobuf;
using Google.Protobuf.Reflection;
using RaftBroker.Transport.Proto;

namespace RaftBroker.UnitTests.Transport;

/// <summary>
/// S0-T06: проверка, что proto-скелет действительно собирается и что контракт совпадает
/// с задуманным. Номера полей здесь - не деталь оформления, а часть протокола: перенумерация
/// поля ломает совместимость узлов разных версий молча, поэтому она должна ронять тест.
/// </summary>
public sealed class ProtoContractTests
{
    [Fact]
    public void Header_CarriesTheFourEnvelopeFields()
    {
        FieldNumbersOf(RaftHeader.Descriptor).Should().Equal(
            ("cluster_id", 1),
            ("from", 2),
            ("to", 3),
            ("term", 4));
    }

    [Fact]
    public void LogEntry_KeepsThePayloadOpaque()
    {
        FieldNumbersOf(LogEntry.Descriptor).Should().Equal(
            ("index", 1),
            ("term", 2),
            ("payload", 3));

        LogEntry.Descriptor.FindFieldByName("payload").FieldType.Should().Be(FieldType.Bytes);
    }

    [Fact]
    public void Service_DeclaresExactlyTheFourAgreedRpcs()
    {
        var methods = RaftService.Descriptor.Methods.Select(method => method.Name).ToArray();

        methods.Should().HaveCount(4);
        methods.Should().BeEquivalentTo("RequestVote", "PreVote", "AppendEntries", "InstallSnapshot");
    }

    [Fact]
    public void SnapshotTransfer_IsClientStreaming_AndEverythingElseIsUnary()
    {
        var snapshot = RaftService.Descriptor.FindMethodByName("InstallSnapshot");

        snapshot.IsClientStreaming.Should().BeTrue();
        snapshot.IsServerStreaming.Should().BeFalse();

        foreach (var name in new[] { "RequestVote", "PreVote", "AppendEntries" })
        {
            var method = RaftService.Descriptor.FindMethodByName(name);

            method.IsClientStreaming.Should().BeFalse();
            method.IsServerStreaming.Should().BeFalse();
        }
    }

    [Fact]
    public void AppendEntries_SurvivesARoundTripThroughTheWire()
    {
        var request = new AppendEntriesRequest
        {
            Header = new RaftHeader
            {
                ClusterId = "demo",
                From = "node-1",
                To = "node-2",
                Term = 7,
            },
            PrevLogIndex = 41,
            PrevLogTerm = 6,
            LeaderCommit = 39,
        };

        request.Entries.Add(new LogEntry { Index = 42, Term = 7, Payload = ByteString.CopyFrom(0x00, 0xFF, 0x7F) });
        request.Entries.Add(new LogEntry { Index = 43, Term = 7, Payload = ByteString.Empty });

        var restored = AppendEntriesRequest.Parser.ParseFrom(request.ToByteArray());

        restored.Should().Be(request);
        restored.Entries.Should().HaveCount(2);
        restored.Entries[0].Payload.ToByteArray().Should().Equal(0x00, 0xFF, 0x7F);
        restored.Entries[1].Payload.Length.Should().Be(0);
    }

    [Fact]
    public void EmptyEntries_AreAHeartbeat_NotAnError()
    {
        var heartbeat = new AppendEntriesRequest
        {
            Header = new RaftHeader { ClusterId = "demo", From = "node-1", To = "node-2", Term = 1 },
        };

        var restored = AppendEntriesRequest.Parser.ParseFrom(heartbeat.ToByteArray());

        restored.Entries.Should().BeEmpty();
        restored.PrevLogIndex.Should().Be(0);
    }

    [Fact]
    public void SnapshotChunks_SurviveTheRoundTrip()
    {
        var chunk = new InstallSnapshotRequest
        {
            Header = new RaftHeader { ClusterId = "demo", From = "node-1", To = "node-3", Term = 12 },
            SnapshotIndex = 500,
            SnapshotTerm = 11,
            Data = ByteString.CopyFromUtf8("snapshot-chunk"),
        };

        var restored = InstallSnapshotRequest.Parser.ParseFrom(chunk.ToByteArray());

        restored.Should().Be(chunk);
        restored.Data.ToStringUtf8().Should().Be("snapshot-chunk");
    }

    [Fact]
    public void VoteMessages_CanBeToldApartByType()
    {
        // PreVote и RequestVote несут одни и те же поля, но разные типы: ответ на пробный
        // запрос не должен иметь возможности притвориться настоящим голосом.
        var preVote = new PreVoteRequest { Header = new RaftHeader(), LastLogIndex = 1, LastLogTerm = 1 };
        var requestVote = new RequestVoteRequest { Header = new RaftHeader(), LastLogIndex = 1, LastLogTerm = 1 };

        preVote.ToByteArray().Should().Equal(requestVote.ToByteArray());
        ((object)preVote).Should().NotBeOfType<RequestVoteRequest>();
    }

    private static IEnumerable<(string Name, int Number)> FieldNumbersOf(MessageDescriptor descriptor)
        => descriptor.Fields.InFieldNumberOrder().Select(field => (field.Name, field.FieldNumber));
}
