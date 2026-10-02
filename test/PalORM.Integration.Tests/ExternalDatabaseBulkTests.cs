using PalORM.MySql;
using PalORM.PostgreSql;
using PalORM.Testing;

namespace PalORM.Integration.Tests;

// 三个测试共用 ext_bulk_entities 表（DROP/CREATE），必须串行
[NotInParallel("ExtBulkTable")]
public sealed class ExternalDatabaseBulkTests
{
    private static DbOptions PgOpts => new()
    {
        ConnectionString = TestEnvironment.ResolvePostgreSqlConnectionString()
    };

    private static DbOptions MySqlOpts => new()
    {
        ConnectionString = TestEnvironment.ResolveMySqlConnectionString()
    };

    private static ExtBulkEntity[] SampleRows() =>
    [
        new()
        {
            Code = "A1", Note = "note-a", Amount = 12.345678m,
            CreatedAt = new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc), OptionalCount = 7,
            Payload = [0x00, 0x01, 0xFF, 0x00]
        },
        // 可空列全 null 行——PG COPY 的 DBNull→NpgsqlDbType 推断疑点（ITM-318）
        new()
        {
            Code = "B2", Note = null, Amount = 0.000001m,
            CreatedAt = new DateTime(2026, 7, 18, 11, 30, 0, DateTimeKind.Utc), OptionalCount = null,
            Payload = null
        },
    ];

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_MigrateAndBinaryCopy_NullableAndUtcDateTime_RoundTrip()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        await db.MigrateAsync();
        try
        {
            long inserted = await db.BulkInsertAsync(SampleRows());
            await Assert.That(inserted).IsEqualTo(2);

            var rows = (await db.GetAllAsync<ExtBulkEntity>()).OrderBy(r => r.Code).ToList();
            await Assert.That(rows.Count).IsEqualTo(2);
            await Assert.That(rows[0].Note).IsEqualTo("note-a");
            await Assert.That(rows[0].Amount).IsEqualTo(12.345678m);
            await Assert.That(rows[0].CreatedAt).IsEqualTo(new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc));
            await Assert.That(rows[1].Note).IsNull();
            await Assert.That(rows[1].OptionalCount).IsNull();
            await Assert.That(rows[1].Amount).IsEqualTo(0.000001m);
            // byte[] 列经 Binary COPY 往返（含 0x00 字节——TEXT 兜底会在此静默变形）
            await Assert.That(rows[0].Payload!.SequenceEqual((byte[])[0x00, 0x01, 0xFF, 0x00])).IsTrue();
            await Assert.That(rows[1].Payload).IsNull();
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        }
    }

    /// <summary>可空列「首行为 null、后续行非 null」的 COPY 参数复用回归。
    /// <para><b>为什么单列一条</b>：v5.6 把 COPY 的参数对象改为每批建一次、逐行只写 Value，
    /// 参数类型由<b>首个</b>绑定行的值推断。现有 <c>PG_MigrateAndBinaryCopy_...</c> 的行序是
    /// 「先全非空、后全 null」，覆盖不到反向顺序——若首行可空列为 null、后续行有值，
    /// 类型推断可能停在首行的形态（ITM-318/ITM-527 登记的 DBNull→NpgsqlDbType 疑点族）。
    /// 本用例把顺序倒过来，是该风险的唯一可证伪入口。</para></summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BinaryCopy_NullFirstThenValue_KeepsTypeInference()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        await db.MigrateAsync();
        try
        {
            // 顺序刻意反转：全 null 行在前，有值行在后
            ExtBulkEntity[] rows =
            [
                new()
                {
                    Code = "NULLS", Note = null, Amount = 0m,
                    CreatedAt = new DateTime(2026, 7, 18, 9, 0, 0, DateTimeKind.Utc),
                    OptionalCount = null, Payload = null
                },
                new()
                {
                    Code = "VALUED", Note = "note-v", Amount = 12.345678m,
                    CreatedAt = new DateTime(2026, 7, 18, 10, 0, 0, DateTimeKind.Utc),
                    OptionalCount = 7, Payload = [0x00, 0x01, 0xFF, 0x00]
                },
            ];

            long inserted = await db.BulkInsertAsync(rows);
            await Assert.That(inserted).IsEqualTo(2);

            List<ExtBulkEntity> read = [.. (await db.GetAllAsync<ExtBulkEntity>()).OrderBy(r => r.Code)];
            await Assert.That(read.Count).IsEqualTo(2);
            // 前一行（首行、全 null）必须原样还原为 null
            await Assert.That(read[0].Note).IsNull();
            await Assert.That(read[0].OptionalCount).IsNull();
            await Assert.That(read[0].Payload).IsNull();
            // 后一行（非首行、有值）必须完整还原——参数类型若停在首行的 null 形态，此处退化
            await Assert.That(read[1].Note).IsEqualTo("note-v");
            await Assert.That(read[1].OptionalCount).IsEqualTo(7);
            await Assert.That(read[1].Amount).IsEqualTo(12.345678m);
            await Assert.That(read[1].Payload.SequenceEqual((byte[])[0x00, 0x01, 0xFF, 0x00])).IsTrue();
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        }
    }

    /// <summary>MySQL 批量插入的**值保真**用例——覆盖 <see cref="AllTypesEntity"/> 的全部白名单类型。
    /// <para><b>为什么需要它</b>：v5.6 把 BulkCopy 的取值载体从 DataTable 换成
    /// <c>EntityDataReader</c>，值统一经 <c>GetValue</c> 以 <c>object</c> 交给驱动按运行时类型
    /// 格式化为文本。DataTable 路径按列声明 <c>typeof(object)</c> 时的行为与读取器一致，
    /// 但这是一条**新代码路径**：bool/Guid/DateTimeOffset/DateOnly/TimeOnly/float 等类型
    /// 一旦被驱动按错误格式写出，就是静默的数据损坏（列值看起来"有值"）。
    /// 既有 BulkCopy 用例只覆盖 string/decimal/DateTime/byte[]/可空。</para>
    /// <para><b>覆盖率自述</b>：服务端 <c>local_infile=OFF</c> 时 provider 会静默回退多值 INSERT，
    /// 本用例就不经过读取器（值往返仍被验证）。读取器自身的契约由 Core.Tests 的
    /// <c>EntityDataReaderTests</c> 确定性覆盖，不依赖服务端能力。</para></summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_BulkCopy_AllWhitelistedTypes_RoundTripPreservesValues()
    {
        await using var db = await DataSession<MySqlProvider>.CreateAsync(MySqlOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS all_types_entities");
        await db.MigrateAsync();

        var sample = new AllTypesEntity
        {
            VInt = 42,
            VShort = 7,
            VByte = 255,
            VString = "端到端",
            VChar = 'Z',
            VBool = true,
            VDecimal = 12.34m,
            VDouble = 3.14159,
            VFloat = 2.5f,
            VDateTime = new DateTime(2026, 7, 18, 10, 30, 0, DateTimeKind.Utc),
            VGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            VDto = new DateTimeOffset(2026, 7, 18, 10, 30, 0, TimeSpan.FromHours(8)),
            VDateOnly = new DateOnly(2026, 7, 18),
            VTimeOnly = new TimeOnly(10, 30, 45),
            VBytes = [0x00, 0x01, 0xFF, 0x00, 0x41],
            VNullableInt = null,
            VNullableTimeOnly = new TimeOnly(23, 59, 59),
            VNullableBytes = [9, 8, 7],
        };

        long inserted = await db.BulkInsertAsync([sample]);

        await Assert.That(inserted).IsEqualTo(1);
        var read = (await db.GetAllAsync<AllTypesEntity>()).Single();
        await Assert.That(read.Id).IsGreaterThan(0); // 自增主键由 MySQL 生成（读取器返回 DBNull）
        await Assert.That(read.VInt).IsEqualTo(sample.VInt);
        await Assert.That(read.VShort).IsEqualTo(sample.VShort);
        await Assert.That(read.VByte).IsEqualTo(sample.VByte);
        await Assert.That(read.VString).IsEqualTo(sample.VString);
        await Assert.That(read.VChar).IsEqualTo(sample.VChar);
        await Assert.That(read.VBool).IsEqualTo(sample.VBool);
        await Assert.That(read.VDecimal).IsEqualTo(sample.VDecimal);
        await Assert.That(read.VDouble).IsEqualTo(sample.VDouble);
        await Assert.That(read.VFloat).IsEqualTo(sample.VFloat);
        await Assert.That(read.VDateTime).IsEqualTo(sample.VDateTime);
        await Assert.That(read.VGuid).IsEqualTo(sample.VGuid);
        await Assert.That(read.VDto.UtcDateTime).IsEqualTo(sample.VDto.UtcDateTime);
        await Assert.That(read.VDateOnly).IsEqualTo(sample.VDateOnly);
        await Assert.That(read.VTimeOnly).IsEqualTo(sample.VTimeOnly);
        await Assert.That(read.VBytes.SequenceEqual(sample.VBytes)).IsTrue();
        await Assert.That(read.VNullableInt).IsNull();
        await Assert.That(read.VNullableTimeOnly).IsEqualTo(sample.VNullableTimeOnly);
        await Assert.That(read.VNullableBytes.SequenceEqual(sample.VNullableBytes)).IsTrue();
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task MySql_MigrateWithUniqueIndexOnString_AndDecimalPrecision_RoundTrip()
    {
        // r19/T-P3-20 观察登记：本测试是一个真库往返链（迁移→幂等→decimal→唯一冲突），
        // 三检查共享同一建表成本——真库不可本地验证时保持单测试形态，拆分留待 CI 环境。
        // ITM-201 真库验证：被索引 string 列 VARCHAR(255)，首次迁移不得报 1170；
        // ITM-303 真库验证：DECIMAL(18,6) 下小数不被截断为整数
        await using var db = await DataSession<MySqlProvider>.CreateAsync(MySqlOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        await db.MigrateAsync();
        try
        {
            // 迁移幂等：二次执行经 1061 兜底不抛
            await db.MigrateAsync();

            long inserted = await db.BulkInsertAsync(SampleRows());
            await Assert.That(inserted).IsEqualTo(2);

            var rows = (await db.GetAllAsync<ExtBulkEntity>()).OrderBy(r => r.Code).ToList();
            await Assert.That(rows[0].Amount).IsEqualTo(12.345678m);
            await Assert.That(rows[1].Amount).IsEqualTo(0.000001m);
            await Assert.That(rows[1].Note).IsNull();
            await Assert.That(rows[1].OptionalCount).IsNull();
            // byte[] 列经 BulkCopy/多值 INSERT 回退往返（local_infile=OFF 时走参数化回退）
            await Assert.That(rows[0].Payload!.SequenceEqual((byte[])[0x00, 0x01, 0xFF, 0x00])).IsTrue();
            await Assert.That(rows[1].Payload).IsNull();

            // 唯一索引真实生效 + IsUniqueViolation 统一判定（ITM-314 真库验证）
            try
            {
                await db.InsertAsync(new ExtBulkEntity
                {
                    Code = "A1", Amount = 1m,
                    CreatedAt = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc)
                });
                throw new InvalidOperationException("重复 code 应触发唯一约束冲突");
            }
            catch (Exception ex)
            {
                await Assert.That(MySqlProvider.IsUniqueViolation(ex)).IsTrue();
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        }
    }

    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_UniqueViolation_IsUniformlyDetected()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        await db.MigrateAsync();
        try
        {
            await db.InsertAsync(new ExtBulkEntity
            {
                Code = "DUP", Amount = 1m,
                CreatedAt = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc)
            });
            try
            {
                await db.InsertAsync(new ExtBulkEntity
                {
                    Code = "DUP", Amount = 2m,
                    CreatedAt = new DateTime(2026, 7, 18, 13, 0, 0, DateTimeKind.Utc)
                });
                throw new InvalidOperationException("重复 code 应触发唯一约束冲突");
            }
            catch (Exception ex)
            {
                await Assert.That(PostgreSqlProvider.IsUniqueViolation(ex)).IsTrue();
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_bulk_entities CASCADE");
        }
    }

    /// <summary>UNNEST-1（2026-10-02）：数组形态（<c>pk = ANY(@ids)</c>）的真库验证。
    /// <para>覆盖 hermetic 夹具无法证明的那一半：真实的 <c>NpgsqlDbType.Array | Bigint</c> 绑定
    /// 被服务端接受，且多批（&gt; 5000 键）逐批正确。语句形状（<c>= ANY</c> + 单参数）由 Core 侧的
    /// <c>BulkDeleteArrayFormTests</c> 锁定——本用例的价值是"驱动与真实 PG 接受这个绑定"，
    /// 而批量路径不通知拦截器（实测记录 0 条），故形状断言不回在此重复。</para>
    /// <para>5001 键的用意：批大小 = SqlLimits.MaxRowsPerBatch（5000），末批只剩 1 个元素，
    /// 同时覆盖"count 取实际批长度而非批大小"的边界。</para></summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BulkDelete_ArrayForm_MultiBatch_DeletesEveryKey()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_array_delete CASCADE");
        await db.ExecuteAsync(
            $"CREATE TABLE ext_array_delete (id BIGINT PRIMARY KEY, name TEXT NOT NULL)");
        try
        {
            const int total = 5_001;
            var rows = new List<ExtArrayDeleteEntity>(total);
            for (long i = 1; i <= total; i++)
                rows.Add(new ExtArrayDeleteEntity { Id = i, Name = $"n{i}" });
            // Binary COPY 无参数上限，整段一次写入
            await db.BulkInsertAsync(rows, batchSize: total);

            long deleted = await db.BulkDeleteAsync<ExtArrayDeleteEntity>(
                [.. rows.Select(static r => (object)r.Id)]);

            await Assert.That(deleted).IsEqualTo(total);
            await Assert.That(await db.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM ext_array_delete")).IsEqualTo(0L);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_array_delete CASCADE");
        }
    }

    /// <summary>数组形态的 Guid 主键（映射 <c>NpgsqlDbType.Array | Uuid</c>）——覆盖
    /// <c>ArrayElementDbType</c> 的非整数分支（GUID/string 类主键在 <c>SqliteParameter</c>
    /// 夹具上无法证伪类型映射错误）。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BulkDelete_ArrayForm_GuidPrimaryKey_DeletesEveryKey()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_array_delete_guid CASCADE");
        await db.ExecuteAsync(
            $"CREATE TABLE ext_array_delete_guid (id UUID PRIMARY KEY, name TEXT NOT NULL)");
        try
        {
            var keep = Guid.NewGuid();
            var remove = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var rows = new List<ExtArrayGuidEntity>
            {
                new() { Id = keep, Name = "keep" }
            };
            foreach (Guid id in remove) rows.Add(new ExtArrayGuidEntity { Id = id, Name = "drop" });
            await db.BulkInsertAsync(rows);
            await Assert.That(await db.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM ext_array_delete_guid")).IsEqualTo(4L);

            // keep 留在表里：只删 remove 的三个，验证数组绑定不误伤相邻行
            long deleted = await db.BulkDeleteAsync<ExtArrayGuidEntity>(
                [.. remove.Select(static id => (object)id)]);

            await Assert.That(deleted).IsEqualTo(3L);
            await Assert.That(await db.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM ext_array_delete_guid WHERE id = {keep}")).IsEqualTo(1L);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_array_delete_guid CASCADE");
        }
    }

    /// <summary>UNNEST 阶段 B（2026-10-02）：批量 UPDATE 数组形态的真库验证——
    /// <c>UPDATE … FROM UNNEST(@u0,…)</c> 被 Npgsql 与真实 PG 接受，2000 行逐位正确
    ///（含可空列混合 null）。语句形状由 Core 侧 BulkUpdateArrayFormTests 锁定
    ///（批量路径不通知拦截器，真库侧无法采形）。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BulkUpdate_ArrayForm_UpdatesEveryRow()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_array_update CASCADE");
        await db.ExecuteAsync(
            $"CREATE TABLE ext_array_update (id BIGINT PRIMARY KEY, name TEXT NOT NULL, qty BIGINT NOT NULL, marker BIGINT NULL)");
        try
        {
            const int total = 2_000;
            var rows = new List<ExtArrayUpdateEntity>(total);
            for (long i = 1; i <= total; i++)
                rows.Add(new ExtArrayUpdateEntity
                {
                    Id = i, Name = $"n{i}", Qty = i, Marker = i % 3 == 0 ? null : i
                });
            await db.BulkInsertAsync(rows, batchSize: total);

            foreach (ExtArrayUpdateEntity row in rows)
            {
                row.Name = $"u{row.Id}";
                row.Qty = row.Id * 7;
                row.Marker = row.Id % 4 == 0 ? null : row.Id * 11;
            }
            long affected = await db.BulkUpdateAsync(rows);

            await Assert.That(affected).IsEqualTo(total);
            await Assert.That(await db.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM ext_array_update")).IsEqualTo(total);
            // 逐位校验：值列与可空列（混合 null）都按位置对应
            await Assert.That(await db.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM ext_array_update WHERE \"name\" = 'u' || \"id\"::text AND \"qty\" = \"id\" * 7 AND ((\"marker\" IS NULL AND \"id\" % 4 = 0) OR (\"marker\" = \"id\" * 11 AND \"id\" % 4 <> 0))"
            )).IsEqualTo(total);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_array_update CASCADE");
        }
    }

    /// <summary>UNNEST 阶段 B：BulkMerge 数组形态的真库验证——<c>INSERT … SELECT * FROM
    /// UNNEST(…) ON CONFLICT</c> 的插入与冲突更新两分支。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
    public async Task PG_BulkMerge_ArrayForm_InsertThenConflictUpdate()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_array_merge CASCADE");
        await db.ExecuteAsync(
            $"CREATE TABLE ext_array_merge (id BIGINT PRIMARY KEY, name TEXT NOT NULL, qty BIGINT NOT NULL, marker BIGINT NULL)");
        try
        {
            const int total = 2_000;
            var rows = new List<ExtArrayMergeEntity>(total);
            for (long i = 1; i <= total; i++)
                rows.Add(new ExtArrayMergeEntity
                {
                    Id = i, Name = $"first{i}", Qty = i, Marker = i % 5 == 0 ? null : i
                });
            long inserted = await db.BulkMergeAsync(rows);

            foreach (ExtArrayMergeEntity row in rows)
            {
                row.Name = $"second{row.Id}";
                row.Qty = row.Id * 3;
                row.Marker = row.Id % 2 == 0 ? null : row.Id * 9;
            }
            long updated = await db.BulkMergeAsync(rows);

            await Assert.That(inserted).IsEqualTo(total);
            await Assert.That(updated).IsEqualTo(total);
            await Assert.That(await db.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM ext_array_merge")).IsEqualTo(total);
            await Assert.That(await db.ScalarAsync<long>(
                $"SELECT COUNT(*) FROM ext_array_merge WHERE \"name\" = 'second' || \"id\"::text AND \"qty\" = \"id\" * 3 AND ((\"marker\" IS NULL AND \"id\" % 2 = 0) OR (\"marker\" = \"id\" * 9 AND \"id\" % 2 <> 0))"
            )).IsEqualTo(total);
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_array_merge CASCADE");
        }
    }

    /// <summary>R-UNNESTB 回归（2026-10-02）：跨晋升阈值的变键 <c>GetAsync</c>。
    /// <para><b>缺陷史</b>：PG 连接串的 auto-prepare 调优（v5.0 阶段 3.1 默认开启）下，
    /// GetByKey 复用槽的「Clear 参数集合 + Add 新参数实例」让驱动沿用 prepare 时的绑定值——
    /// 晋升后所有变键读取都返回晋升那一次的行（探针 mergearray 变体 C 实测；SQLite 无
    /// auto-prepare，Core 套件抓不到）。修复 = 参数集合持久持有 + 就地写 Value（键换算经
    /// 从不执行的探针命令走同一生成绑定器）。</para>
    /// <para>12 次读取 &gt; 晋升阈值 3：覆盖新建路径（前 2 次）、晋升（第 3 次）、
    /// 复用就地写（第 4 次起），且键值每次变化。</para></summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
