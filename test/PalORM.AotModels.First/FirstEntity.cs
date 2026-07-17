namespace PalORM.AotModels.First;

[Table("aot_first_model")]
public sealed partial class FirstEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}
