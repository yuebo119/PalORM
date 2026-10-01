namespace PalORM;

/// <summary>二进制批量写入的定型行槽（PG Binary COPY 专用抽象）。
/// <para><b>为什么存在</b>：COPY 行写入原先经生成器 <c>BindInsertValues</c> 落到
/// <see cref="System.Data.Common.DbParameter"/> 池再由 Provider 读出——每个值类型单元格
/// 装箱一次（S1Row 形态实测 104 B/行）。本接口让生成代码按列的编译期 CLR 类型直写
/// （Provider 实现转调 <c>NpgsqlBinaryImporter.Write&lt;T&gt;</c>），装箱归零；
/// 线类型仍取自运行时按列采样的 NpgsqlDbType（PG-4 单一类型真源不变）。</para>
/// <para><b>方言中立</b>：接口在 Core、生成物只引用本接口——实体程序集不感知
/// Npgsql 类型，Provider 各自独立实现（ITM 架构约束：Provider 不跨引用）。</para>
/// <para><b>AOT 契约</b>：全部实现为编译期生成的直调与 Provider 的泛型直调，
/// 零反射、零 Expression.Compile、零运行时代码生成。</para></summary>
public interface IBinaryRowSink
{
    /// <summary>当前行该列写 NULL（生成代码对可空列的 null 分支调用）。</summary>
    void WriteNull(int ordinal);

    /// <summary>写 <see cref="long"/> 列（对应 DbType.Int64 形态）。</summary>
    void WriteInt64(int ordinal, long value);

    /// <summary>写 <see cref="int"/> 列（对应 DbType.Int32 形态）。</summary>
    void WriteInt32(int ordinal, int value);

    /// <summary>写 <see cref="short"/> 列（对应 DbType.Int16 形态）。</summary>
    void WriteInt16(int ordinal, short value);

    /// <summary>写 <see cref="decimal"/> 列（对应 DbType.Decimal 形态）。</summary>
    void WriteDecimal(int ordinal, decimal value);

    /// <summary>写 <see cref="double"/> 列（对应 DbType.Double 形态）。</summary>
    void WriteDouble(int ordinal, double value);

    /// <summary>写 <see cref="float"/> 列（对应 DbType.Single 形态）。</summary>
    void WriteSingle(int ordinal, float value);

    /// <summary>写 <see cref="bool"/> 列（对应 DbType.Boolean 形态）。</summary>
    void WriteBoolean(int ordinal, bool value);

    /// <summary>写 <see cref="Guid"/> 列（对应 DbType.Guid 形态）。</summary>
    void WriteGuid(int ordinal, Guid value);

    /// <summary>写 <see cref="DateTime"/> 列（对应 DbType.DateTime 形态）。</summary>
    void WriteDateTime(int ordinal, DateTime value);

    /// <summary>写 <see cref="DateTimeOffset"/> 列（对应 DbType.DateTimeOffset 形态）。</summary>
    void WriteDateTimeOffset(int ordinal, DateTimeOffset value);

    /// <summary>写字符串列（对应 DbType.String 形态）；运行时 null 值按 NULL 写入。</summary>
    void WriteString(int ordinal, string? value);

    /// <summary>写二进制列（对应 DbType.Binary 形态）；运行时 null 值按 NULL 写入。</summary>
    void WriteBytes(int ordinal, byte[]? value);
}
