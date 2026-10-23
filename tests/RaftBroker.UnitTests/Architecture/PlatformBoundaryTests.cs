using System.Text.RegularExpressions;

namespace RaftBroker.UnitTests.Architecture;

/// <summary>
/// Платформенного кода нет вовсе - это и есть переносимость (S1-T11, ADR <c>0008-supported-platforms.md</c>).
/// </summary>
/// <remarks>
/// Требование "запускается на Windows и на Linux" доказывает матрица CI (один и тот же
/// <c>scripts/verify.ps1</c> на двух раннерах), а не этот файл: прогон на одной ОС о другой
/// ничего не говорит. Здесь проверяется то, что видно на любой ОС и падает до пуша, -
/// отсутствие платформенного кода: диск трогает только <c>Storage/IO</c>, ветвлений по ОС нет,
/// литералов с обратным слэшем нет.
/// </remarks>
public sealed partial class PlatformBoundaryTests
{
    /// <summary>Единственный каталог, которому разрешено знать про файловую систему.</summary>
    private const string IoLayer = "src/RaftBroker.Storage/IO/";

    [Fact]
    public void FileSystemApis_AreUsedOnlyInsideTheIoLayer()
        => Violations(FileSystemApi(), path => path.StartsWith(IoLayer, StringComparison.Ordinal))
            .Should().BeEmpty("диск трогает только SystemStorageFileSystem: остальной код видит IStorageFileSystem");

    [Fact]
    public void OperatingSystemBranching_NeverAppearsInSource()
        => Violations(OperatingSystemApi(), _ => false)
            .Should().BeEmpty("ветка \"на Windows так, на Linux иначе\" на каждой ОС исполняется наполовину, то есть не проверяется нигде");

    [Fact]
    public void Source_HasNoBackslashLiterals()
    {
        // C# не использует обратный слэш вне литералов, поэтому вхождение в тексте без
        // комментариев - это литерал "\\", @"a\b" или '\'. Путь собирается Path.Combine.
        ReadSources()
            .SelectMany(file => file.Text.Split('\n')
                .Select((line, number) => (file.Path, Line: number + 1, Text: line))
                .Where(line => line.Text.Contains('\\'))
                .Select(line => $"{line.Path}:{line.Line}: {line.Text.Trim()}"))
            .Should().BeEmpty("разделитель пути - свойство платформы, а не константа");
    }

    [Fact]
    public void Scanner_SeesTheCodeItIsSupposedToCheck()
    {
        // Страховка от вакуумных проверок: переименованный каталог или сломанный сканер
        // оставили бы три теста выше зелёными на пустом списке файлов.
        var sources = ReadSources();

        sources.Should().HaveCountGreaterThan(20);
        sources.Should().Contain(file => file.Path.StartsWith(IoLayer, StringComparison.Ordinal));
        FileSystemApi().Matches(sources.Single(file => file.Path == IoLayer + "SystemStorageFileSystem.cs").Text)
            .Should().NotBeEmpty("сканер обязан видеть платформенный код там, где он разрешён");
    }

    private static List<string> Violations(Regex pattern, Func<string, bool> isAllowed)
        => [.. ReadSources()
            .Where(file => !isAllowed(file.Path))
            .SelectMany(file => pattern.Matches(file.Text)
                .Select(match => $"{file.Path}:{LineOf(file.Text, match.Index)}: {match.Value}"))];

    private static int LineOf(string text, int index) => text[..index].Count(character => character == '\n') + 1;

    /// <summary>Исходники <c>src</c> без комментариев, с относительными путями через <c>/</c>.</summary>
    private static List<(string Path, string Text)> ReadSources()
        => [.. Directory.EnumerateFiles(Path.Combine(LayerReferenceTests.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(file))
            .Select(file => (
                Path: Path.GetRelativePath(LayerReferenceTests.RepoRoot, file).Replace('\\', '/'),
                Text: WithoutComments(File.ReadAllText(file))))];

    private static bool IsBuildOutput(string file)
        => file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // shortcut: режет строку по первому "//", не разбирая литералы и блочные комментарии -
    // в src таких нет. Появится литерал вида "http://" - перейти на разбор литералов.
    private static string WithoutComments(string code)
        => string.Join('\n', code.Split('\n').Select(line =>
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);

            return comment < 0 ? line : line[..comment];
        }));

    /// <summary>Вызовы файловой системы: то, что знает про платформу.</summary>
    [GeneratedRegex(@"(?<![\w.])(File|Directory|FileStream|FileInfo|DirectoryInfo|DriveInfo|FileSystemWatcher)\.", RegexOptions.CultureInvariant)]
    private static partial Regex FileSystemApi();

    /// <summary>Ветвление по ОС: то, что на другой платформе не исполняется вовсе.</summary>
    [GeneratedRegex(@"OperatingSystem\.|RuntimeInformation\.|SupportedOSPlatform|#if\s+(WINDOWS|LINUX)", RegexOptions.CultureInvariant)]
    private static partial Regex OperatingSystemApi();
}
