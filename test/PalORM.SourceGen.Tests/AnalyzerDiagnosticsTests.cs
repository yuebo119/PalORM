using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PalORM.SourceGen.Tests;

/// <summary>PALORM002-005 与 PALORM017 诊断负向测试——验证触发条件且不级联生成错误。
/// PALORM006/007 当前无 Analyzer 报告点（006 由 SqlFileEmitter 的 Obsolete-error 机制承担），
/// 见 DiagnosticDescriptors_006And007_RemainRegisteredWithoutAnalyzerTrigger。</summary>
public sealed class AnalyzerDiagnosticsTests
{
    [Test]
    public async Task PropertyWithoutColumn_ReportsPalorm002_WithoutCascadingErrors()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM002")).IsTrue();
        await Assert.That(diagnostics.Single(d => d.Id == "PALORM002").Severity)
            .IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task PropertyNamedIdOrWithColumn_DoesNotReportPalorm002()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM002")).IsFalse();
    }

    [Test]
    public async Task ForeignKeyToUnknownTable_ReportsPalorm003_WithoutCascadingErrors()
    {
        const string source = """
            using PalORM;
            [Table("orders")]
            public sealed class Order
            {
                [Key] public long Id { get; set; }
                [Column("customer_id")]
                [ForeignKey("no_such_table", "id", OnDelete = DeleteAction.Cascade)]
                public long CustomerId { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM003")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task ForeignKeyToKnownTable_DoesNotReportPalorm003()
    {
        const string source = """
            using PalORM;
            [Table("customers")]
            public sealed class Customer { [Key] public long Id { get; set; } }
            [Table("orders")]
            public sealed class Order
            {
                [Key] public long Id { get; set; }
                [Column("customer_id")]
                [ForeignKey("customers", "id", OnDelete = DeleteAction.Cascade)]
                public long CustomerId { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM003")).IsFalse();
    }

    [Test]
    public async Task ForeignKeyWithoutOnDelete_ReportsPalorm004_WithoutCascadingErrors()
    {
        const string source = """
            using PalORM;
            [Table("customers")]
            public sealed class Customer { [Key] public long Id { get; set; } }
            [Table("orders")]
            public sealed class Order
            {
                [Key] public long Id { get; set; }
                [Column("customer_id")]
                [ForeignKey("customers", "id")]
                public long CustomerId { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM004")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task CompositePrimaryKey_ReportsPalorm019()
    {
        // ITM-311：复合主键的 BindDelete 单 key 语义无法表达，必须明确拒绝
        const string source = """
            using PalORM;
            [Table("order_lines")]
            public sealed class OrderLine
            {
                [Key] public long OrderId { get; set; }
                [Key] public long LineNo { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM019")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task InheritedKey_DoesNotReportPalorm001()
    {
        // ITM-559：[Key] 在基类时，计数走基类链——不得误报"无 [Key]"
        const string source = """
            using PalORM;
            public abstract class AuditBase
            {
                [Key] public long Id { get; set; }
            }
            [Table("audited")]
            public sealed class Audited : AuditBase
            {
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM001")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task NullableValueTypeKey_ReportsPalorm022()
    {
        // ITM-560：long? 主键生成 CS0037 不编译代码——编译期明确拒绝
        const string source = """
            using PalORM;
            [Table("nullable_keyed")]
            public sealed class NullableKeyed
            {
                [Key] public long? Id { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM022")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task InitOnlyKey_ReportsPalorm022()
    {
        // ITM-561：init-only 主键此前零诊断静默跳过，运行期才报 not registered
        const string source = """
            using PalORM;
            [Table("init_keyed")]
            public sealed class InitKeyed
            {
                [Key] public long Id { get; init; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM022")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    // ─── PALORM022：非整型主键 + AutoIncrement（ITM-589）───

    [Test]
    public async Task GuidKeyWithDefaultAutoIncrement_ReportsPalorm022()
    {
        // ITM-589：Guid 主键 + 默认 AutoIncrement=true（[Key] 无参）静默忽略，
        // 运行时 InsertAsync 才失败——编译期必须明确拒绝。
        const string source = """
            using PalORM;
            [Table("guid_default")]
            public sealed class GuidDefault
            {
                [Key] public System.Guid Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM022")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task StringKeyWithExplicitAutoIncrement_ReportsPalorm022()
    {
        // ITM-589：string 主键 + 显式 [Key(AutoIncrement = true)] 同样拒绝。
        const string source = """
            using PalORM;
            [Table("string_explicit")]
            public sealed class StringExplicit
            {
                [Key(AutoIncrement = true)] public string Id { get; set; } = "";
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM022")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task StringKeyWithAutoIncrementFalse_DoesNotReportPalorm022()
    {
        // ITM-589 正向：string 主键 + AutoIncrement=false（应用侧赋值，如雪花 ID/外部 ID）合法。
        const string source = """
            using PalORM;
            [Table("string_app_assigned")]
            public sealed class StringAppAssigned
            {
                [Key(AutoIncrement = false)] public string Id { get; set; } = "";
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM022")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task LongKeyWithAutoIncrement_DoesNotReportPalorm022()
    {
        // ITM-589 正向：long 主键 + AutoIncrement=true 是合法默认（数据库自增）。
        const string source = """
            using PalORM;
            [Table("long_autoinc")]
            public sealed class LongAutoInc
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM022")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task IndexOnUnknownColumn_ReportsPalorm020()
    {
        // ITM-565：索引列拼错此前编译期静默，MigrateAsync 运行期才报列不存在
        const string source = """
            using PalORM;
            [Table("indexed_entities")]
            [Index("ix_typo", "typo_col")]
            public sealed class IndexedEntity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d =>
            d.Id == "PALORM020" && d.GetMessage(null).Contains("typo_col", System.StringComparison.Ordinal))).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task OrmCallInsideLoop_ReportsPalorm005_WithoutCascadingErrors()
    {
        // ITM-574：语义判定后仅 PalORM 命名空间的方法参与 N+1 检测——测试方法置于 PalORM 命名空间
        const string source = """
            using System.Collections.Generic;
            using PalORM.TestSupport;
            namespace PalORM.TestSupport
            {
                public static class RepoExtensions
                {
                    public static object From<T>(this object receiver) => new();
                }
            }
            namespace PalORM.TestSupport.Consumer
            {
                public sealed class Repo
                {
                    public void LoadAll(List<long> ids)
                    {
                        foreach (long id in ids)
                        {
                            _ = this.From<object>();
                        }
                    }
                }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM005")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task NonPalormOrmLikeCallInsideLoop_DoesNotReportPalorm005()
    {
        // ITM-574 负向：EF Core 等第三方库的同名方法（非 PalORM 命名空间）循环内调用不误报
        const string source = """
            using System.Collections.Generic;
            public sealed class Repo
            {
                public void LoadAll(List<long> ids)
                {
                    foreach (long id in ids)
                    {
                        _ = this.From<object>();
                    }
                }
                private object From<T>() => new();
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM005")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task PalormCallInLambdaInsideLoop_DoesNotReportPalorm005()
    {
        // ITM-574 负向：循环体内定义、循环外执行的 lambda 不是 N+1
        const string source = """
            using System;
            using System.Collections.Generic;
            using PalORM.TestSupport;
            namespace PalORM.TestSupport
            {
                public static class RepoExtensions
                {
                    public static object From<T>(this object receiver) => new();
                }
            }
            namespace PalORM.TestSupport.Consumer
            {
                public sealed class Repo
                {
                    public void BuildLoaders(List<long> ids, List<Func<object>> loaders)
                    {
                        foreach (long id in ids)
                        {
                            loaders.Add(() => this.From<object>());
                        }
                    }
                }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM005")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task OrmCallOutsideLoop_DoesNotReportPalorm005()
    {
        const string source = """
            public sealed class Repo
            {
                public void LoadOne()
                {
                    _ = this.From<object>();
                }
                private object From<T>() => new();
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM005")).IsFalse();
    }

    [Test]
    public async Task IndexAndUnique_AfterAdrB_DoNotReportPalorm017()
    {
        // ADR-B 后 [Index]/[Unique] 参与索引 DDL 生成，PALORM017 停报
        const string source = """
            using PalORM;
            [Table("entities")]
            [Index("idx_name", "name")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")]
                [Unique]
                public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM017")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task DefaultValueAndColumnSchemaArgs_ReportPalorm017()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name", Length = 128)]
                public string Name { get; set; } = "";
                [Column("created_at")]
                [DefaultValue("NOW()")]
                public string CreatedAt { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        int count = diagnostics.Count(d => d.Id == "PALORM017");
        // [Column(Length=…)] + [DefaultValue] = 2 处独立告警（[Unique] 已由 ADR-B 落地停报）
        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task PlainEntity_DoesNotReportPalorm017()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM017")).IsFalse();
    }

    // PALORM006/007 已删除（v2.1.0）——006 由 SqlFileEmitter Obsolete-error 承担，
    // 007 无 schema 对照数据源。编号不复用。原测试 DiagnosticDescriptors_006And007_RemainRegisteredWithoutAnalyzerTrigger 已删。

    // ─── PALORM020：索引声明有效性（ITM-203）───

    [Test]
    public async Task EmptyColumnsIndex_ReportsPalorm020_WithoutCascadingErrors()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            [Index("idx_orphan")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM020")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task DuplicateIndexName_SameEntity_ReportsPalorm020()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            [Index("idx_x", "name")]
            [Index("idx_x", "value")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("value")] public long Value { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Count(d => d.Id == "PALORM020")).IsEqualTo(1);
    }

    [Test]
    public async Task ExplicitIndexCollidingWithDerivedUniqueName_ReportsPalorm020()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            [Index("ux_t_code", "name")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("code")]
                [Unique]
                public string Code { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        // [Unique] 派生 ux_t_code 与显式 [Index("ux_t_code")] 冲突
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM020")).IsTrue();
    }

    [Test]
    public async Task ValidIndexDeclarations_DoNotReportPalorm020()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            [Index("idx_name", "name")]
            [Index("idx_value", "value")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("value")] public long Value { get; set; }
                [Column("code")]
                [Unique]
                public string Code { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM020")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    // ─── PALORM021：列名唯一性（ITM-409）───

    [Test]
    public async Task DuplicateColumnName_ReportsPalorm021()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("name")] public string DisplayName { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Count(d => d.Id == "PALORM021")).IsEqualTo(1);
    }

    [Test]
    public async Task DuplicateColumnName_CaseInsensitive_ReportsPalorm021()
    {
        // 三方言标识符默认大小写不敏感（PG 折叠小写/MySQL 依 OS/SQLite 不敏感）——统一按不敏感判定
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("Name")] public string Name { get; set; } = "";
                [Column("name")] public string DisplayName { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM021")).IsTrue();
    }

    [Test]
    public async Task DistinctColumnNames_DoNotReportPalorm021()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("display_name")] public string DisplayName { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM021")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    // ─── 基类链判定口径漂移（ITM-587/588/601）───
    // 派生类继承 AuditBase/TenantBase 放列、用 new 隐藏基类同名属性时，
    // PALORM014/018/021 必须走 EnumerateMappedProperties（同 TableModel）——不得误报。

    [Test]
    public async Task SoftDeleteColumnInBase_DoesNotReportPalorm014()
    {
        // ITM-587：派生类继承 AuditBase（基类含 deleted_at）+ [SoftDelete] ——不得误报 PALORM014。
        const string source = """
            using PalORM;
            public abstract class AuditBase
            {
                [Column("deleted_at")] public System.DateTime? DeletedAt { get; set; }
            }
            [Table("orders_softdelete_base")]
            [SoftDelete]
            public sealed class OrderSoftDeleteBase : AuditBase
            {
                [Key] public long Id { get; set; }
                [Column("status")] public string Status { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM014")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task TenantColumnInBase_DoesNotReportPalorm018()
    {
        // ITM-588：派生类继承 TenantBase（基类含 tenant_id）+ [TenantAware] ——不得误报 PALORM018。
        const string source = """
            using PalORM;
            public abstract class TenantBase
            {
                [Column("tenant_id")] public string TenantId { get; set; } = "";
            }
            [Table("docs_tenant_base")]
            [TenantAware]
            public sealed class DocTenantBase : TenantBase
            {
                [Key] public long Id { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM018")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task NewHiddenBaseColumn_DoesNotReportPalorm021()
    {
        // ITM-601：派生类用 new 隐藏基类同名属性——EnumerateMappedProperties 的 seen 集合
        // 按派生优先跳过基类版本，与 TableModel 列收集口径一致，不得误报 PALORM021。
        const string source = """
            using PalORM;
            public abstract class NamedBase
            {
                [Column("name")] public virtual string Name { get; set; } = "";
            }
            [Table("derived_named")]
            public sealed class DerivedNamed : NamedBase
            {
                [Column("name")] public override string Name { get; set; } = "";
                [Key] public long Id { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM021")).IsFalse();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task ConcurrencyTokenInBase_ReportsPalorm013()
    {
        // ITM-607：派生类继承 AuditBase.Version（基类 [ConcurrencyCheck]）+ 自身 RowVer——
        // 走 EnumerateMappedProperties 后看到 2 个令牌应报 PALORM013。
        // 此前 type.GetMembers() 只查声明类型漏掉基类令牌，与 TableModel 列收集口径不一致。
        const string source = """
            using PalORM;
            public abstract class AuditVersioned
            {
                [ConcurrencyCheck] public long Version { get; set; }
            }
            [Table("double_versioned")]
            public sealed class DoubleVersioned : AuditVersioned
            {
                [Key] public long Id { get; set; }
                [ConcurrencyCheck] public long RowVer { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM013")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task ForeignKeyInBase_ReportsPalorm003_WithBaseChain()
    {
        // ITM-607（290 行块）：基类属性的 [ForeignKey] 同样需被 PALORM003 检查。
        // 基类声明 FK 引用未知表，派生类继承应触发——此前 type.GetMembers() 漏掉基类 FK。
        const string source = """
            using PalORM;
            [Table("known_target")]
            public sealed class KnownTarget
            {
                [Key] public long Id { get; set; }
            }
            public abstract class FkBase
            {
                [Column("parent_id")]
                [ForeignKey("no_such_table", "id")]
                public long ParentId { get; set; }
            }
            [Table("derived_fk")]
            public sealed class DerivedFk : FkBase
            {
                [Key] public long Id { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM003")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    // ITM-510：索引名大小写碰撞（MySQL 大小写不敏感）——ix_Foo/ix_foo 应报 PALORM020。
    [Test]
    public async Task IndexNames_DifferingOnlyByCase_ReportsPalorm020()
    {
        const string source = """
            using PalORM;
            [Table("entities")]
            [Index("ix_Foo", "name")]
            [Index("ix_foo", "value")]
            public sealed class Entity
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
                [Column("value")] public long Value { get; set; }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Count(d => d.Id == "PALORM020")).IsEqualTo(1);
    }

    // ITM-512：命名空间校验——非 PalORM 命名空间的同名注解不得被当 PalORM 注解处理。
    [Test]
    public async Task NonPalORMTableAttribute_DoesNotTriggerDiagnostics()
    {
        const string source = """
            namespace Other
            {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class TableAttribute : System.Attribute
                {
                    public TableAttribute(string name) { }
                }
            }
            namespace Consumer
            {
                // 挂的是 Other.TableAttribute（非 PalORM）——不应触发 PALORM001（缺主键）等
                [Other.Table("plain")]
                public sealed class PlainPoco
                {
                    public long Id { get; set; }
                    public string Name { get; set; } = "";
                }
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id.StartsWith("PALORM", System.StringComparison.Ordinal))).IsFalse();
    }

    // ITM-512 下沉：DataAnnotations 的同名 [Table]/[Key]/[Column] 混挂不得被 PalORM 误判。
    [Test]
    public async Task DataAnnotationsAttributes_AreNotTreatedAsPalORM()
    {
        const string source = """
            using System.ComponentModel.DataAnnotations;
            using System.ComponentModel.DataAnnotations.Schema;
            [Table("da_table")]
            public sealed class DaPoco
            {
                [Key] public long Id { get; set; }
                [Column("name")] public string Name { get; set; } = "";
            }
            """;

        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);

        // 挂的全是 DataAnnotations（非 PalORM 命名空间）——不应触发任何 PALORM 诊断
        await Assert.That(diagnostics.Any(d => d.Id.StartsWith("PALORM", System.StringComparison.Ordinal))).IsFalse();
    }

    private static async Task<(ImmutableArray<Diagnostic> AnalyzerDiagnostics, ImmutableArray<Diagnostic> CompileErrors)>
        AnalyzeAsync(string source)
    {
        string[] trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var references = trustedAssemblies
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(TableAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "AnalyzerDiagnosticsConsumer",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        ImmutableArray<Diagnostic> analyzerDiagnostics = await compilation
            .WithAnalyzers([new PalORMAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        ImmutableArray<Diagnostic> compileErrors = [.. compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)];
        return (analyzerDiagnostics, compileErrors);
    }

    // === v5.0 新规则测试（PALORM023-027, 031-033, 034-037, 040）===

    [Test]
    public async Task PALORM023_EntityWithNoInsertableColumns_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [IgnoreOnInsert] public string A { get; set; } = "";
                [Timestamp] public System.DateTimeOffset Updated { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM023")).IsTrue();
    }

    [Test]
    public async Task PALORM023_EntityWithInsertableColumn_DoesNotReport()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                public string A { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM023")).IsFalse();
    }

    [Test]
    public async Task PALORM024_EntityWithNoUpdatableColumns_Reports()
    {
        // r8-D-A：无可更新列用真源真排除形态构造（Computed/Timestamp/Key）——
        // 纯 [IgnoreOnInsert] 实体的旧断言锁定的是误报行为（运行时 UPDATE 正常：
        // IsUpdatableColumn 真源不排除 IgnoreOnInsert），随谓词对齐反转
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Computed("1")] public string A { get; set; } = "";
                [Timestamp] public System.DateTimeOffset B { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM024")).IsTrue();
    }

    [Test]
    public async Task PALORM024_IgnoreOnlyEntity_DoesNotReport()
    {
        // r8-D-A 锁定：纯 [IgnoreOnInsert] 列在 UPDATE 真源中可更新——不报 024
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [IgnoreOnInsert] public string A { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM024")).IsFalse();
    }

    [Test]
    public async Task PALORM024_EntityWithUpdatableColumn_DoesNotReport()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                public string A { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM024")).IsFalse();
    }

    [Test]
    public async Task PALORM025_TimestampOnNonTemporalType_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Timestamp] public int Version { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM025")).IsTrue();
    }

    [Test]
    public async Task PALORM025_TimestampOnDateTime_DoesNotReport()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Timestamp] public System.DateTime Updated { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM025")).IsFalse();
    }

    [Test]
    public async Task PALORM026_NotMappedWithKey_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [NotMapped] [Key] public long Id { get; set; }
                public string Name { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM026")).IsTrue();
    }

    [Test]
    public async Task PALORM026_NotMappedAlone_DoesNotReport()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [NotMapped] public string Transient { get; set; } = "";
                public string Name { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM026")).IsFalse();
    }

    [Test]
    public async Task PALORM027_ConverterWithOwnedJson_Reports()
    {
        const string source = """
            using PalORM;
            public sealed class IdConv : IValueConverter<string, string>
            {
                public string ToProvider(string v) => v;
                public string FromProvider(string v) => v;
            }
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [OwnedJson] [Converter(typeof(IdConv))] public string Payload { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM027")).IsTrue();
    }

    [Test]
    public async Task PALORM027_ConverterAlone_DoesNotReport()
    {
        const string source = """
            using PalORM;
            public sealed class IdConv : IValueConverter<string, string>
            {
                public string ToProvider(string v) => v;
                public string FromProvider(string v) => v;
            }
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Converter(typeof(IdConv))] public string Payload { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM027")).IsFalse();
    }

    [Test]
    public async Task PALORM034_KeyWithNonDefaultValue_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; } = -1;
                public string Name { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM034")).IsTrue();
    }

    [Test]
    public async Task PALORM034_KeyWithAutoIncrementFalse_DoesNotReport()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key(AutoIncrement = false)] public long Id { get; set; } = 12345;
                public string Name { get; set; } = "";
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM034")).IsFalse();
    }

    [Test]
    public async Task PALORM035_ConcurrencyCheckWithIgnoreOnInsert_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [ConcurrencyCheck] [IgnoreOnInsert] public int Version { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM035")).IsTrue();
    }

    [Test]
    public async Task PALORM035_ConcurrencyCheckAlone_DoesNotReport()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [ConcurrencyCheck] public int Version { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM035")).IsFalse();
    }

    [Test]
    public async Task PALORM037_RequiredWithNullableAnnotation_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Required] public string? Name { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM037")).IsTrue();
    }

    [Test]
    public async Task PALORM040_TenantColumnNullableString_Reports()
    {
        const string source = """
            using PalORM;
            [Table("t")] [TenantAware]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Column("tenant_id")] public string? TenantId { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM040")).IsTrue();
    }

    [Test]
    public async Task PALORM040_TenantColumnValueType_DoesNotReport()
    {
        const string source = """
            using PalORM;
            [Table("t")] [TenantAware]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Column("tenant_id")] public long TenantId { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM040")).IsFalse();
    }

    [Test]
    public async Task PALORM031_InferredGenericCall_Reports_VersionedEntity()
    {
        // ITM-614 探针：推断式调用（无显式 <E>）的 ma.Name 是 IdentifierNameSyntax——
        // 语法层 GenericNameSyntax 判定漏报。语义层（IMethodSymbol.TypeArguments）修复后应报。
        // 泛型方法形态避免依赖具体 Provider 程序集（DataSession<TProvider> 约束即可编译）。
        const string source = """
            using PalORM;
            using System.Collections.Generic;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [ConcurrencyCheck] public long Version { get; set; }
            }
            public static class C
            {
                static void M<TProvider>(DataSession<TProvider> s, List<E> list)
                    where TProvider : IDbProvider
                {
                    _ = s.BulkUpdateBatchAsync(list);
                }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> compileErrors) =
            await AnalyzeAsync(source);

        await Assert.That(diagnostics.Any(d => d.Id == "PALORM031")).IsTrue();
        await Assert.That(compileErrors).IsEmpty();
    }

    [Test]
    public async Task PALORM031_ExplicitGenericCall_StillReports_VersionedEntity()
    {
        // 对照组：显式 <E> 形态在修复前后都应报告（语义层判定不得回归）
        const string source = """
            using PalORM;
            using System.Collections.Generic;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [ConcurrencyCheck] public long Version { get; set; }
            }
            public static class C
            {
                static void M<TProvider>(DataSession<TProvider> s, List<E> list)
                    where TProvider : IDbProvider
                {
                    _ = s.BulkUpdateBatchAsync<E>(list);
                }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM031")).IsTrue();
    }

    [Test]
    public async Task PALORM032_InferredGenericInclude_ReportsUnregisteredEntity()
    {
        // ITM-662 锁定 032 语义层修复（推断式 Include 引用未注册实体应报）
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                public long AuthorId { get; set; }
            }
            public sealed class Author { public long Id { get; set; } }
            public static class C
            {
                static void M<TProvider>(DataSession<TProvider> s)
                    where TProvider : IDbProvider
                {
                    _ = s.From<E>().Include((E e) => e.AuthorId, (Author a) => a.Id);
                }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, ImmutableArray<Diagnostic> errors) =
            await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM032")).IsTrue();
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task PALORM033_PalORMFooNamespace_DoesNotReport()
    {
        // ITM-649/662 锁定：用户命名空间 PalORMFoo 下自建 QueryBuilder 的 Select 不误报
        const string source = """
            namespace PalORMFoo
            {
                public sealed class QueryBuilder<T> where T : class, new()
                {
                    public QueryBuilder<T> Select(System.Linq.Expressions.Expression<System.Func<T, object?>> m) => this;
                    public QueryBuilder<T> ToListAsync() => this;
                }
                public sealed class E { public long Id { get; set; } }
                public static class C
                {
                    static void M(QueryBuilder<E> b) { _ = b.Select(x => x.Id); _ = b.ToListAsync(); }
                }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM033")).IsFalse();
    }

    [Test]
    public async Task PALORM036_ValueOnlyEntityInDisabledContext_DoesNotReport()
    {
        // ITM-648/662 锁定：纯值类型实体（无引用属性）在 NRT 禁用项目不报（噪音消除）
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                public int Count { get; set; }
                public System.DateTime When { get; set; }
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM036")).IsFalse();
    }

    [Test]
    public async Task PALORM034_DefaultBangInitializer_DoesNotReport()
    {
        // ITM-650/662 锁定：default! 等等价默认写法白名单
        const string source = """
            using PalORM;
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; } = default!;
                public string Name { get; set; } = default!;
            }
            """;
        (ImmutableArray<Diagnostic> diagnostics, _) = await AnalyzeAsync(source);
        await Assert.That(diagnostics.Any(d => d.Id == "PALORM034")).IsFalse();
    }
}
