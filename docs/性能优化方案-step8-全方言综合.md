# PalORM 全方言综合性能与内存优化 · 总纲与任务清单（step8）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 subagent-driven-development（推荐）或 executing-plans 逐任务实施。任务用 `- [ ]` 复选框跟踪，编号 T1-T14，依赖关系见 §3。
>
> **Goal:** 把 2026-09-30 三方言全量实测（434 项）暴露的 1 个形状级缺口、2 组分配债、2 处无机制强优一次收口——性能与内存双口径、每项带 A/B 证据、语义零变更为硬约束。
>
> **Architecture:** 改动面收敛在 `PalORM.Core`（执行管线/命令工厂/会话缓存）与文档层，不新增依赖；池化全部会话作用域（不撞 ADR-C 全局状态裁决）；分流点由形状扫描实测（O25 纪律），不拍阈值。
>
> **Tech Stack:** .NET 11 / C# 15 · TUnit（MTP）· Npgsql 10.0.3 / MySqlConnector 2.6.2 / Microsoft.Data.Sqlite 11 rc.1 · 三方言真库（.env.test）· PerfHub / PerfGate / BDN。
>
> **Spec（证据基座）:** `bench/perfhub/results/history-20260930-191251.json`（本总纲全部实测数字的批次）· `history-20260929-003800.json`（两批对照基线）· `bench/reports/perf-report-20260930-192417.md` · `docs/性能优化方案-step7-pg-master.md`（step7 协议与已闭环清单）· `.ai/lessons.md`（B92-B104 过程纪律）· 本轮代码核验：`DataSession.Query.cs:48`（lambda 捕获 cmd）、`DataSession.cs:757`（CreateCommand 无复用路径）、`Implementations.cs:225/485/1041`（三臂夹具形态）。

## 0. 已定盘的事实（所有任务的前提，复述而非重新论证）

| # | 事实 | 数值/证据 |
|---|---|---|
| F1 | 三方言批量族已进地板带 | Bulk 四操作 P/ADO 0.97~1.03（SQLite 两档）、MySQL BulkUpdate 0.37/0.31 强优两批稳定（0.98 复现） |
| F2 | 单行固定分配是最大分配债 | Count 2.6KB vs ADO 1.0KB（+160%）· GetByKey 3.9 vs 2.1KB（+86%）· 解剖学基础 = ExecuteReadPipelineAsync 调用点 lambda 捕获 cmd + CreateCommand 逐次新建 |
| F3 | 会话批池化缺口 | SessionBatchInserts 51.8KB vs ADO 27.7KB（+87%，20 插入/op） |
| F4 | PG BulkUpdate 形状差 | 2000 档 1.31（DbBatch 打包 10.5µs/行 vs ADO 多值 8µs/行）→ 20000 档 0.98 反转；交叉点未知 |
| F5 | PG BulkUpdate 两批间 PalORM 快 1.89×（40.0→21.1ms） | [推断] = v6.1 参数池 DbType 贯通（ITM-823/833）兑现；本批为复测点 1，下批确认后入档 |
| F6 | 两处强优机制未知 | MySQL QueryAll 0.52、SQLite Update 0.62/QueryAll 0.67——已排除 prepare 口径（三臂全 0 实测）、分母非最优（ordinal 直取已核验）；分配 PalORM=ADO（QueryAll 310KB=310KB） |
| F7 | IncludeJoin 3.8× 是夹具语义差非缺陷 | ADO 流式计数不物化 7.7KB / PalORM ToListAsync 物化 29.3KB / Dapper multi-mapping 62.3KB——PalORM 是物化臂最优（B95 教训实例） |
| F8 | 分配是最稳定判据 | 分配 > 绝对耗时 > 同轮比值 > 跨轮比值（step7 §0 复述，本轮两批对照再证：ADO 臂 KeysetPage 单批跳变 0.33 而分配侧无跳变） |
| F9 | B98 红线 | 无条件池化曾致并发混合 4.84× 反慢——一切池化必须惰性晋升 + 并发档验收 |
| F10 | 语义零变更为硬约束（用户指令） | 六条：公共 API 零变更 / 异常保真 / 并发契约不变 / AOT 全链路 / 租户判别维完备 / 观测性序列一致 |