#pragma warning disable PALORM005 // 循环内单行读正是被测行为（跨晋升阈值的变键复用）
    public async Task PG_GetAsync_VaryingKeys_AcrossPromotion_ReturnsMatchingRows()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_getbykey_reuse CASCADE");
        await db.ExecuteAsync(
            $"CREATE TABLE ext_getbykey_reuse (id BIGINT PRIMARY KEY, name TEXT NOT NULL)");
        try
        {
            const int total = 10;
            var rows = new List<ExtGetByKeyEntity>(total);
            for (long i = 1; i <= total; i++)
                rows.Add(new ExtGetByKeyEntity { Id = i, Name = $"n{i}" });
            await db.BulkInsertAsync(rows, batchSize: total);

            for (int round = 0; round < 12; round++)
            {
                long id = (round % total) + 1;
                ExtGetByKeyEntity? found = await db.GetAsync<ExtGetByKeyEntity>(id);
                await Assert.That(found).IsNotNull();
                await Assert.That(found!.Id).IsEqualTo(id);
                await Assert.That(found.Name).IsEqualTo($"n{id}");
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_getbykey_reuse CASCADE");
        }
    }
#pragma warning restore PALORM005

    /// <summary>R-UNNESTB 回归：跨晋升阈值的变参 <c>Where</c> 查询（A9 From&lt;T&gt; 槽）——
    /// 同一形状、不同值，晋升后每次结果必须跟随本次参数（同上缺陷面）。</summary>
    [Test]
    [Property("Category", "ExternalDatabase")]
