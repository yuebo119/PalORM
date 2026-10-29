using AotProbe.Adapters.NHibernateLib;
using NHibernate;
using NHibernate.Cfg;
using NHibernate.Dialect;
using NHibernate.Driver;
using NHibernate.Linq;
using NHibernate.Mapping.ByCode;
using NHibernate.Tool.hbm2ddl;
using PalORM.AotProbe;
using static AotProbe.Adapters.AdapterValues;

namespace AotProbe.Adapters;

// NHibernate 适配器（官方路径 = 默认形态；重反射重动态代理，预判 🟥）。5.6 + mapping-by-code +
// MicrosoftDataSqliteDriver + SQLiteDialect + SchemaExport——全部官方文档形态。
internal static class LibAdapter
{
    public static ProbeObservation Run(string probeId, string dbPath)
    {
        var probe = Enum.Parse<ProbeId>(probeId);
        return Run(probe, dbPath);
    }

    private static Configuration BuildConfig(string dbPath)
    {
        var cfg = new Configuration();
        cfg.DataBaseIntegration(db =>
        {
            db.ConnectionString = $"Data Source={dbPath}";
            db.Dialect<SQLiteDialect>();
            db.Driver<SQLite20Driver>(); // 5.6 官方 SQLite 配对（无 Microsoft.Data.Sqlite 驱动，程序集扫描定案）
        });
        var mapper = new ModelMapper();
        mapper.Class<OrderRow>(m =>
        {
            m.Table("ap_order");
            m.Id(x => x.Id, id => id.Generator(Generators.Native));
            m.Property(x => x.Name, p => p.Column("name"));
            m.Property(x => x.Amount, p => p.Column("amount"));
            m.Property(x => x.Category, p => p.Column("category"));
        });
        mapper.Class<CatalogRow>(m =>
        {
            m.Table("ap_catalog");
            m.Id(x => x.Id, id => id.Generator(Generators.Assigned));
            m.Property(x => x.Label, p => p.Column("label"));
        });
        cfg.AddMapping(mapper.CompileMappingFor(new[] { typeof(OrderRow), typeof(CatalogRow) }));
        return cfg;
    }

    private static ProbeObservation Run(ProbeId probe, string dbPath)
    {
        // 每探针全新库文件：非 Ddl 探针用 raw ADO 幂等建表（Ddl 探针走 SchemaExport 自证）
        if (probe != ProbeId.P0Ddl)
        {
            RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
            RawChannel.EnsureCatalogTableAsync(dbPath).GetAwaiter().GetResult();
        }
        Configuration cfg = BuildConfig(dbPath);
        using ISessionFactory factory = cfg.BuildSessionFactory();
        return probe switch
        {
            ProbeId.P0Ddl => Ddl(cfg, dbPath),
            ProbeId.P0InsertExplicitPk => InsertExplicitPk(factory, dbPath),
            ProbeId.P0InsertAutoPk => InsertAutoPk(factory, dbPath),
            ProbeId.P0GetByKey => GetByKey(factory, dbPath),
            ProbeId.P0UpdateDelete => UpdateDelete(factory),
            ProbeId.P0QueryAll => QueryAll(factory, dbPath),
            ProbeId.P0Aggregate => Aggregate(factory, dbPath),
            ProbeId.P0Transaction => Transaction(factory, dbPath),
            _ => throw new ArgumentOutOfRangeException(nameof(probe), probe, null),
        };
    }

    private static ProbeObservation Ddl(Configuration cfg, string dbPath)
    {
        new SchemaExport(cfg).Execute(false, true, false);
        long tables = RawChannel.TableCountAsync(dbPath).GetAwaiter().GetResult();
        return Ok(ProbeId.P0Ddl, F("ddl.table.count", tables));
    }

