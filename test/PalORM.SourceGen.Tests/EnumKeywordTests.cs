namespace PalORM.SourceGen.Tests;

/// <summary>2026-10-22 终审 P2-9：枚举成员名为 C# 关键字（如 @class）时，生成源的类型引用
/// 必须带 @，否则 CS1001；数据库字符串字面量仍用裸名。</summary>
public sealed class EnumKeywordTests
{
    [Test]
    public async Task EnumMemberKeyword_GeneratesCompilableAccessors()
    {
        const string source = """
            using PalORM;
            public enum Status { @class = 1, Ok = 2 }
            [Table("t")]
            public sealed class E
            {
                [Key] public long Id { get; set; }
                [Column("status", StoreAs = StoreAs.AsString)]
                public Status Status { get; set; }
            }
            """;
        var result = GeneratorTestHost.RunGenerator(source, "EnumKeywordConsumer");
        await Assert.That(GeneratorTestHost.FormatErrors(result.OutputCompilation)).IsEmpty();
        await Assert.That(result.GeneratedSources.Values.Any(
            static text => text.Contains("@class", System.StringComparison.Ordinal))).IsTrue();
    }
}
