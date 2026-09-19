# PalORM 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/) 规范。

## [未发布·性能轮] — 读路由会话级复用 · 查询构建分配减半 · SQL 零漂移 · 测试凭据自动加载

> 变更范围：v5.5.1 后 8 个提交，src/PalORM.Core 七个文件 + src/PalORM.Sqlite +
> src/PalORM.SourceGen + src/PalORM.Testing + 四个测试项目
> 验证：`PalORM.ci.slnf` 0 警告 0 错误 · Core 260/260 · SourceGen 190/190（快照语义等价已机器校验）·
> Integration 180/180（含 PG/MySQL 真库，无需手动 source 凭据）· 三个 AOT 程序
> `publish -p:PublishAot=true` 后原生运行 PASSED · SQL 转储 22 场景与基线提交 b2e5741 逐字节一致

### ⚡ 性能（实测数据来自 .ai/perf-probe，分配字节数复现性优于 1%）

- **读路由连接改为会话级复用**（行为变更）：原每次读查询都 `CreateConnection` +
  `Open` + Provider 初始化（SQLite 为 7 条 PRAGMA），实测单查询 **33.75µs → 8.98µs
  （−73%）**、分配 5815B → 2944B。读连接由 `DataSession` 持有并在会话释放时关闭；
  连接失效时丢弃重建，Provider 初始化随新物理句柄补设。
  `ConnectionLease` 随之简化为纯借用语义（移除 `OpenOwnedAsync`）。
- **查询构建分配**：`From<T>()` 构建器基线 **440B → 64B**；`ToSql()` **984B → 392B**；
  `WhereIn(500)` **89800B → 71456B**、`WhereIn(2000)` **432393B → 325537B**。
  手段：`QueryBuilderServices`/`QueryBuilderContext` 改 `readonly record struct`；
  `AddClause` 写时复制按 `Count+4` 预留容量；SELECT 列清单按 (Type, Dialect) 缓存；
  IN 列表改单 `ValueStringBuilder` 拼接；会话侧三处 `ConcurrentDictionary.GetOrAdd`
  捕获闭包改 `TryGetValue`，默认过滤三形态一次缓存。
- **PG Binary COPY 参数复用（T19，真库实测）**：COPY 行循环原先每行 `Parameters.Clear()` +
  `binder` 重建 `columnCount` 个参数（1 万行 × 4 列 = 4 万个 `NpgsqlParameter`）。现改为每批
  建一次、逐行只写 Value，取值走生成器已产出的 `BindInsertValues`（与 `MultiValueBulkInsert`
  的 v4.6 池同一机制，<b>无需改生成器</b>；旧模型程序集该绑定器为 null 时自动回退逐行路径）。
  实测（10K 行，4 列实体）：**882.3 → 211.1 B/行（−76%）**，时间 77.7 → 70.0 ms；PG 由
  「比 MySQL 多值 INSERT 贵 2.4 倍」（882 vs 361）变为「便宜 1.7 倍」。
  收益随列宽放大——19 列实体（18 个插入列）：**4844.7 → 816.9 B/行（−83%）**，
  即原始分析所引「1 万行 × 20 列 = 20 万参数对象」的规模上，单次批量插入少分配约 40 MB。
  参数对象仍留在命令集合内——`WriteRowAsync` 读 `NpgsqlParameter.NpgsqlDbType`，
  脱离集合会丢类型推断。
  <br>新增 `PG_BinaryCopy_NullFirstThenValue_KeepsTypeInference`：参数类型由首个绑定行推断，
  现有用例的行序是「先全非空、后全 null」，覆盖不到反向顺序，该用例把顺序倒过来钉住该风险。
  AOT 全链路复验：`dotnet publish -r win-x64 -p:PublishAot=true` 后原生运行
  `PalORM AOT PG verification PASSED`（该程序含 BulkInsert COPY 路径）。
- **批量 UPDATE 取值走零分配绑定器（T15-v2，生成器新增 `BindUpdateValues`）**：
  单语句批量 UPDATE 的目标参数池本身没问题，真正的削减点在 probe 命令**逐行 `BindUpdate`
  建参数**（40000 行 × 4 列 = 16 万次创建）。生成器现按 `BindInsertValues` 同一形态发射
  `BindUpdateValues(DbParameter[], entity, int paramOffset)`——只写 Value、零 `CreateParameter`，
  列序与 `BindUpdate` 逐位一致（SET 列 → 主键 → 并发令牌）。
  `CrudBindings`/`CrudMetadata` 新增可选字段（追加参数，不破既有调用点；旧模型程序集为 null 时
  `ExecuteBatchUpdateAsync` 回退 probe 路径）。
  <br>真库实测（数据准备置于计时区外，两侧同规模）：
  PG 单批 2671.6 → **1512.7 B/行**（−43%），多批 2750.0 → **1609.2 B/行**（−41%）；
  MySQL 单批 2291.7 → **1810.0 B/行**（−21%），多批 2457.0 → **2006.4 B/行**（−18%）；
  正确性两方言 `BulkUpdateBatchAsync` 均返回全行数且值校验通过。S3 反向验证一致。
  <br>注：上一轮（T15）只把参数创建挪进池、取值仍逐行建参数，创建总量不变，真库 A/B 判定为
  净负收益并已回退——零分配绑定器到位后池才有意义，两条合并才是完整的优化。
- **MySQL BulkCopy 参数复用（T20，真库实测）**：`MySqlBulkCopyInserter` 行循环原先每行
  `Parameters.Clear()` + `Binder` 重建 `columnCount` 个 `MySqlParameter`。现改为每批建一次、
  逐行只写 Value，取值走生成器已产出的 `BindInsertValues`（零 `CreateParameter`，
  与 `MultiValueBulkInsert` 的 v4.6 池同机制；旧模型程序集回退逐行 `Binder`）。
  <br>真库实测（10K 行）：4 列实体 **671.7 → 294.1 B/行（−56%）**；
  19 列实体 **3100.3 → 940.3 B/行（−70%）**，时间 161 → 141 ms。
  <br><b>该改动翻转了两条路径的优劣</b>：改前 BulkCopy 分配比多值 INSERT 回退路径更差
  （4 列 671.7 vs 359.1 = 贵 87%；19 列 3100.3 vs 2307.0 = 贵 34%），改后反超
  （4 列 294.1 vs 359.1 = 便宜 18%；19 列 940.3 vs 2307.0 = 便宜 59%）。O25 的
  「能力检测替代行数阈值」判据因此成立——原先差的是实现，不是判据。
  正确性：`MySql_BulkInsert_LocalInfileOn_InsertsAllRows` 通过（其 `name='row-9'` → 9
  的跨列断言可捕获池下标错位导致的静默串列）。S3 反向验证一致。AOT 复验 PASSED。
- **MySQL `local_infile` 配置补记**：BulkCopy 路径以 `local_infile=ON` 为唯一前置，而
  MySQL 8 默认 OFF——`bench/PalORM.Benchmarks/docker-compose.yml` 的 mysql 服务原先未开，
  任何人按该文件起库都只会走多值 INSERT 回退，BulkCopy 与 PG COPY 都测不到。已补
  `command: ["--local-infile=1"]`。<b>注意</b>：本次在真库上是用 `SET GLOBAL local_infile=ON`
  于运行时开启，<b>不随 MySQL 重启保留</b>；要持久化需在服务端 `my.cnf` 写 `local-infile=1`
  并重启（该实例不在本仓库管辖范围）。
- **AOT 三 Provider 首次全部本地验证通过**：生成器改动影响全部实体的生成代码，据此重跑
  `dotnet publish -r win-x64 -p:PublishAot=true` + 原生运行——SQLite / PostgreSQL / MySQL
  三个 AotTest 应用均 `PASSED`（此前 PG/MySQL 一直标注「CI 待验证」，本次借真库可用补齐）。
- **10K 行物化保持在地板**：相对 ADO.NET 地板 +0.05% 分配（未改动该路径）。

### 📝 行为变更

- 读连接生命周期：由「每查询创建并释放」改为「每会话懒创建并复用」。影响面为
  配置了 `ReadConnectionString` 的会话——读副本连接在会话存续期内保持打开。
- `ReadSessionSetupSql` 的执行时机从"每次读连接建立"收敛为"每会话首次建立读连接"。
- GridReader 释放不再级联释放被借用的连接（连接归会话所有）。
- **SQLite 上的池参数由「抛 `NotSupportedException`」改为「忽略」**（缺陷修复）：
  原实现 `SqliteProvider.CreateConnection` 见 `DbOptions.PoolExplicitlyConfigured` 即抛，
  而该标记由 `WithPool(...)` 与 `PALORM_MAX_POOL_SIZE` 环境变量置位，`DbOptions.Production(...)`
  预设内部就调用 `WithPool`——于是**任何走生产预设或环境变量的 SQLite 部署都在会话构造期
  必失败**，等于装不起来。（ITM-315 把「与默认值比对」改成显式标记位是对的判断，
  错在把"无意义的配置"升级成了"不可用的部署"。）
  忽略的依据：SQLite 是进程内嵌入式库，没有服务端连接池可承接这三个旋钮——驱动自带的池
  只有 `Pooling=on/off` 一个开关，无法表达最大连接数/空闲寿命/存活期。
  `MaxPoolSize` / `PoolIdleTimeoutSeconds` / `PoolLifetimeMinutes` 对 PG / MySQL 照常生效。
  非静默：Provider 方法注释与 README 配置表均标注"SQLite 忽略"。

### 📝 文档

- **表达式树可提到静态字段**（README + `docs/API参考.md`）：表达式树在调用点构造，
  库无法替调用方缓存，内联 lambda 在每查询都会重建树与闭包。in-situ 实测（同一查询，
  唯一差异是 lambda 内联还是来自 `static readonly` 字段）：`Set(expr, value)` −18.0% 分配 /
  −18.3% 耗时，单行查询 `+OrderBy` −12.4% / −7.2%，`WhereIn(500)` −0.72%，
  万行结果可忽略——即"表达式树占比随查询本身变重而摊薄"。给出
  `private static readonly Expression<Func<Order, DateTime>> ByCreatedAt = o => o.CreatedAt;` 模式。
- **弹性管线的每查询开销更正**（源码注释 + README）：`QueryBuilderExtensions` 原注释称
  "默认直通路径零开销"，两处都不成立——默认配置（`MaxRetries=3` / 熔断阈值 5）**不是**直通，
  只读查询默认就走执行器。实测为**常数 ≈272 B/查询，与结果行数无关**（单行查询 +8% 分配，
  千行 +0.2%）。三项独立测量之和与 in-situ A/B 之差逐字节吻合（168 + 56 + 48 = 272，
  实测 272~278）：超时 `CTS` + `CancelAfter` 定时器 ≈168 B（每次尝试一份，这是
  `CommandTimeout` 语义本身，覆盖连接获取与读取器迭代，非驱动侧 `CommandTimeout` 的子集）、
  调用点把单次尝试内核转成委托 56 B、执行器机械（熔断进出 + 异步状态机）≈48 B。
  <br>可剥的只有后两项（104 B），须把只读内核从 async 局部函数改成 struct 内核 + 泛型约束
  （顺带消掉两条分支共有的 ≈250 B display class）；实测耗时无差异，故记录而不实施。
  <br>另一项实测更正：`CancellationTokenSource.CreateLinkedTokenSource(ct)` 换 `new CTS()`
  在 `ct` 不可取消时**零收益**（两者同为 168 B）——联动源对不可取消的父令牌不注册，
  尺寸等价。原以为的优化路径经测量为死路，未改代码。
  <br>同时钉住直通配置的语义代价：`MaxRetries=0` + `CircuitBreakerThreshold=0` 下读路径
  既不建 CTS 也不包装超时，慢命令抛驱动自身异常，而非带 `PalORM.InfrastructureTimeout`
  标记的 `TimeoutException`（驱动的 `CommandTimeout` 仍然生效）。

### ⚡ 性能（①：BulkMergeAsync 集合化——远程库 8~10×，往返维度首个大项）

- **`BulkMergeAsync` 由「N 行 = N 次往返」改为分区集合化**：源码核实原实现是
  `foreach → SaveCoreAsync → 每行一次 ExecuteNonQueryAsync`（包在一个事务里）。
  现按键状态分区——默认键行走逐条 INSERT（保留 ID 回填契约：多值形态无法按行还原
  InsertCoreAsync 的物化/回填）；非默认键行（bulk-merge 主流场景）改为**多行 UPSERT**
  分批（每语句 ≤900 参数）：PG/SQLite `ON CONFLICT (pk) DO UPDATE SET c=excluded.c`，
  MySQL `ON DUPLICATE KEY UPDATE c=VALUES(c)`（与单行 upsert 生成 SQL 同形态）。
  <br>**真库实测（1,000 行）**：MySQL **588 → 57 ms（10.3×）**、分配 3250 → 1448 KB（−55%）；
  PG **571 → 71 ms（8.0×）**、分配 2362 → 1951 KB（−17%）。
  本地 SQLite（往返免费）：分配 −47%（8753 → 4623 KB/5K 行），耗时持平（72→83 ms，
  集合化的 SQL 构建抵消了省下的往返——本地收益有限是预期内，主战场是远程库）。
  <br>语义契约（6 项用例锁定）：既有键更新+新键插入、混合键分区+自增键 ID 回填、
  400 行跨批（>900 参数）、重复执行幂等、返回值=处理行数（不依赖 MySQL
  ON DUPLICATE KEY 的 affectedRows 口径）、[ConcurrencyCheck] 实体保持逐条路径的
  ITM-503 拒绝。
  <br>实现要点：`BindUpsert` 每行从 @p0 起命名且 **MySQL 参数集合在 Add 时校验重名**
  （实测撞出 ArgumentException）——不能直接往批命令逐行追加；改为 scratch 命令绑定 →
  值拷贝进预建参数池（池参数批内连续下标命名，逐行只写 Value）。
  <br>批内重复主键的语义边界（如实登记）：旧行为后行静默覆盖前行（last-wins 巧合，
  非契约）；集合化后 MySQL 保持 last-wins，PG/SQLite 对同语句影响同一行**明确报错**。
- **顺带根治一类 E1 同源 flaky**：多个 PG 集成用例并行调 `MigrateAsync`（全实体建表），
  PG 并发 DDL 在系统目录（pg_type/pg_class）竞态 → 23505 偶发失败。实测 3 轮 1 次
  （PessimisticLockTests 撞 ExtBulk 组用例）。已把两个锁用例编入 `ExtBulkTable` 编组，
  3 轮连绿。

### 📄 性能测试报告（2026-09-19）

- **`docs/性能测试报告-2026-09-19.md`**：优化前后对比总报告——批量路径 −47~84%、
  查询构建 −58~100%、启动 JIT −71%、稀疏负载省 13.2 ms/查询、并发单线程 +15~40%、
  门禁 27/27 零回归；含被否掉方向的防回潮记录与口径说明。

### ⚡ 性能（T5b：子句持久化链表，彻底删除子句 COW）

