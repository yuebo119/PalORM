namespace AotProbe.Adapters.FrifloJsonFliox;

// Friflo 实体：纯字段类（无特性标注，EntitySet<TKey,TEntity> 注册时自动发现 schema）
public class OrderRow
{
    public long id;
    public string name = "";
    public double amount;
    public string category = "";
}

public class CatalogRow
{
    public long id;
    public string label = "";
}

public class CategoryCount
{
    public string category = "";
    public long count;
}
