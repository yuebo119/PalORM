using AotProbe.Adapters.FrifloJsonFliox;
using Friflo.Json.Fliox.Hub.Client;   // FlioxClient, EntitySet
using Friflo.Json.Fliox.Hub.Host;      // FlioxHub, EntityDatabase
using Friflo.Json.Fliox.Hub.SQLite;    // SQLiteDatabase
using PalORM.AotProbe;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// Friflo.Json.Fliox 适配器（⑤类文档/嵌入式，声明 AOT 友好）。
// API 形态：FlioxClient 子类持有 EntitySet<TKey,TEntity>，CRUD 后 Sync() 持久化。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return RunAsync(probe, dbPath).GetAwaiter().GetResult();
    }

    private sealed class ProbeClient : FlioxClient
    {
        public readonly EntitySet<long, OrderRow> orders;
        public readonly EntitySet<long, CatalogRow> catalog;

        public ProbeClient(FlioxHub hub) : base(hub)
        {
            orders = new EntitySet<long, OrderRow>(this, null);
            catalog = new EntitySet<long, CatalogRow>(this, null);
        }
    }

    private static (FlioxHub hub, ProbeClient client) CreateClient(string dbPath)
    {
        var database = new EntityDatabase("probe", new SQLiteDatabase(dbPath));
        var hub = new FlioxHub(database, "probe");
        return (hub, new ProbeClient(hub));
    }

    private static async Task<ProbeObservation> RunAsync(ProbeId probe, string dbPath)
    {
        return probe switch
        {
            ProbeId.P0GetByKey => await GetByKeyAsync(dbPath),
            ProbeId.P0QueryAll => await QueryAllAsync(dbPath),
            _ => throw new NotSupportedException($"{probe} 暂未覆盖（Friflo API 发现阶段）"),
        };
    }

    private static async Task<ProbeObservation> GetByKeyAsync(string dbPath)
    {
        var (hub, client) = CreateClient(dbPath);
        await using (hub)
        {
            client.orders.Create(new OrderRow { id = 1, name = Name1, amount = 12345.67, category = Cat1 });
            _ = await client.TrySyncAsync();

            OrderRow? found = client.orders.Get(1);
            return Ok(ProbeId.P0GetByKey,
                F("get.id", found?.id ?? 0L),
                F("get.name", found?.name ?? ""),
                F("get.category", found?.category ?? ""),
                FT("materialized.type", found?.GetType()));
        }
    }

    private static async Task<ProbeObservation> QueryAllAsync(string dbPath)
    {
        var (hub, client) = CreateClient(dbPath);
        await using (hub)
        {
            client.orders.Create(new OrderRow { id = 1, name = Name1, amount = 12345.67, category = Cat1 });
            client.orders.Create(new OrderRow { id = 2, name = Name2, amount = 12345.67, category = Cat1 });
            client.orders.Create(new OrderRow { id = 3, name = Name3, amount = 12345.67, category = Cat2 });
            _ = await client.TrySyncAsync();

            var ranges = client.orders.GetRange(new[] { 1L, 2L, 3L });
            List<OrderRow> rows = ranges?.Entities?.Select(e => e.Value).ToList() ?? [];
            return Ok(ProbeId.P0QueryAll,
                F("list.count", rows.Count),
                FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
                F("names.byid", string.Join("|", rows.Select(r => r.name))));
        }
    }
}