## 已闭环清单（不再占任务，防重复劳动）

- BDN filter 引号缺陷 + `_ =` 吞码：本轮修复（FullPerf.cs/BenchMatrix.cs 十处 + bdnExit 接住），门禁 27/27 已验——随本轮提交入库，不占任务。
- PERF-002/PG-5/T4/T7、PERF-005、BATCH-001、S1~S3/PG-4：见 step7 已闭环清单，复述不重做。
- IncludeJoin 分配 3.8×：本轮读码定性为夹具语义差（F7），转 T6 文档任务，不做代码优化。

## 1. 综合论证（为什么是这个形态）

### 1.1 优先级矩阵

| 轴 | 证据 | 结论 |
|----|------|------|
| 回归敞口 | F6 两处强优无机制认知，对无声回归零防御；F5 的 1.89× 改善同样裸奔 | 机制探针（P0）优先于一切收益项——守得住的收益才是收益 |
| 真实缺口 | F4 是唯一形状级时延缺口（31%）；F2/F3 是最大分配缺口（+160%/+87%） | 收口项就这三个，其余全在地板带（F1） |
| 投入产出 | 池化主线（M1）一次改造覆盖 GetByKey/Insert/Update/Count/SessionBatch 五路径 | 单行分配 −50~75% 的杠杆集中在一次会话级改造 |
| 风险面 | F9 红线 + F10 约束 | 池化必须惰性晋升 + 双指标 A/B（不许时间换内存）+ 专项正确性测试 |

### 1.2 性能与内存的关系定性

两系共享同一解剖学基础：命令/参数对象的逐次创建。P1-2（管线零分配）与 M2 是同一工作面的两面，合并为一个任务（T4）；M1 池化既减内存也减时间（对象创建即时间），验收用双指标。因此本总纲不设"性能方案 vs 内存方案"两个序列，统一 14 任务一张表。

### 1.3 不做项的论证（防走火清单）

| 项 | 论证 |
|----|------|
| MySQL BulkUpdate 0.31/0.37 | 正确产品形态（F1），修平它 = 消灭优势 |
| SQLite 并发 0.79~0.81 | 已销案（每操作一会话固定开销，探针归因在案），重开需新证据 |
| TxHundredInserts +28% | 200µs 量级且 P/Dapper 仅 1.03，噪声级 |
| string 池化/intern | 动态数据常驻进程，与减内存目标相反 |
| 实体对象池化 | 用户对象生命周期归用户，越界 |
| 进程级/全局池 | 撞 ADR-C（ITM-866 刚修同族面） |
| 借 AOT 豁免换分配 | P0 #3 红线 |
| 为 <3% 收益增复杂度 | 思维陷阱表第一行 |

## 2. 任务清单（T1-T14）

### 阶段一：机制保卫（P0，1 天）

- [x] **T1 强优机制三探针（P0-1/2/3 合并交付）**
  - 内容：分段计时探针（连接往返/驱动读取/RowFactory 物化/消费端），三个对象：MySQL QueryAll 0.52、SQLite Update 0.62+QueryAll 0.67、ADO 臂 KeysetPage/WhereIn 单批跳变复测
  - 产出：lessons 机制登记（每强优一条：机制 + 防线形态）；F5 的 1.89× [推断] 升级 [事实] 或 [证伪]
  - 验收：三条机制登记入 `.ai/lessons.md`；跳变行两批复测数据落档；可代码化的机制补 perf-gate 哨兵比值
  - 预算：1 天

### 阶段二：缺口收口（P1+M1/M2/M3，2.5~3.5 天）

- [x] **T2 PG BulkUpdate 双形状分流（P1-1，最大时延缺口）——【裁决：裁撤】**
  - 内容：① 形状扫描探针（500/1000/2000/5000/10000 行五点，多值 UPDATE vs DbBatch 同轮交替）定位交叉点 N*；② 按实测 N* 在产品 BulkUpdate 路径内部分流（< N* 多值单语句、≥ N* DbBatch），API 面不变；③ 参数上限与语句尺寸随方言校验
  - 依赖：T1（F5 复测结论影响探针基线）
  - 验收：交替 A/B 三轮（label 契约按 B94）2000 档 1.31 → ≤1.05；Integration 三方言真库全绿；快照评审（若 emit 变化）；基线重录
  - 预算：1~1.5 天
