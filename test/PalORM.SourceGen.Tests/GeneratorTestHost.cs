using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PalORM.SourceGen.Tests;

/// <summary>快照/对称性测试共享的生成器宿主——与 GeneratorPhase2Tests 内私有助手同构，
/// 独立成类避免改动既有测试文件。</summary>
internal static class GeneratorTestHost
{
    internal sealed record GeneratorResult(
        CSharpCompilation OutputCompilation,
        IReadOnlyDictionary<string, string> GeneratedSources);

    internal static GeneratorResult RunGenerator(string source, string assemblyName = "SnapshotConsumer")
    {
        CSharpCompilation compilation = CreateCompilation(source, assemblyName);
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.Single().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new PalORMGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out Compilation outputCompilation,
            out _);

        var generatedSources = driver.GetRunResult().Results
            .SelectMany(static result => result.GeneratedSources)
            .ToDictionary(
                static generated => generated.HintName,
                static generated => generated.SourceText.ToString(),
                StringComparer.Ordinal);
        return new GeneratorResult((CSharpCompilation)outputCompilation, generatedSources);
    }

    internal static CSharpCompilation CreateCompilation(string source, string assemblyName)
    {
        string[] trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<MetadataReference> references = trustedAssemblies
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(TableAttribute).Assembly.Location));
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        return CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references.DistinctBy(static reference => reference.Display, StringComparer.Ordinal),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    internal static string FormatErrors(Compilation compilation)
        => string.Join("\n", compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString()));
}
