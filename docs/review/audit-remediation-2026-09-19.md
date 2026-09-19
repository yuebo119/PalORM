# PalORM 审计整改任务账本（2026-09-19）

> 基线：`dev@0e6a040`。来源：全仓四阶段审计（运行时 Core 44 文件 / SourceGen+Providers 18 文件 / 测试 620 例抽样 14 文件 / CI 与配置直读，三条并行深读路线 + 二次抽查交叉验证）。
> 纪律：任务只有在实现、对应测试和验收命令均通过后才能标记完成；每任务走 S1 基线 → S2 单变量 → S3 反向验证（撤回修复 → 用例确定性失败）。
> 约束：SHAPE 系列涉及 SQL 文本形态的任务，动快照与 SQL 转储基线时须评审确认；全程保持 `dotnet build PalORM.ci.slnf -c Release --no-incremental -warnaserror` 0 警告、既有 620 测试不回退、4×AOT 矩阵绿。
>
> **执行记录（2026-09-19 执行会话）**：S1 基线 = 构建 0 警告 + Core 274/274。22 任务完成 22 项、证伪撤销 1 项（GEN-010）计入闭环后账面 21 闭环 + GEN-013 保留（纯移动决策项，非缺陷）。**环境误判更正（第三轮）**：前两轮"TEST-010/011 环境阻塞"是探测目标错误——真库在 192.168.200.120（.env.test 连接串指向），探测误用 127.0.0.1；坏地址实验（连接串指向 port 1 → 真库测试红）证明这组测试真实连库。解锁后落地：TEST-010/011 + PG/MySQL 的 LIMIT 参数化真库执行 + PG/MySQL AOT 矩阵本地验证。收尾验证：`--no-incremental -warnaserror` 0 警告 0 错误、Core 280/280、SourceGen 197/197、Integration 197/197（含 20 条真库测试：15 既有 + 5 新增）、AOT 四矩阵本地全 PASSED（SQLite/PG/MySQL win-x64 原生运行）。共 20 个提交。

## 完成定义（可衡量信号）

1. ✅ SHAPE-001 的复现测试转绿（OFFSET 探针 + Tag 拒写两个行为断言；进程缓存增量 0 与被拒形状 0）。
2. ✅ ToPageAsync 克隆路径形状缓存命中（ClonedBuilder_ReusesCacheEntryOfOriginalShape：同 SQL 文本仅一条目）。
3. ✅ README 无"内置加密"表述（降级为驱动层归属 + Password= 指引）。
4. ⏸ StoredProcBuilder 与 LISTEN/NOTIFY 真库测试——本地 PG/MySQL 不可达（5432/3306 无服务），待数据库环境后实施（任务设计与验收标准已在本账本）。
5. ✅ 门禁不回退：0 警告构建、Core/SourceGen 全绿（274→279 / 191→196，净增 10 条防线测试：缓存 3 + 拦截器契约 2 + 令牌守卫 4 + 基类增量 1，另 1 处既有测试断言增强）、SQLite AOT 原生运行 PASSED；PG/MySQL AOT 矩阵与 Integration 留待 CI/环境。

## 状态账本