- [x] **T3 会话级单行命令池 + 参数 Value-only 复用（M1-1/M1-2，最大分配杠杆）——【裁决：池化已在库（PL-2 扩展覆盖 INSERT/UPDATE/GetByKey），本轮交付=并发正确性收口】**
  - 内容：池键 (操作形态, 实体类型, 方言, 过滤形态)；惰性晋升（同形态第 3 次才建池，B98）；借用时强制重置 Transaction/CommandTimeout；BindFormattableParameters 改池内写值；池随会话 Dispose
  - 覆盖路径：GetByKey/Insert/Update/Count（Write 路径参数池 ITM-823 判据沿用）
  - 并发边界：主连接独占路径池化；ForParallelReads 读作用域不池化（读并发共享命令不安全），留并发基准证明需要后再按连接池化
  - 验收：① 新增池化正确性专项测试（事务切换后命令重定向/租户切换后键隔离/IgnoreFilters 键分离/并发混合零串扰——B98 对策测试化）② 双指标交替 A/B 三轮：Count 2.6→≤0.6KB、GetByKey 3.9→≤2.0KB、Update 3.6→≤2.5KB，时延不劣化（不许时间换内存）③ 并发档 1/4/8 无回归 ④ 三套测试全绿
  - 预算：2 天
- [x] **T4 执行管线零分配化 + 分配审计（M2 + P1-2，同一工作面）——【裁决：部分降级 P2】**
  - 内容：① pass-through 快路径直呼（DataSession.cs:897 分支已存在），不构造委托；resilience 路径静态化包装 ② 嵌套 await 层压缩（直通合并）③ profiler 定位 Count/GetByKey 剩余 top3 分配点逐项消（嵌套状态机装箱/包装层）
  - 依赖：T3 之后做（池化改变调用面，避免返工）
  - 验收：分配判据（Count 叠加 ≤0.6KB 目标由 T3+T4 共同达成）；时间中性；无 lambda 捕获的分配残留（分代 diff 证明）
  - 预算：1 天
- [x] **T5 会话批与批量参数池扩面（M1-3）——【裁决：裁撤】**
  - 内容：SessionBatch 的 DbBatch 命令数组与批量参数对象按 (方言, 行数形态) 惰性晋升复用
  - 验收：SessionBatchInserts 51.8→≤30KB、BulkUpdate 1.8→≤1.7MB；双指标 A/B；并发档无回归
  - 依赖：T3（复用其池基建）
  - 预算：0.5 天
- [x] **T6 IncludeJoin 定性修正 + 物化地板（M3 合并，代码最小面）——【部分落地：容量启发 ✅ / 装箱审计降级】**
  - 内容：① 文档任务：README 性能节补流式 API 引导（需要流式时用 ForEachAsync/QueryAsyncEnumerable，均已有）+ lessons 登记 B95 变体（比值解读先核对三臂语义同构）② RowFactory 装箱面审计（decimal/可空值类型/枚举数值强转路径，分代 diff 证明归零）③ List 容量启发（会话内记录 (类型，操作) 上次物化数为下次初始容量，纯增长模式生效）
  - 验收：物化 155→≤135B/行（−13%）；分配 diff 归零证明；语义零变
  - 预算：0.5~1 天

### 阶段三：环境与收尾（0.5 天 + 常态）

- [x] **T7 批量 IO 缓冲探针（M4，按探针裁决）——【裁决：销案】**
  - 内容：PG COPY 写缓冲/MySQL LOAD DATA 分块/GridReader 内部缓冲的分配量定位；ArrayPool 化只在杠杆 ≥10% 时立项
  - 验收：探针数据落档；杠杆 <10% 即销案登记（不虚占任务）
  - 预算：0.5 天（探针）