- **`QueryBuilder` 子句存储由 List+COW 改为持久化链表（cons list）**：每次
  `AddClause` 只分配一个不可变节点（48 B：前驱引用 + 子句 + 计数），零复制。
  struct 副本共享链引用——链不可变，副本隔离天然成立；这正是原 COW 存在的原因
  （QUERY-001：List 原地修改会泄漏到副本），链结构以更小的常量分配满足同一契约。
  只读遍历经 `MaterializeClauses()` 惰性物化为数组（每执行一次、此后复用；
  子句数为缓存有效期哨兵——链增长后长度必然不符而重建）。
  <br>实测（S1 单行查询，直通配置，5 万次/点，S3 双向确认）：
  `ToListAsync` 单行 **2,528 → 2,456（−72 B）**，`FirstOrDefault` **2,752 → 2,352（−14.5%）**；
  GetAsync/Insert/Update 不变 ✓。
  <br>**构建器路径单行查询本轮累计：2,976 → 2,456 B（−17.5%）**。
  CloneForExecution 语义不变（参数深拷贝保留），链重建仅为挂载拷贝参数。

### ⚡ 性能（T5c：BuildSql 输出缓存 + 据此修复跨方言缓存污染缺陷）

- **BuildSql 输出按形状缓存**：BuildSql 输出仅由「子句 Sql 文本序列 + 方言 + SplitQuery +
  Take/Skip 值」决定（LIMIT/OFFSET 的值直接内联进文本——键必须含值）。同一形状的查询
  复用同一 SQL string 实例，命中时跳过整个分段拼接。
  <br>实测（S1 单行查询，直通配置，5 万次/点）：
  `ToListAsync` 单行 **2,816 → 2,528（−288 B，−10.2%）**；
  `FirstOrDefault` **2,752 → 2,424（−11.9%）**；GetAsync/Insert/Update 不变 ✓。
  本轮起点 2,976 → **2,528，T1+T5a+T5c 合计 −15.0%**。
  <br>正确性设计：哈希只用于选桶，命中后**全量核对**——子句序列逐条值相等 +
  字段包（`SqlShapeCache.ShapeFields` 记录结构体：方言/SplitQuery/Take/Skip/表名/CTE 名）值相等；
  任一不等即未命中重建，哈希碰撞不会产出错误 SQL。容量以应用内"不同形状数"为界。
  S3 反向验证：禁用命中 → 2,816/2,752（退化确认），恢复 → 2,528/2,424。
- **T5c 暴露并修复一个此前不可见的真实缺陷：跨方言缓存污染**。形状键若无方言，
  PG 会话与 MySQL 会话的同形状查询（用户手写的 WHERE 文本无引用符差异）会互相复用
  对方方言的 SQL——`PessimisticLockTests`（上一轮新增的 PG/MySQL 同形状锁用例）
  实测捕获：MySQL 收到 PG 双引号 SQL 报语法错误。根因是 SELECT 列清单的引用符随方言
  不同，而它不在子句序列内。修复：`ShapeFields` 加入 `Dialect` 并参与哈希与核对。
  变异探针：禁用文本核对 + 恒定哈希 → 集成套件 5 例失败（错表 SQL 被当场抓出）；
  同时实证核对层的碰撞防护设计有效（恒定哈希单独注入时 184 全过——只损失性能不出错）。

### ⚡ 性能（T5a：删除冗余平铺参数列表，COW 减半）

- **`QueryBuilder` 删除冗余的平铺参数列表（`_parameters` List）**，代之以 `int _parameterCount`：
  每个子句本就自带参数（`QueryClause.Parameters`），扁平视图唯一被读取处
  （`GetParametersForKinds`）本来就从子句遍历——平铺列表是纯粹的重复状态，
  却让每次 `AddClause` 的写时复制要**复制两个列表**（子句表 + 参数表，各带 +4 槽）。
  删除后 COW 减半（每次 AddClause 少 2 次分配），单行查询实测 **−120 B**：
  `ToListAsync` 单行 2,936 → 2,816、`FirstOrDefault` 2,873 → 2,752；
  `GetAsync`/`Insert`/`Update` 不变（不走 AddClause 路径，确认无副作用）。
  S3 反向验证：摘掉改动 → 2,936/2,873（退化回基线），复用 → 2,816/2,752。
  平铺计数语义保留（参数全局编号 @pN 跨子句递增、WhereIn 65535 守卫）。
- 累计：构建器路径单行查询自本轮起点 2,936 B → **2,816 B**，加上 T1 前 2,976 →
  全程 **−160 B（−5.4%）**。与专用路径（GetAsync 1,656 B）的剩余差距
  在 COW 的另一半（子句表）、BuildSql 拼接与执行器包装——下一轮候选，
  仍需先量化再动手。

### ⚡ 性能（T4 流式终结器 ForEachAsync）+ T2 参数池化的实测裁决

- **新增 `ForEachAsync(action, ct)`**——流式消费查询结果，逐行回调**不物化列表**。
  真库 A/B（S1 × 100K）：物化 12.7MB 分配 / 8.5MB 列表存活 → 流式 10.7MB 分配（−16%）/
  **存活 0（调用方不再持有整表）**，耗时持平（54 vs 50 ms）。
  价值在解锁百万行级导出/ETL 而不 OOM——存活内存不随行数增长。
  语义契约与 ToListAsync 对齐：拦截器 OnBefore 按尝试/OnAfter 携带行数/OnError 通知、
  弹性覆盖同口径、**不写 WithCache**（流式没有可缓存的列表，与 First 族截断防护同理）、
  回调异常原样上抛并释放 reader/连接。API 参考表已补行。
- **T2（会话级参数池化）实测裁决：不做**。量化：3 参数组创建总成本 248 B，
  其中装箱 ~56 B 不可省，池化上限 ~190 B/查询（T1 后约占构建器路径 10%、
  全路径 ~6%）；而参数对象跨查询复用要面对 PG/MySQL 的类型推断残留风险
  （NullFirstThenValue 先例证明推断发生在首次绑定）+ 池归还生命周期横跨全部读写路径
  （漏归还只是无收益，错归还即正确性事故）。6% 分配收益换 Provider 正确性风险
  与生命周期复杂度，不成立。若将来 profiler 显示参数创建成为真实热点再重启，
  届时先在 PG/MySQL 真库上复现类型推断行为。

### ⚡ 性能（T1 形状缓存：立项预估被实测修正）

- **构建器 SQL 形状缓存**：`FormattableSqlFormatter` 的格式化输出是
  （Format 文本, 槽位偏移, 参数个数）的**纯函数**——Format 文本是编译期 ldstr 常量
  （同调用点恒同实例），据此以值相等键缓存输出，QueryBuilder 热路径
  （`FormattableSqlFormatter.FormatCached`）命中时复用同一 SQL 文本实例。
  <br>**实测收益 −40 B/查询（2,976 → 2,936，−1.3%），远小于立项预估的 −36%**——
  立项时把 Where+ToSql 的 1,048 B 归因于"格式扫描与输出串"，实测拆解证明格式扫描
  已走 ValueStringBuilder 栈分配、几乎零分配，可省的只有输出串本身。
  **预估−36% 是错的，按实测修正并如实记录。** 保留原因：正收益、零行为变化、
  命中时还省格式扫描时间。formatter 随之重构为纯字符串签名
  `Format(string format, int baseIndex, int argumentCount)`（纯函数性显式化，
  既有 FormattableString 重载委托保留，契约测试不变）。
- **同场测得的真实结论（比 T1 本身更有价值）**：单行查询的两条路径差距
  （构建器 2,936 B vs GetAsync 1,656 B）**不是** SQL 格式化造成的——格式化已近乎零分配。
  剩余差距在：每子句参数对象创建（~83 B/个）、子句写时复制（COW 两列表 ×4 槽）、
  BuildSql 拼接、执行器包装。这些构成下一轮的真实候选，全部需先量化再动手。
- `FirstOrDefaultAsync` 的容量 1 已存在（`Take(1)` → `Min(1, 16384)`），原计划 T3 撤销。

### ⚙️ 测评流程：一键全量 + 报告生成器

- **`scripts/run-full-perf.sh`**：单命令跑完整测评（构建 → 负载×2 → 内存曲线 → 启动量具 →
  BDN 微基准 → 门禁判定），产出 `bench/reports/perf-report-<时间戳>.md`（bench/reports/ 已
  gitignore——报告按需人工登记进 BENCHMARKS.md，不入库）。
- **门禁工具新增 `report` 子命令**：读取 BDN 结果 + 负载/内存 JSON，生成带表格的 markdown
  报告（环境头 + 微基准判定表 + 并发分位数表 + 内存曲线表）。
- **基准项目新增 `--memory` 模式**：大结果集内存曲线（10K/100K）+ 查询构建分配，
  复用 StandardShapes（S1），产出 memory-sqlite.json。
- 流程加固：BDN 步骤前清空结果目录——混入陈旧/截断报告会让门禁解析失败
  （本日实测：被中断的运行留下半截 JSON，check 直接 FATAL）。

### 📐 性能测评体系（规范 + 标准数据 + 并发负载测试）

- **新增规范真源 `docs/性能基准规范.md`**：12 维度矩阵（含逐项归属与状态）、标准数据形状
  S1–S5 的权威定义、行数档位、环境记录要求、测量口径、流程（日常/门禁/优化轮/基线重录）、
  新增基准准入清单、禁止事项。BENCHMARKS.md 自此只登记结果，"怎么测"以本文件为准。
- **新增标准数据形状**（`bench/PalORM.Benchmarks/StandardShapes.cs`，唯一权威定义）：
  S1 Narrow（4 列基线）/ S2 Wide（19 列全类型）/ S3 Sparse（确定性 70% null）/
  S4 Blob（32B/1KB/64KB）/ S5 LongText（2000 字符）。种子完全确定（值由行号派生、无随机），
  任何一次生成的库内容逐位相同；DDL 一律经 `MigrateAsync`，禁止手写建表。
- **补齐维度 3/4/11：并发负载测试**（`--workload`，此前全部结论都是单线程中位数）：
  N 线程 × 80/20 读写混合，每操作采样 → 近邻秩 p50/p95/p99 + ops/s，输出 schema 2 草案 JSON。
  首跑（S1 × 10K，SQLite WAL，Windows/.NET 11）：吞吐峰值在 2 线程（71,711 ops/s，+31%），
  8 线程回落到单线程一半以下——SQLite 单写者锁竞争的典型形状；
  p50 ≈ 0.01 ms 与读路由优化后的 8.98 µs 独立互证。**首跑数据只登记不设阈值**
  （分位数噪声底待积累，规范 §6）。
- `scripts/run-benchmarks.sh` 新增 `workload` 目标。

### 🧪 覆盖审计：会话级弹性配置器与 TagWithCaller（此前零覆盖）

- **系统审计"文档声明的 API 是否被测试执行过"**（ForUpdate 空白的同类排查）：39 个文档声明
  的调用式 API 里，`SingleAsync`/`SingleOrDefaultAsync` 经核实有覆盖（首轮启发式误报）；
  真缺口是 4 个**从未在任何测试中出现**的 API：会话级 `WithRetry` / `WithCircuitBreaker` /
  `WithTimeout`（v5.4 弹性特性的**运行期入口**——既有弹性测试全部走 `DbOptions` 构造期配置，
  若热替换 `UpdateResilience` 回归，既有测试全绿而生产配置路径失效）与 `TagWithCaller`。
  新增 `SessionResilienceConfiguratorTests`（4 项）补上。
- **实测钉住会话弹性方法的真实语义——是"叠加"而非"清空"**：`UpdateResilience` 把合并后的
  配置写回 `_options`（DataSession.cs:352），所以 `WithRetry(3)` 之后再 `WithTimeout(...)`，
  重试**仍然生效**。三个方法的 XML 文档写的是"重置当前弹性策略状态"——这个措辞有歧义，
  我第一版测试就按"重置=清空"断言、被当场否掉；文档已改写为准确表述
  （"以合并后的当前配置重建执行器；既有 builder 因快照语义不受影响"）。
  <br>另钉住 `resetAfter` 语义：`TimeSpan.Zero` 意味着开闸即半开、探针永远放行，
  看不到 `CircuitBreakerOpenException`（用例注释里留了这条）。
- 假连接（`FlakyConnection`）补了一个 `LastCreatedCommand` 捕获点，
  使 `WithTimeout` 的秒数能被断言"真的到达命令对象"。

### 🧪 AOT：PG/MySQL 原生程序补锁子句的"原生执行"验证

- 两个 AOT 验收程序（`PalORM.AotTest.Pg` / `PalORM.AotTest.MySql`）新增
  `VerifyPessimisticLocksAsync`：事务内**真执行** `FOR UPDATE` / `FOR SHARE` /
  `SKIP LOCKED` 并取回行。此前锁子句的原生验证是空的——SQLite 执行不了锁语句（ITM-639），
  SQLite AOT 程序只能验形态；而 PG/MySQL 这两个真正支持锁的方言，其 AOT 程序从未执行过锁。
  <br>**验证**：两程序 Release 构建与 `publish -p:PublishAot=true` 均 0 警告 0 错误，
  原生运行双双 PASSED（新增锁路径在原生二进制里真执行通过）。
  <br>PG 侧顺带钉住一个真实方言细节：`AotPgEntity.Id` 无 `[Column]` 特性时，PG 的实际列名是
  带引号的大小写敏感 `"Id"`——WHERE 手写小写 `id` 直接撞 42703（首跑即踩）。锁验证因此改用
  显式映射的 `name` 列，并在代码注释里说明了原因；MySQL 列名不区分大小写，同形代码不受影响。

### 🐛 修复（测试）：时间比值断言导致 flaky —— 换成对象同一性判定 + 订正 B4 解读

- **上一轮新增的 `RegistryIncrementalCostTests` 是 flaky 的**（5 轮红 4）：它用同一进程内的
  耗时比值断言（`未变化重跑 < 冷跑 × 0.2`、`改一个实体 < 冷跑 × 0.4`）。首轮调阈值到 0.5/0.6
  **反而更糟**——失败输出给了真相：同一比值在 0.2~0.8 之间跳。
  <br>根因：**TUnit 并行跑套件，墙钟测量全程与被并行执行的其它测试争 CPU**——同 run 内的比值
  也压不住（perf-gate 那套"同 run 比值可消机器差异"的经验在并行测试里不成立）。
  <br>改法：断言换成 **Roslyn 增量缓存的对象同一性**（命中缓存时输出实例被复用），
  完全不受负载影响。5 轮连绿。
- **B4 最终结论：生成器的增量设计正确，无需改动**（此前两版解读都被实测推翻，过程见下）。
  对象同一性实测（120 实体，**一实体一语法树**——真实工程布局）：
  · 输入未变重跑：**361 个输出全部复用同一批实例**（缓存生效的确定证据，11 ms）
  · 改一个实体：**复用 357 / 重发 4**——恰好是该实体的 3 份产物
    （RowFactory/CommandFactory/Migration）+ 1 份聚合注册表
  <br>即值比较器（`TableModel` 全字段 `EquatableArray`，结构等值）与每实体输出节点
  **确实按实体生效**，聚合注册表只重发自己这一份文件。原"改一个实体全量重发"的担忧不成立。
  <br><b>两版错误解读的成因（值得记住的教训）</b>：第一版拿耗时比值（0.2）当粒度证据，
  第二版把量具的**单语法树布局**当成生成器行为——把 120 个实体拼在一个源字符串里，
  改一个实体就是"整棵树变了"，于是复用数掉到 0。测增量必须用真实布局（一实体一文件）；
  改用一树一实体后复用数立刻变成 357/361。用例里留了复现提示以防后人重踩。

