using System.Runtime.CompilerServices;

namespace PalORM.SourceGen.Tests;

/// <summary>
/// SourceGen 生成物快照基线（ITM-139/328 收口）。
/// 覆盖：PALORM016 白名单全部 16 类型 × SoftDelete/TenantAware/Converter/OwnedJson 特性组合 × 三方言产物。
/// 快照即评审对象：Emitter 任何改动导致生成物变化，此测试立即失败，diff 本身就是评审材料。
/// 刷新基线：PALORM_UPDATE_SNAPSHOTS=1 dotnet run，然后人工评审 git diff 后提交。
/// </summary>
internal sealed class SnapshotTests
{
    /// <summary>白名单全类型实体 + 全特性组合实体——快照输入源。改动此源码=改动基线，需同步刷新快照。</summary>
    private const string _snapshotSource = """
        using PalORM;
        using System.Text.Json.Serialization;

        public readonly record struct Ulid(string Value);

        public sealed class UlidConverter : IValueConverter<Ulid, string>
        {
            public string ToProvider(Ulid value) => value.Value;
            public Ulid FromProvider(string value) => new(value);
        }

        public sealed class Payload { public string Value { get; set; } = ""; }

        // 手写最小 STJ 上下文——测试编译不运行 STJ 源生成器，仅需满足编译与 PALORM 校验
        [JsonSerializable(typeof(Payload))]
        public class SnapshotJsonContext : JsonSerializerContext
        {
            public static SnapshotJsonContext Default { get; } = new(null);
            public SnapshotJsonContext(System.Text.Json.JsonSerializerOptions? options) : base(options) { }
            public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(System.Type type) => null;
            protected override System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => null;
        }

        // 实体 1：PALORM016 白名单全部 16 类型（含可空变体抽样）
        [Table("all_types")]
        public sealed partial class AllTypesEntity
        {
            [Key] public long Id { get; set; }
            [Column("int_value")] public int IntValue { get; set; }
            [Column("short_value")] public short ShortValue { get; set; }
            [Column("byte_value")] public byte ByteValue { get; set; }
            [Column("string_value")] public string StringValue { get; set; } = "";
            [Column("char_value")] public char CharValue { get; set; }
            [Column("bool_value")] public bool BoolValue { get; set; }
            [Column("decimal_value")] public decimal DecimalValue { get; set; }
            [Column("double_value")] public double DoubleValue { get; set; }
            [Column("float_value")] public float FloatValue { get; set; }
            [Column("datetime_value")] public System.DateTime DateTimeValue { get; set; }
            [Column("guid_value")] public System.Guid GuidValue { get; set; }
            [Column("dto_value")] public System.DateTimeOffset DateTimeOffsetValue { get; set; }
            [Column("date_value")] public System.DateOnly DateOnlyValue { get; set; }
            [Column("time_value")] public System.TimeOnly TimeOnlyValue { get; set; }
            [Column("nullable_int")] public int? NullableInt { get; set; }
            [Column("nullable_string")] public string? NullableString { get; set; }
            [Column("nullable_guid")] public System.Guid? NullableGuid { get; set; }
            [Column("nullable_time")] public System.TimeOnly? NullableTimeOnly { get; set; }
        }

        // 实体 2：SoftDelete × TenantAware × Converter × OwnedJson(对象+字符串) 全特性同体
        [SoftDelete]
        [TenantAware]
        [Table("full_feature")]
        [Index("ix_full_feature_code_kind", "code", "kind")]
        public sealed partial class FullFeatureEntity
        {
            [Key] public long Id { get; set; }
            [Column("code")] [Required] public string Code { get; set; } = "";
            [Column("kind")] public string Kind { get; set; } = "";
            [Column("external_id")]
            [Converter(typeof(UlidConverter))]
            public Ulid ExternalId { get; set; }
            [Column("payload")]
            [OwnedJson(typeof(SnapshotJsonContext))]
            public Payload Payload { get; set; } = new();
            [Column("raw_json")]
            [OwnedJson]
            public string RawJson { get; set; } = "";
            [Column("uniq_token")]
            [Unique]
            public string UniqueToken { get; set; } = "";
            [Column("row_updated_at")]
            [Timestamp]
            public System.DateTime RowUpdatedAt { get; set; }
            [Column("total_display")]
            [Computed("code || '-' || kind")]
            public string TotalDisplay { get; set; } = "";
            [Column("deleted_at")] public string? DeletedAt { get; set; }
            [Column("tenant_id")] public string TenantId { get; set; } = "";
        }

        // 实体 3：字符串主键 + 保留字标识符（引用符方言差异敏感面）
        [Table("order")]
        public sealed partial class ReservedWordEntity
        {
            [Key] public string Id { get; set; } = "";
            [Column("select")] public string Value { get; set; } = "";
        }

        // 实体 4：继承基类映射属性（ITM-502 防线——基类列不得静默丢失）
        public abstract class AuditBase
        {
            [Column("created_by")] public string CreatedBy { get; set; } = "";
            [Column("created_at")] public System.DateTimeOffset CreatedAt { get; set; }
        }
        [Table("inherited")]
        public sealed partial class InheritedEntity : AuditBase
        {
            [Key] public long Id { get; set; }
            [Column("name")] public string Name { get; set; } = "";
        }
        """;