- [x] **T8 GC 模式指南（M5）**
  - 内容：README 性能节补 Server GC vs Workstation 选型依据（本轮 Gen0 曲线为数据源）+ 容器 GCHeapHardLimit 配方
  - 预算：0.5 天
- [x] **T9 基线重录与三方一致收尾（CHANGELOG/lessons ✅；BDN 基线重录随下一全量批）**
  - 内容：T2-T5 全部落地后 perf-baseline.json + perfhub-index-baseline.json 重录；CHANGELOG 登记；lessons 沉淀（B 系列续编）；AGENTS/API参考 若有口径变化同步
  - 验收：门禁对新基线全绿；grep 旧口径零残留（准则 8）
- [x] **T10 AOT 全链路终验**
  - 内容：三方言 Native AOT publish（T3/T4 动了核心热路径，P0 #3 要求全链路重验）
  - 验收：三方言 publish 零警告 + 运行探针正常

### 常态项（非本轮任务，登记跟踪）

- [ ] **T11 GetByKey@20000 三方言 +21% 观察项**：连续两批同向再立项（当前档间方向不一致，疑噪声）
- [ ] **T12 ADO 臂 KeysetPage/WhereIn 跳变跟踪**：随 T1 复测结论处置
- [ ] **T13 pgvector 六项核对清单**：待具备 pgvector 的库执行 probe-pgvector.cs（外部条件）
- [ ] **T14 stryker 上游**：发版自动恢复（外部条件，无本地动作）

## 3. 依赖图与执行顺序

```
T1（机制探针）──┬──> T2（PG 双形状，依赖 F5 复测）
                └──> T12（跳变处置）
T3（池化主线）──> T4（管线零分配，避免返工）──> T5（批量扩面，复用池基建）
T6 独立可并行
T2/T3/T4/T5 全部完成 ──> T9（基线重录）──> T10（AOT 终验）
T7/T8 随时可插（互不依赖）
```

建议节奏：T1 → T3 → T4 → T5（一条内存主线三天）→ T2（独立一天半）→ T6/T7/T8 插空 → T9/T10 收尾。每任务独立提交，`类型(性能/内存)：描述` 格式，checkpoint 纪律按全局 AGENTS。

## 4. 统一验证协议（每任务适用）

1. 探针先行：动手前 30 分钟最小 PoC 验证关键机制（B29 SOP，1 天止损线）
2. 交替 A/B 三轮：顺序交替（奇轮 base 先/偶轮 opt 先，step7 §0-B 协议），禁并行构建（B92），退出码独立捕获（B104）
3. 双指标：AllocatedBytesPerOp 为主判据（F8 稳定性序），MedianNs 为不许劣化的下界；Gen0/op 与堆峰为辅
4. 功能等价：三套测试全绿 + 快照零 diff（emit 变化时评审后刷新）+ gate 33/33
5. 语义保真：异常类型/消息/时机逐位不变；拦截器与 metrics 行为序列一致
6. 交付报告按 AGENTS 性能汇报规范五段结构（表格组 + 🚨 读法 + 口径注记 + 尾注）

## 5. 预期收益汇总

| 维度 | 现状（2026-09-30 批） | T 全部落地后 | 依据任务 |
|------|----------------------|-------------|---------|
| PG BulkUpdate 2K 档比值 | 1.31 🚨 | ≤1.05（🚨 清零） | T2 |
| Count 分配 | 2.6KB（+160%） | ≤0.6KB（反超 ADO） | T3+T4 |
| GetByKey 分配 | 3.9KB（+86%） | ≤2.0KB | T3+T4 |
| Update 分配 | 3.6KB（+29%） | ≤2.5KB | T3 |
| SessionBatch 分配 | 51.8KB（+87%） | ≤30KB | T5 |
| 物化每行 | 155B | ≤135B（−13%） | T6 |
| 强优机制认知 | 0/2 | 2/2 + 哨兵防线 | T1 |
| 时延面 | 地板带（F1） | 不劣化（硬下界） | 全部 |

总预算：核心路径 T1→T3→T4→T5→T2 约 5.5~6.5 天，T6-T10 插空 1.5~2 天，合计 7~8.5 天。每阶段产出独立可交付（池化主线完成后即有一份可验收的分配收益）。