### 🧪 门禁新增 D12（文档 API ↔ 源码一致性）+ 补锁子句执行验证

- **新增门禁 D12**（`.ai/scripts/doc-consistency-check.sh`）：`docs/API参考.md` 表格里声明的
  调用式 API（形如 `` `.Name(` ``）必须在源码中存在对应的公开/内部声明。来源是上一轮的真实缺陷——
  `OrderByDescending`/`ThenByDescending` 被文档列为可用 API 而源码从未存在（`git log -S` 确认），
  调用方照文档写会撞上指向 LINQ 扩展的 CS0411。**这类偏差此前没有任何守护**；纯靠"下次有人
  偶然发现"不可接受（本条正是偶然发现的）。当前 39 个声明全部通过。
  变异探针：往表格塞一个虚构方法 → D12 红并点名该方法。
- **补 `ForUpdate`/`ForShare` 的"执行"验证**：此前整个套件里锁子句**只被 DryRun 断言过形态、
  从未真正执行**——"锁子句拼在语句合法位置、目标方言能接受"这件事零覆盖（锁子句错了只在运行时炸，
  而 SQLite 执行不了锁语句，见 ITM-639）。新增 `PessimisticLockTests` 在真 PG/MySQL 上
  **事务内执行** `FOR UPDATE`/`FOR SHARE`/`SKIP LOCKED` 并取回行，另断言锁子句出现在 WHERE 之后
  （位置是拼接顺序的函数，顺序回归会让语句非法而形态断言看不出来）。
  变异探针：把锁子句改成 `FOR UPDATEX` → 两个用例都执行失败（2/2 红），证明它们真在跑真库。
- **B3（两个无内部消费者的平面字典）量化收口**：实测 `BindInsert` + `BindUpdate` 的发射载荷
  占注册文件 **3.8%**（`RowFactories` 1.2%、`BindDelete` 1.5% 作对照，全在活的路径上）。
  3.8% 远小于 B2 的 19.6%，而可选改法要么是破坏性 API 变更、要么给运行时加一条派生路径而
  用户零收益——**关闭该项**，留待下个主版本随其它破坏性项一并处理。

### 🔬 生成器成本收口（B4/B5：两项"待办"以数据关闭）

- **B5（SQL 构建 memoize）实测否掉**：单条 30 列 INSERT 构建 0.44 µs；生成器每模型每方言调
  `BuildInsertSql` 3 次（经由两个 RETURNING 变体）× 3 方言 = 9 次，500 实体合计 **≈2 ms**——
  占实测生成耗时（4.4 s，500 实体 × 30 列）的 **0.05%**。为 0.05% 去把缓存穿进多个 emitter
  的签名不值得，未实施。
- **B4（增量重生成）不再按原假设处理，实测修正了假设**：原判断是"注册表是聚合输出，
  改一个实体必然全量重发"。新增守卫 `RegistryIncrementalCostTests` 用同一 driver 连跑三轮实测
  （120 实体 × 3 列）：
  | 轮次 | 耗时 | 含义 |
  |---|---|---|
  | 冷跑 | 653 ms | 全量 |
  | 输入未变重跑 | **9 ms** | 增量缓存生效（72×） |
  | 改一个实体 | **131 ms** | 冷跑的 **20%** |
  <br>**该解读后来被对象同一性测量否掉**（见下条"订正"）。500 实体规模按此推算单次编辑约数百毫秒——
  可接受，故当时不建议为它拆成按块多文件输出。

### 🧪 AOT 覆盖（表达式构建器此前从未被原生验证）

- **补上 P0 级验证缺口**：三个 AOT 验收程序此前只走 CRUD/Bulk/OwnedJson/软删路径——
  `OrderBy`/`ThenBy`/`OrderByDescending`/`Select`/`GroupBy`/`Having`/`WhereIn`/`WhereNotIn`/
  `Set`/`Include`/`ThenInclude`/`With(CTE)`/`UnsafeWindowOver`/`ForUpdate`/`ForShare`/
  `WithCache`/`AsPrepared`/`Tag` **一次都没被调用过**，即"它们 AOT 兼容"从未被原生运行验证。
  <br>做法：先在 JIT 侧写冒烟（`ExpressionBuilderSmokeTests`，迭代快、失败定位准），
  跑绿后原样搬进 `PalORM.AotTest`，`publish -p:PublishAot=true` 后**原生运行 PASSED**。
  断言口径双轨：可执行的断结果（排序/筛选/CTE 行数、`Set` 影响行数、Include 行数），
  只影响 SQL 的断形态（投影列、GROUP BY/HAVING、锁子句、标签）。
- **修一处文档与 API 不符**：`docs/API参考.md` 一直把 `.OrderByDescending` / `.ThenByDescending`
  列为可用 API，而源码里**从未存在**（`git log -S` 确认）——调用方写出
  `.OrderByDescending(x => x.Id)` 时编译器会去匹配 LINQ 扩展并抛难以归因的 CS0411
  「无法推断类型参数」。已补两个便捷方法（转调 `OrderBy(member, descending: true)`），
  并纳入上述 AOT 冒烟。
- **锁语句按 ITM-639 登记契约验证**：`ForUpdate`/`ForShare` 在 SQLite 上**故意不在构建期拒绝**
  （既定契约允许在 SQLite 上预览面向 PG/MySQL 的锁语句形态），执行才报语法错误。
  冒烟因此只断言 SQL 形态——我第一版直接执行并撞上 `SQLite Error 1: near "FOR"`，
  核实到该登记后按契约改正（这类"看起来是缺陷、实为登记取舍"的行为，改动前必须先查登记）。
- 冒烟用例自身的两处断言错误由用例当场抓出并订正：`Select` 投影被写成"应含未列出的列"、
  CTE 结果行数写成总数而非筛选后行数。

### ⚡ 性能（B2：只发射会被读到的 SQL 载荷）

- **生成器按方言族只发射会被读到的 `CommandSqlSet` 字段**，另一族与本族无关字段置 `""`：
  - `Insert` **全方言无消费者**（运行时一律走 `InsertReturning` 或 `InsertWithLastInsertId`），
    恒为空串。核实方式：原两条 `.Insert` 命中分别是 `_interceptors.Insert(...)` 与
    `columns.Insert`，与 SQL 集无关。
  - PostgreSQL/SQLite 读 `InsertReturning`/`UpsertReturning`；MySQL 读
    `UpsertMySql`/`InsertWithLastInsertId`（分发点是 `TProvider.SupportsReturningClause`）。
    非本族的字段置空——跨族字段本就不可达。
  <br>**实测**：快照（4 实体 × 20 列）注册文件 **48,265 → 38,789 字符（−19.6%）**；
  500 实体 × 30 列规模下约省 1 MB 级元数据字符串（生成物同时更小、编译更快）。
  逐块机器校验：12 个 `CommandSqlSet` 块每族必需字段齐备、恰好清空 3 个无关字段，无过度清空。
- **为什么置空而不删字段**：删除是破坏性 API 变更（旧生成器产物与外部构造点编译失败）。
  置空对既有消费者零破坏，代价是外部读者会读到空串——由 `CommandSqlSet` 的字段文档逐项声明
  "哪族有值"。**下个主版本可连同其它破坏性项（如 B3 的死字典）一并移除，一次迁移。**
- **B2b（SQLite 复用 PG 的 `CommandSqlSet` 实例）实测否掉**：两族七载荷**逐位完全相同**
  （4 实体 × 7 字段全等，已加断言），而 C# 编译器本就把相同字面量在元数据串堆里存一份——
  共享实例只省一次构造调用的 IL（约 50 B/实体），不值得引入局部变量与发射分支。

### 💔 行为变更（池空闲超时默认值）

- **`DbOptions.PoolIdleTimeoutSeconds` 默认值 30 → 0**，`0` 语义为**不覆盖驱动默认值**
  （Npgsql 300 秒 / MySqlConnector 180 秒原样保留）。`WithPool(...)` 的
  `idleTimeoutSeconds` 默认同步改为 0，故 `Production(...)` 预设（只传池大小）也不再改空闲超时。
  <br>**为什么改**：实测被覆盖时的代价是"间隔超过该值之后的首次查询必须重建物理连接"——
  跨网段 `SELECT 1` 池内 **0.300 ms** vs 新建连接 **13.523 ms**，即**多付 13.2 ms（44 倍）**。
  稀疏流量（查询间隔 &gt; 30 秒）会稳定踩到，而现象是"PalORM 慢"或"数据库慢"，
  几乎不可能归因到池参数。省服务端连接的收益（原 30 秒的动机）改为由显式配置获取。
  <br>**迁移**：依赖"30 秒回收空闲连接"的部署显式写
  `WithPool(maxSize, idleTimeoutSeconds: 30, lifetimeMinutes)`；不写即为驱动默认。
  合法值域由"正数"放宽为"非负"（0 合法）。
  <br>**契约测试**：`ProviderConnectionFactories_LeaveDriverIdleTimeoutAtDriverDefault_WhenNotConfigured`
  以"连接串里该键的有无"为观察面（builder 只序列化显式设过的键）——
  默认与"只给池大小"两种形态断言键缺席，显式 17 断言键出现，双向可判。

### ⚡ 性能（MySQL BulkCopy：去掉 DataRow 层）

- **批量插入的取值载体由 DataTable 换成 `EntityDataReader`**（新增 `src/PalORM.MySql/EntityDataReader.cs`）：
  原实现逐行 `table.NewRow()` + 逐列写值，实测每行 372.0/360.0 B（10K 行 × 4 列，两次采样）；
  读取器按序号直读参数池，每行 **57.0 B**（两次采样逐位一致），时间同向更快
  （114.2/112.7 ms → 100.9/100.4 ms）。**分配 −84%、时间 −11%，两条轴都赢**。
  <br>真库经真实 API 复核（同一探针口径）：4 列 **294.1 → 105.9/106.4 B/行（−64%）**；
  19 列 **940.3 → 502.0/526.9 B/行（−46%~−47%）**。
  <br>源码原注释称"MySqlBulkCopy 对 DataTable 路径有专门优化"——**该判断被实测证伪**，
  注释已按实测更正。取值仍走生成器发射的 `BindInsertValues`（零 `CreateParameter`），
  与 v5.6 的参数池是同一条链的两半：池到位后 DataRow 层就是唯一的每行分配主项。
- **保留的既有守卫一条未减**：`ITM-709` Warnings 非空即失败（防静默数据损坏）、`ITM-615`
  显式列名映射、自增 PK 前置补 DBNull、`ITM-710` 分批、`ITM-655` 超时 0=无限、
  `ITM-656` 行数规范化、旧模型程序集的 `Binder` 回退路径（读取器只多一步"把新建参数引用抄进池"）。
- **新增 7 项读取器契约单测**（`EntityDataReaderTests`，不依赖数据库）：逐行绑定与耗尽语义、
  前置主键列恒 DBNull、C# null → DBNull、列名/序号/形状、空范围、未知列名、分块读取不静默返回 0。
  <br>为什么必须有它：服务端 `local_infile=OFF` 时 provider 静默回退多值 INSERT（不经过读取器），
  真库用例照样通过——读取器契约必须有**不依赖服务端能力**的覆盖。为此给 `PalORM.MySql`
  加了 `InternalsVisibleTo("PalORM.Core.Tests")`（与 `PalORM.Core` 同例）。
  <br>**变异探针**：把读取器的列偏移故意改错 → 真库用例 2/5 失败，证明新增覆盖确实经过读取器。
- **新增真库值保真用例**：`MySql_BulkCopy_AllWhitelistedTypes_RoundTripPreservesValues`
  覆盖 `AllTypesEntity` 全部白名单类型（bool/Guid/DateTimeOffset/DateOnly/TimeOnly/float/byte[]/
  可空变体）——既有 BulkCopy 用例只覆盖 string/decimal/DateTime/byte[]。读取器把值统一以
  `object` 交给驱动按运行时类型格式化，格式错误即静默数据损坏，这是本次换路径的主要风险面。
- 顺带修探针自身缺陷：宽表 DDL 对 PG 用了 MySQL 的 `AUTO_INCREMENT`（历史遗留），
  使 PG 侧的宽实体对照此前直接抛 42601——现按方言分支。

### 🧩 生成器（B1：注册表分块）

- **注册表生成由单个巨型方法改为按 IL 预算分块**：原先全部注册代码落在单个
  `[ModuleInitializer] Initialize()` 里，IL 随实体数线性增长——实测 **500 实体（4 列）
  即单方法 611,449 B IL**。现在按**估算 IL**（每实体固定 ≈900 B + 每列 ≈60 B，由 4 列/30 列
  两个实测点反解）切成 `AddChunk{N}` 方法，每块写同一个可变 `RegistryDraft`，最后仍是一次
  `PalORM_Runtime.Register`。
  <br>为什么不是"每块各自调 Register"：`Register` 每次都复制并重建累计状态，逐块调用会把
  17 个字典反复拷贝，N 块累计 O(N²/块) 次插入。分块只切**构建**。
  <br>为什么按估算 IL 而不是固定实体数：单实体 IL 随列数增长，固定"每块 25 个实体"在宽表上
  会重新撑破方法体（30 列实体单块可达 143 KB）。
  <br>实测（`RegistryScaleTests`，3 样本/侧，500 实体 × 4 列）：单方法 IL **611,449 → 21,632 B**；
  注册初始化器**全部方法**的 JIT 编译耗时中位数 **1299.2 → 376.8 ms（−71%）**——
  超大方法的 JIT 代价是超线性的，分块不只是把风险摊开。切块是列宽感知的：4 列实体每块 18 个、
  30 列实体每块 7 个，单方法 IL 稳定在 ≈21.5 KB。
  <br>新增规模守卫 `RegistryScaleTests`：两维（实体数 × 列宽）断言"无生成方法超过 64 KB IL"。
  变异探针实测其有区分力——把块预算调到 10 MB（退回单体）后两个用例都失败（123,842 / 143,522 B）。
- **`docs/API参考.md` 的注册字典计数订正**：原称"16 个注册字典"且列表漏 `SensitiveColumnMasks`
  ——`RegistryFragment` 实为 17 个属性。计数与列表已同步。

### 📝 文档（AOT 验收）

- `docs/AOT部署指南.md` 的 PG / MySQL 两节原先只有 publish 命令、**没有运行命令与凭据要求**
  ——按文档操作会在原生程序启动后拿到一串连接失败的堆栈，无从判断是"环境没配好"还是"AOT 链路坏了"
  （本轮实际发生过）。现已补上带 `PALORM_PG_CONNECTION` / `PALORM_MYSQL_CONNECTION` 的完整命令，
  并说明本地可用 `set -a && . ./.env.test && set +a` 载入。
- 明确记录取向：这两个程序**刻意不自己读 `.env.test`**——它们是 AOT 验收程序，凭据应由环境显式注入
  （CI 直接注入 secret）；自动读仓库本地文件会让"从仓库跑"与"从 CI 跑"在凭据环节分叉。
