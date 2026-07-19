using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PalORM.SourceGen.Tests;

[Table("test_users")]
internal sealed partial class TestUser
{
    [Key] public long Id { get; set; }
    [Column("name")]
    [Required]
    public string Name { get; set; } = "";
    [Column("email")] public string? Email { get; set; }
}

[Table("external_users")]
internal sealed partial class ExternalUser
{
    // ITM-589：string 主键须显式 AutoIncrement = false（应用侧赋值，如雪花 ID/外部系统 ID）
    [Key(AutoIncrement = false)] public string Id { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
}

[Table("versioned_users")]
internal sealed partial class VersionedUser
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("version")]
    [ConcurrencyCheck]
    public long Version { get; set; }
}

internal sealed class SourceGenIntegrationTests
{
    [Test]
    public async Task StableHintName_IsDeterministicAndUniquePerSymbol()
    {
        const string firstIdentity = "global::Queries.Load(global::System.Int32)";
        const string secondIdentity = "global::Queries.Load(global::System.String)";

        string firstRun = PalORMGenerator.CreateStableHintName("SqlFile", firstIdentity);
        string secondRun = PalORMGenerator.CreateStableHintName("SqlFile", firstIdentity);
        string overload = PalORMGenerator.CreateStableHintName("SqlFile", secondIdentity);
        string sanitizedCollisionA = PalORMGenerator.CreateStableHintName("SqlFile", "global::A.B");
        string sanitizedCollisionB = PalORMGenerator.CreateStableHintName("SqlFile", "global::A_B");

        await Assert.That(firstRun).IsEqualTo(secondRun);
        await Assert.That(firstRun).IsNotEqualTo(overload);
        await Assert.That(sanitizedCollisionA).IsNotEqualTo(sanitizedCollisionB);
        await Assert.That(firstRun).DoesNotContain("-");
    }

