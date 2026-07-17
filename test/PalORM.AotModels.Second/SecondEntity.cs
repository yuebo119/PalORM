namespace PalORM.AotModels.Second;

[Table("aot_second_model")]
public sealed partial class SecondEntity
{
    [Key] public long Id { get; set; }
    [Column("value")] public int Value { get; set; }
}