- 状态表更新：PG / MySQL 由"原生 publish 通过，CI 运行待验证"改为"本机原生运行通过
  （2026-09-16，对远程开发库）· CI 容器运行待验证"——本轮三个 AOT 程序 publish 后原生运行均 PASSED，
  但按本文档既有口径，最终状态仍以 CI 服务容器为准。

### 🧪 新增测试

- `ReadRouteConnectionReuseTests`（3）：用 `ReadSessionSetupSql` 作副作用探针证伪
  "每查询新建连接"，含对照组证明计数有区分力。
- `DefaultFilterFormsTests`（3）：软删 + 租户的三种拼接形态在 Count/GetAll/Get 上各钉一条。
- `SqlitePoolParameterTests`（3）：`Production` 预设在 SQLite 上能构造会话、
  `WithPool` 被忽略而非拒绝、配了池参数的会话仍能正常执行查询。
- `RegistryScaleTests`（1 个用例 × 2 组参数）：生成 120 实体 × 4 列 与 60 实体 × 30 列，
  编译后直读 PE 元数据，断言**无生成方法超过 64 KB IL**。变异探针：块预算调到 10 MB
  （退回单体）→ 两个用例都失败（123,842 / 143,522 B）。
- 既有用例改名随契约变更：`SqliteConnectionFactory_RejectsUnsupportedPoolOptions`
  → `..._IgnoresUnsupportedPoolOptions`（原断言 `Throws<NotSupportedException>`）。
- 既有用例定位器随分块变更：`NonNumericPrimaryKeys_AreNotMarkedAsGenerated` 原按
  `IndexOf("SetIdDelegates =")` 切片——分块后条目不再连续，切片会落在片段赋值处，
  两条负向断言将在空集上**静默通过**。改为按行提取并先断言"恰好一条"，定位失效即失败。

### 🧪 性能门禁重做与基线重录

- **门禁原先结构上不可能变红**（三处叠加）：
  ① 过滤器 `*SqliteBenchmarks*` 匹配不到任何类名——仓库里最接近的是
  `SqliteSpeedBenchmarks`，中间隔着 `Speed`；实测 BDN 打印可用列表并以**退出码 0** 结束，
  跑 0 个基准；
  ② 回归判定 grep 控制台表格（`grep 'PalORM_QueryAll' | grep 'ms |'`），列宽/单位随 BDN
  版本变化，抓不到就落 `::warning::` 分支；
  ③ 基线阈值是绝对毫秒，而实测同机两次运行的 ADO.NET 地板从 6.97ms 漂到 10.0ms（43%）。
  另有第四处：artifact 上传路径写成 `bench/PalORM.Benchmarks/BenchmarkDotNet.Artifacts/`，
  而 `dotnet run --project` 的 CWD 是仓库根，真实位置是仓库根的 `BenchmarkDotNet.Artifacts/`。
- **重做**：判定改读 BDN 的 `-report*.json`（稳定契约）；范围收为
  `CrudBenchmarks` + `OrmComparisonBenchmarks`（27 项，约 5 分钟，CI 40 分钟预算内）；
  阈值只对**分配字节数（+20%）**与**相对同轮手写对照的比值（+10%）**设——比值在同轮内计算，
  把机器差异约掉，跨机器可比；缺基准/缺对照/schema 不符/哨兵缺失一律 `exit 1`，
  删掉"不可判定即放行"分支。
- **仪器已验证**（此前从未验证过它能否失败）：阳性对照 27 项 `exit 0`；**四处**变异全部被抓到
  ——分配抬高 50%、**中位耗时 ×2（仅耗时比触发，独立于分配维度）**、删掉 Crud 类（哨兵）、
  结果目录为空，均 `exit 1`。
  过程中阳性对照还抓出脚本自身一个恒红缺陷：哨兵按 `CrudBenchmarks.PalORM_` 做前缀匹配，
  而 `FullName` 是 `PalORM.Benchmarks.CrudBenchmarks.PalORM_QueryAll`——改为按叶子名判定。
- **门禁脚本改为 C#**（`tools/PalORM.PerfGate`，已加入 `PalORM.slnx` 与 `PalORM.ci.slnf`）：
  与 `PalORM.Scaffold` 同例的仓库内部工具，用 STJ 源生成上下文序列化（与仓库 AOT 纪律一致）。
  原先的 `python3` 内联片段读控制台表格、失败落 warning 分支——即"仪器本身从未被验证"；
  抽成工具后可本地对同一份 BDN 产物反复跑阳性对照与变异探针。`scripts/perf-baseline.py` 已删。
- **基线重录**：新增 `bench/baselines/perf-baseline.json`（schema 1，27 项，6 项带比值），
  由新工具 `tools/PalORM.PerfGate`（`record` / `check` 两个子命令，供门禁与
  `run-benchmarks.sh` 共用，避免两套解析漂移）生成。v5.0.json 保留为历史记录。
  <br>**判定维度扩到三个**：分配字节数（+20%）、分配比（+10%）、**耗时比（+30%）**。
  耗时比取同轮 PalORM 与手写对照的中位数之比，阈值放宽是因为门禁用 1/3/5 短 job、
  中位数本身有可观方差——它挡的是量级性 CPU 退化而非几个百分点的波动。
  <br>录得的分配比与耗时比揭示一个结构性事实：**分配差距远大于时间差距**——
  `PalORM_QueryAll` 1.137× / 1.161×，`PalORM_Update` 8.095× / 1.281×，
  `PalORM_Insert` 4.335× / 1.463×，`PalORM_GetByKey` 3.288×。
  单行写路径分配是手写 ADO.NET 的 4~8 倍而耗时只慢 1.3~1.5 倍（短命 Gen0 分配，
  回收成本未等比体现）：看分配判断优化空间，看耗时比判断用户感知退化。
- **阈值标定：用同一份代码连跑 3 轮量出噪声底**（每轮 27 项、与门禁同参 1/3/5）。
  原耗时比阈值 +30% 是推断值，现为实测标定：
  分配维度**逐位相同**（仅 `PalORM_QueryAll` 在 1547164 ± 30 B 浮动 = ±0.002%），
  分配比三轮四位小数全同；耗时比相对基线的偏离为 −9.4% ~ **+11.1%**（3 轮 × 6 项 = 18 个采样），
  逐项最坏波动 15.5%（`PalORM_VaryingShape` 1.245→1.438）。
  结论：+30% 是最坏偏离的 2.7 倍，任何小于 20% 的耗时比阈值都只是在测抖动，维持 +30%；
  同代码重跑门禁自检 27/27 PASS，阳性对照（基线耗时比压到 1.0）`exit 1` 且仅该项失败。
- **新发现：基线录于 Windows，门禁跑在 `ubuntu-latest`——跨平台分配一致性未验证**，
  且本机无 Docker 无法复现，故分配阈值保留 +20% 不收紧（实测该维度噪声仅 ±0.002%，
  一旦跨平台一致即可收紧到 +5%）。工具据此新增平台族提示：基线录制平台与当前不同族时
  先打印警告行，避免把平台差读成真回归。族归一按 `RuntimeInformation.OSDescription`——
  实现时踩到一个坑：Ubuntu 上该字段是 `"Ubuntu 24.04.1 LTS"`，字面不含 `Linux`，
  只认 `Linux` 关键词既会漏判（探针实测输出族名是整串描述），也会把两个 Ubuntu 补丁版本
  判成不同族而每次运行都误报；改为多发行版标记（Linux/Ubuntu/Debian/Alpine/CentOS/Fedora/
  Red Hat/Rocky/SUSE）归一到 `Linux`。

- `scripts/run-benchmarks.sh` 同步：`sqlite` 与 `scale` 的过滤器改为实际类名，
  报告路径改到仓库根，基线保存从「读 CSV 拼 JSON」改为调用同一脚本
  （原实现还有个真 bug：`tail | while` 的子 shell 使 `FIRST` 标志失效，
  条目间不输出逗号，生成的是非法 JSON）。

### 🔧 修复（测试基础设施）

- **集成测试必须先手动 `source scripts/set-test-env.sh` 才会通过**：`TestEnvironment`
  现于解析连接串前自动从仓库根 `.env.test` 补入**缺失**的 `PALORM_*` 变量。
  优先级为「显式环境变量 > `.env.test` > 报错」，已设置的值恒不被覆盖，故 CI 注入
  secret 的路径完全不读该文件；文件缺失时保持原有显式报错。
  <br>此前的失败信息是"环境变量未设置"，容易被误读为"没有可用数据库实例"——本轮实测
  发生过一次该误判，并据此错误地推迟了两项远程 Provider 优化。
- **`FindFileUpwards` 的深度差一错误**：`AppContext.BaseDirectory` 以目录分隔符结尾，
  `Path.GetDirectoryName` 首次调用只剥掉它、返回同一层，白耗一次迭代，深度上限实际
  少一层。该缺陷此前被 `appsettings.test.json` 的"复制到输出目录"（i=0 即命中）掩盖，
  只在查找未被复制的 `.env.test` 时显形。已用 `Path.TrimEndingDirectorySeparator`
  归一化起点，上限 6 → 8（RID 特定输出 / AOT publish 更深一层）。
- 新增 6 项单测锁定上述两点
- **批量 UPDATE 参数池（T15）经真库实测否决并回退**：原方案假定参数池在整个
  `BulkUpdateBatchAsync` 调用内建一次、跨批复用；实际 `ExecuteBatchUpdateAsync` 是
  **每批调用一次**，池随之为每批新建，参数创建总量不变，还多出 `DbParameter[]` 数组。
  真库 A/B（PG 与 MySQL 各 40000 行 = 3 批）：含改动 2779.6 / 2689.9 B/行，回退后
  2750.1 / 2582.9 B/行——**改动是净负收益**。单批 1000 行两侧同为约 2670 B/行（无变化）。
  <br>真正的削减点在 probe 命令逐行 `BindUpdate` 建参数（40000 行 × 4 列 = 16 万次），
  需生成器发射 `BindUpdateValues`（对齐既有 `BindInsertValues`）才能消除，属后续项。
- **secret-guard 文件名黑名单误拦 `.env` 家族模板**：黑名单的 `\.env\.[^e]` 只能豁免
  `.env.example` 这一种形态，`.env.<x>.example`（如仓库跟踪的 `.env.test.example`）被拦，
  挡住了对模板的正常修改。已加 `*.example` 豁免（仅文件名规则；内容规则照常执行），
  并补 11 条文件名自测向量（3 误报豁免 + 8 真阳性拦截，含 `.env.production`/`.env.local`/
  `id_rsa` 必须仍被拦截）。守卫自身变异探针：摘掉豁免后自测退出码 1，抓不到才算失败。
- **架构文档测试计数漂移**：`docs/架构设计.md` 与 `docs/API参考.md` 声明的 `[Test]` 标记数
  落后于实测（579 vs 实际 594），D10 门禁已红——上一批新增 12 项测试时未跑
  `doc-consistency-check.sh` 所致。已用该脚本的 `--fix` 机械同步（项目为此漂移提供的
  正规入口），D10 转绿。
：`FindFileUpwards` 用"恰好需要的层数"作上限以区分两种实现
  （变异探针实测：摘掉归一化后仅该用例失败，另 5 项通过），`ParseDotEnv` 覆盖注释/空行/
  引号剥离/非 `PALORM_` 键拒绝。

## [5.5.1] — 依赖全量升级：Roslyn 5.9 · Sqlite.Core rc.1 对齐 SDK · 漏洞清零

> 变更范围：v5.5.0 后 2 个提交，仅 Directory.Packages.props（无 API/行为变更）
> 验证：CI -warnaserror 0/0 · Core 238 / SourceGen 188（快照字节级零漂移）/
> Integration 172（PG/MySQL 真库）· AOT 三平台原生运行 PASSED · 漏洞/过时双归零

### 📦 依赖升级（7 项）

- **Microsoft.CodeAnalysis.CSharp/Analyzers**：5.6.0 → 5.9.0（源生成器构建基础；
  13 份快照字节级对比确认生成物零漂移）
- **Microsoft.Data.Sqlite.Core**：11.0.0-preview.7 → 11.0.0-rc.1（与 SDK 11.0.100-rc.1
  同轨对齐；.NET 11 GA 后切 stable）
- **SonarAnalyzer.CSharp**：10.32.0.713 → 10.34.0.3385（质量门禁）
- **TUnit / TUnit.Assertions**：1.65.0 → 1.66.27
- **Microsoft.SourceLink.GitHub**：10.0.400 → 10.0.401
- **RepoDb / RepoDb.Sqlite.Microsoft**：1.15.x → 1.16.0（bench 对照）
- **漏洞清零**：bench 的 SQLitePCLRaw NU1903（High，GHSA-2m69-gcr7-jv3q）随升级消除

## [5.5.0] — r20/r21 两轮评审全清偿 · [SensitiveData] 脱敏落地 · 方言词法与可观测性

> 变更规模：v5.4.0 后 14 个提交 · 58 文件 · +1975/−383 行（实测）
> 两轮全量评审（r20 43 项 + r21 复检 46 项）全部收口；Core 238 / SourceGen 188 / Integration 172 全绿；三平台 AOT 原生验证 PASSED

### 🔒 安全（P1 级）
- **[SensitiveData] 脱敏信道重构（ITM-763/764/794/751）**：掩码载体由生成器参数的
  SourceColumn（挪用 ADO.NET DataAdapter 标准语义）迁移为运行时注册表
  `PalORM_Runtime.SensitiveColumnMasks` → `QueryBuilder.Set` 登记参数名→掩码 →
  `QueryContext.SensitiveParameterMasks` 传递 → `AuditInterceptor` 替换真实值。
  端到端测试覆盖真实执行路径（此前 r20 实现信道断路、单测手工构造参数掩盖）；
  空/空白 Mask 归一为默认掩码（防明文回退）
- **BulkCopy Warnings 异常不回显服务端原文（ITM-777）**——MySQL 1366 内嵌被拒原始值
- **TestEnvironment 模板 null 守卫 + 异常声明补齐（ITM-776/746 doc）**

### ⚙️ 可靠性与契约
- **回退 r20 的 ITM-722 过度修复（P0）**——MySQL 1061 幂等迁移恢复"警告+跳过"；
  新增 `LastMigrationSkippedIndexes` 无副作用可观测通道（ITM-765）
- **ITM-640 外部事务失效改持续失败（ITM-767）**——原一次性响亮失败后静默自动提交
- **只读 SQLite（Mode=ReadOnly）修复（ITM-766）**——journal_mode=WAL 在只读连接抛 Error 8
- **WhereJson 方言守卫（ITM-770）+ value 归一拒绝 enum/char/TimeSpan/byte[]（ITM-771）**
- **DDL 尊重显式 Zero 无限等待；探活类保留 30s 兜底（ITM-769/762）**
- **PG COPY 超时包装修正（ITM-759/760）**——cleanup 异常挂实际抛出对象；timeoutCts 确证判据；
  CreateAsync 连接超时补 InfrastructureTimeout 标记
- **ConnectionTimeout 补上限（ITM-749）**——CancelAfter 上限 ≈49.7 天
- **半开探针非计数失败熔断复位（ITM-772）**——原 Open 谎言状态使熔断失效

