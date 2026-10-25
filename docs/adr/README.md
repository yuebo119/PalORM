# ADR 索引

> 评审系统「评估级」发现的架构决策记录。状态流转：提议（Proposed）→ 已批准（Accepted）→ 已实施（Implemented）；被后续决策修订的条目保留原文存档并在状态行注明。
> **同步纪律**：新建 ADR 必须在同一次提交内把条目加入本索引，编号顺延、不复用、不跳号。
> **编号沿革**：2026-09-02 索引重建时发现 ADR-G 曾被两篇决策共用——ADR-G 现专属 MySQL
> AllowLoadLocalInfile 安全策略（代码注释引用面在 src），byte[] 二进制列支持改号为
> ADR-K（引用面仅 docs，已同步更新）。

| # | 标题 | 状态 | 来源 |
|---|------|------|------|
| [ADR-A](ADR-A-queryasync-列序契约.md) | QueryAsync 原生 SQL 列序契约 | 已实施 | 2026-07-17 评审 ITM-117/150（GEN-06） |
| [ADR-B](ADR-B-index-fk-迁移支持范围.md) | Index/FK 迁移 DDL 支持范围 | 已实施 | 2026-07-17 评审 ITM-104/151（GEN-03） |
| [ADR-C](ADR-C-cachestore-全局状态.md) | CacheStore 进程级静态缓存与"零全局可变状态"冲突 | 已实施 | 2026-07-17 评审 ITM-115/152（ARCH-07/CONC-03） |
| [ADR-D](ADR-D-多程序集实体布局.md) | 多程序集实体布局的官方支持范围 | 已裁决 D3（降级告警，2026-09-02 追认实施） | 2026-07-18 评审 ITM-420 |
| [ADR-E](ADR-E-dbdatasource-单例化取舍.md) | DbDataSource 单例化取舍 | 已批准 E1（不做） | v5.0-roadmap 阶段 4.1，2026-07-25 用户裁决 |
| [ADR-F](ADR-F-auto-tagging-interceptor.md) | Auto Tagging Interceptor | 已批准 | v5.1 Auto Tagging 特性，2026-07-28 实施完成 |
| [ADR-G](ADR-G-mysql-allowloadlocalinfile-安全策略.md) | MySQL AllowLoadLocalInfile 安全策略 | 已裁决 | 2026-08-15 评审 ITM-612/EVAL-1 |
| [ADR-H](ADR-H-irowfactory-接口处置.md) | IRowFactory 接口处置 | 已裁决 | 2026-08-15 评审 ITM-640/EVAL-2 |
| [ADR-I](ADR-I-legacy-commandsqls-移除窗口.md) | legacy CommandSqls 双份生成的移除窗口 | 已修订（2026-09-02 提前实施） | 2026-08-15 评审 ITM-640/EVAL-3 |
| [ADR-J](ADR-J-legacy-createtablesql-双轨收敛.md) | legacy CreateTableSql 双轨收敛 | 已裁决并实施 | 2026-09-02 架构评审第二批（同 ADR-I 模板） |
| [ADR-K](ADR-K-byte[]-二进制列支持.md) | byte[] 二进制列原生支持（原 ADR-G） | 已实施 | 2026-08-21/22，消费方 payload 诉求 + 驱动层 PoC |
| [ADR-L](ADR-L-多租户缓存结构性隔离.md) | 多租户缓存结构性隔离 | 已采纳 | 2026-09-19（取代 ADR-C 部分裁决） |
| [ADR-M](ADR-M-弃用清单与移除窗口.md) | 弃用清单与移除窗口 | 已修订 | 2026-09-28 v6.0 需求定稿裁决 |
| [ADR-N](ADR-N-v6.0-破坏性变更汇总.md) | v6.0 破坏性变更汇总 | 已采纳 | 2026-09-28 v6.0 需求定稿 R6 裁决 |
| [ADR-O](ADR-O-查询缓存写联动失效.md) | 查询缓存的写联动失效策略 | 已采纳（C1 维持 + C3 落地） | 2026-09-30 用户裁决 |
| [ADR-P](ADR-P-版本化批量更新冲突报告.md) | 版本化批量更新的单语句路由与冲突报告 | 已采纳（计数级 v1，行级诊断留 v2） | 2026-10-20 用户裁决六项推荐 |
| [ADR-Q](ADR-Q-SQLite行预编译批量.md) | SQLite 行预编译批量形态（A+B+E） | 已实施 | 2026-10-25 探针 A/B + 用户裁决 |
| [ADR-R](ADR-R-SQLite批删json-each.md) | SQLite 批量删除的 json_each 键集合形态 | 已采纳 | 2026-10-25 用户裁决（DeleteProbe 三源互证） |
| [ADR-S](ADR-S-RepoDB第五臂对标形态.md) | CrossBench 第五臂（RepoDB 1.17.0）的对标形态与路径选择 | 已采纳 | 2026-10-26 用户指令 + RepoDB 源码审读 + 同批 A/B |
