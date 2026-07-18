using PalORM.Testing;

namespace PalORM.Integration.Tests;

// 全类型回归实体：覆盖 PALORM016 白名单全部受支持类型（ITM-301 教训——
// 白名单类型必须有"生成→编译→物化"端到端验证，否则 TimeOnly/char 这类
// 生成物缺陷会潜伏到消费方编译期才暴露）。
[Table("all_types_entities")]
public partial class AllTypesEntity
{
    [Key] public long Id { get; set; }
    [Column("v_int")] public int VInt { get; set; }
    [Column("v_short")] public short VShort { get; set; }
    [Column("v_byte")] public byte VByte { get; set; }
    [Column("v_string")] [Required] public string VString { get; set; } = "";
    [Column("v_char")] public char VChar { get; set; }
    [Column("v_bool")] public bool VBool { get; set; }
    [Column("v_decimal")] public decimal VDecimal { get; set; }
    [Column("v_double")] public double VDouble { get; set; }
    [Column("v_float")] public float VFloat { get; set; }
    [Column("v_datetime")] public DateTime VDateTime { get; set; }
    [Column("v_guid")] public Guid VGuid { get; set; }
    [Column("v_dto")] public DateTimeOffset VDto { get; set; }
    [Column("v_dateonly")] public DateOnly VDateOnly { get; set; }
    [Column("v_timeonly")] public TimeOnly VTimeOnly { get; set; }
    [Column("v_nullable_int")] public int? VNullableInt { get; set; }
    [Column("v_nullable_timeonly")] public TimeOnly? VNullableTimeOnly { get; set; }
}

public class AllTypesEntityTests
{
    private static AllTypesEntity NewSample() => new()
    {
        VInt = 42,
        VShort = 7,
        VByte = 255,
        VString = "端到端",
        VChar = 'Z',
        VBool = true,
        VDecimal = 12.34m,
        VDouble = 3.14159,
        VFloat = 2.5f,
        VDateTime = new DateTime(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc),
        VGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        VDto = new DateTimeOffset(2026, 7, 18, 10, 30, 0, TimeSpan.FromHours(8)),
        VDateOnly = new DateOnly(2026, 7, 18),
        VTimeOnly = new TimeOnly(10, 30, 45),
        VNullableInt = null,
        VNullableTimeOnly = new TimeOnly(23, 59, 59),
    };

    [Test]
    public async Task AllWhitelistedTypes_InsertAndMaterialize_RoundTrip()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        var sample = NewSample();
        await db.InsertAsync(sample);

        var all = await db.GetAllAsync<AllTypesEntity>();
        await Assert.That(all.Count).IsEqualTo(1);
        var read = all[0];

        await Assert.That(read.VInt).IsEqualTo(42);
        await Assert.That(read.VShort).IsEqualTo((short)7);
        await Assert.That(read.VByte).IsEqualTo((byte)255);
        await Assert.That(read.VString).IsEqualTo("端到端");
        await Assert.That(read.VChar).IsEqualTo('Z');
        await Assert.That(read.VBool).IsTrue();
        await Assert.That(read.VDecimal).IsEqualTo(12.34m);
        await Assert.That(read.VDouble).IsEqualTo(3.14159);
        await Assert.That(read.VFloat).IsEqualTo(2.5f);
        await Assert.That(read.VDateTime).IsEqualTo(new DateTime(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc));
        await Assert.That(read.VGuid).IsEqualTo(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        await Assert.That(read.VDto.UtcDateTime).IsEqualTo(new DateTime(2026, 7, 18, 2, 30, 0, DateTimeKind.Utc));
        await Assert.That(read.VDateOnly).IsEqualTo(new DateOnly(2026, 7, 18));
        await Assert.That(read.VTimeOnly).IsEqualTo(new TimeOnly(10, 30, 45));
        await Assert.That(read.VNullableInt).IsNull();
        await Assert.That(read.VNullableTimeOnly).IsEqualTo(new TimeOnly(23, 59, 59));
    }

    [Test]
    public async Task NullableColumns_NullRoundTrip()
    {
        await using var db = await TestDb.SqliteAsync();
        await db.MigrateAsync();

        var sample = NewSample();
        sample.VNullableTimeOnly = null;
        await db.InsertAsync(sample);

        var read = (await db.GetAllAsync<AllTypesEntity>())[0];
        await Assert.That(read.VNullableInt).IsNull();
        await Assert.That(read.VNullableTimeOnly).IsNull();
    }
}