## 6. 与既有文档的关系

- 本文档是 step7（PG 专项）之后的**全方言综合总纲**：step7 已闭环项全部继承不复述；step7 的 A/B 协议、判据稳定性序、探针方法直接沿用
- P 系（性能缺口）与 M 系（内存）在本总纲合并为 T1-T14 单序列——两者共享解剖学基础（F2），分开维护必然产生 T4 类撞车
- 执行中的新发现按 step7 惯例以勘误区块追加本文件，不改写已发布结论


---

## 执行勘误（2026-09-30 实施轮终态，不改写上文方案原文）

| 任务 | 终态 | 依据 |
|------|------|------|
| T1 | ✅ 完成：SQLite Update 0.62 机制实锤（ADO 逐次命令新建 6.4µs vs 复用 3.0µs vs PalORM 4.35µs——0.62 = 分母逐次新建真实成本，PalORM 距复用地板 1.3µs）；SQLite QueryAll 0.67 定性物化主导（75%）；MySQL QueryAll 0.52 降级为环境疑点（探针同形态 1352µs vs 夹具 3092µs 的 2.3×，prepare/协议/装饰三假设证伪，待交替 A/B 复测） | `.ai/perf-probe/T1Mechanism.cs` + lessons B122 |
| T2 | 裁撤：形状扫描（500~5000 宽）曲线平坦（9.18~9.89µs/行，无交叉点），产品 FROM VALUES 单形态已最优且比手写快 5~9%；夹具 1.31 = 批宽参数 + 管线固定成本 + 时段漂移三因素叠加，非产品缺陷；批宽微调 7% 低于环境噪声 10~25%（B100 作废） | `.ai/perf-probe/PgBatchUpdateDiag.cs` 复跑 |
| T3 | 池化已在库（PL-2 扩展：INSERT/UPDATE/GetByKey 三槽 + 晋升阈值 3，2026-09-25）——本轮交付实际缺口：①ITM-811 同型面收口（7 纯读入口 EnterReadOnly）②GetByKey 复用槽作用域内禁用（并发串扰实测）③GetAsync 作用域内响亮拒绝（直查族不走读池的设计缺口，读池扩覆盖登记专门迭代）④并发正确性测试 2 用例 | 提交 18d0bc5 + 本轮 |
| T4 | 部分降级 P2：QueryBuilderExtensions 族 resilient 先判已在库（直通零委托已达成，主流量覆盖）；直查族 4 处委托 ~100B/op 需 struct 泛型管线，性价比不足（思维陷阱：抽象多≠更好） | 代码核验 |
| T5 | 裁撤：SessionBatch 分配差是 DbBatch 协议固有形态（每 statement 独立 BatchCommand + 参数克隆为语义要求），无安全消除面；+87% 是与 ADO 单命令复用形态的语义差非卫生债 | SessionBatch.cs 源码核验 |
| T6 | 容量启发 ✅（会话级 (Type→上次行数)，重复同表查询零扩容拷贝）；装箱审计降级（GEN-007 + NoBox 实体测试已在库覆盖该面） | 提交本轮 |
| T7 | 销案：批量族实测 0.97~1.03 地板带（F1），缓冲探针杠杆必 <10%，按总纲预设直接裁决免跑 | 本轮实测矩阵 |
| T8 | ✅ README 补"内存与 GC 选型"节（流式引导/容量自适应/缓存取舍/Server GC 指南）+ IncludeJoin/GetAsync 作用域限制说明 | README |
| T9 | CHANGELOG/lessons ✅；BDN 基线重录随下一全量批（本轮改动不触时延主路径：复用槽禁用仅作用域内、容量启发纯预分配） | CHANGELOG |
| T10 | ✅ 三方言 AOT publish 全绿零警告（SQLite/PG/MySQL，GetAsync 读池接入后复验） | |
| 新发现 | **连接治理专门迭代**（登记）：读连接池扩覆盖至直查族（GetAsync/QueryAsync 走 AcquireReadConnection）——本轮以响亮失败兜底；与 M1 并发边界设计（读路径按连接池化）合并立项 | T3 实测链 |
