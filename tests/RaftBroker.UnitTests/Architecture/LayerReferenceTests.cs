using System.Xml.Linq;

namespace RaftBroker.UnitTests.Architecture;

/// <summary>
/// Границы слоёв зафиксированы тестом, а не соглашением (S0-T08).
/// </summary>
/// <remarks>
/// <para>
/// Правило одно: ссылка между проектами может идти только вниз по таблице слоёв
/// (ранг цели строго меньше ранга источника). Из этого автоматически следует
/// и требование спецификации этапа ("<c>Core</c> не ссылается ни на что",
/// "<c>RaftBroker.Raft</c> не ссылается на <c>RaftBroker.StateMachine</c>"), и
/// отсутствие циклов в графе проектов.
/// </para>
/// <para>
/// Таблица - это решение об архитектуре, а не список текущего состояния. Если задача
/// требует новой ссылки, таблицу нужно править осознанно: правка таблицы и есть
/// принятое решение, поэтому она должна попасть в описание задачи и в журнал.
/// Незаписанный в таблицу проект роняет тест: новый слой обязан быть назван явно.
/// </para>
/// </remarks>
public sealed class LayerReferenceTests
{
    /// <summary>Ранг проекта: ссылаться можно только на проекты с меньшим рангом.</summary>
    private static readonly Dictionary<string, int> LayerRanks = new(StringComparer.Ordinal)
    {
        // src: порядок из раздела 3 плана (Core <- Storage <- Raft <- StateMachine <- Server).
        ["RaftBroker.Core"] = 0,
        ["RaftBroker.Storage"] = 1,
        ["RaftBroker.Client"] = 1,
        ["RaftBroker.Raft"] = 2,
        ["RaftBroker.StateMachine"] = 3,
        ["RaftBroker.Transport"] = 3,
        ["RaftBroker.Server"] = 4,
        ["RaftBroker.Cli"] = 5,

        // tests: тестовый проект вправе ссылаться на что угодно из src.
        ["RaftBroker.TestKit"] = 1,
        ["RaftBroker.UnitTests"] = 100,
        ["RaftBroker.IntegrationTests"] = 100,
        ["RaftBroker.SimulationTests"] = 100,
        ["RaftBroker.Benchmarks"] = 100,
    };

    /// <summary>Проекты, которые являются тестовыми: на них не должен ссылаться ни один боевой проект.</summary>
    private static readonly HashSet<string> TestProjects = new(StringComparer.Ordinal)
    {
        "RaftBroker.TestKit",
        "RaftBroker.UnitTests",
        "RaftBroker.IntegrationTests",
        "RaftBroker.SimulationTests",
        "RaftBroker.Benchmarks",
    };

    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>Все ссылки между проектами решения. Отдельный кейс на каждую ссылку - чтобы падение называло виновника.</summary>
    public static TheoryData<string, string> ProjectReferences
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var edge in ReadProjectReferences())
            {
                data.Add(edge.From, edge.To);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ProjectReferences))]
    public void ProjectReference_GoesDownTheLayerOrder(string from, string to)
    {
        LayerRanks.Should().ContainKey(from, "каждый проект обязан быть в таблице слоёв");
        LayerRanks.Should().ContainKey(to, "каждый проект обязан быть в таблице слоёв");

        var fromRank = LayerRanks[from];
        var toRank = LayerRanks[to];

        toRank.Should().BeLessThan(
            fromRank,
            $"{from} (слой {fromRank}) может ссылаться только на проекты с меньшим рангом, а {to} имеет ранг {toRank}. "
            + "Если ссылка действительно нужна, впишите её в таблицу слоёв осознанно - вместе с обоснованием в описании задачи.");
    }

    [Fact]
    public void LayerTable_ListsExactlyTheProjectsThatExist()
    {
        var discovered = ReadProjectReferences()
            .SelectMany(edge => new[] { edge.From, edge.To })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // 13 проектов из раздела 3 плана (8 в src/, 5 в tests/). Число зафиксировано
        // намеренно: 14-й проект обязан попасть в таблицу слоёв и в план, а не появиться молча.
        discovered.Should().HaveCount(13);

        discovered.Should().BeEquivalentTo(
            LayerRanks.Keys,
            "таблица слоёв должна описывать ровно те проекты, которые есть в решении");
    }

    [Fact]
    public void Core_DoesNotReferenceAnyProject()
    {
        ReadProjectReferences()
            .Where(edge => edge.From == "RaftBroker.Core")
            .Should().BeEmpty("Core - нижний слой: зависимость в нём означала бы, что слой выбран неверно");
    }

    [Fact]
    public void Raft_DoesNotReferenceStateMachine()
    {
        // Инвариант этапа: Raft работает с байтами и вызывает IStateMachine через интерфейс,
        // поэтому он не знает о реализации машины состояний.
        ReadProjectReferences()
            .Where(edge => edge.From == "RaftBroker.Raft" && edge.To == "RaftBroker.StateMachine")
            .Should().BeEmpty("Raft не должен зависеть от StateMachine: связь идёт через IStateMachine");
    }

    [Fact]
    public void SourceProjects_DoNotReferenceTestProjects()
    {
        ReadProjectReferences()
            .Where(edge => !TestProjects.Contains(edge.From) && TestProjects.Contains(edge.To))
            .Should().BeEmpty("боевой проект, зависящий от тестового, нельзя собрать без тестов");
    }

    [Fact]
    public void ProjectDiscovery_FindsTheSolution()
    {
        // Страховка от вакуумных проверок: если разбор csproj сломается, проверки выше
        // пройдут на пустом списке ссылок и ничего не заметят.
        ReadProjectReferences().Should().NotBeEmpty();
        File.Exists(Path.Combine(RepoRoot, "RaftBroker.sln")).Should().BeTrue();
    }

    private static List<ProjectEdge> ReadProjectReferences()
    {
        var edges = new List<ProjectEdge>();

        foreach (var directory in new[] { "src", "tests" })
        {
            var projectsRoot = Path.Combine(RepoRoot, directory);
            Directory.Exists(projectsRoot).Should().BeTrue($"каталог {directory} должен существовать: из него читаются ссылки");

            foreach (var projectFile in Directory.EnumerateFiles(projectsRoot, "*.csproj", SearchOption.AllDirectories))
            {
                var project = Path.GetFileNameWithoutExtension(projectFile);
                var document = XDocument.Load(projectFile);

                foreach (var reference in document.Descendants("ProjectReference"))
                {
                    var include = reference.Attribute("Include")?.Value;

                    if (string.IsNullOrWhiteSpace(include))
                    {
                        continue;
                    }

                    edges.Add(new ProjectEdge(project, Path.GetFileNameWithoutExtension(include)));
                }
            }
        }

        return edges;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RaftBroker.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Не найден корень решения (RaftBroker.sln) выше {AppContext.BaseDirectory}.");
    }

    private sealed record ProjectEdge(string From, string To);
}
