namespace PalORM.SourceGen;

/// <summary>方言选择性发射（GEN-008，2026-09-23）：MSBuild 属性 <c>PalORMTargetDialects</c>
/// （逗号分隔：<c>postgresql</c> / <c>mysql</c> / <c>sqlite</c>）声明消费方实际使用的方言集合。
/// <para><b>未声明的方言不发射 SQL 载荷</b>——该方言的 <c>CommandSqlSet</c> 发射为 <c>default</c>，
/// 运行时取到即响亮失败（提示把该方言加进属性并重编译），不会静默产出空 SQL。</para>
/// <para><b>收益（实测）</b>：注册文件里方言 SQL 字面量占 42.9%（16,405 / 38,209 字节，4 实体语料），
/// 单方言应用可去掉其中约 2/3（≈2.7KB/实体）。</para>
/// <para><b>裁剪范围（ITM-896 收窄声明，r24）</b>：仅 <c>CommandSqlSet</c>（CRUD SQL）；
/// CreateTableSqlSet/CreateIndexSqlSet（DDL 载荷）恒全发射三方言——若未来接线 DDL 裁剪，
/// 须同步补 SqlSets DDL Get 的 GEN-008 式 null 防御（ITM-907 登记项）。</para>
/// <para>缺省 / 空 / 全不识别 = 三方言全发射（与既有行为逐位一致）。</para></summary>
internal readonly record struct DialectSelection(bool Sqlite, bool PostgreSql, bool MySql)
{
    internal static readonly DialectSelection All = new(true, true, true);

    internal static DialectSelection Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return All;
        bool sqlite = false;
        bool postgreSql = false;
        bool mySql = false;
        foreach (string raw in value!.Split(','))
        {
            string token = raw.Trim();
            if (token.Length == 0) continue;
            if (token.Equals("sqlite", System.StringComparison.OrdinalIgnoreCase)) sqlite = true;
            else if (token.Equals("postgresql", System.StringComparison.OrdinalIgnoreCase)
                || token.Equals("pg", System.StringComparison.OrdinalIgnoreCase)) postgreSql = true;
            else if (token.Equals("mysql", System.StringComparison.OrdinalIgnoreCase)) mySql = true;
        }
        // 全不识别 → 全发射：宁可多发射也不让消费方拿到空载荷（拼错属性的代价是体积，不是运行期故障）
        return sqlite || postgreSql || mySql ? new DialectSelection(sqlite, postgreSql, mySql) : All;
    }

    // ITM-896（r24）：删除 Includes(SqlGenerationDialect)——GEN-008 接线时只接了
    // CommandSqlSet（RegistryEmitter 内联三元），本方法自引入起全仓零调用（死方法）。
    // DDL 裁剪若立项，届时按需重建（连同 SqlSets 的 null 防御，见类级注释）。
}