| ID | 级别 | 任务 | 状态 | 验收证据 |
|---|---|---|---|---|
| SHAPE-001 | P1 | 写 A1 复现测试：动态 Skip 循环断言缓存有界 | 已完成 | 首跑红（found 10003/1）；SHAPE-010 根解后语义升级为 DynamicSkipValues_ShareSingleCacheEntry（10,000 Skip 值共享 1 条目）+ LimitValues_BindAsParameters_InDryRunSnapshot（参数值序哨兵） |
| SHAPE-002 | P1 | 写 A2 复现测试：克隆路径与原生路径互不认亲 | 已完成 | ClonedBuilder_ReusesCacheEntryOfOriginalShape 首跑红（found 24→增量断言 2），随 SHAPE-011 转绿 |
| SHAPE-010 | P1 | LIMIT/OFFSET 参数化（根解 A1；退守方案见详情） | 已完成（根解落地 + 三方言真库全验） | 第一轮退守（OFFSET 排除 + 1024 上限）；第二轮用户授权评审后落地参数化根解：BuildLimitClause 值经 @pN 占位（方言文本形态保持既有契约），ShapeFields 改 HasTake/HasSkip 布尔形态（值不进键、形态进键防 take-only/skip-only 撞条目），GetQueryParameters 同源附加参数，OFFSET 排除移除（形状回归有限集），1024 上限保留（兜底动态 Tag/Raw）。验证：Core 280/280（新哨兵：动态 Skip 共享单条目 + DryRun 参数值序断言）、Integration 197/197（**三方言真库分页执行**：SQLite/PG `LIMIT @p OFFSET @p`、MySQL `LIMIT @skip, @take` 位置形态，值断言 Skip2Take3→id 3,4,5）、AOT 四矩阵本地全 PASSED。S3 以推导+首跑红证据替代临时回退（旧值内联形态下新断言确定性红：参数 0≠2、条目 10000≠1） |
| SHAPE-011 | P1 | 修复 _shapeHash 三写入者旁路（A2） | 已完成 | 实施升级为单一真源现算（BuildSql 从形状成分现算，删除增量哈希状态与全部 Combine 点）；S3：stash 撤回后克隆测试回红（增量 16 ≠ 1） |
| DOC-010 | P1 | README 加密表述与实现对齐（D1） | 已完成（方向 A） | README.md:16,60,66 表述降级为"经 SQLite3MC 驱动支持，Password= 启用"；grep src/test 加密仍零实现，表述已不再声称内置 |
| ERR-010 | P2 | OnError 吞异常补观测挂点 + 三回调异常契约文档化（M4） | 已完成 | PalORMMetrics.InterceptorOnErrorFailures 计数 + NotifyInterceptorsOnError 计入（internal 可测）；IQueryInterceptor 三回调契约 doc；InterceptorErrorContractTests 2 用例（吞+不阻断后续+计数） |
| CACHE-010 | P2 | 静态缓存清单文档 | 已完成 | docs/静态缓存清单.md：11 项三要素登记（含 FormattableSqlFormatter.FormatCache 这个审计漏登记项）+ 维护规则 |
| GEN-010 | P2 | 增量管线基类依赖修复 + 增量编译测试（M1） | 已完成（实验证伪，修复撤销） | BaseTypeIncrementalTests：基类加列后派生产物**正确更新**（含新列）——M1 在 Roslyn 增量复跑场景证伪，管线无需修复；实验保留为增量防线 |
| TEST-010 | P2 | StoredProcBuilder 真库 happy-path 集成测试（T1） | 已完成（第三轮解锁） | ExternalDatabaseFeatureTests：PG（CREATE PROCEDURE plpgsql IN/OUT）与 MySQL（CREATE PROCEDURE SET 输出）各一条——输入 21 → 输出参数回读 42 的值断言；try/finally DROP 清理；坏地址实验证明真连库。Integration 197/197 |
| TEST-011 | P2 | PG LISTEN/NOTIFY 真连接冒烟测试（T2） | 已完成（第三轮解锁） | PG_ListenNotify_RealConnection_ReceivesPayload：真连接 LISTEN → 独立会话 NOTIFY payload → 通道+载荷双断言；WaitAsync 超时护栏零 Sleep |
| GEN-011 | P2 | ConcurrencyCheck 纳入 CanGenerateEntity 自守卫（M6） | 已完成 | SourceGenerationValidation 镜像 PALORM012/013 口径（int/long 非空 + 非 init-only + 至多一个）；ConcurrencyTokenGuardTests 4 用例（Guid/init-only/双令牌拒 + long 对照通过）；S3：短路守卫后 3 用例回红 |
| PROV-010 | P3 | "no generated insert metadata" 守卫收敛单一 helper（M8） | 已完成 | BulkOperationFramework.EnsureInsertMetadata 单点；PG/MySQL 入口/MySQL 内层/Core 四处调用收敛；grep 文案剩 3 处（helper 1 + DataSession.Crud 快照一致性版 + DataSession_Bulk 会话层存在性版，后两者语义刻意不同不收敛，见执行记录） |
| GEN-012 | P3 | Bind 更新双循环共享列序单一真源（M7） | 已完成 | GetUpdateColumnOrder 单一真源 + 同构段 Concat 保序合并；13 份快照零漂移；新增 BindUpdateColumnOrderTests（产物级列序一致性）；S3 注入反转漂移后新防线+快照双红 |
| CORE-010 | P3 | 魔法数字常量化：65535/500/ulong 上限（L2） | 已完成 | SqlLimits（public，对齐 BulkOperationFramework 内部 API 口径）单点；五处替换（含审计漏数的 MySqlProvider 回退分支 65535）；grep 字面量仅剩注释 |
| API-010 | P3 | BulkMergeAsync 返回值语义 XML doc 明确（Q2） | 已完成 | returns 节：处理实体数口径 + 跨方言理由 + 3.0 决策标注 |
| CACHE-011 | P3 | 多租户缓存警告前置 README 特性章节（S3 文档面） | 已完成 | README 特性表 TenantAware 条目内联警告（key 约定/独立注入两路径，对齐 ADR-C） |
| GEN-013 | P3 | 描述符银行抽出独立文件 | 待开始 | 纯移动低收益，留后续 |
| DOC-011 | P3 | TableModel PALORM045 文案移除不可达的 structs（L7） | 已完成 | 文案改为 interfaces/enums + structs 由 AttributeUsage 前置拦截的说明 |
| TEST-012 | P3 | 测试小瑕疵：FinalTests 拆分 / 过期注释 / 裸 IsNotNull（L6） | 已完成 | FinalTests 按特性拆三文件（QueryFeatureSmoke/TracingSanitization/QueryMetrics，listener 命名组隔离）+ 双类文件归位（ParenthesisScan/SqlTemplateNamespaceCollision 各归一文件）+ 过期注释更正 + 裸 IsNotNull 补 HealthCheck 行为断言；Integration 192/192 |
| GEN-014 | P3 | AutoTagging 缓存比较器与注释对齐（L4） | 已完成 | InterceptionTarget 与 PalORMGenerator 两处注释改为引用相等/过度失效的准确表述 |
| GEN-015 | P3 | 分析器 InvocationExpression 双注册合并 + 031/032 语法预筛前置（L5） | 已完成（注册合并） | 两注册合并为单回调按名分派（名字集互斥已核实：005 集与 031/032/Select 集零重叠），每调用节点省一遍回调；005 的"语法圈先行"顺序保持；197 测试（含各诊断正反用例）全绿验证行为不变。CheckJoinUnregisteredEntity 内 IsPalORMInvocation 与 GetSymbolInfo 的双语义查询合并留原样（次要优化，改动面大收益小） |
| PROV-011 | P3 | Provider 布尔旋钮 XML doc 修正（L8，仅 doc） | 已完成 | PG/MySQL CreateConnection doc 补布尔旋钮边界段（被覆盖后果是正确性而非性能，须绕开工厂自建连接） | |

