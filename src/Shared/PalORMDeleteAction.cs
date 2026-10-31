namespace PalORM;

/// <summary>外键删除行为（Core 与 SourceGen 共享的单一真源）。
/// <para>SourceGen 是 netstandard2.0，不能引用 Core；本文件通过 Compile Link 同时编译进
/// PalORM.Core 与 PalORM.SourceGen。SourceGen 侧使用不同的内部类型名，避免测试程序集
/// 同时引用两个程序集时出现 CS0433 类型歧义。</para></summary>
#if PALORM_SOURCEGEN
internal enum SourceGenDeleteAction
#else
public enum DeleteAction
#endif
{
    /// <summary>不做处理（NO ACTION）。</summary>
    NoAction = 0,

    /// <summary>级联删除引用行（CASCADE）。</summary>
    Cascade = 1,

    /// <summary>将引用列置为 NULL（SET NULL）。</summary>
    SetNull = 2,

    /// <summary>存在引用时禁止删除（RESTRICT）。</summary>
    Restrict = 3
}