    private static ProbeObservation InsertExplicitPk(ISessionFactory factory, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertExplicitPk is A-group only");
#else
        using (ISession session = factory.OpenSession())
        using (ITransaction tx = session.BeginTransaction())
        {
            var row = new CatalogRow { Id = CatalogIdValue, Label = CatalogLabelValue };
            session.Save(row);
            tx.Commit();
        }
        CatalogRow? readBack;
        using (ISession session = factory.OpenSession())
        {
            readBack = session.Get<CatalogRow>(CatalogIdValue);
        }
        string raw = RawChannel.ScalarStringAsync(dbPath, "SELECT label FROM ap_catalog WHERE Id = @p0",
            ("@p0", CatalogIdValue)).GetAwaiter().GetResult();
        return Ok(ProbeId.P0InsertExplicitPk,
            F("insert.id", CatalogIdValue),
            F("orm.readback.label", readBack?.Label ?? ""),
            F("raw.stored.label", raw),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static ProbeObservation InsertAutoPk(ISessionFactory factory, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0InsertAutoPk is A-group only");
#else
        long id;
        using (ISession session = factory.OpenSession())
        using (ITransaction tx = session.BeginTransaction())
        {
            var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
            id = (long)session.Save(row);
            tx.Commit();
        }
        OrderRow? readBack;
        using (ISession session = factory.OpenSession())
        {
            readBack = session.Get<OrderRow>(id);
        }
        string raw = RawChannel.ScalarStringAsync(dbPath, "SELECT name FROM ap_order WHERE Id = @p0",
            ("@p0", (object)id)).GetAwaiter().GetResult();
        return Ok(ProbeId.P0InsertAutoPk,
            F("insert.id", id),
            F("orm.readback.name", readBack?.Name ?? ""),
            F("raw.stored.name", raw),
            F("orm.readback.amount", readBack is null ? "" : readBack.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            FT("orm.readback.amount.type", readBack?.Amount.GetType()),
            FT("materialized.type", readBack?.GetType()));
#endif
    }

    private static ProbeObservation GetByKey(ISessionFactory factory, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        const long keyId = 1;
        OrderRow? foundB;
        using (ISession session = factory.OpenSession())
        {
            foundB = session.Get<OrderRow>(keyId);
        }
        OrderRow? found = foundB;
#else
        long keyId;
        using (ISession session = factory.OpenSession())
        using (ITransaction tx = session.BeginTransaction())
        {
            var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
            keyId = (long)session.Save(row);
            tx.Commit();
        }
        OrderRow? found;
        using (ISession session = factory.OpenSession())
        {
            found = session.Get<OrderRow>(keyId);
        }
#endif
        return Ok(ProbeId.P0GetByKey,
            F("get.id", found?.Id ?? 0L),
            F("get.name", found?.Name ?? ""),
            F("get.category", found?.Category ?? ""),
            FT("materialized.type", found?.GetType()));
    }

    private static ProbeObservation UpdateDelete(ISessionFactory factory)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0UpdateDelete is A-group only");
#else
        int updated, deleted;
        string afterAmount;
        bool afterDeleteFound;
        using (ISession session = factory.OpenSession())
        {
            long id;
            using (ITransaction tx = session.BeginTransaction())
            {
                var row = new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 };
                id = (long)session.Save(row);
                tx.Commit();
            }
            using (ITransaction tx = session.BeginTransaction())
            {
                OrderRow? loaded = session.Get<OrderRow>(id);
                if (loaded is null)
                    throw new InvalidOperationException("reload failed before update");
                loaded.Amount = AmountUpdated;
                session.Update(loaded);
                tx.Commit();
                updated = 1;
            }
            session.Clear();
            using (ISession fresh = factory.OpenSession())
            {
                OrderRow? afterUpdate = fresh.Get<OrderRow>(id);
                afterAmount = afterUpdate?.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                using (ITransaction tx = fresh.BeginTransaction())
                {
                    OrderRow? victim = fresh.Get<OrderRow>(id);
                    if (victim is not null)
                    {
                        fresh.Delete(victim);
                        tx.Commit();
                        deleted = 1;
                    }
                    else
                    {
                        deleted = 0;
                    }
                }
                afterDeleteFound = fresh.Get<OrderRow>(id) is not null;
            }
        }
        return Ok(ProbeId.P0UpdateDelete,
            F("update.affected", updated),
            F("orm.afterupdate.amount", afterAmount),
            F("delete.affected", deleted),
            F("orm.afterdelete.found", afterDeleteFound));
#endif
    }

    private static ProbeObservation QueryAll(ISessionFactory factory, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2).GetAwaiter().GetResult();
#else
        using (ISession session = factory.OpenSession())
        using (ITransaction tx = session.BeginTransaction())
        {
            session.Save(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
            session.Save(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 });
            session.Save(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 });
            tx.Commit();
        }
#endif
        using (ISession session = factory.OpenSession())
        {
            List<OrderRow> rows = session.Query<OrderRow>().OrderBy(x => x.Id).ToList();
            return Ok(ProbeId.P0QueryAll,
                F("list.count", rows.Count),
                FT("item.type", rows.Count > 0 ? rows[0].GetType() : null),
                F("names.byid", string.Join("|", rows.Select(r => r.Name))));
        }
    }

    private static ProbeObservation Aggregate(ISessionFactory factory, string dbPath)
    {
#if AOTPROBE_B
        RawChannel.EnsureOrderTableAsync(dbPath).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name1, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name2, AmountText, Cat1).GetAwaiter().GetResult();
        RawChannel.SeedOrderAsync(dbPath, Name3, AmountText, Cat2).GetAwaiter().GetResult();
