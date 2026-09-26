using System.Text.Json.Serialization;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

public sealed class OwnedJsonTests
{
    [Test]
    public async Task OwnedJson_RawString_IsStoredWithoutDoubleEncoding()
    {
        const string json = "{\"key\":\"value\"}";
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        var entity = await db.InsertAsync(new JsonTestEntity
        {
            Name = "raw",
            Data = json,
            Details = new JsonDetails { Key = "object", Count = 1 }
        });
        await using var command = db.GetRawConnection().CreateCommand();
        command.CommandText = "SELECT data FROM json_test WHERE Id = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = entity.Id;
        command.Parameters.Add(parameter);

        var stored = (string?)await command.ExecuteScalarAsync();
        await Assert.That(stored).IsEqualTo(json);
    }

    [Test]
    public async Task OwnedJson_Object_UsesSourceGeneratedContextForRoundTrip()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        var entity = await db.InsertAsync(new JsonTestEntity
        {
            Name = "object",
            Data = "{}",
            Details = new JsonDetails { Key = "value", Count = 7 }
        });
        var found = await db.GetAsync<JsonTestEntity>(entity.Id);

        await Assert.That(found!.Name).IsEqualTo("object");
        await Assert.That(found.Details.Key).IsEqualTo("value");
        await Assert.That(found.Details.Count).IsEqualTo(7);
    }

    // L45 回归（2026-09-26）：OwnedJson 读路径探针仅验 Microsoft.Data.Sqlite 就把 emit 改成
    // GetFieldValue<byte[]>（Span 解析），MySqlConnector 对 TEXT 列该调用抛 InvalidCastException
    // （CI MySQL Native AOT job 实证）。已回滚 emit 到 GetString；以下三项方言 round trip
    // 钉死三方言 OwnedJson 读路径行为——本类故障再发生会在这里先炸（CI Integration service 覆盖）。

    [Test]
    public async Task OwnedJson_Object_MySql_RoundTrip()
    {
        await using var db = await TestDb.MySqlAsync();
        await db.MigrateAsync();

        var entity = await db.InsertAsync(new JsonTestEntity
        {
            Name = "mysql-object",
            Data = "{}",
            Details = new JsonDetails { Key = "mysql-value", Count = 7 }
        });
        var found = await db.GetAsync<JsonTestEntity>(entity.Id);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Details.Key).IsEqualTo("mysql-value");
        await Assert.That(found.Details.Count).IsEqualTo(7);
    }

    [Test]
    public async Task OwnedJson_Object_PostgreSql_RoundTrip()
    {
        await using var db = await TestDb.PostgreSqlAsync();
        await db.MigrateAsync();

        var entity = await db.InsertAsync(new JsonTestEntity
        {
            Name = "pg-object",
            Data = "{}",
            Details = new JsonDetails { Key = "pg-value", Count = 9 }
        });
        var found = await db.GetAsync<JsonTestEntity>(entity.Id);

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.Details.Key).IsEqualTo("pg-value");
        await Assert.That(found.Details.Count).IsEqualTo(9);
    }

    [Test]
    public async Task CTE_MultiCondition_Works()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();
        await db.InsertAsync(new Product { Name = "C1", Price = 100m, Stock = 5 });
        await db.InsertAsync(new Product { Name = "C2", Price = 50m, Stock = 3 });
        var result = await db.From<Product>().With("top", $"SELECT * FROM products WHERE price > {60m}").ToListAsync();
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Name).IsEqualTo("C1");
    }
}

#region Test Entities
[Table("json_test")]
public partial class JsonTestEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
    [Column("data")] [OwnedJson] public string Data { get; set; } = "{}";
    [Column("details")] [OwnedJson(typeof(OwnedJsonSerializerContext))] public JsonDetails Details { get; set; } = new();
}

public sealed class JsonDetails
{
    public string Key { get; set; } = "";
    public int Count { get; set; }
}

[JsonSerializable(typeof(JsonDetails), TypeInfoPropertyName = "JsonDetailsInfo")]
internal sealed partial class OwnedJsonSerializerContext : JsonSerializerContext;
#endregion