#pragma warning disable PALORM005 // 循环内同形查询正是被测行为（A9 槽跨晋升阈值的变参复用）
    public async Task PG_Where_VaryingValues_AcrossPromotion_ReturnsMatchingRows()
    {
        await using var db = await DataSession<PostgreSqlProvider>.CreateAsync(PgOpts);
        await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_getbykey_reuse CASCADE");
        await db.ExecuteAsync(
            $"CREATE TABLE ext_getbykey_reuse (id BIGINT PRIMARY KEY, name TEXT NOT NULL)");
        try
        {
            const int total = 10;
            var rows = new List<ExtGetByKeyEntity>(total);
            for (long i = 1; i <= total; i++)
                rows.Add(new ExtGetByKeyEntity { Id = i, Name = $"n{i}" });
            await db.BulkInsertAsync(rows, batchSize: total);

            for (int round = 0; round < 12; round++)
            {
                long id = (round % total) + 1;
                List<ExtGetByKeyEntity> found = await db.From<ExtGetByKeyEntity>()
                    .Where($"\"id\" = {id}").ToListAsync();
                await Assert.That(found).Count().IsEqualTo(1);
                await Assert.That(found[0].Name).IsEqualTo($"n{id}");
            }
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE IF EXISTS ext_getbykey_reuse CASCADE");
        }
    }