    [Test]
    public async Task ObjectOwnedJsonWithoutContext_ReportsPalorm008()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("payload")]
                [OwnedJson]
                public Payload Payload { get; set; } = new();
            }
            public sealed class Payload { public string Value { get; set; } = ""; }
            """;
        var compilation = CSharpCompilation.Create(
            "OwnedJsonDiagnostic",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(TableAttribute).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = await compilation.WithAnalyzers([new PalORMAnalyzer()]).GetAnalyzerDiagnosticsAsync();

        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "PALORM008")).IsTrue();
    }

    [Test]
    public async Task ObjectOwnedJsonWithInvalidContext_ReportsPalorm009()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("payload")]
                [OwnedJson(typeof(NotAJsonContext))]
                public Payload Payload { get; set; } = new();
            }
            public sealed class Payload { public string Value { get; set; } = ""; }
            public sealed class NotAJsonContext { }
            """;

        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);

        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "PALORM009")).IsTrue();
    }

    [Test]
    public async Task ObjectOwnedJsonContextWithoutJsonSerializable_ReportsPalorm009()
    {
        const string source = """
            using PalORM;
            using System.Text.Json.Serialization;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("payload")]
                [OwnedJson(typeof(AppJsonContext))]
                public Payload Payload { get; set; } = new();
            }
            public sealed class Payload { public string Value { get; set; } = ""; }
            public partial class AppJsonContext : JsonSerializerContext { }
            """;

        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);

        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "PALORM009")).IsTrue();
    }

    [Test]
    public async Task ObjectOwnedJsonWithRegisteredContext_HasNoContextDiagnostic()
    {
        const string source = """
            using PalORM;
            using System.Text.Json.Serialization;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("payload")]
                [OwnedJson(typeof(AppJsonContext))]
                public Payload Payload { get; set; } = new();
            }
            public sealed class Payload { public string Value { get; set; } = ""; }
            [JsonSerializable(typeof(Payload))]
            public partial class AppJsonContext : JsonSerializerContext { }
            """;

        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);

        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id is "PALORM008" or "PALORM009" or "PALORM010")).IsFalse();
    }

    [Test]
    public async Task ObjectOwnedJsonOnGenericEntity_ReportsPalorm015()
    {
        const string source = """
            using PalORM;
            using System.Text.Json.Serialization;
            [Table("entities")]
            public sealed class Entity<T>
            {
                [Key] public long Id { get; set; }
                [Column("payload")]
                [OwnedJson(typeof(AppJsonContext))]
                public Payload Payload { get; set; } = new();
            }
            public sealed class Payload { public string Value { get; set; } = ""; }
            [JsonSerializable(typeof(Payload))]
            public partial class AppJsonContext : JsonSerializerContext { }
            """;

        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);

        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "PALORM015")).IsTrue();
    }

    [Test]
    public async Task ObjectOwnedJsonWithNestedContext_ReportsPalorm010()
    {
        const string source = """
            using PalORM;
            using System.Text.Json.Serialization;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("payload")]
                [OwnedJson(typeof(Contexts.AppJsonContext))]
                public Payload Payload { get; set; } = new();
            }
            public sealed class Payload { public string Value { get; set; } = ""; }
            public static class Contexts
            {
                [JsonSerializable(typeof(Payload))]
                public partial class AppJsonContext : JsonSerializerContext { }
            }
            """;

        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);

        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "PALORM010")).IsTrue();
    }

    [Test]
    public async Task RuntimeRegistryProperties_AreReadableButNotExternallyWritable()
    {
        const string source = """
            using System;
            using System.Collections.Frozen;
            using PalORM;
            public static class Consumer
            {
                public static int Read() => PalORM_Runtime.TableNames.Count;
                public static void Replace() => PalORM_Runtime.TableNames = FrozenDictionary<Type, string>.Empty;
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = GetCompilerDiagnostics(source);
        Diagnostic[] errors = [.. diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];

        await Assert.That(errors.Length).IsEqualTo(1);
        await Assert.That(errors[0].Id).IsEqualTo("CS0200");
    }

    private static ImmutableArray<Diagnostic> GetCompilerDiagnostics(string source)
    {
        string[] trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var references = trustedAssemblies
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(TableAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "RuntimeRegistryConsumer",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return compilation.GetDiagnostics();
    }

    private static async Task<ImmutableArray<Diagnostic>> GetAnalyzerDiagnosticsAsync(string source)
    {
        string[] trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var references = trustedAssemblies
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(TableAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "OwnedJsonDiagnostic",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return await compilation.WithAnalyzers([new PalORMAnalyzer()]).GetAnalyzerDiagnosticsAsync();
    }

    [Test]
    public async Task GeneratedRowFactory_IsAccessible()
    {
        // 验证源生成器产出的 RowFactory 已注册
        var factory = PalORM_Runtime.RowFactories[typeof(TestUser)];
        await Assert.That(factory).IsNotNull();
        string suffix = PalORMGenerator.CreateGeneratedTypeSuffix(
            "global::PalORM.SourceGen.Tests.TestUser");
        await Assert.That(factory).IsTypeOf(
            Type.GetType($"PalORM.Generated.RowFactory_{suffix}, PalORM.SourceGen.Tests")!);
    }

    [Test]
    public async Task TableName_IsRegistered()
    {
        var tableName = PalORM_Runtime.TableNames[typeof(TestUser)];
        await Assert.That(tableName).IsEqualTo("test_users");
    }

    [Test]
    public async Task QualifiedTableConfiguration_ReportsPalorm011()
    {
        const string source = """
            using PalORM;
            [Table("entities", Schema = "app")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
            }
            """;

        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);

        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "PALORM011")).IsTrue();
    }

    [Test]
    public async Task InvalidConcurrencyTokenType_ReportsPalorm012()
    {
        const string source = """
            using PalORM;
            [Table("items")]
            public sealed class Item
            {
                [Key] public long Id { get; set; }
                [ConcurrencyCheck] public string Version { get; set; } = "";
            }
            """;
        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);
        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "PALORM012")).IsTrue();
    }

    [Test]
    public async Task MultipleConcurrencyTokens_ReportPalorm013()
    {
        const string source = """
            using PalORM;
            [Table("items")]
            public sealed class Item
            {
                [Key] public long Id { get; set; }
                [ConcurrencyCheck] public int Version { get; set; }
                [ConcurrencyCheck] public long Revision { get; set; }
            }
            """;
        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);
        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "PALORM013")).IsTrue();
    }

    [Test]
    public async Task SoftDeleteWithoutDeletedAtColumn_ReportsPalorm014()
    {
        const string source = """
            using PalORM;
            [Table("items"), SoftDelete]
            public sealed class Item
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;
        var diagnostics = await GetAnalyzerDiagnosticsAsync(source);
        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "PALORM014")).IsTrue();
    }

    [Test]
    public async Task CommandSqls_AreGenerated()
    {
        var sqls = PalORM_Runtime.CommandSqls[typeof(TestUser)];
        await Assert.That(sqls.Insert).Contains("INSERT INTO test_users");
        await Assert.That(sqls.Update).Contains("UPDATE test_users");
        await Assert.That(sqls.Delete).Contains("DELETE FROM test_users");
    }

    [Test]
    public async Task BindInsert_IsRegistered()
    {
        var hasBinder = PalORM_Runtime.BindInsert.TryGetValue(typeof(TestUser), out _);
        await Assert.That(hasBinder).IsTrue();
    }

    [Test]
    public async Task BindUpdate_IsRegistered()
    {
        var hasBinder = PalORM_Runtime.BindUpdate.TryGetValue(typeof(TestUser), out _);
        await Assert.That(hasBinder).IsTrue();
    }

    [Test]
    public async Task BindUpsert_IsRegisteredAndIncludesPrimaryKey()
    {
        var metadata = PalORM_Runtime.CrudMetadatas[typeof(TestUser)];
        await Assert.That(metadata.BindUpsert).IsNotNull();
        await Assert.That(metadata.InsertColumns).DoesNotContain("Id");
        await Assert.That(metadata.UpsertColumns[0]).IsEqualTo("Id");

        var externalKeyMetadata = PalORM_Runtime.CrudMetadatas[typeof(ExternalUser)];
        await Assert.That(externalKeyMetadata.InsertColumns[0]).IsEqualTo("Id");
    }

    [Test]
    public async Task ConcurrencyUpdate_UsesAtomicIncrementAndSingleOldVersionParameter()
    {
        var metadata = PalORM_Runtime.CrudMetadatas[typeof(VersionedUser)];
        // ITM-137 后 legacy/方言双实现统一为单方法（无方言 = 不 quote），格式为 "col = expr"
        await Assert.That(metadata.Sqls.Update).Contains("version = version + 1");
        await Assert.That(metadata.Sqls.Update).Contains("WHERE Id = @p1 AND version = @p2");
        await Assert.That(metadata.IncrementVersion).IsNotNull();
        var entity = new VersionedUser { Id = 7, Name = "B", Version = 3 };
        metadata.IncrementVersion!(entity);
        await Assert.That(entity.Version).IsEqualTo(4);
    }

    [Test]
    public async Task BindDelete_IsRegistered()
    {
        var hasBinder = PalORM_Runtime.BindDelete.TryGetValue(typeof(TestUser), out _);
        await Assert.That(hasBinder).IsTrue();
    }

    [Test]
    public async Task PkColumn_IsId()
    {
        var pk = PalORM_Runtime.PkColumns[typeof(TestUser)];
        await Assert.That(pk).IsEqualTo("Id");
    }

    [Test]
    public async Task ColumnNames_AreGenerated()
    {
        var cols = PalORM_Runtime.ColumnNames[typeof(TestUser)];
        await Assert.That(cols.Count).IsEqualTo(3);
        await Assert.That(cols).Contains("Id");
        await Assert.That(cols).Contains("name");
        await Assert.That(cols).Contains("email");
    }

    [Test]
    public async Task CreateTableSql_IsGenerated()
    {
        var ddl = PalORM_Runtime.CreateTableSql[typeof(TestUser)];
        await Assert.That(ddl).Contains("CREATE TABLE IF NOT EXISTS \"test_users\"");
        await Assert.That(ddl).Contains("NOT NULL");
        await Assert.That(ddl).Contains("PRIMARY KEY AUTOINCREMENT");
    }

    [Test]
    public async Task PropertyToColumn_IsGenerated()
    {
        var mapping = PalORM_Runtime.PropertyToColumn[typeof(TestUser)];
        await Assert.That(mapping["Id"]).IsEqualTo("Id");
        await Assert.That(mapping["Name"]).IsEqualTo("name");
        await Assert.That(mapping["Email"]).IsEqualTo("email");
    }

    // ─── 生成物结构断言（非 Verify 快照——无 .verified 基线，为结构性 Contains 断言；
    //     逐字符快照守护见 ITM-139，需引入 Verify 基线后再改名回 Snapshot） ─────

    [Test]
    public async Task RowFactory_GeneratedTypeStructure()
    {
        var factory = PalORM_Runtime.RowFactories[typeof(TestUser)];
        await Assert.That(factory).IsNotNull();
        // Verify the generated RowFactory type name
        string typeName = factory.GetType().FullName!;
        string suffix = PalORMGenerator.CreateGeneratedTypeSuffix(
            "global::PalORM.SourceGen.Tests.TestUser");
        await Assert.That(typeName).Contains($"RowFactory_{suffix}");
    }

    [Test]
    public async Task CommandFactory_GeneratedSqlStructure()
    {
        var sqls = PalORM_Runtime.CommandSqls[typeof(TestUser)];
        // Verify SQL patterns match expected structure
        await Assert.That(sqls.Insert).Contains("INSERT INTO test_users");
        await Assert.That(sqls.Insert).Contains("VALUES");
        await Assert.That(sqls.Update).Contains("UPDATE test_users SET");
        await Assert.That(sqls.Update).Contains("WHERE");
        await Assert.That(sqls.Delete).Contains("DELETE FROM test_users");
    }

    [Test]
    public async Task TypeMapper_VerifyExistence()
    {
        // TypeMapper 按需生成：TestUser 无自定义类型，应无 TypeMapper 生成
        string suffix = PalORMGenerator.CreateGeneratedTypeSuffix(
            "global::PalORM.SourceGen.Tests.TestUser");
        Type? generatedType = Type.GetType(
            $"PalORM.Generated.TypeMapper_{suffix}, PalORM.SourceGen.Tests");
        // 无自定义类型映射时 TypeMapper 不生成
        await Assert.That(generatedType).IsNull();
    }
}