### 🧹 生成器与诊断（39 条口径）
- **IsBalancedParentheses 补三类方言词法（ITM-753）**——MySQL `'`、方括号、PG dollar-quoting
- **PALORM031 降 Warning（ITM-752，SQLite 回退使其在该方言合法）；PALORM046 补命名冲突
  检查（ITM-754）；PALORM012/037 消息修正（ITM-780/781）；PALORM023 对齐 int/long 真源（ITM-782）**
- **诊断口径三方一致**——37→39 条（36 分析器+3 生成器），明细表补 PALORM041/046

### 📊 可观测性
- `db.system.name` 改 OTel semconv 小写（ITM-768）；instrumentation version 5.4.0（ITM-773）；
  PgNotificationListener.LastError 生命周期修正（ITM-761）；From\<T\> 孤儿 doc 归位（ITM-774）

### 🧹 死码与防御（P3 18 项）
AutoTaggingEmitter 头部残留/死分支/不可达守卫清理；AddJoin 空子句守卫（ITM-757）；
EquatableArray 双向防御拷贝（ITM-737/783）；WithOutputParam 可空解包（ITM-787）等

### 🧪 测试
- 新增：SensitiveDataMaskingE2ETests（脱敏端到端 3）· ReadOnlyAndTransactionGuardTests（4）·
  WhereJsonSqlGenerationTests（13，含方言守卫与归一拒绝）· ParenthesisScanAndTemplateCollisionTests（15）
- 迁移：WhereJson DryRun 测试自 Integration 迁 Core.Tests（方言守卫使旧模式失效）

## [未发布·r21 复检轮] — 回退上批过度修复 · 方言词法/诊断契约/文档三方一致

> 本轮为 2026-09-10 r20 修复批（7 提交）的复检结果。独立分片评审发现并修复如下问题。

### 🐛 修复（严重）

- **回退 r20 的 ITM-722 过度修复**：该修复把 MySQL 1061（索引名已存在）从"警告日志 + 跳过"
  改为"无可见日志即抛异常"，使**正常二次迁移变成硬失败**——与 ITM-528 既有裁决
  （"1061 无法区分同名同构/异构，故不改判定逻辑"）及 `ExternalDatabaseBulkTests` 的
  "二次迁移经 1061 兜底不抛"断言直接冲突，且会中断同批后续实体的迁移。
  现恢复为警告日志 + 跳过，并在注释中登记"不得因日志不可见而改为抛异常"。
  （本地 SQLite/PG 不触发该分支，CI 的 MySQL 容器用例是唯一防线——已在报告盲区登记）
- **ITM-751：`[SensitiveData(Mask = "")]` 把脱敏静默关成明文**：空掩码写入
  `p.SourceColumn = ""`，而读取侧以 `IsNullOrEmpty` 判"无掩码"→ 回落输出真实值。
  现生成器把空白 Mask 归一为默认 `***MASKED***`。
- **ITM-752：PALORM031 对 SQLite 误报 Error 阻断编译**：描述符断言"必抛 NotSupportedException"，
  但 SQLite 已方言回退逐条路径（方法文档亦明写"SQLite 回退则支持"）。分析器无 Provider 信息，
  降为 Warning 并订正文案。
- **ITM-749：`ConnectionTimeout` 无上限**：`CancellationTokenSource.CancelAfter` 的 delay
  上限是 uint.MaxValue-1 毫秒（约 49.7 天），超出抛与配置项无关的 `ArgumentOutOfRangeException`
  （实测 49 天 OK、50/60 天均抛）。与已修的 ITM-695 同族，`DbOptions.Validate` 补齐上限。

### 🐛 修复（方言与诊断）

- **ITM-753：`IsBalancedParentheses` 漏判三类方言词法**——MySQL 反斜杠转义单引号
  （`'it\'s'`）、方括号标识符（SQLite/T-SQL `[we(ird]`）、PG dollar-quoting（`$$a(b$$`）
  被当作未闭合区间吞掉后续括号，合法 `[Computed]` 表达式被判不平衡 → PALORM044 Error 误拒
- **ITM-754：PALORM046 漏检生成类名冲突**——同命名空间既有非 partial 的 `SqlTemplates`
  类型时，生成的 partial 声明冲突以 CS0260 落在 `.g.cs`（属 ITM-573 家族要消灭的形态）
- **ITM-755**：`SqlTemplateEmitter` 宿主形状判定的冗余合取条件（死条件）已简化

### 📝 文档

- **诊断口径三方一致**：`docs/API参考.md` 与 README 由"37 条 / PALORM001-045"
  订正为"39 条（36 分析器 + 3 生成器）/ PALORM001-046"，明细表补齐 PALORM041/046 行
- **ITM-746 补 XML doc**：`TestEnvironment` 两个公开 `Resolve*` 方法补 `InvalidDataException`
  声明（模板格式非法；消息不回显模板内容以免泄露字面量凭据）
- D10 计数同步（`docs/架构设计.md` / `docs/API参考.md`）

### 🧪 测试

- 新增 `ParenthesisScanAndTemplateCollisionTests`：方言词法参数化用例（11 组，含三类新增方言形态）
  + PALORM044 端到端 + PALORM046 命名冲突三态（非 partial 冲突 / partial 不冲突 / global 不冲突）
- PALORM031 用例补严重级断言（Error → Warning）

## [5.4.0] — 弹性只读管线 · 编译时诊断 PALORM045 · legacy SQL/DDL 收敛至方言单一真源

> 变更规模：29 个提交 · 84 个文件 · +2893/−1112 行（v5.3.0…v5.4.0 实测）
> 评审整改批次：2×P0（CI 门禁失效）+ 3×P1（弹性脱节/事务静默降级/生成器崩溃 UX）+ 工程防线托管
> 架构评审整改批次（2026-09-02）：评审报告 P1×2 + P2×4 + P3×3 全项清偿（详见下文各节）
> 架构评审第二批（2026-09-02）：复审报告 P2×2 + P3×3 + 可选项全项清偿（ADR-J + 包契约测试补强）

### ✨ 新增

- **弹性策略接入只读查询内置管线**：`WithRetry`/`WithCircuitBreaker` 此前对内置管线无效
  （全库唯一消费点是显式 `ExecuteWithResilience`）——现覆盖 `From<T>()` SELECT 家族、
  `GetAsync`/`GetAllAsync` 与聚合五兄弟；写入路径与事务内查询维持直连（幂等性契约 ITM-310）
- **编译时诊断 PALORM042-044**（ITM-640 收口）：`[Timestamp]+[Computed]` 冲突、SQL 标识符
  含控制字符/空串、`[Computed]` 表达式 NUL 或括号不平衡——此前以生成器异常（CS8785）
  或静默跳过呈现，现编译期精确定位；生成器侧改为防御性跳过不崩溃
- **编译时诊断 PALORM045**（架构评审 2026-09-02）：生成器 transform 失败面兜底 Warning——
  分析器规则被 `.editorconfig`/ruleset 抑制时，实体静默跳过的唯一编译期线索
  （此前该场景编译期零反馈，故障延迟到运行期 not registered）
- **[SqlFile] 增量管线接入文件内容**：.sql 文件经 AdditionalFiles（包 targets 自动注入
  `**/*.sql`，仓库内项目经 Directory.Build.props）进入增量缓存键——仅编辑 .sql 也触发
  重新生成（消除 ITM-585 陈旧缓存限制）；项目根改读 `build_property.ProjectDir`（不再
  从源文件路径向上找 *.csproj）；生成器零磁盘 IO
- **legacy CreateTableSql 生成段移除**（ADR-J，复用 ADR-I 修订模板）：三方言 DDL 是唯一
  真源；`RegistryFragment.CreateTableSql` 放宽为可选（旧片段仍可注册，运行时从不执行）；
  `MigrateAsync` 实体枚举源改走 `TableNames.Keys`，缺方言键的 recompile 错误保持不变
- **测试补强**：PG/MySQL 连接串自动调优值断言（此前仅注释承载）、SQLite 文件库/内存库
  PRAGMA 断言、`WithTransaction` 内未释放 GridReader 由事务收口兜底释放的安全网行为锁定、
  SqlFile AdditionalFiles 管线正/负例、独立双跑锁定 .sql 内容→嵌入产物映射（零串扰）、
  包契约测试与 NuGet 包消费者 AOT 冒烟补入 SqlFile 用例（覆盖包分发路径的 targets 注入契约）

### 💔 破坏性变更

- 默认 `DbOptions`（MaxRetries=3/CircuitBreakerThreshold=5）下，只读查询的瞬时故障现在
  自动重试并计入熔断——与连接建立重试同口径；`Testing` 预设（零重试零熔断）行为不变；
  事务内查询不受影响（重试会以次生异常掩盖根因）
- **legacy 无方言 CommandSqls 生成段移除**（ADR-I 修订：原绑定 v6.0，提前理由见 ADR-I
  修订节）：重编译模型程序集时须同步升级 PalORM.Core/Provider 包——"旧运行时 + 新生成
  片段"从注册成功/CRUD 时抛错改为注册期抛键集校验错误（均为响亮失败）；
  `RegistryFragment.CommandSqls` 放宽为可选（旧片段仍可注册，运行时从不消费）；
  `CrudMetadata` 新增不含 SQL 载荷的推荐构造，旧构造保留供旧生成片段二进制兼容
- **legacy CreateTableSql 生成段移除**（ADR-J，同模板）：`RegistryFragment.CreateTableSql`
  放宽为可选（运行时从不执行）；重编译模型程序集须同步升级包——影响面与上一条相同；
  `MigrateAsync` 实体枚举改走 `TableNames`，行为仅"旧片段错误消息时点"变化
- `QuerySingleAsync` 多于 1 行时的异常消息由精确总数改为 "at least 2"（流式精确单行——
  读到第 2 行立即失败并释放 reader，不再物化全表）；PALORM003 默认严重度由 Error 复议为
  Warning（多程序集布局可误报，见 ADR-D）；PALORM020 消息格式模板化、PALORM041 category
  归一为 "PalORM"；PALORM034 判定由初始化器文本白名单改为语义常量值（等价写法自然归一）

### 🐛 修复

| 问题 | 影响 | 修复方式 |
|------|------|---------|
| perf-gate.yml 在 CI 必然失败 | 性能门禁从未生效 | BDN fork clone 步骤 + restore 收窄 + `[perf]` 标签门控 |
| CI 秘密扫描第二道防线空转 | 40 类自定义规则从未消费 CI 差异集 | secret-guard 新增 `--range` 模式并接入 security job；临时仓库探针双向验证 |
| UseTransaction 外部事务被外部 Dispose 后静默降级自动提交 | 写操作丢失事务隔离无反馈（ITM-640） | 失效的外部事务响亮失败；`UseTransaction(null)` 显式清场逃生门 |
| `QuerySingleAsync` 为判"恰好一行"物化全表 | 大表上静默全读 | 流式精确单行：读第 2 行立即失败并释放 reader（对齐 QueryFirstAsync 策略） |
| `SessionOperationState.DisposeWaitTimeout` 可变静态 + 单读人工契约 | 双读事故两次发生（ITM-581/629），纪律已被证伪 | 结构化为实例状态：生产只读、测试经 `DataSession.DisposeWaitTimeout` 按会话设置，无需保存/还原全局值 |
| `BoundedQueryCache` 每实例注册 ObservableGauge 且回调闭包持有字典 | instrument 与缓存字典不可 GC（review R8 登记的泄漏） | 进程级单次注册 + 弱引用实例表（回调顺带剪枝死引用），指标名与标签形状不变 |
| PALORM034 初始化器文本白名单 | `0.0e0`/`1_000` 等等价写法误报、变体拼写漏报 | 语义常量值判定（GetConstantValue），仅保留语义可证的 Guid.Empty 特判与 MinValue 哨兵例外 |
| PALORM005 N+1 检测语义查询先行 | 绝大多数非循环调用白付 GetSymbolInfo | 语法圈检查前移（与 PALORM033 修复同口径） |
| SqlFileEmitter 注释声称"RS1041 强制 netstandard2.0 故不能用 AdditionalTexts" | 错误理由误导后来者（该 API 与 TFM 无关） | 注释更正为真实动机（内容进缓存键），并以此为基础完成 AdditionalTexts 重构 |
| 运行时异常消息中英混杂（15 处） | 公共库消费者无法统一检索 | 全部统一为英文（DataSession/Transactions/QueryBuilder/PostgreSqlExtensions/PgNotificationListener/SqliteProvider） |

### 🔧 工程

- **ADR-D 裁决落档（D3 降级告警）**：唯一开放 46 天的草案正式关闭——2026-09-02 第一批
  整改的 PALORM003 Warning 化即 D3 语义（"无法在本程序集验证，运行时自负"），本裁决为
  追认；D1（跨程序集 FK 扫描）保留为未来选项。至此 11 篇 ADR 全部状态终结，决策台账
  零未决项
- **AI 质量系统 v7.2 升级**（lessons B46-B53 八项实践登记 + AF 节 B54-B55 两项质检沉淀，
  计数修正至 62）：secret-guard 白名单正则变量化并入 --selftest（9 向量）；
  test-quality-scripts.sh skip_ai 真修复并接入 ci.yml gate（"声称在岗"防线收口）；
  .githooks 薄包装托管消除 cp 副本漂移
- **全量质检运行产出修复四则**：G18 门禁词边界化（裸子串误命中 RunInTransactionScopeAsync）、
  G9 豁免链补检测器自测向量文件、test-gate T-DEF-1 引入带理由豁免表（聚合计数器模式豁免）、
  IdentifierConsistencyTests 两处零断言改 Assert 形态
- **源码精炼批次**（评审整改，净 -220 行）：MySQL UPSERT 死代码对删除（UPSERT SQL 收敛
  至生成物单一真源）；WithTransaction 双重载委托泛型核心；聚合 Sum/Max/Min/Avg 共享
  ExecuteAggregateScalarAsync 内核（Count 形态不同保留独立）；Bulk 家族四处事务骨架
  收敛至 RunInTransactionScopeAsync 单内核（ITM-676/556/704 语义逐点保持）
- **legacy CommandSqls 链清除**（ADR-I 修订：由 v6.0 窗口提前至本批次，裁决变更理由
  与混合场景影响分析见 ADR-I 修订节）：生成物删除 7 legacy const + 无方言字典，方言 SQL
  唯一真源；快照基线已刷新并人工评审 diff（净删除，无语义漂移）
- **变异测试接入 CI**（mutation-tests.yml，每周六自动 + 手动）：Core 既有配置生效化，
  新增 SourceGen emitter + 分析器变异面——threshold-break=40 自此首次具备约束力
- `.githooks/pre-commit` 薄包装托管（`core.hooksPath` 方案），消除 cp 拷贝式安装的脚本漂移
- secret-guard 白名单精确豁免 MySQL uint64 LIMIT 常量（20 位连续数字误触身份证号规则）
- perf-gate 基线 v4.0.json→v5.0.json；回归解析失败从静默放行改为显式警告标注不可判定
- README 驱动版本对齐 Directory.Packages.props 实际值（MySqlConnector 2.6.2 / SQLite3MC 2.4.0）

### 🧪 验证

- **单元测试 369 项全绿**：Core 209 + SourceGen 160（Release 配置，`GITHUB_ACTIONS=true` 模拟 CI 环境）
- **Integration 181 项**：本地实跑 171 通过、10 项因本地无 PG/MySQL 服务容器未执行
  （环境变量缺失，非代码失败）；CI release 流水线以 postgres:17 / mysql:8.4 容器执行全量