#pragma warning restore PALORM005
}

#region Test Entities
// ITM-317/318 真库验证实体：可空列 + UTC DateTime + decimal 精度 + 唯一索引 string 列。
// PG Binary COPY 对 NpgsqlDbType 推断的两个疑点（DBNull 无法推断 / UTC DateTime 与
// TIMESTAMP 列错配）与 MySQL 索引 DDL(1170)/DECIMAL 截断都在此覆盖。
[Table("ext_bulk_entities")]
[Index("ux_ext_bulk_entities_code", "code", Unique = true)]
public partial class ExtBulkEntity
{
    [Key] public long Id { get; set; }
    [Column("code")] [Required] public string Code { get; set; } = "";
    [Column("note")] public string? Note { get; set; }
    [Column("amount")] public decimal Amount { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("optional_count")] public int? OptionalCount { get; set; }
    // CA1819 误报：ORM 实体列需要可变数组读写
#pragma warning disable CA1819
    [Column("payload")] public byte[]? Payload { get; set; }
#pragma warning restore CA1819
}

/// <summary>UNNEST-1 真库验证：长整型主键的数组形态（<c>NpgsqlDbType.Array | Bigint</c>）。</summary>
[Table("ext_array_delete")]
public partial class ExtArrayDeleteEntity
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] [Required] public string Name { get; set; } = "";
}