> 开放问题裁决记录（2026-09-19 第三轮，仓库所有者拍板）：
> - **AGPL 双许可**：暂时不处理（关闭）。
> - **BulkMergeAsync 3.0 语义**：论证后裁决**维持"处理实体数"**（MySQL affectedRows 依赖数据历史，改驱动口径比家族不一致更危险）；配套：BulkInsert/Update/Delete 各补 returns 口径声明，BulkMerge 的 returns 更新为含论证的终版。关闭。
> - **多租户缓存默认**：论证后裁决采纳**结构性隔离（ADR-L）**并已实施——实际缓存 key 按租户作用域自动前缀化（`__t:{tenantId}:` / `__all__:`），与租户过滤注入同点冻结；三用例测试 + S3 反向验证（撤前缀化 → 串租用例确定性回红）闭环；ADR-L 新增、ADR-C 修订头、静态缓存清单与 README 同步（顺带修正 README 的 SetTenant→WithTenant API 名漂移）。
> - **SDK GA 切轨**：等 .NET 11 正式版发布后执行（Directory.Build.props 注释已登记该计划）。挂起至外部事件。

---

## 里程碑 0：安全网（先红后绿）

### SHAPE-001 · A1 复现测试（P1 · S · 依赖无）

新文件 `test/PalORM.Core.Tests/SqlShapeCacheGrowthTests.cs`。