#else
        using (ISession session = factory.OpenSession())
        using (ITransaction tx = session.BeginTransaction())
        {
            session.Save(new OrderRow { Name = Name1, Amount = Amount, Category = Cat1 });
            session.Save(new OrderRow { Name = Name2, Amount = Amount, Category = Cat1 });
            session.Save(new OrderRow { Name = Name3, Amount = Amount, Category = Cat2 });
            tx.Commit();
        }
#endif
        using (ISession session = factory.OpenSession())
        {
            long total = session.Query<OrderRow>().LongCount();
            List<CategoryCount> grouped = session.Query<OrderRow>()
                .GroupBy(x => x.Category)
                .Select(g => new CategoryCount { Category = g.Key, Count = g.LongCount() })
                .ToList();
            return Ok(ProbeId.P0Aggregate,
                F("count.total", total),
                F($"group.{Cat1}", grouped.FirstOrDefault(g => g.Category == Cat1)?.Count ?? -1L),
                F($"group.{Cat2}", grouped.FirstOrDefault(g => g.Category == Cat2)?.Count ?? -1L),
                FT("group.row.type", grouped.Count > 0 ? grouped[0].GetType() : null));
        }
    }

    private static ProbeObservation Transaction(ISessionFactory factory, string dbPath)
    {
#if AOTPROBE_B
        throw new NotSupportedException("P0Transaction is A-group only");
#else
        using (ISession session = factory.OpenSession())
        using (ITransaction tx = session.BeginTransaction())
        {
            session.Save(new OrderRow { Name = TxKept, Amount = Amount, Category = Cat1 });
            tx.Commit();
        }
        long kept = RawChannel.CountAsync(dbPath, "ap_order", "name", TxKept).GetAwaiter().GetResult();

        using (ISession session = factory.OpenSession())
        using (ITransaction tx = session.BeginTransaction())
        {
            session.Save(new OrderRow { Name = TxDropped, Amount = Amount, Category = Cat2 });
            tx.Rollback();
        }
        long dropped = RawChannel.CountAsync(dbPath, "ap_order", "name", TxDropped).GetAwaiter().GetResult();

        return Ok(ProbeId.P0Transaction,
            F("commit.visible", kept == 1),
            F("rollback.visible", dropped > 0));
#endif
    }
}
