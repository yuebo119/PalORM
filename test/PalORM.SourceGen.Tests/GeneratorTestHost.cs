using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PalORM.SourceGen.Tests;

/// <summary>快照/对称性测试共享的生成器宿主——与 GeneratorPhase2Tests 内私有助手同构，
/// 独立成类避免改动既有测试文件。</summary>
internal static class GeneratorTestHost
{
    internal sealed record GeneratorResult(
        CSharpCompilation OutputCompilation,
        IReadOnlyDictionary<string, string> GeneratedSources);

    internal static GeneratorResult RunGenerator(string source, string assemblyName = "SnapshotConsumer")
        => RunGenerator(source, assemblyName, analyzerConfigOptions: null);

    /// <summary>运行生成器，支持注入 analyzerConfigOptions（如 build_property.PalORMAutoTagging=true）。</summary>
    internal static GeneratorResult RunGenerator(
        string source,
        string assemblyName,
        IReadOnlyDictionary<string, string>? analyzerConfigOptions)
    {
        CSharpCompilation compilation = CreateCompilation(source, assemblyName);
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.Single().Options;

        // 构造 AnalyzerConfigOptionsProvider——源生成器经 context.AnalyzerConfigOptionsProvider 读取
        AnalyzerConfigOptionsProvider configProvider = TestConfigOptionsProvider.Create(
            analyzerConfigOptions ?? new Dictionary<string, string>());

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new PalORMGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            optionsProvider: configProvider);
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

/// <summary>
/// 测试用 AnalyzerConfigOptionsProvider——把字典转为 GlobalOptions 供源生成器读取。
/// 用于测试 PalORMAutoTagging 等 build_property 开关。
/// </summary>
internal sealed class TestConfigOptionsProvider : AnalyzerConfigOptionsProvider
{
    private readonly TestGlobalOptions _globalOptions;

    private TestConfigOptionsProvider(IReadOnlyDictionary<string, string> options)
    {
        _globalOptions = new TestGlobalOptions(options);
    }

    internal static TestConfigOptionsProvider Create(IReadOnlyDictionary<string, string> options)
        => new(options);

    public override AnalyzerConfigOptions GlobalOptions => _globalOptions;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => TestGlobalOptions.Empty;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => TestGlobalOptions.Empty;

    private sealed class TestGlobalOptions : AnalyzerConfigOptions
    {
        internal static readonly TestGlobalOptions Empty = new(new Dictionary<string, string>());
        private readonly IReadOnlyDictionary<string, string> _options;

        internal TestGlobalOptions(IReadOnlyDictionary<string, string> options)
        {
            _options = options;
        }

        public override bool TryGetValue(string key, out string value)
        {
            if (_options.TryGetValue(key, out string? v))
            {
                value = v;
                return true;
            }
            value = "";
            return false;
        }
    }
}