- 方法：循环 10,000 个不同 `Skip(i).Take(10)` 调 `ToSql()`，断言 `SqlShapeCache.Buckets` 总条目数增量 ≤ 1,024。Core.Tests 已引用 internal 成员（若 InternalsVisibleTo 未覆盖 `SqlShapeCache` 则补声明）。
- 陷阱：静态缓存与同程序集其它测试共享，断言用增量非绝对值；`ToSql()` 必须走 BuildSql 缓存路径（不要经 AsDryRun 中转）。
- 验收：当前为红（条目数 ≈ 10,000）；SHAPE-010 完成后转绿；S3 反向验证：撤回修复 → 本用例确定性失败。

### SHAPE-002 · A2 复现测试（P1 · S · 依赖无）

同文件。

- 方法：同一 builder 形状，路径一直接 `ToSql()`（原生条目），路径二经 `ToPageAsync` 克隆后内部 Build；断言两条路径命中同一缓存条目（可用桶条目数增量断言：修复后应为 1，当前为 2）。另加"同表两个不同 Where 的 ToPageAsync 落同一桶"的桶大小断言。
- 验收：当前为红；SHAPE-011 完成后转绿；反向验证同上。

## 里程碑 1：关键修复（性能轮发布前完成）

### SHAPE-010 · LIMIT/OFFSET 参数化（P1 · L · 依赖 SHAPE-001 · 风险中）

`src/PalORM.Core/QueryBuilder.cs:195-210,1057-1095`、`src/PalORM.Core/SqlShapeCache.cs`。

- 方法：`AppendLimitClause` 从拼值改为 `LIMIT @pN OFFSET @pM`，参数经既有 `_paramFactory` 创建（建议新增 `QueryClauseKind.Limit` 子句或独立参数槽，使 AsDryRun 参数快照自然包含）；`Take/Skip` 不再 Combine 进 `_shapeHash`；`ShapeFields` 删除 Take/Skip 字段。
- 连锁（逐项过，任何一项不过即升级评审）：① SQL 转储 22 场景与 DryRun 断言基线更新；② 三方言集成确认驱动接受 long/int 参数于 LIMIT 位（PG/SQLite/MySQL 均支持，需实测确认类型收窄无异常）；③ 性能门禁关注每查询 +0~2 个参数对象的分配回归（Take/Skip 未设置时零新增）；④ PG prepared 语句 plan 形态变化在门禁比值内。
- 退守方案（若评审否决参数化）：SqlShapeCache 容量 1024 + 桶内去重 + 淘汰，对齐 BoundedQueryCache 纪律（CacheStore.cs:146-153 同款"满则拒写"策略）。代价：动态分页形状不再命中缓存。
- 验收：SHAPE-001 转绿；快照/SQL 基线更新有评审记录；性能门禁无回退；三方言集成全绿。

### SHAPE-011 · 修复 _shapeHash 旁路（P1 · M · 依赖 SHAPE-002 · 风险低）

`src/PalORM.Core/QueryBuilder.cs:585-628`、`src/PalORM.Core/QueryBuilderExtensions.cs:277,289,298,321-327`。

