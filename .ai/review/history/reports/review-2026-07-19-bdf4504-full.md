# PalORM 全量 Review 报告：bdf4504（review v3.1 · 全量档 · 整改回归验证轮）

> 日期：2026-07-19 · 基线：`bdf4504`（dev，工作树干净）· 前序基线 8f289f7（45 项整改）
> 方法：engine v2.2 全量档——review-scope 分 4 片按行数均衡（各 ~2150 行），4 个子代理各领一片地毯逐行（七流问题卡+误判库速版+整改热点复查），主线程做跨片不变式 + 探针裁决 + 定稿门三问。
> 主题：**整改回归验证**——上一轮 45 项整改改动 21 文件 489 行，本轮确认整改是否引入新缺陷/留后门。
> 结论：**REQUEST_CHANGES** — 1 P1（整改回归）+ 3 P2 + 5 P3。8607 行 43 文件逐行覆盖 100%（账本见段 1）；6 项探针实证；1 项二次确认误报。**45 项整改热点全部复查，无一修错，仅 ITM-509/513 修复方式引入连带回归。**

---

## 段 1：范围与覆盖度账本

**范围**：src/ 全部 43 文件 8607 行，四片逐行 100%（片1 2150 / 片2 1952 / 片3 2170 / 片4 2155 行，各片账本已逐文件 wc 核对，全部勾销）。
**盲区**：并发 6 项静态推演未压测；PgNotificationListener 未故障注入；ITM-512 已用 DataAnnotations 混挂 build 实证（非静态推断）。

## 段 2：评审基线
```
提交 bdf4504 · 工作树干净 · SDK 11.0.100-preview.6.26359.118
防线全绿：gate 28/28 · Core 150/150 · SourceGen 89/89 · Integration 154/154（真库）
```

## 段 3：发现清单 + 定稿门对照

### P1（整改回归，本迭代必修）

| ID | 发现 | 位置 | 可信度 | 信息源 | 已对照模式 |
|:--:|------|------|:--:|--------|:--:|
| ITM-546 | **ITM-509 修复过度拦截，误拒合法 SQL 字面量**：字面 `@p<n>` 检测是纯文本扫描，不理解 SQL 字符串字面量——`$"WHERE email = 'a@p1.com'"`、`LIKE '%@p2%'` 等引号内含 `@p<数字>` 的合法 SQL 被抛 FormatException。这些 SQL 此前合法可用（provider 不把引号内 `@` 当参数标记），整改后被误拒 | FormattableSqlFormatter.cs:38-45 | ✅ | 探针实证（邮箱+LIKE 双误拒） | 已排除 2 |

### P2

| ID | 发现 | 位置 |
|:--:|------|------|
| ITM-547 | **ITM-513 拦截器接口文档未同步**：IQueryInterceptor XML 注释仍称"UPDATE/QueryMultiple 不经拦截器"，但 ITM-513 已补齐 ExecuteNonQueryAsync 三段式 + QueryMultipleAsync OnBefore——文档与行为矛盾，实现方据旧文档判断会重复审计或漏观察 | IQueryInterceptor.cs:6-8 |
| ITM-548 | **ITM-513 QueryMultipleAsync 的 OnBefore 无配对 OnAfter/OnError**：对 begin/end 配对建模的拦截器（OnBefore 开 span、OnAfter 关）造成资源泄漏——OnBefore 触发后 GridReader 读取阶段的成功/失败永不回调。与 SELECT/UPDATE 三段式不对称 | QueryBuilderExtensions.cs:222-227 |
| ITM-549 | **[Column] schema 参数静默失效**：Length/Precision/Scale/TypeName/DefaultValue 注解 TableModel 全传 null 不读，`[Column(Precision=10,Scale=2)]`/`[DefaultValue]` 无效（decimal 恒 DECIMAL(18,6)）。既存缺陷，ITM-518 精度硬编码掩盖了用户可配精度的缺失。PALORM017 已对部分发告警 | TableModel.cs:117 |

### P3

| ID | 发现 | 位置 |
|:--:|------|------|
| ITM-550 | RowFactoryEmitter char 形态不对称：HasCharColumn 认 `char`/`global::System.Char`，switch 剥 global:: 后认 `char`/`System.Char`——当前基元 char 恒为关键字形态两处都命中（探针证实编译往返正常），但若未来某路径产 `System.Char` 会漏生成 ReadChar → CS0103。防御缺口（当前不可达） | RowFactoryEmitter.cs:63 vs 140 |
| ITM-551 | ThenInclude<TGC,TParent> 的 parentTable 未做 _cteName 重映射（ITM-515 只修了 Include）——With(CTE)+ThenInclude 且 TParent==根 T 时 ON 引用不在 FROM 的表名。边界/误用面 | QueryBuilder.cs:306 |
| ITM-552 | UPDATE setColumns 谓词在 BuildUpdateSql 与 GenerateBindUpdateBody 两处内联复制（INSERT 走单一 IsInsertable）——当前一致，参数序漂移温床 | CommandFactoryEmitter.cs:183,252 |
| ITM-553 | StoredProc 枚举存储 StoreAs（AsInt32/AsString）在 FromContext 未被读取——enum 列恒 TEXT，公开注解静默无效 | TableModel.cs:184 |
| ITM-554 | ComputedAttribute 用裸串 `ToDisplayString()=="PalORM.ComputedAttribute"` 比对，未走 ITM-512 统一的 IsPalORMAttribute——命名空间安全但破坏一致性 | TableModel.cs:76 |