    [Test]
    public async Task GeneratedSources_MatchCommittedSnapshots()
    {
        GeneratorTestHost.GeneratorResult result =
            GeneratorTestHost.RunGenerator(_snapshotSource, "SnapshotConsumer");

        // 编译探针：生成物必须可编译（ITM-301 防线——TimeOnly 曾生成不存在的 DbDataReader 方法）
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();

        string snapshotDir = GetSnapshotDirectory();
        bool update = Environment.GetEnvironmentVariable("PALORM_UPDATE_SNAPSHOTS") == "1";
        if (update)
        {
            // ITM-415：更新模式跳过全部比对断言——shell 残留导出会让快照防线整体静默关闭。
            // CI 上禁止（ci.yml 显式置空该变量并由本守卫兜底）；本地显眼输出警示。
            if (Environment.GetEnvironmentVariable("CI") is not null)
                throw new InvalidOperationException(
                    "PALORM_UPDATE_SNAPSHOTS=1 must never be set in CI — snapshot baseline would be silently rewritten instead of verified.");
#pragma warning disable CA1303 // 本地开发者警示输出，非本地化面
            Console.WriteLine("⚠⚠⚠ PALORM_UPDATE_SNAPSHOTS=1：快照基线正在被重写，本轮不做任何比对断言。评审 git diff 后提交，并 unset 该变量。⚠⚠⚠");
#pragma warning restore CA1303
            Directory.CreateDirectory(snapshotDir);
        }

        var mismatches = new List<string>();
        foreach ((string hintName, string content) in result.GeneratedSources.OrderBy(
            static pair => pair.Key, StringComparer.Ordinal))
        {
            string snapshotPath = Path.Combine(snapshotDir, hintName + ".snap");
            string normalized = Normalize(content);
            if (update)
            {
                await File.WriteAllTextAsync(snapshotPath, normalized);
                continue;
            }
            if (!File.Exists(snapshotPath))
            {
                mismatches.Add($"缺少快照基线: {hintName}（新生成物需评审后用 PALORM_UPDATE_SNAPSHOTS=1 建立基线）");
                continue;
            }
            string baseline = Normalize(await File.ReadAllTextAsync(snapshotPath));
            if (!string.Equals(baseline, normalized, StringComparison.Ordinal))
                mismatches.Add($"生成物偏离基线: {hintName}（{FirstDiffLine(baseline, normalized)}）");
        }

        if (!update)
        {
            // 反向守卫：基线里存在但本轮未生成 = 生成物静默消失
            foreach (string stale in Directory.EnumerateFiles(snapshotDir, "*.snap")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null && !result.GeneratedSources.ContainsKey(name))!)
            {
                mismatches.Add($"快照存在但未再生成: {stale}");
            }
        }

        await Assert.That(string.Join("\n", mismatches)).IsEmpty();
    }

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

    private static string FirstDiffLine(string baseline, string actual)
    {
        string[] baselineLines = baseline.Split('\n');
        string[] actualLines = actual.Split('\n');
        int max = Math.Max(baselineLines.Length, actualLines.Length);
        for (int i = 0; i < max; i++)
        {
            string expected = i < baselineLines.Length ? baselineLines[i] : "<eof>";
            string got = i < actualLines.Length ? actualLines[i] : "<eof>";
            if (!string.Equals(expected, got, StringComparison.Ordinal))
                return $"首个差异行 {i + 1}: 基线「{Truncate(expected)}」实际「{Truncate(got)}」";
        }
        return "长度不同";
    }

    [Test]
    public async Task MySqlUpsert_AutoIncrementPk_IncludesLastInsertId_AndExplicitPkDoesNot()
    {
        // ITM-608 防回退：自增 PK 实体的 MySQL upsert SQL 必须同时含
        // ① "pk = LAST_INSERT_ID(pk)" 赋值（更新已有行时 SELECT LAST_INSERT_ID() 返回该行 PK）
        // ② "; SELECT LAST_INSERT_ID()" 后缀（消费端 ExecuteScalarAsync 回读新行 PK）。
        // v4.1 预构建曾因 updateColumns(排除 PK 再 Quote) 与 Quote(pk) 比较恒 false 两者全丢。
        string autoIncrement = await File.ReadAllTextAsync(Path.Combine(
            GetSnapshotDirectory(), "CommandFactory_global__AllTypesEntity_7f6c8145.g.cs.snap"));
        string autoUpsertLine = autoIncrement.Split('\n')
            .First(l => l.Contains("UpsertMySqlSql", StringComparison.Ordinal));
        await Assert.That(autoUpsertLine).Contains("Id = LAST_INSERT_ID(Id)");
        await Assert.That(autoUpsertLine).Contains("; SELECT LAST_INSERT_ID()");

        // 非自增 PK（Id 显式插入）的 upsert 不加 LAST_INSERT_ID——消费端走 ExecuteNonQueryAsync，
        // 多余结果集反而致错（InsertWithLastInsertIdSql 行的后缀不属 upsert，行级断言避免误圈）。
        string explicitPk = await File.ReadAllTextAsync(Path.Combine(
            GetSnapshotDirectory(), "CommandFactory_global__ReservedWordEntity_c143a9c9.g.cs.snap"));
        string explicitUpsertLine = explicitPk.Split('\n')
            .First(l => l.Contains("UpsertMySqlSql", StringComparison.Ordinal));
        await Assert.That(explicitUpsertLine.Contains("LAST_INSERT_ID", StringComparison.Ordinal)).IsFalse();
    }

    private static string Truncate(string line)
        => line.Length <= 120 ? line : line[..120] + "…";

    private static string GetSnapshotDirectory([CallerFilePath] string sourcePath = "")
        => Path.Combine(Path.GetDirectoryName(sourcePath)!, "Snapshots");
}