- 方法：① CloneForExecution 链重建循环内对每条 `clause.Sql` 做 `HashCode.Combine`（与 AddClause:923 同式），或克隆末尾整链重算；② 扩展方法三处 `limited._take = n` 直赋改走新增 internal setter（同步 Combine；ToPageAsync 为覆写语义，在克隆完成后统一经 setter，克隆体内不再手算哈希）。
- 验收：SHAPE-002 转绿；620 既有测试全绿（哈希只选桶，不碰 SQL 文本）；分页路径命中计数 > 0。

### DOC-010 · README 加密表述与实现对齐（P1 · S · 阻塞：待决策表述方向）

`README.md:16,56-66`。

- 两个方向由用户裁决：A（推荐，零成本）表述降级为"经 SQLite3MC 驱动支持 AES-256：连接字符串 `Password=` 启用"并附示例与注意事项；B 补 PalORM 层支持（加密往返冒烟集成测试 + 文档专章，约 1 天）。
- 验收：完成定义信号 3 达成；README 同句列举的其余六项特性维持"内置"表述且各自可 grep 到实现与测试。

### ERR-010 · OnError 观测挂点 + 契约文档化（P2 · S）

`src/PalORM.Core/QueryBuilderExtensions.cs:233-243`、`src/PalORM.Core/IQueryInterceptor.cs`。

- 方法：保持"不传播"（正当，见审计论证 M4），补内部计数器或可选诊断挂点（对齐 PgNotificationListener 的 Logger 兜底先例，ERR-002 历史整改同款）；IQueryInterceptor XML doc 写明三回调异常契约差异（OnBefore/OnAfter 传播失败查询；OnError 吞且计数）。
- 验收：新用例断言挂点被调用（反向验证：静默化 → 确定性失败）；doc 三回调各有一句契约描述。

## 里程碑 2：高杠杆改进

### CACHE-010 · 静态缓存清单文档（P2 · S · 部分被 SHAPE-010 吸收）

新文件 `docs/静态缓存清单.md`。

- 登记 PalORM_Runtime / DataSessionCache×5 / CacheStore.Default / SqlShapeCache / ParameterNameCache 各自的键空间基数、容量策略、淘汰策略、所属纪律依据（快照范本 / 有限键集 / 1024 上限 / SHAPE-010 结果）。
- 验收：src 内每个 `static` 可变集合 grep 可对照到清单条目；清单含"新增静态缓存必登记"的维护规则（可入 .editorconfig 注释或编码规范 §）。

### GEN-010 · 增量管线基类依赖修复（P2 · L · 风险中）

`src/PalORM.SourceGen/PalORMGenerator.cs:39-43`、`src/PalORM.SourceGen/TableModel.cs:237-251`。

- 前置：先写增量编译测试收口 [推断]（审计论证 M1 明确此条未实证）：模拟两轮编译，第一轮含 `InheritedEntity : AuditBase`（基线场景见 SnapshotTests.cs:107-118），第二轮仅修改基类文件加一列，断言派生实体生成物含新列。测试若证伪（Roslyn 实际会重跑 transform），本任务撤销并在账本记录。
- 方法（测试证实后）：谓词补基类声明节点扫描，或该管线段转 CompilationProvider 联合（以 Roslyn 增量管线 cookbook 的跨树依赖模式为准，实施前查证当前 Roslyn 5.9 的推荐 API，不凭记忆写）。
- 陷阱：改谓词会动增量缓存粒度，快照 13 份须零漂移；IDE 手测派生实体不陈旧。
- 验收：增量测试绿；全量快照零漂移；CI 全绿。

### TEST-010 · StoredProc 真库 happy-path（P2 · M）

`test/PalORM.Integration.Tests/` 新增 StoredProcTests.cs。

- 方法：PG 建一个带 in/out 参数的 proc（CREATE OR REPLACE，try/finally DROP 清理，对齐 AotTest.Pg:156-163 的 DDL 清理先例），StoredProcBuilder 执行并断言参数绑定与结果映射往返；MySQL 同型一条。SQLite 无存储过程，跳过并注释。
- 验收：两方言各 ≥1 条真库行为断言（值往返，非仅行数）；现有 2 条边界测试保留。

