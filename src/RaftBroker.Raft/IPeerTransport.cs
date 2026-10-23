using RaftBroker.Core.Primitives;
using RaftBroker.Raft.Messages;

namespace RaftBroker.Raft;

/// <summary>
/// Отправка RPC конкретному пиру (<c>S2-T05</c>): ответ-значение, без адресов, кодов и ретраев.
/// </summary>
/// <remarks>
/// <para>
/// Минимум намеренный: gRPC-транспорт (<c>S4-T01</c>) реализует этот же интерфейс, и всё, что
/// здесь появится (адресация, коды ошибок, ретраи, стримы), придётся переделывать там. Имена и
/// семантика сообщений повторяют <c>proto/raft.proto</c>.
/// </para>
/// <para>
/// Сбой связи - исключение. Узел считает это «ответа нет», а не поводом остановить раунд:
/// недоступный пир не должен срывать выборы, где кворум могли дать остальные.
/// </para>
/// <para>
/// Таймауты раунда живут в транспорте (deadline в <c>S4-T01</c>), а не в узле: собственный
/// таймер узла означал бы реальное ожидание, несовместимое с виртуальными часами (<c>TestClock</c>).
/// </para>
/// </remarks>
public interface IPeerTransport
{
    /// <summary>Просит голос у пира.</summary>
    /// <param name="peer">Кому отправлять.</param>
    /// <param name="request">Запрос голоса.</param>
    /// <param name="cancellationToken">Отмена: прерванный вызов ответом не считается.</param>
    ValueTask<RequestVoteResponse> RequestVoteAsync(NodeId peer, RequestVoteRequest request, CancellationToken cancellationToken);

    /// <summary>Отправляет пиру AppendEntries: heartbeat на этом этапе, репликацию - с <c>S3</c>.</summary>
    /// <param name="peer">Кому отправлять.</param>
    /// <param name="request">Сообщение лидера.</param>
    /// <param name="cancellationToken">Отмена: прерванный вызов ответом не считается.</param>
    ValueTask<AppendEntriesResponse> AppendEntriesAsync(NodeId peer, AppendEntriesRequest request, CancellationToken cancellationToken);
}