/// <summary>UNNEST-1 真库验证：Guid 主键的数组形态（<c>NpgsqlDbType.Array | Uuid</c>）。</summary>
[Table("ext_array_delete_guid")]
public partial class ExtArrayGuidEntity
{
    [Key(AutoIncrement = false)] [Column("id")] public Guid Id { get; set; }
    [Column("name")] [Required] public string Name { get; set; } = "";
}

/// <summary>UNNEST 阶段 B 真库验证：批量 UPDATE 数组形态（含可空列混合 null）。</summary>
[Table("ext_array_update")]
public partial class ExtArrayUpdateEntity
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] [Required] public string Name { get; set; } = "";
    [Column("qty")] public long Qty { get; set; }
    [Column("marker")] public long? Marker { get; set; }
}

/// <summary>UNNEST 阶段 B 真库验证：BulkMerge 数组形态（同列布局、独立表）。</summary>
[Table("ext_array_merge")]
public partial class ExtArrayMergeEntity
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] [Required] public string Name { get; set; } = "";
    [Column("qty")] public long Qty { get; set; }
    [Column("marker")] public long? Marker { get; set; }
}

/// <summary>R-UNNESTB 回归：跨晋升阈值变键读取的载体实体。</summary>
[Table("ext_getbykey_reuse")]
public partial class ExtGetByKeyEntity
{
    [Key(AutoIncrement = false)] [Column("id")] public long Id { get; set; }
    [Column("name")] [Required] public string Name { get; set; } = "";
}
#endregion