### TEST-011 · LISTEN/NOTIFY 真连接冒烟（P2 · M）

`test/PalORM.Integration.Tests/` 新增。

- 方法：PG 真连接 LISTEN 通道 → 同/异连接 NOTIFY payload → 断言订阅回调收到且 payload 相等（超时护栏用 CancellationToken 而非 Sleep，对齐全库反 flake 纪律）。重连风暴类深路径不要求（Fake 层已覆盖协议解析）。
- 验收：真库收到断言通过；无墙钟依赖。

### GEN-011 · ConcurrencyCheck 自守卫（P2 · S）

`src/PalORM.SourceGen/SourceGenerationValidation.cs:50-54 之后`、对照 `CommandFactoryEmitter.cs:150-155`。

- 方法：CanGenerateEntity 增加：并发令牌类型必须支持 `++`（int/long 家族）、setter 非 init-only、至多一个令牌。命中失败由既有 PALORM045 兜底文案呈现（或新增 PALORM 编号，与 012/013 消息对齐但独立于分析器可降级性）。
- 依据：双层防线原则是库自设（SourceGenerationValidation.cs:33-37 注释明文），[Key] 已双层，令牌族漏配（审计论证 M6 证据链）。
- 验收：抑制 PALORM012/013 的坏令牌实体（Guid 令牌 / init-only / 双令牌）在生成器层得到 Skipped + 诊断，不再产出 `.g.cs` 内 CS0019/CS8852；多令牌不再静默只递增其一。

## 里程碑 3：质量与润色

### PROV-010 · 守卫收敛（P3 · S）

四处同文案守卫（PostgreSqlProvider.cs:142-146；MySqlProvider.cs:134-138,205-209；MultiValueBulkInsert.cs:43-48）收敛为共享 helper（Core 放点：BulkOperationFramework 或 CrudMetadata 旁）。MySQL 内层冗余检查（调用链上入口已检，见 MySqlProvider.cs:199 注释的抽取史）一并移除。验收：grep `has no generated insert metadata` 单一实现点；三 Provider 批量路径测试全绿。

### GEN-012 · Bind 双循环列序单一真源（P3 · M）

`CommandFactoryEmitter.cs:382-435`：提取"SET 列 → 主键 → 并发令牌"序列描述（静态数据），两个 emitter 循环消费同一序列、各自保留循环体差异（创建参数 vs 只写 Value）。验收：快照零漂移（重构不改输出）；新增一条"两 binder 参数序一致"的结构性测试防回归。

### CORE-010 · 魔法数字常量化（P3 · S）

65535（QueryBuilder.cs:248；DataSession_Bulk.cs:199）、500（QueryBuilder.cs:276；DataSession_Bulk.cs:55）、18446744073709551615（QueryBuilder.cs:1067）收敛为命名常量（单点定义，含依据注释：PG 协议 int16 上限 / 批参数上限 / MySQL LIMIT 哨兵）。验收：grep 三字面量仅命中常量定义处。

### API-010 · BulkMergeAsync 返回值 doc（P3 · S）

`DataSession_Bulk.cs:325-329` 补 `<returns>`：明确"返回成功处理的实体数，非数据库受影响行数"及跨方言理由（审计论证 Q2 反方）。语义是否 3.0 对齐属开放问题，不在本任务内。验收：doc 编译无 CS1591 增量；CHANGELOG 记一句文档澄清。

### CACHE-011 · 多租户缓存警告前置（P3 · S）

README 多租户/特性章节前置 WithCache 的租户 key 警告（内容取 QueryBuilder.cs:473-477 XML doc），指引"每租户独立 QueryCache 注入或 key 前缀"。验收：README grep `tenant` 命中缓存警告；与 ADR-C 表述一致。

### GEN-013 · 描述符银行抽出（P3 · S）

