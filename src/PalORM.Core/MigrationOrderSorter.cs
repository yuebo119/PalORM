namespace PalORM;

/// <summary>P2-12：跨程序集建表序的运行时兜底——按各实体 CREATE TABLE 文本中的
/// <c>REFERENCES</c> 边做稳定拓扑排序。
/// <para><b>为什么需要</b>：生成器的 <see cref="PalORM_Runtime.TableMigrationOrder"/> 只在
/// 单程序集内拓扑（PALORM003 的扫描面同界）。多程序集注册时片段按注册序拼接，后加载程序集
/// 的引用方表可能排在被引用表之前，空库首迁 <c>CREATE TABLE</c> 直接因前向 FK 失败。
/// 这里只信自己生成的 DDL（三方言均为 <c>REFERENCES &lt;quoted-table&gt; (&lt;col&gt;)</c>），
/// 不新增注册载荷，旧片段同样受益。</para>
/// <para><b>边界</b>：① 引用目标不在注册表（外部表/未加载程序集）不构成边——无法为它排序；
/// ② 环（A→B→A）按原序放行，与生成器对环的处理同口径（空库上的循环 FK 本就无法顺序创建）；
/// ③ 解析面是 DDL 文本，若某列 DEFAULT 的表达式中恰好含 " REFERENCES " 且其后名称与
/// 某注册表同名，会多出一条边——排序只会更保守，不改变正确性。</para></summary>
internal static class MigrationOrderSorter
{
    internal static List<(Type Type, string Ddl, IReadOnlyList<string> IndexDdls)> SortByReferences(
        List<(Type Type, string Ddl, IReadOnlyList<string> IndexDdls)> entries,
        IReadOnlyDictionary<Type, string> tableNames)
    {
        int count = entries.Count;
        if (count < 2) return entries;

        HashSet<int>[] dependencies = BuildDependencyMap(entries, tableNames);

        var emitted = new bool[count];
        var ordered = new List<(Type, string, IReadOnlyList<string>)>(count);
        while (ordered.Count < count)
        {
            int pick = PickReady(emitted, dependencies);
            // 环：按输入序放行剩余项（稳定且必然终止）
            if (pick < 0) pick = Array.IndexOf(emitted, false);
            emitted[pick] = true;
            ordered.Add(entries[pick]);
        }
        return ordered;
    }

    private static HashSet<int>[] BuildDependencyMap(
        List<(Type Type, string Ddl, IReadOnlyList<string> IndexDdls)> entries,
        IReadOnlyDictionary<Type, string> tableNames)
    {
        int count = entries.Count;
        var indexByTable = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            if (tableNames.TryGetValue(entries[i].Type, out string? table))
                indexByTable[table] = i;
        }
        var dependencies = new HashSet<int>[count];
        for (int i = 0; i < count; i++)
        {
            dependencies[i] = [];
            foreach (string reference in EnumerateReferences(entries[i].Ddl))
            {
                if (indexByTable.TryGetValue(reference, out int target) && target != i)
                    dependencies[i].Add(target);
            }
        }
        return dependencies;
    }

    private static int PickReady(bool[] emitted, HashSet<int>[] dependencies)
    {
        for (int i = 0; i < emitted.Length; i++)
        {
            if (emitted[i] || !IsReady(i, emitted, dependencies)) continue;
            return i;
        }
        return -1;
    }

    private static bool IsReady(int index, bool[] emitted, HashSet<int>[] dependencies)
    {
        foreach (int dependency in dependencies[index])
        {
            if (!emitted[dependency]) return false;
        }
        return true;
    }

    /// <summary>提取 DDL 中全部 <c>REFERENCES</c> 目标表名（去引号）。标识符由
    /// <c>SqlGeneration.QuoteIdentifier</c> 产出：SQLite/PG 双引号、MySQL 反引号；
    /// 未加引号的形态按空白截断兜底。</summary>
    private static IEnumerable<string> EnumerateReferences(string ddl)
    {
        const string marker = " REFERENCES ";
        int index = 0;
        while ((index = ddl.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            index += marker.Length;
            if (index >= ddl.Length) yield break;
            char quote = ddl[index];
            if (quote is '"' or '`')
            {
                int end = ddl.IndexOf(quote, index + 1);
                if (end < 0) yield break;
                yield return ddl[(index + 1)..end];
                index = end + 1;
            }
            else
            {
                int end = ddl.IndexOf(' ', index);
                if (end < 0) end = ddl.Length;
                yield return ddl[index..end];
                index = end;
            }
        }
    }
}
