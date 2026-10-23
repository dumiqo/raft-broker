using RaftBroker.Core.Primitives;

namespace RaftBroker.Raft;

/// <summary>
/// Состав кластера для подсчёта кворума (<c>S2-T05</c>, правило 7 спецификации этапа S2).
/// </summary>
/// <remarks>
/// <para>
/// Число узлов и состав большинства не зашиты в счётчики голосов: и выборы, и CheckQuorum
/// (<c>S2-T09</c>) спрашивают кворум здесь. На S2 это всегда одна статическая конфигурация
/// (<c>C_0</c>), но с <c>S7-T02</c> та же точка вернёт joint-конфигурацию, где кворум -
/// большинство <b>обеих</b> половин (R8), и переписывать выборы не придётся.
/// </para>
/// <para>
/// Кворум спрашивается <b>множеством</b> ответивших, а не их числом: joint-конфигурацию нельзя
/// выразить одним числом голосов, и шов, принимающий <c>int</c>, пришлось бы менять вместе с S7.
/// </para>
/// </remarks>
public interface IClusterConfiguration
{
    /// <summary>Все узлы-избиратели текущей конфигурации, включая себя.</summary>
    IReadOnlyCollection<NodeId> Voters { get; }

    /// <summary>Образуют ли указанные узлы кворум.</summary>
    /// <param name="acknowledgers">Ответившие узлы; повторы и чужие узлы не считаются.</param>
    bool IsQuorum(IReadOnlyCollection<NodeId> acknowledgers);
}