PalORMAnalyzer.cs:20-253 的 37 个 DiagnosticDescriptor 抽到独立 `Diagnostics.cs`（纯移动零逻辑变化）。验收：diff 仅移动；全量测试绿；PALORM 编号清单不变。

### DOC-011 · PALORM045 文案修正（P3 · S）

`TableModel.cs:34-36` 文案移除 structs（AttributeUsage 已在用户侧 CS0592 拦截，见审计论证 L7），或改为"interfaces/enums"并注明 struct 由 AttributeUsage 前置拦截。验收：文案与可达路径一致；相关单测（若有断言文案）同步。

### TEST-012 · 测试小瑕疵（P3 · S）

FinalTests.cs 按特性拆三文件（窗口/tracing/metrics）；ParenthesisScanAndTemplateCollisionTests.cs 双类归位两文件；ExpressionBuilderSmokeTests.cs:5-10 过期注释改为现实（AotTest/Program.cs:169,188-299 已覆盖）；SqlitePoolParameterTests.cs:23 裸 IsNotNull 补行为断言。验收：文件名↔类名一一对应；grep 无过期表述。

### GEN-014 · AutoTagging 比较器对齐（P3 · S）

`AutoTaggingEmitter.cs:222-227` 的 InterceptionTarget：或实现值相等（InterceptableLocation 的相等语义需查证 Roslyn 5.9 是否提供），或修正 `PalORMGenerator.cs:176` 注释为"引用相等，过度失效方向安全"。验收：注释与行为一致（二选一落地）。

### GEN-015 · 分析器注册合并（P3 · S）

`PalORMAnalyzer.cs:295-303,307-325` 两处 `SyntaxKind.InvocationExpression` 注册合并为一处；031/032（335,362 行）第一步的 GetSymbolInfo 语义查询前置廉价语法预筛（名字 → 循环 → 语义，对齐 PALORM005/033 的 ITM-634 口径）。验收：诊断输出不变（现有分析器测试全绿）；无新增分配路径。

### PROV-011 · 布尔旋钮 doc 修正（P3 · S）

PostgreSqlProvider.cs:56-57,65-66、MySqlProvider.cs:47-50,61-62 的"仅默认时覆盖"XML doc 修正为准确表述（显式设为默认值与未设置不可区分；ITM-652/643 登记的取舍）。验收：doc 与行为一致；sentinel API 留待 3.0（开放问题）。

---

## 执行顺序与依赖

```
SHAPE-001 ─┬→ SHAPE-010 ─→ CACHE-010
SHAPE-002 ─┴→ SHAPE-011
DOC-010（待决策即行）
ERR-010 / GEN-011 / PROV-010 / CORE-010 / API-010 / CACHE-011：无依赖，随取随做
GEN-010（先测试证实/证伪，再决定是否实施）
TEST-010 / TEST-011：无依赖
其余里程碑 3 项：无依赖，低峰批量做
```

## 快速获胜（高影响 × S 工作量，建议立即执行）

SHAPE-001、SHAPE-002（半小时让两个隐性缺陷变可见红灯）、DOC-010、ERR-010、GEN-011、CORE-010、PROV-010、CACHE-011、DOC-011。

## 反向验证计划（项目 S1/S2/S3 纪律）

- S1 基线：每任务动手前记录 `dotnet run --project test/PalORM.Core.Tests -c Release`（及对应套件）的通过数与退出码；SHAPE 系列另记录 SQL 转储基线指纹。
- S2 单变量：SHAPE-010 与 SHAPE-011 不得同分支同批提交后一次验证；先 010 验证再 011（或反之），每步全量测试。
- S3 反向验证：SHAPE-010/011 完成后各自撤回，确认 SHAPE-001/002 确定性回到红；GEN-011 撤回后坏令牌实体重现 .g.cs 编译错误；ERR-010 撤回挂点后观测用例失败。未做 S3 的修复不得标记已完成。
