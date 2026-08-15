using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PalORM.SourceGen.Tests;

/// <summary>Obsolete 预告诊断契约测试——PALORM900（T1 缺口补齐批次）。
/// [Obsolete(DiagnosticId="PALORM900")] 由编译器在引用点发射：触发侧断言诊断 ID 出现，
/// 不触发侧断言正常实体代码零 PALORM900。</summary>
public sealed class ObsoleteDiagnosticTests
{
    [Test]
    public async Task PALORM900_Fires_WhenIRowFactoryReferenced()
    {
        // 编译一个引用 IRowFactory<T> 的泛型约束——Obsolete 预告在引用点产生 PALORM900。
        string source = """
            public sealed class LegacyHolder<T>
                where T : global::PalORM.IRowFactory<T>
            {
            }
            """;

        CSharpCompilation compilation = GeneratorTestHost.CreateCompilation(source, "ObsoleteProbe");

        bool fires = compilation.GetDiagnostics()
            .Any(static diagnostic => diagnostic.Id == "PALORM900");

        await Assert.That(fires).IsTrue();
    }

    [Test]
    public async Task PALORM900_DoesNotFire_ForNormalEntities()
    {
        string source = """
            using PalORM;

            [Table("widgets")]
            public sealed class Widget
            {
                public long Id { get; set; }
                public string? Name { get; set; }
            }
            """;

        CSharpCompilation compilation = GeneratorTestHost.CreateCompilation(source, "ObsoleteProbeNegative");

        bool fires = compilation.GetDiagnostics()
            .Any(static diagnostic => diagnostic.Id == "PALORM900");

        await Assert.That(fires).IsFalse();
    }
}