- **Release 严格构建**：`PalORM.ci.slnf -c Release --no-incremental -warnaserror` 0 警告 0 错误
  （本地离线环境以临时清空 auditSources 规避 NU1900 联网审计；CI 在线环境不受影响）
- **包契约**：`test-package-contract.sh` PASS——本地打 5 个 5.4.0 包 → 仓库外独立消费工程
  → 实体身份契约 + SqlFile AdditionalFiles 注入验证
- **pack 计数 5 个**（Core/SourceGen/Sqlite/PostgreSql/MySql），nuspec 元数据抽查
  id/version/license(AGPL-3.0-only)/projectUrl/releaseNotes 全部正确
- **快照基线 13 份一致**（`PALORM_UPDATE_SNAPSHOTS=1` 刷新后 diff 人工评审为净删除）

## [5.3.0] — byte[] 二进制列原生支持（契约显式化 + AOT 全链 + 基准背书）

> 6 个提交 · 30 个文件 · +743/−92 行 · 四环节编译期契约补齐（读取/绑定/DDL/BulkInsert）
> 设计决策与放弃项见 [ADR-K](docs/adr/ADR-K-byte[]-二进制列支持.md)（发布时编号 ADR-G，2026-09-02 索引重建改号）；使用规范见 [二进制列最佳实践](docs/二进制列最佳实践.md)

### 💔 破坏性变更

- 无。白名单从"拒绝 byte[]"放宽为"收窄放行"，此前被 PALORM016 拒绝的实体现在可生成——纯放宽，Converter 出口 `string` 的既有路径不受影响。

### ✨ 新增

- **`byte[]` 一维数组列原生支持**：不再强制 Base64 TEXT 或 `[Converter]` 中转
  - 白名单收窄放行：仅元素为 `System.Byte` 且 `Rank == 1` 的数组——`int[]`/`string[]`/多维数组仍被 PALORM016 拒绝
  - DDL 映射：`MigrationEmitter.GetBinaryDbType` 三方言 BYTEA（PG）/ BLOB（SQLite）/ LONGBLOB（MySQL 数据列，4GB 上限对齐 Pomelo 惯例）；主键/索引列 VARBINARY(255)（BLOB 索引前缀约束，防错误 1170）
  - 参数绑定显式化：生成 binder 对 byte[] 列发射 `DbType.Binary`——PG Binary COPY 经 `NpgsqlParameter.DbType → NpgsqlDbType.Bytea` 显式分派，不依赖驱动运行时推断
  - 等值过滤：`Where($"col = {bytes}")` 参数化直通（含 0x00 字节），Native AOT 原生二进制下验证
  - Scaffold 反向工程闭环：BLOB/bytea/varbinary 全族 → 可用实体（修复前"生成即不可用"）
  - 锁定测试：`ByteArrayColumns_GenerateCrudAndBlobDdl`、`BinaryColumn_EqualityPredicate_FiltersRows`、`ScaffoldReverseEngineeringTests` ×2、AllTypes/BulkBinary/ExtBulk 往返（PG COPY 与 MySQL 真库）

### 🐛 修复

| 问题 | 影响 | 修复方式 |
|------|------|---------|
| byte[] 列落 TEXT 兜底（ITM-661(r4) 登记） | PG TEXT 拒 0x00 字节、静默变形为 hex 文本；MySQL strict mode 报 1366 | 白名单放行 + GetBinaryDbType 三方言 BLOB 映射 |
| Scaffold 可空引用列不加 `?`（预存缺口） | 反向工程的可空 TEXT/BLOB 列读 NULL 即抛 SqlNullValueException | 生成物带 `#nullable enable`，可空列（含 string/byte[]）加 `?` 后缀驱动 IsDBNull 守卫 |
| bench BDN fork NU1100 环境项 | 本地基准全部无法运行（T10 债根源） | 根 NuGet.Config 为 fork 源键补通配映射 |
| secret-guard v3 三处误报 | 合法提交被拦，诱发 `--no-verify` 习惯化 | 规则 14 占位符连接串/规则 32 大小写敏感/根 NuGet.Config 文件名精确豁免；钩子按文档程序同步 |
| T10 噪声债（r18/T-P3-07） | SqlBuild 基线 Error/Mean 16.9% 超阈 | 高精度重跑 ≤4.9%，基线数字入档 |

### ⚡ 性能

- **二进制列基准首跑**（SQLite 内存，StandardJob 3/5/10，详见 BENCHMARKS.md）：
  - 64KB 档：原生 BLOB 插入 **108μs / 69KB 分配 / 零 Gen2** vs Base64 TEXT 246μs / 432KB / **Gen0+Gen1+Gen2 全触发**——2.3 倍延迟、6.3 倍分配；Base64 编码串 85.3KB 越过 .NET 85000B LOH 阈值（实测坐实）
  - 256B 档两者相当（噪声区间）——二进制优势随载荷尺寸放大

### 📦 依赖升级

- 无

### 🧪 验证

- **523 个测试全绿**：Core 195 + SourceGen 147 + Integration 181（含 PG COPY / MySQL 真库 byte[] 往返）
- **Native AOT**：win-x64 本地 publish + 原生二进制实跑通过（含 0x00 等值参数化/物化往返/NULL 守卫）；linux/osx 由本次 tag 触发的 release workflow 验证
- **快照基线** 13 份一致（重生成并人工评审 diff：四条 DDL 均为 BLOB 语义、绑定器含 DbType.Binary）
- CI slnf 非增量构建零警告；tech-debt 扫描 12/12

### 📚 参考

- [ADR-K：byte[] 二进制列支持](docs/adr/ADR-K-byte[]-二进制列支持.md) · [二进制列最佳实践](docs/二进制列最佳实践.md) · [基准数字](bench/PalORM.Benchmarks/BENCHMARKS.md)

## [5.2.0] — 质量收口（14 轮 AI 评审 + record 支持 + 隔离级别全链 + 真库回归）

> 58 个提交 · 127 个文件 · +5390/−2939 行 · 48 个产品代码文件变更
> 14 轮 AI 自动化评审-修复迭代，每批修复经下一轮独立验证确认

## 💔 破坏性变更

- **`CrudColumns` 构造函数**：2 参数 → 3 参数（新增 `update` 列集）
  ```csharp
  // v5.1.0
  new CrudColumns(insertColumns, upsertColumns)
  // v5.2.0
  new CrudColumns(insertColumns, upsertColumns, updateColumns)
  ```
- **`IDbProvider.BulkInsertAsync`**：新增 `IsolationLevel` 参数（自定义 Provider 需同步签名）
- **`[Table]` 实体类约束**：record 类型现在被支持（不再是 class-only）

## ✨ 新增功能

- **record 实体支持**：`[Table] public record User { [Key] public long Id { get; set; } }` 现在完全可生成。
  - `get; set` 属性 → 真实生成（RowFactory/CommandFactory/Migration/Registry 全输出）
  - 位置参数 `record User(string Name)` → PALORM015 Error（缺无参构造，有定位诊断）
  - `[Key]` init-only → PALORM022 Error（防 CS8852，有定位诊断）
  - 锁定测试 ×3（GeneratorPhase2Tests）
- **隔离级别全链透传**：`WithIsolationLevel()` 现在在所有自开事务路径生效
  - `ToPageAsync`（此前仅单条 CRUD 生效）
  - `BulkInsertAsync`（PG COPY / MySQL BulkCopy / MySQL 多值 fallback）
  - `BulkDeleteAsync` / `BulkUpdateAsync` / `BulkUpdateBatchAsync` / `BulkMergeAsync` / `SeedAsync`
- **`CrudColumns.Update` 列集**：批量 UPDATE 不再反解 SQL 文本，直接消费编译期元数据
- **`SqlTemplate` 生成物 `global using System`**：限定引用（`{Math.PI}`）在无 ImplicitUsings 项目不再 CS0246

## 🐛 修复

| 问题 | 影响 | 修复 |
|------|------|------|
| MySQL Upsert 自增主键不回填 | `entity.Id` 恒为 0 | LAST_INSERT_ID 死分支 + 补 SELECT 后缀 |
| Raw 子句分页 Total 虚高 | 分页统计错误 | BuildCountSql 补 Raw 消费 |
| PG `WhereJson<T>(bool)` 恒空 | 查询静默返回空结果 | bool 特判小写 "true" |
| Audit `logParameters=false` 仍泄露 PII | PG DETAIL 参数值入日志 | else 分支不再传 exception 实例 |
| 连接池参数被静默覆盖 | 用户 `Max Pool Size=500` 被改为默认 100 | 仅默认值时覆盖策略 |
| Bulk 空列表+未注册类型静默 0 | 同一非法输入两种结果 | 六方法统一前置检查 |
| PgListener 首连挂起 | LISTEN 瞬态失败后 `StartAsync` 永久阻塞 | TrySetResult 锚定 startupPhase |
| ToPageAsync 缓存键污染 | 页截断结果写入用户缓存致同键 ToListAsync 丢行 | 克隆后清 `_cacheKey` |
| UPDATE + Take/Skip 扩大范围 | `.Set().Take(5)` 静默丢 LIMIT 更新全表 | 构建期显式拒绝 |
| PALORM024 谓词误报 | 纯 `[IgnoreOnInsert]` 实体被 Error 阻断 | 分析器谓词对齐生成器真源 |
| PALORM025 Nullable 时间误报 | `DateTime?` 属性被 Error 阻断 | UnwrapNullable 判定 |
| PALORM031/032 推断式调用漏报 | `BulkUpdateBatchAsync(list)` 不报 | 语义层 TypeArguments |
| `WithTransaction` 已释放事务误导报错 | 报"不属于主连接"而非"已释放" | 先查 Connection null |

## 📦 依赖升级

| 包 | 旧版本 | 新版本 |
|---|--------|--------|
| Microsoft.Data.Sqlite.Core | 11.0.0-preview.6 | 11.0.0-preview.7 |
| MySqlConnector | 2.6.1 | 2.6.2 |
| SQLite3MC.PCLRaw.bundle | 2.3.6 | 2.4.0 |
| Microsoft.CodeAnalysis.Analyzers | 5.3.0 | 5.6.0 |
| Microsoft.SourceLink.GitHub | 8.0.0 | 10.0.400 |
| TUnit / TUnit.Assertions | 1.61.38 | 1.65.0 |
| SonarAnalyzer.CSharp | 10.30 | 10.32 |

## 🧪 验证

- **518 个测试全绿**：Core 195 + SourceGen 144 + Integration 179（含 PG/MySQL 真库回归）
- **Native AOT** 三平台 publish 零错误 + 原生二进制运行通过
- **快照基线** 13 份一致（源生成器输出锁定）
- **每批修复经下一轮独立地毯评审验证**（连续 4 轮零 P0-P2 终证）

## 🔧 开发基础设施

- **敏感信息三层防护**：.gitignore 20+ 模式 → pre-commit hook 9 类检测 → CI gitleaks 全历史扫描
- **Release 自动化**：从 CHANGELOG 自动提取版本段 + 组装标准 Release Body
- **CHANGELOG 规范化**：全部版本统一 emoji 段头七段结构


## [5.1.0] — Auto Tagging Interceptor（SourceGen 自动 SQL 源码定位）

> 基于 `docs/v5.1-auto-tagging-design.md` + ADR-F。本版本引入 **opt-in 的自动 Query Tagging**，
> 用户零代码改动即可让 SQL 自动带源码定位注释（`/* 相对路径:行号 方法名 */`）。

### ✨ 新增

- **Auto Tagging Interceptor**（opt-in）：消费侧 csproj 设 `<PalORMAutoTagging>true</PalORMAutoTagging>` 启用。
  源生成器在编译期检测 `QueryBuilderExtensions` 的 6 个终态方法调用（`ToListAsync`/`FirstAsync`/
  `FirstOrDefaultAsync`/`SingleAsync`/`SingleOrDefaultAsync`/`ExecuteNonQueryAsync`），
  为每个调用点生成 `[InterceptsLocation]` 拦截方法，自动注入 `builder.Tag(...)` 后调用原方法。
  - SQL 日志自动含 `/* Controllers/UserController.cs:42 GetUserList */` 样式注释
  - 路径规范化：绝对路径 → 相对工作目录（避免泄露编译机目录结构到 DB 日志）
  - AOT 全兼容：net11 NativeAOT publish 0 警告实测通过
- **buildTransitive targets**：NuGet 包消费侧只需一行 `<PalORMAutoTagging>true</PalORMAutoTagging>`，
  targets 自动注入 `Features`/`InterceptorsNamespaces`/`CompilerVisibleProperty`
- **ADR-F**：`docs/adr/ADR-F-auto-tagging-interceptor.md`（决策记录 + 6 项技术约束）
- **B29 教训**：`.ai/lessons.md` AC 章节（Interceptor 实施工程化缺陷 + PoC 驱动 SOP）

### 🔧 技术约束（详见 ADR-F）

| 约束 | 说明 |
|------|------|
| `GetInterceptableLocation` 是扩展方法 | 在 `CSharpExtensions` 类，非 `SemanticModel` 实例方法 |
| `InterceptsLocationAttribute` 命名空间 | 编译器硬编码要求 `System.Runtime.CompilerServices` |
| MSBuild Property 传递 | 需 `<CompilerVisibleProperty>` 显式声明 |
| `TagWithCaller` 不能直接复用 | Caller* 在拦截器中返回拦截器自身位置，需用 `Tag(string)` + 编译期常量 |

### 🧪 测试

- 新增 4 个 AutoTagging 单元测试（开关关闭零生成 + 开关开启生成拦截器 + 签名匹配 + 路径规范化）
- `test/PalORM.AotTest.MySql` 扩展：启用 PalORMAutoTagging + 2 个拦截调用点
- 125 个 SourceGen 测试全部通过（零回归）

### 📚 参考

