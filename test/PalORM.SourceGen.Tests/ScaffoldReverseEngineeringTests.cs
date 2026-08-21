using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using PalORM.Scaffold;

namespace PalORM.SourceGen.Tests;

/// <summary>Scaffold → PalORM 反向工程链路回归（BLOB/byte[]）。
/// <para>背景：Scaffold.TypeMapper 早已把 BLOB/bytea/varbinary 全族映射为 byte[]，
/// 但白名单放行前生成的实体被 PALORM016 整体拒绝——"生成即不可用"。byte[] 原生支持后
/// 本测试锁定全链路：DB 类型名 → C# 实体 → 源生成零诊断 → CRUD/BLOB DDL 齐。</para></summary>
internal sealed class ScaffoldReverseEngineeringTests
{
    [Test]
    public async Task TypeMapper_BinaryFamily_MapsToByteArray_AcrossDialects()
    {
        // SQLite 类型亲和性：BLOB 精确匹配
        await AssertBinaryAsync("BLOB", SchemaDialect.Sqlite);

        // PG/MySQL 二进制族（含长度后缀剥离：varbinary(8000) → varbinary）
        foreach ((string dbType, SchemaDialect dialect) in new[]
                 {
                     ("bytea", SchemaDialect.PostgreSql),
                     ("blob", SchemaDialect.PostgreSql),
                     ("binary", SchemaDialect.PostgreSql),
                     ("bytea", SchemaDialect.MySql),
                     ("varbinary(8000)", SchemaDialect.MySql),
                     ("binary(16)", SchemaDialect.MySql),
                     ("tinyblob", SchemaDialect.MySql),
                     ("mediumblob", SchemaDialect.MySql),
                     ("longblob", SchemaDialect.MySql),
                 })
        {
            await AssertBinaryAsync(dbType, dialect);
        }

        // 非二进制对照——防映射表被误改波及
        (string textType, _) = TypeMapper.Map("TEXT", SchemaDialect.Sqlite);
        await Assert.That(textType).IsEqualTo("string");
        (string varcharType, _) = TypeMapper.Map("varchar(255)", SchemaDialect.MySql);
        await Assert.That(varcharType).IsEqualTo("string");
    }

    [Test]
    public async Task ScaffoldedBlobEntity_PassesGeneration_WithCrudAndBlobDdl()
    {
        SchemaTable table = new("doc_blobs",
        [
            new SchemaColumn("id", "INTEGER", IsPrimaryKey: true, IsAutoIncrement: true, IsNullable: false),
            new SchemaColumn("title", "TEXT", IsPrimaryKey: false, IsAutoIncrement: false, IsNullable: false),
            new SchemaColumn("content", "BLOB", IsPrimaryKey: false, IsAutoIncrement: false, IsNullable: false),
        ]);
        string source = EntityGenerator.Generate(table, SchemaDialect.Sqlite, "Scaffolded");

        // 生成物含 byte[] 属性与表注解
        await Assert.That(source).Contains("[Table(\"doc_blobs\")]");
        await Assert.That(source)
            .Contains("[Column(\"content\")] public byte[] Content { get; set; } = default!;");

        // PalORM 分析器：零错误诊断（白名单放行前此处报 PALORM016、实体整体跳过生成）
        CSharpCompilation compilation = GeneratorTestHost.CreateCompilation(source, "ScaffoldConsumer");
        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        await Assert.That(diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Id)).IsEmpty();

        // 源生成器：产物可编译 + CRUD 绑定 + 三方言 BLOB DDL
        GeneratorTestHost.GeneratorResult result = GeneratorTestHost.RunGenerator(source);
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
        string generated = string.Join("\n", result.GeneratedSources.Values);
        await Assert.That(generated).Contains("GetFieldValue<byte[]>");
        await Assert.That(generated).Contains("BYTEA");
        await Assert.That(generated).Contains("LONGBLOB");
        await Assert.That(generated).Contains("BLOB NOT NULL");
    }

    private static async Task AssertBinaryAsync(string dbType, SchemaDialect dialect)
    {
        (string csharpType, bool isReferenceType) = TypeMapper.Map(dbType, dialect);
        await Assert.That(csharpType).IsEqualTo("byte[]");
        await Assert.That(isReferenceType).IsTrue();
    }
}