## 段 4：优先级判定

| ID | 危害 | 复杂度 | 优先级 | 理由 |
|:--:|:--:|:--:|:--:|------|
| ITM-546 | 中 | 易 | P1 | 破坏合法用例（含 @p 的邮箱/LIKE），整改自引入；修法：只在引号外检测，或改为宽松警告，或移除该检测（@pN 冲突极罕见且 provider 层本会报错） |
| ITM-547/548 | 中 | 易 | P2 | 文档矛盾 + 拦截器语义不对称；同 ITM-513 连带 |
| ITM-549 | 中 | 中 | P2 | 用户可配精度缺失，既存 |

## 段 5：方法论自省

**回归验证轮的问题卡增益**：本轮问题卡额外挂了"整改热点复查"维度，四片各自逐 ITM 追踪修复正确性——45 项热点全部给出"复查:正确"或具体 REG。这直接抓住了 ITM-509/513 的连带回归（修复本身对，但拦截面/文档有副作用）。ITM-512（本轮最重，命名空间收紧）用 DataAnnotations 混挂 build 实证（误判库模式4执行），确认三端对称零误判。
**探针**：6 项——ITM-502×401 三特性叠加不变式、ITM-517 校验边界、char 编译往返、ITM-509 误拒双场景、ITM-512 EF 混挂、Builder 超时统一性核实。
**定稿门三问**：ITM-546 对照误判库（非模式2，真误拒）+ 反证搜索（provider 是否本就接受？是，引号内 @ 非参数标记）+ 可复现（邮箱/LIKE 触发路径明确）。

## 段 6：跨报告一致性

- 前序 `review-2026-07-19-8f289f7-full.md`（45 项整改）。**零复发**：ITM-401/402/403/404/405/501/502/503/506/507 修复经交叉验证仍在位。
- **整改质量证据**：45 项热点无一修错方向——ITM-501（22 调用点全覆盖 + Builder 侧同一向上取整）、ITM-505（精确类型匹配替代 EndsWith）、ITM-506/507（熔断逐路径追踪正确）、ITM-512（三端对称 build 实证）均确认正确。
- **ITM-529 二次确认误报**：片4 独立复核 RegistryEmitter 位置写入 Sqlite→PG→MySql 与 Get 具名 switch 读取严格对应，CreateTable/CreateIndex 同型一致——维持上轮误报结论。
- **连带回归识别**：ITM-546（←ITM-509）、ITM-547/548（←ITM-513）——修复正确但引入的副作用面，计入"整改连带"，非新根因类。

## 段 7：建议

**P1**：ITM-546 FormattableSqlFormatter 的 `@p<n>` 检测应仅在 SQL 字符串字面量外生效（跟踪引号状态），或降级为只在真正生成同号占位符时才冲突检测，或直接移除（`@pN` 手写冲突极罕见，且 baseIndex>0 时生成号不从 0 起，实际碰撞概率极低）。**推荐移除该检测**——它拦截的是理论问题，却破坏了真实合法用例。
**P2**：ITM-547 更新 IQueryInterceptor 文档；ITM-548 评估 QueryMultiple 是否经 GridReader.DisposeAsync 回调 OnAfter/OnError；ITM-549 [Column] schema 参数接入或 PALORM017 扩告警。
**下沉**：ITM-546→FormattableSqlFormatter 单测（邮箱/LIKE 不抛）；ITM-550→HasCharColumn 补 `System.Char`；ITM-512→AnalyzerDiagnosticsTests 补 DataAnnotations 混挂快照。

## 段 8：质量指标

| 指标 | 本轮值 |
|------|--------|
| 发现数 | P0:0 · P1:1 · P2:3 · P3:5 |
| 探针数 | 运行探针 6（不变式/校验/char/误拒/EF混挂/超时统一） |
| 逃逸 | 0 |
| 复发 | 0 |
| **证伪** | **1**（ITM-529 二次确认误报）；另 REG-1 Builder 超时、REG-2 char 形态 两疑点探针后降级 P3/证伪 |

**局限**：并发 6 项静态未压测；ITM-548 拦截器泄漏未写实际 begin/end 拦截器压测；ITM-549/553 的注解失效为既存非本轮引入。