- 设计文档：`docs/v5.1-auto-tagging-design.md`（含"实施差异说明"章节，B9 教训）
- EF Core 参考实现：[Thirty25 博客](https://thirty25.blog/blog/2025/04/ef-core-source-gen-interceptors)

## [5.0.0] — 驱动现代化 + 调优（包升级 + 连接串/PRAGMA 性能调优）

> 基于 v5.0-roadmap.md 的 5 阶段方案。本版本聚焦**驱动层现代化 + 默认调优**，
> 不引入新公共 API。架构级改造（DbDataSource 单例化 / MySqlBulkCopy / DbBatch）
> 与功能增值（阶段 5）作为独立后续工作，不在 v5.0 主线。

### 💔 破坏性变更（测试源码层，公共 API 零破坏）

仅影响**测试代码**（src/ 公共 API 面零改动）：
- TUnit 0.19.24→1.61.15 引入 4 类测试 API 适配（10 处修复）：
  - `Assert.ThrowsAsync<T>(Task)` 重载移除→改用 `() => task`
  - `Assert.ThrowsAsync<T>` 返回类型可空化（`TException?`）→访问 `.Message` 加 `!`
  - `HasCount()` 已弃用→改用 `Count().IsEqualTo(n)`
  - `WithMessage(string)` 新增 `StringComparison` 重载→加 `Ordinal`

### 📦 依赖升级

| 包 | v4.6 | v5.0 | 备注 |
|---|:---:|:---:|------|
| TUnit | 0.19.24 | **1.61.15** | 测试框架现代化 |
| TUnit.Assertions | 0.7.9 | **1.61.15** | 与 TUnit 元包版本锁定 |
| Microsoft.NET.Test.Sdk | 17.13.0 | **删除** | TUnit 1.x 走 MTP 模式，不需 VSTest 桥接 |
| Npgsql | 9.0.3 | **10.0.3** | RowFactory 兼容（GetDateTime 仍返回 DateTime） |
| MySqlConnector | 2.4.0 | **2.6.1** | 含安全修复 GHSA-473q-m89c-ghf8 |
| BenchmarkDotNet | 0.14.0 | **0.15.8** | 基准工具升级 |
| Dapper | 2.1.66 | **2.1.79** | 基准对照升级 |
| RepoDb | 1.13.1 | **1.15.1** | 基准对照升级 |
| RepoDb.Sqlite.Microsoft | 1.13.1 | **1.15.0** | 基准对照升级 |

**保持不动**（有约束）：Microsoft.CodeAnalysis.Analyzers 5.3.0（5.6.0 不在 NuGet.Config 配置的 dotnet-tools 源，触发 NU1103）；Microsoft.Data.Sqlite.Core 11.0-preview.6（等 net11 GA）。

### ⚡ 性能调优

**PG 连接串调优**（PostgreSqlProvider.CreateConnection，用户显式值优先）：
- MaxAutoPrepare 0→100（自动预编译，查询延迟 -30~50%）
- AutoPrepareMinUsages 5→2（第 2 次起 Prepare）
- NoResetOnClose false→true（归还连接跳过 DISCARD ALL，+30% localhost 吞吐）
- ReadBufferSize/WriteBufferSize 8192→16384（大结果集/大值写入吞吐）
- Enlist true→false（跳过 TransactionScope 检查）

**MySQL 连接串调优**（MySqlProvider.CreateConnection，用户显式值优先）：
- AutoEnlist true→false / ConnectionReset true→false（归还池更快）
- CancellationTimeout 2→5（防连接泄漏）
- AllowLoadLocalInfile false→true（MySqlBulkCopy 前提，为后续阶段铺路）
- ServerRedirectionMode Disabled→Preferred（Azure MySQL 直连）

**SQLite PRAGMA 调优**（SqliteProvider.InitializeConnectionAsync）：
- synchronous=NORMAL（WAL 下安全，减少 fsync）
- cache_size=-65536（64MB 页缓存，默认 2MB）
- temp_store=MEMORY / wal_autocheckpoint=1000
- mmap_size=268435456（256MB，**仅文件数据库**，:memory: 跳过）

**global.json**：rollForward `disable`→`latestMinor`（允许 SDK 补丁版本前滚）。

### ✨ 新增：MySQL BulkInsert 能力检测分流

`MySqlProvider.BulkInsertAsync` 改为 **local_infile 能力检测** 分流（替代原 2000 行阈值）：
- **local_infile=ON**（服务端）→ 走 `MySqlBulkCopy`（LOAD DATA LOCAL INFILE 协议，~4.84x）
- **local_infile=OFF** 或非 MySqlConnection → 走多值 INSERT

**无阈值**：不再用行数阈值（2000 是伪精确），改为环境能力检测——行为可预测。与 PG COPY 永远走最优协议对齐。检测开销：每次 BulkInsert 额外 1 次 SHOW VARIABLES RTT（<1ms，批量场景占比可忽略）。

**部署约束**（已文档化）：客户端连接串默认追加 `AllowLoadLocalInfile=true`（v5.0 阶段 3.2）；服务端需 `local_infile=ON`（MySQL 默认 OFF，需 `SET GLOBAL local_infile=ON` 或 my.cnf）。

**DataTable 设计**：包含目标表全部列（含 AUTO_INCREMENT 主键），主键列填 NULL 让 MySQL 自增。

### 📐 调优判断策略

"用户未显式设置"的判据：属性当前值等于 ADO.NET 默认值。该判据在罕见场景（用户显式
设成默认值）下会把用户意图当作默认覆盖，但调优参数主动设成低性能默认值的实际场景极少，
收益（透明调优）大于风险。用户可通过显式设置非默认值避免被覆盖。

### ❌ 不做项（v5.0 排除 + 理由）

| 项 | 理由 |
|------|------|
| 阶段 3.4 NpgsqlParameter\<T\> 零装箱 | **已实测决策不做**——装箱占比 ~24.5% 但 PG COPY/MySQL BulkCopy 已无装箱；SQLite 无泛型参数 API。详见 `docs/boxing-benchmark-design.md` |
| 阶段 3.6 SQLite Pooling/Cache 解除限制 | 强制 Pooling 有测试隔离风险；用户可在连接串显式配置 Pooling=true / Cache=Shared |
| 阶段 4.1 DbDataSource 单例化 | **已批准 E1（不做）**——5 条 ORM 实践调研 + EF Core #3086 反面证据 + Npgsql 官方 "discouraged 非 deprecated"。详见 `docs/adr/ADR-E-dbdatasource-单例化取舍.md` |
| 阶段 4.3a BulkMerge 多值 UPSERT | 分流有害（语义偏差）+ RETURNING 多行回填复杂 + 用户无需求。BulkUpdateBatchAsync（4.3b）已完成 |
| 阶段 5.1 单操作 timeout/retry override | API 一致性困境（20+ 方法）+ 替代方案充分（多 DataSession / CancellationToken）|
| 阶段 5.6 ASP.NET Core 集成包 | 设计哲学冲突（无 DI）+ 替代方案充分（单例 DbOptions + 工厂模式）|
| 阶段 5.7 MySQL VECTOR 映射 | Innovation 版 + MySqlVector\<T\> API 不完整 + MySQL 8.4 测试环境不支持 |
| Microsoft.CodeAnalysis.Analyzers 5.6.0 | NuGet.Config 约束 Microsoft.CodeAnalysis.* 只从 dotnet-tools 源，该源无 5.6.0 stable |

### ✨ 功能增值（阶段 5 第一梯队）

**5.2 SessionSetupSql**（`DbOptions.SessionSetupSql` + `DbOptions.ReadSessionSetupSql`）：
- 主连接 + 读副本分别配置会话级 SQL（如 `SET TIME ZONE` / `SET search_path` / `SET statement_timeout`）
- 多条 SQL 分号分隔一次 `ExecuteNonQueryAsync` 执行（三方言均支持多语句）
- `IsNullOrWhiteSpace` 判断 null/空白等价未设置（向后兼容）
- 读连接初始化器：无 ReadSessionSetupSql 时用 static 委托（零闭包分配）

**5.4 AuditInterceptor**（`src/PalORM.Core/AuditInterceptor.cs`）：
- 实现 `IQueryInterceptor` 三段式：OnBefore/OnAfter/OnError
- Priority=200（让用户业务拦截器优先）
- `logParameters` 默认 false（脱敏，避免凭据/PII 写入日志）
- `ILogger.IsEnabled` 短路优化（无订阅者零开销）
- 覆盖面继承 IQueryInterceptor：仅实体 SELECT + QueryBuilder UPDATE；INSERT/DELETE/Bulk/存储过程不经过

**5.5b AdvisoryXactLock**（`src/PalORM.PostgreSql/AdvisoryLockExtensions.cs`）：
- 4 个扩展方法：`AcquireXactLockAsync(long)` / `AcquireXactLockAsync(int,int)` / `TryAcquireXactLockAsync(long)` / `TryAcquireXactLockAsync(int,int)`
- 事务级锁（`pg_advisory_xact_lock` / `pg_try_advisory_xact_lock`），事务结束自动释放
- 单 bigint key 和双 int key 是独立锁空间（不冲突）
- 用法：`await db.WithTransaction(async ct => await db.AcquireXactLockAsync(key, ct))`

**5.5 ForUpdate**（`QueryBuilder.ForUpdate(skipLocked)`）：v5.0 前已实现，本次确认存在。

### ✨ Scaffold 三 Provider 支持

`tools/PalORM.Scaffold` 从 SQLite-only 扩展为三 Provider：
- **新增 ISchemaProvider 抽象**（`tools/PalORM.Scaffold/ISchemaProvider.cs`）：屏蔽三方言元数据差异
- **三 Provider 实现**：
  - `SqliteSchemaProvider`：sqlite_master + PRAGMA table_info
  - `PostgreSqlSchemaProvider`：information_schema + JOIN tables/key_column_usage
  - `MySqlSchemaProvider`：information_schema.COLUMNS（DATABASE() 当前库过滤）
- **TypeMapper**（`tools/PalORM.Scaffold/TypeMapper.cs`）：DB 类型 → C# 类型，按方言分支
  - SQLite 按亲和性（INTEGER/REAL/TEXT/BLOB/NUMERIC）
  - PG/MySQL 按精确类型名 + 长度后缀裁剪（如 `varchar(255)` → `varchar`）
  - 覆盖 40+ 类型（含 uuid/jsonb/bytea/tinyint/DateOnly/TimeOnly 等）
- **EntityGenerator**：与 Provider 解耦，从 SchemaTable DTO 生成 C# 实体类
  - snake_case → PascalCase 转换（表名 + 列名）
  - 列名与属性名不同时加 `[Column("原名")]`
  - 自增 PK 加 `[Key(AutoIncrement = true)]`
  - 引用类型属性加 `= default!`（避免 nullable 警告）
  - 值类型可空列加 `?` 后缀
- **CLI 扩展**：`--dialect sqlite|pg|mysql` + `--namespace NS` + `--output DIR`
  - 方言别名：pg/postgres/postgresql、mysql/my、sqlite/sql
  - 兼容旧位置参数（args[1] 当 namespace）

**真实集成验证**（三 Provider 连真实库 scaffold）：
- SQLite：建表 → 生成 `UserOrders` 实体（含可空列 + 自增 PK）
- PostgreSQL：连 PG 18.4，生成 `AllTypesEntities`（uuid/bool/DateOnly/TimeOnly 类型完整映射）
- MySQL：连 MySQL 8.4.10，生成 `AllTypesEntities`（datetime/decimal/char 类型映射）

### ✨ 批量 UPDATE 单语句化

**新增** `DataSession.BulkUpdateBatchAsync<T>`（方案 Y 严格版）：
- 单次 RTT 完成 N 行 UPDATE（PG: UPDATE FROM VALUES 4x 提速；MySQL/SQLite: CASE WHEN）
- 永远走批量，无内部阈值，N=1 也走批量（用户显式选择）
- 带 `[ConcurrencyCheck]` 的实体调用抛 `NotSupportedException`（批量无法表达每行 version 匹配）
- 参数上限自动分批（PG/MySQL 65535，SQLite 999），物理约束非性能阈值
- 租户过滤自动追加 `AND tenant_id = @p`（与 BulkUpdateAsync 对齐）

**新增 BatchUpdateSqlBuilder**（`src/PalORM.Core/BatchUpdateSqlBuilder.cs`）：静态 SQL 构造器，与 DataSession 解耦降低认知复杂度。参数顺序对应 BindUpdate 输出：`[setCol0, setCol1, ..., pk]`（SET 列先，PK 在末尾）。

**与 BulkUpdateAsync 的语义差异**：
- BulkUpdateAsync 逐条执行 + 乐观锁检查（每行 affectedRows==1 否则 ConcurrencyConflictException）
- BulkUpdateBatchAsync 单语句批量 + 不区分"行不存在"与"并发修改"（返回受影响总行数）

### ✨ 诊断规则完整化（PALORM001-040）

**扩充 13 条新规则**（PALORM023-027, 031-033, 034-037, 040）：

- **P0 防运行时崩溃**（8 条）：
  - PALORM023/024：实体无可插入/可更新列（运行期 `throw`）
  - PALORM025：`[Timestamp]` 标在非时间类型（NOT NULL 无 DEFAULT 每次插入失败）
  - PALORM026：`[NotMapped]` 与映射特性互斥（避免 PALORM001 误报）
  - PALORM027：`[Converter]` 与 `[OwnedJson]` 互斥（消息比 PALORM015 更精准）
  - PALORM031：`BulkUpdateBatchAsync<T>` 对 `[ConcurrencyCheck]` 实体调用（必崩）
  - PALORM032：`Include/Join` 引用未注册实体（运行期 throw）
  - PALORM033：`Select(projection).ToListAsync()` 调用链（必崩）

- **P1 防静默错误**（5 条）——防止不 throw 但数据错/丢失/安全绕过：
  - PALORM034：`[Key]` 非默认初值让 SaveAsync 永远走 Update（数据静默丢失）
  - PALORM035：`[ConcurrencyCheck]+[IgnoreOnInsert]` 让乐观锁基线为 0（安全绕过）
  - PALORM036：`#nullable disable` 下引用类型不生成 IsDBNull 守卫（NULL 读取崩溃）
  - PALORM037：`[Required]` + 可空注解矛盾（DDL/读取行为不一致）
  - PALORM040：`[TenantAware]` 租户列可空（跨租户数据可见，多租户安全漏洞）

**修复 7 项现有规则缺陷**：

- F1：PALORM005 N+1 检测遗漏 Bulk/Save/Get 方法（功能遗漏）
- F2：PALORM002 消息"does not match table schema"语义错位（实际是"建议加 [Column]"）
- F3：PALORM017 对每个 `[ForeignKey]` 无条件报 Warning（过度报告，FK 在 Include 中有效）
- F4：PALORM015 消息"writable mapped properties"含义模糊（修订为原因清单）
- F5：PALORM012 类型限制未说明"emitter 用 ++ 自增"理由
- F6：PALORM003 跨程序集局限未在消息中提示可降级
- F7：PALORM010 无正例测试（补 `DoesNotReport`）

**精准化 1 条消息**：

- F8：PALORM009 补"partial sealed class"要求说明（STJ 源生成器约束）

### 🧪 验证

- dotnet build：0 错误
- Core.Tests: 174/174 通过
- SourceGen.Tests: 121/121 通过（原 104 + 新增 17 条诊断规则测试）
- Integration.Tests: 173/173 通过
- 总计：468/468 全部通过
- 环境：PG 18.4 + MySQL 8.4.10（MySQL local_infile=ON 部署约束）

---

## [4.6.0] — 极致性能（25+ 项分配优化）

> 基于 v4.0 实施后的深度性能审计与基准驱动迭代优化。
> 本版本为 **non-breaking**——公共 API 无破坏性变更。

### ⚡ 性能成果（vs v4.0 实测）

| 操作 | v4.0 分配 | v4.6 分配 | 改善 |
|------|:---------:|:---------:|:----:|
| GetByKey | 4.65 KB | **3.98 KB** | **-14%** |
| Insert | 5.16 KB | **4.97 KB** | **-4%** |
| Update | 8.31 KB | **7.44 KB** | **-10%** |
| BulkInsert 10K | 10.66 MB | **4.97 MB** | **-53%** |

BulkInsert 分配已优于 Dapper 62%（4.97MB vs 12.97MB）。

### v4.1：性能优化（IRowFactory 委托化 + Converter 单例 + 快照合并）

详见 v3.1/v4.0 CHANGELOG 条目。核心：IRowFactory → Func 委托、Converter static readonly 单例、CRUD Volatile.Read 合并。

### v4.2：极致降内存第一批（8 项）

- FormattableSqlFormatter 删除丢弃的 CompositeFormat.Parse + 改用 ValueStringBuilder
- QueryAsync 结果 List 起步容量 16
- InsertWithLastInsertId 预构建为 CommandSqlSet const
- QueryBuilder _clauses/_parameters 初始容量预分配 4/8
- 参数名预缓存 ParameterNameCache（@p0..@p1023 零分配索引取用）
- GetParametersForKinds 去 LINQ Contains + AsReadOnly
- 软删/租户过滤 SQL 片段 per-(Type,Dialect) 缓存
- From\<T\> 读连接工厂闭包提取为实例字段

### v4.3：ParameterNameCache public + probe 缓存跳过

- ParameterNameCache 提为 public，SourceGen binder emit 改用 GetName
- ProbeBinderAsync 结果缓存（InsertBinderValidated=true 跳过 probe）
- BulkInsert 10K 省 30K 次字符串插值

### v4.4：极致降内存第二批（8 项）

- BuildSql TrimEnd 先裁剪再 ToString（省 1 次 string 分配）
- CacheStore OTel counter 预构造 hit/miss KVP
- GridReader/StoredProcBuilder list 起步容量 16
- QueryClauseKinds 提为非泛型 static readonly（省临时数组）
- AppendSelectColumns sourceName quote 提循环外
- byte[] 列 GetValue → GetFieldValue<byte[]>
- BuildLimitClause 直接写 ValueStringBuilder

### v4.5：SessionOperationState TCS 延迟创建 + Exit 不写 AsyncLocal

- Enter 不创建 TCS，仅设 _isActive=true（省 88B/操作）
- Exit 不写 _currentOperationOwner.Value=null（省 ~300B EC 拷贝）
- Dispose/WaitForActive 条件创建 TCS

### v4.6：极致降内存第三批（6 项）

- owner=new object()→this + EC 相等短路（省 324B/操作）
- ExitTransactionFlow 不清 AsyncLocal（省 300B/事务收口）
- GetAsync 完整 SELECT SQL 缓存（省 120B/GetByKey）
- FormattableSqlFormatter 用 ParameterNameCache（省 32B/占位符）
- BulkInsert SqliteParameter 复用：新增 BindInsertValuesToBatch 旁路 binder，满批预分配参数池跨批复用（省 ~1.76MB/10K行）
- HasClause 位掩码 O(n)→O(1)（省 Predicate 委托分配/链式调用）

### 🧪 验证
- Core 161/161 + SourceGen 104/104 + Integration 160/160 = **425/425**
- 技术债扫描 12/12 + 门禁 G1-G29 全绿

---

## [4.0.0] — 性能与一致性收口（CommandFactory 单例 + Volatile 合并 + API 治理）

> 基于 v3.1 实施后的代码审查与 4 路并行深度调研产出，详见 `docs/v4.0-improvement-plan.md`。
> 本版本为 **non-breaking**——公共 API 无破坏性变更，仅 `DiffAsync` 标 `[Obsolete]`。

### ⚡ 核心优化

#### 优化 A：CommandFactory Converter 单例对齐（v3.1 最大遗留不对称）
- **问题**：v3.1 RowFactoryEmitter 已用 `static readonly _conv_<prop>` 单例，CommandFactoryEmitter 仍每次 `new Converter()`——同一项目内 emit 模式不对称
- **改动**：`CommandFactoryEmitter` 类级生成 `private static readonly IValueConverter<TClr,TProv> _conv_<prop> = new Converter();`
- **迁移路径**：BindInsert / BindUpdate / BindUpsert / BindDelete 全部从 `((IConverter)new Converter()).ToProvider(...)` 改为 `_conv_<prop>.ToProvider(...)`
- **收益**：百万行 BulkInsert 含 2 Converter 列省 ~200 万次 Gen0 分配
- **代码清理**：删除未使用的 `GetConverterInterfaceType` 方法

#### 优化 B：CRUD 路径 Volatile.Read 合并
- **问题**：v3.1 优化 3a 让 `From<T>()` 用了 `CurrentState` 单次快照，但 CRUD 路径（GetAsync / GetAllAsync / BulkDeleteAsync）未对齐
- **改动**：三处 `PalORM_Runtime.RowFactories/TableNames/ColumnNames` 独立访问 → 单次 `PalORM_Runtime.CurrentState` 快照
- **收益**：每次查询省 ~2 次内存屏障（fence）

#### 优化 D：List Capacity 起步优化
- **问题**：`ExecuteQueryAsync` 和 `GetAllAsync` 默认 `List<T>()` (=0)，10K 行场景扩容 14 次
- **改动**：默认 `new List<T>(16)` 起步
- **收益**：10K 行场景扩容次数从 14 降至 10

### 💔 API 治理（Breaking）

- **`DiffAsync<T>`** 标 `[Obsolete]`——本质是 `ValidateSchemaAsync<T>` 的字符串前缀薄包装
- **`GetRawConnection`** XML doc 强化「⚠️ 危险操作」警示（不重命名，避免破坏性）
- **`[Column].Length/Precision/Scale/TypeName/StoreAs`**：保留——已有 PALORM017 告警 + ITM-549 文档完整说明
- **`NamingConvention`**：保留——有实际 `ApplyNaming` 用途（自定义 SQL 场景归一标识符）
- **`WithMetrics(name)`**：保留——已有文档说明「名称仅保留 API 兼容」
- **`IRowFactory<T>`**：保留——v3.1 已决定作为公共契约

### 📚 AOT 文档补录

- **`docs/AOT部署指南.md`** 新增「聚合/标量查询 AOT 注意事项」小节
- 澄清 `Convert.ChangeType` 经评估确认 AOT 安全（走 IConvertible 接口分发）
- 非 IConvertible 类型（Guid/枚举/DateOnly）在 JIT 与 AOT 下行为一致

### 评估后跳过的方案项（B9 教训应用）

| 方案项 | 跳过原因 |
|--------|---------|
| 优化 C（QueryBuilder O(N²) 拷贝消除） | 构建时间占 QueryAll 4.73ms 的 0.02%，非瓶颈；重构将重新引入 QUERY-001 |
| 预构建 SQL（QuotedColumnList） | emit 三方言版本复杂度高，9-30KB 额外生成代码换取 ~150ns/查询 |
| 源生成器 emit 工程化（S2/S3/S4/S5） | S2 跨版本不稳定；S3 破坏快照基线；S4 实际仍被 MigrateAsync 使用；S5 增加状态参数 |
| AddRange 参数批处理 | ADO.NET Provider 行为不一致，风险高于收益 |
| Tracing/Metrics 短路 | 已是 const string + intern，零分配 |

### 🧪 验证

- 构建：0 警告 0 错误
- Core Tests：156/156 全绿
- SourceGen Tests：104/104 全绿（含快照重生成）
- 技术债扫描：12/12 通过

---

## [3.1.0] — 性能优化（IRowFactory 委托化 + Converter 单例 + 快照合并）

> 基于 v3.0.0 真实场景基准数据的深度优化，详见 `docs/v3.1-performance-plan.md`。

### ⚡ 性能成果（vs v3.0.0）

| 操作 | v3.0.0 | v3.1 | vs ADO.NET | 改善 |
|------|:---:|:---:|:---:|:---:|
| QueryAll 10K | 6.9ms (177%) | **5.35ms (132%)** | 4.05ms (100%) | -22% 时间 |
| GetByKey | 65μs (232%) | **26μs (141%)** | 18.4μs (100%) | **-60% 时间** |

### 优化 1：IRowFactory&lt;T&gt; → Func&lt;DbDataReader, T&gt; 委托（核心）
- **源生成器 emit 重写**：`sealed class : IRowFactory<T>` → `internal static class + static readonly Func<DbDataReader, T> Read` 委托字段
- **注册方式变更**：`RowFactories[type] = RowFactory_X.Instance` → `RowFactories[type] = RowFactory_X.Read`（委托装箱为 object）
- **所有调用点迁移**：QueryBuilder._factory、DataSession.Crud/Query、GridReader、StoredProcBuilder 共 9 处 `(IRowFactory<T>)factory).Read(reader)` → `((Func<DbDataReader, T>)factory)(reader)`
- **原理**：接口虚分发（vtable 查找 + 间接跳转）→ 委托直接 invoke（.NET 8+ JIT 对 static delegate invoke 有更好内联支持）

### 优化 2：Converter 单例缓存
- **RowFactoryEmitter emit**：每个 `[Converter]` 列从"每次 Read `new Converter()`"改为类级 `private static readonly IValueConverter<TClr,TProv> _conv_<prop> = new Converter();`
- **收益**：带 Converter 的实体每行每列省一次 Gen0 分配 + GC 压力
- **NRT 抚慰**：lambda 内 `_conv_X!.FromProvider(...)` 加 `!` 告知分析器字段已完成初始化

### 优化 3：state 快照合并 + Stopwatch 延迟 + 拦截器空跳过
- **3a：合并 Volatile.Read**：`From<T>()` 内 3 次 `PalORM_Runtime.RowFactories/TableNames/ColumnNames` 各自 `Volatile.Read` 合并为单次 `PalORM_Runtime.CurrentState` 快照（属性公开为 `internal static`）
- **3b：Stopwatch 延迟创建**：`ExecuteQueryAsync` / `ExecuteNonQueryAsync` 中 `Stopwatch.StartNew()` 改为仅在 Tracing/Metrics/拦截器任一启用时分配；热路径默认配置省一次 StartNew + Stop
- **3c：拦截器空列表跳过**：`foreach (interceptor) OnBefore/OnAfter/OnError` 加 `if (interceptors.Count == 0) return` 守卫；默认会话无拦截器时省迭代开销
- **抽取辅助方法**：`NotifyInterceptorsOnBefore/OnAfter` 共享于 SELECT/UPDATE 管线，降低认知复杂度

### 📦 文件变更
- `src/PalORM.Core/IRowFactory.cs` — 接口保留（向后兼容），XML 注释更新说明迁移
- `src/PalORM.Core/PalORM_Runtime.cs` — `RuntimeRegistryState` 从 private → internal，新增 `CurrentState` 属性
- `src/PalORM.Core/QueryBuilder.cs` — `_factory` 字段 + `QueryBuilderServices.Factory` 类型 `IRowFactory<T>` → `Func<DbDataReader, T>`
- `src/PalORM.Core/QueryBuilderExtensions.cs` — Stopwatch 延迟 + 拦截器辅助方法 + 调用点迁移
- `src/PalORM.Core/DataSession.Crud.cs` — `From<T>()` 快照合并 + 4 处 cast 迁移
- `src/PalORM.Core/DataSession.Query.cs` — 2 处 cast 迁移
- `src/PalORM.Core/GridReader.cs` — 2 处 cast 迁移
- `src/PalORM.Core/StoredProcBuilder.cs` — 1 处 cast 迁移
- `src/PalORM.SourceGen/RowFactoryEmitter.cs` — emit 重写
- `src/PalORM.SourceGen/RegistryEmitter.cs` — 注册值从 Instance → Read

### 🧪 验证
- Core Tests: 156/156 全绿
- SourceGen Tests: 104/104 全绿（5 个快照基线重生成 + 评审通过）
- SQLite Integration Tests: 149/149 全绿（7 个 PG/MySQL 环境变量失败与本次无关）
- 技术债扫描: 12/12 全通过
- 基准对比: QueryAll 177%→132%、GetByKey 232%→141%

## [3.0.0] — Breaking Changes（架构精炼 + Breaking API 移除 + 质量增值）

### 💔 Breaking Changes
- **移除 DataSession.ForRead() / ForWrite()**：请使用 `From<T>().ForRead()` / `From<T>().ForWrite()`
- **移除 CrudMetadata 旧 9 参 ctor**：请使用聚合 ctor（CrudBindings + CrudColumns）
- **移除 QueryBuilder.ThenInclude&lt;TGrandChild&gt;(单参)**：请使用双参 `ThenInclude<TGrandChild, TParent>(grandChildKey, parentKey)`

### ✨ 架构精炼
- **删除 8 个 Obsolete 公共 API**：MinimumLogLevel / ParameterPrefix / CreateConnection 单参 / GetLimitOffsetClause / LogQuery / RecordQueryStart / RecordQueryDuration / QueryBuilder 14 参 ctor
- **合并 TypeMapperEmitter → RowFactoryEmitter**：DateTimeOffset 读取直接内联 `GetFieldValue<T>`
- **PalORM_Runtime 拆 3 文件**：EntityFeatures.cs / SqlSets.cs / CrudMetadata.cs
- **Resilience 拆 CircuitBreaker + Exceptions**：熔断状态机独立为 `internal sealed class CircuitBreaker`
- **DataSession God Object 拆 4 partial**：Crud.cs / Query.cs / Transactions.cs / Schema.cs（1597→6 文件）
- **PgNotificationListener partial 拆分**：NpgsqlNotificationConnection.cs + PgNotificationEventArgs.cs
- **抽取 BulkOperationFramework**：消除 MultiValueBulkInsert/PostgreSqlProvider 间的 probe+cleanup 重复
- **ColumnModel 瘦身**：删除 4 个恒 null 预留字段（Length/Precision/Scale/DefaultExpression）
- **EquatableArray 独立文件**：从 TableModel.cs 提取

### 🧪 质量增值
- **集成 SonarAnalyzer.CSharp 10.29.0**：CI 守护层——P0 安全 + P1 设计规则全部为 error
- **BulkOperationFramework**：三 Provider 共享的 probe + cleanup 骨架
- **测试配置双层覆盖**：appsettings.test.json + .env.test + TestEnvironment 读取器
- **测试 helper 集中化**：TestInterceptors.cs（CountingTestInterceptor + CallbackTestInterceptor + OrderedInterceptor）
- **测试方法拆行**：AdvancedTests + QueryTests + FinalTests + MultiEntityTests 单行→多行
- **PALORM006/007 占位诊断删除**：零报告描述符移除
- **P1-2 规则升级为 error**：S3776/S107/S927/S2681/S125/S1066/S1994/S2189

### 📚 AI 系统
- **`.ai/lessons.md` v6.0**：14 个缺陷 + SOP + 决策矩阵 + 技术债扫描 SOP（自包含手册）
- **PR 模板**：编译/测试/Sonar/三方一致/精炼守护/反模式预防 6 类清单
- **`docs/编码规范.md` 第 18 节**：SonarAnalyzer 守护层规则配置

## [2.0.1] — 2026-07-15
- 初始发布
- Core + 3 Provider（SQLite/PostgreSQL/MySQL）+ SourceGen + Testing
- 面向严格 Native AOT（IsAotCompatible + IsTrimmable）
- 源生成器：RowFactory / CommandFactory / Migration / Registry / SqlFile / SqlTemplate
- 编译期诊断：PALORM001-040（33 条，含 P0 防崩溃 + P1 防静默错误 + 调用级 API 误用）
- 三方言支持：SQLite / PostgreSQL / MySQL
- 弹性执行器：重试 + 退避 + 超时 + 熔断
- 批量操作：MultiValue INSERT / PG Binary COPY
- 查询 DSL：Where/OrderBy/Take/Skip/Include/Join/CTE/Window/Cache
- 软删除 + 多租户 + 乐观锁
- PG NOTIFY/LISTEN
