using System.Data.Common;

namespace PalORM.Scaffold;

/// <summary>数据库 schema 元数据提供者——为 <see cref="EntityGenerator"/> 屏蔽三方言差异。
/// <para>各 Provider 实现：SqliteSchemaProvider / PostgreSqlSchemaProvider / MySqlSchemaProvider。
/// 通过 <see cref="SchemaDialect"/> 区分类型映射策略。</para></summary>
internal interface ISchemaProvider
{
    /// <summary>Provider 对应的方言（影响类型映射）。</summary>
    SchemaDialect Dialect { get; }

    /// <summary>获取所有用户表（排除系统表）。
    /// <para>SQLite: sqlite_master；PG/MySQL: information_schema.tables。</para></summary>
    Task<IReadOnlyList<SchemaTable>> GetTablesAsync(DbConnection connection, CancellationToken ct = default);
}

/// <summary>Scaffold 支持的方言。</summary>
internal enum SchemaDialect
{
    Sqlite,
    PostgreSql,
    MySql
}

/// <summary>表元数据——表名 + 列清单。</summary>
internal sealed record SchemaTable(string Name, IReadOnlyList<SchemaColumn> Columns);

/// <summary>列元数据——足够生成 C# 属性 + PalORM 注解。
/// <para><b>DbType</b>: 原始数据库类型名（SQLite 弱类型 'INTEGER'/'TEXT'；PG 'bigint'/'varchar'；MySQL 'int'/'varchar'）。
/// 由 <see cref="TypeMapper"/> 按 <see cref="SchemaDialect"/> 映射到 C# 类型。</para></summary>
internal sealed record SchemaColumn(
    string Name,
    string DbType,
    bool IsPrimaryKey,
    bool IsAutoIncrement,
    bool IsNullable);
