# 交叉基准基线（3 批窗口中位）

> 数据源：crossbench-20261025-142615.json · crossbench-20261025-144400.json · crossbench-20261025-153523.json
> 四臂：ADO.NET（地板）/ PalORM / Dapper / FreeSql，同批同数据同量具（两遍反序中位）。
> 读表：每格 = 时间 [对 ADO 倍数] / 内存 [对 ADO 倍数]；[1.00] = ±0.5% 持平带；≤0.67 或 ≥1.5 越线加粗；📌 = 越线待判读；⚠地板 = ADO 臂批间散布 >20%；🏆 = 该格最快臂。
> 口径与形态注记见 bench/crossbench/README.md 与 docs/adr/ADR-Q/R。

## 总览

四臂 × 三方言 × 14 操作（CRUD 与批量各两档、事务仅 2000 档）= **69 格**，3 批窗口中位。所有倍数均以 ADO.NET 手写 SQL 为基准 [1.00]。

### CRUD 单行与读（基准 = ADO [1.00]）

- 🥇 GetByKey SQLite：PalORM **0.80**（快 20%），FreeSql **4.57**（慢 357%，全方言最差 4.87）
- QueryAll MySQL 20000 分配：PalORM **0.91**（快 9%），Dapper 1.11（慢 11%）
- Insert SQLite：PalORM 0.99（快 1%），FreeSql **1.73**（慢 73%）

### 批量处理（基准 = ADO [1.00]）

- 🥇 BulkDelete PG 20000：PalORM **0.39**（快 61%），分配 **0.10**（快 90%）
- 🥇 UpsertBatch PG 20000：PalORM **0.57**（快 43%），分配 **0.37**（快 63%）
- BulkInsert SQLite 20000：PalORM 0.92（快 8%），Dapper 1.52（慢 52%），FreeSql **3.55**（慢 255%）
- BulkUpdate 三方言两档：PalORM 0.94~1.08，Dapper 最差 **3.96**（慢 296%）

### 事务（基准 = ADO [1.00]）

- PalORM 全项 0.98~1.16，最快 EnumInserts MySQL **0.98**
- FreeSql 最差 TxRollback SQLite **12.93**，Dapper 最差 TxRollback SQLite **3.51**
- EnumInserts SQLite 分配：PalORM 1.00（持平），FreeSql **8.42**（慢 742%）

### 最快胜场

| 臂 | 最快 / 69 格 |
|---|---:|
| PalORM | **35**（51%） |
| ADO.NET（地板） | **27**（39%） |
| Dapper | **6**（9%） |
| FreeSql | **1**（1%） |

> P/ADO 中位 **0.99**（1.00 = 贴平手写 SQL 地板），全距 0.39~1.16。

## 表一：CRUD 单行与读（三方言，两档）

> 胜场：PalORM 15 · ADO 9 · Dapper 6 · FreeSql 0

### 测试项说明

| 操作 | 测什么 | 总评 |
|---|---|---|
| GetByKey | 主键单行点查 | PalORM 贴地板（SQLite 会话税被 SQL 常量缓存抵消）；FreeSql 全方言最慢（表达式每次求值） |
| GetAllAsync | 全表直查 API（PalORM 专用 GetAllAsync，其余同 QueryAll） | 联网方言四臂接近；SQLite Dapper（反射物化）与 FreeSql（表达式管线）明显偏慢 |
| QueryAll | 全表物化查询（From<T>().ToListAsync / Select.ToList） | 同 GetAllAsync：PalORM ≈ ADO 地板，SQLite 两竞品明显偏慢 |
| Insert | 单行插入（自增主键） | 联网方言 fsync 主导四臂持平；SQLite PalORM 持平、FreeSql 明显偏慢（表达式管线） |
| Update | 单行整行更新 | 联网方言 fsync 主导持平；FreeSql 在 SQLite 与 MySQL 明显偏慢（表达式管线） |

**SQLite**

| 操作 | 档 | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql |
|---|---:|:-:|---|---|---|---|
| GetByKey | 2000 | 🏆 **PalORM** | 7.6 µs [1.00] / 2.1KB [1.00] | **6.0 µs [0.80] / 2.1KB [1.02]** | 9.9 µs [1.30] / 2.8KB [1.34] | 34.7 µs [**4.57**] / 10.2KB [**4.85**] |
| GetAllAsync | 2000 | 🏆 **PalORM** | 972.2 µs [1.00] / 308KB [1.00] | **971.3 µs [1.00] / 294KB [0.95]** | 1.40 ms [1.44] / 513KB [**1.66**] | 1.41 ms [1.45] / 314KB [1.02] |
| QueryAll | 2000 | ADO | **981.9 µs [1.00] / 308KB [1.00]** | 983.1 µs [1.00] / 293KB [0.95] | 1.41 ms [1.43] / 513KB [**1.66**] | 1.42 ms [1.44] / 314KB [1.02] |
| Insert | 2000 | 🏆 **PalORM** | 18.1 µs [1.00] / 2.8KB [1.00] | **17.9 µs [0.99] / 2.7KB [0.97]** | 18.9 µs [1.04] / 3.5KB [1.28] | 31.1 µs [**1.73**] / 5.7KB [**2.06**] |
| Update | 2000 | ADO | **7.5 µs [1.00] / 2.5KB [1.00]** | 7.5 µs [1.00] / 2.9KB [1.15] | 8.2 µs [1.09] / 3.3KB [1.30] | 20.9 µs [**2.81**] / 7.4KB [**2.95**] |
| GetByKey | 20000 | ADO | **6.5 µs [1.00] / 2.1KB [1.00]** | 6.8 µs [1.06] / 2.2KB [1.02] | 7.7 µs [1.19] / 2.8KB [1.33] | 31.4 µs [**4.87**] / 10.4KB [**4.93**] |
| GetAllAsync ⚠地板 | 20000 | ADO | **10.08 ms [1.00] / 3.3MB [1.00]** | 10.11 ms [1.00] / 2.9MB [0.89] | 14.44 ms [1.43] / 5.3MB [**1.61**] | 14.40 ms [1.43] / 3.3MB [1.00] |
| QueryAll | 20000 | 🏆 **PalORM** | 10.14 ms [1.00] / 3.3MB [1.00] | **10.05 ms [0.99] / 2.9MB [0.89]** | 14.25 ms [1.41] / 5.3MB [**1.61**] | 14.66 ms [1.45] / 3.3MB [1.01] |
| Insert | 20000 | 🏆 **PalORM** | 18.8 µs [1.00] / 2.8KB [1.00] | **18.5 µs [0.98] / 2.7KB [0.97]** | 19.2 µs [1.02] / 3.6KB [1.30] | 31.6 µs [**1.68**] / 5.7KB [**2.05**] |
| Update | 20000 | 🏆 **PalORM** | 7.8 µs [1.00] / 2.5KB [1.00] | **7.7 µs [0.99] / 2.9KB [1.15]** | 8.6 µs [1.10] / 3.3KB [1.30] | 23.4 µs [**3.00**] / 7.4KB [**2.95**] |

**MySQL**

| 操作 | 档 | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql |
|---|---:|:-:|---|---|---|---|
| GetByKey | 2000 | 🏆 **PalORM** | 208.2 µs [1.00] / 4.6KB [1.00] | **204.8 µs [0.98] / 4.9KB [1.08]** | 209.0 µs [1.00] / 5.9KB [1.28] | 455.0 µs [**2.19**] / 16.7KB [**3.66**] |
| GetAllAsync | 2000 | 🏆 **PalORM** | 1.23 ms [1.00] / 399KB [1.00] | **1.16 ms [0.95] / 384KB [0.96]** | 1.19 ms [0.97] / 447KB [1.12] | 1.49 ms [1.21] / 407KB [1.02] |
| QueryAll ⚠地板 | 2000 | 🏆 **PalORM** | 1.16 ms [1.00] / 399KB [1.00] | **1.16 ms [1.00] / 384KB [0.96]** | 1.21 ms [1.04] / 447KB [1.12] | 1.48 ms [1.27] / 407KB [1.02] |
| Insert | 2000 | Dapper | 4.27 ms [1.00] / 18.0KB [1.00] | 4.17 ms [0.98] / 17.4KB [0.97] | **4.07 ms [0.95] / 16.9KB [0.94]** | 4.45 ms [1.04] / 23.2KB [1.29] |
| Update ⚠地板 | 2000 | Dapper | 231.2 µs [1.00] / 5.5KB [1.00] | 210.7 µs [0.91] / 5.9KB [1.09] | **204.6 µs [0.88] / 5.2KB [0.96]** | 445.0 µs [**1.92**] / 13.3KB [**2.45**] |
| GetByKey | 20000 | 🏆 **PalORM** | 240.6 µs [1.00] / 4.6KB [1.00] | **224.8 µs [0.93] / 5.0KB [1.07]** | 236.8 µs [0.98] / 5.9KB [1.28] | 521.2 µs [**2.17**] / 17.1KB [**3.67**] |
| GetAllAsync ⚠地板 | 20000 | 🏆 **PalORM** | 10.27 ms [1.00] / 4.0MB [1.00] | **10.11 ms [0.98] / 3.7MB [0.91]** | 10.25 ms [1.00] / 4.5MB [1.11] | 11.53 ms [1.12] / 4.1MB [1.00] |
| QueryAll ⚠地板 | 20000 | 🏆 **PalORM** | 10.01 ms [1.00] / 4.0MB [1.00] | **9.90 ms [0.99] / 3.7MB [0.91]** | 10.14 ms [1.01] / 4.5MB [1.11] | 11.80 ms [1.18] / 4.1MB [1.00] |
| Insert | 20000 | ADO | **4.21 ms [1.00] / 17.2KB [1.00]** | 4.29 ms [1.02] / 17.8KB [1.03] | 4.24 ms [1.01] / 21.8KB [1.27] | 4.44 ms [1.06] / 23.5KB [1.37] |
| Update ⚠地板 | 20000 | ADO | **215.6 µs [1.00] / 5.3KB [1.00]** | 217.3 µs [1.01] / 5.8KB [1.10] | 216.9 µs [1.01] / 5.2KB [1.00] | 440.3 µs [**2.04**] / 13.3KB [**2.53**] |

**PostgreSQL**

| 操作 | 档 | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql |
|---|---:|:-:|---|---|---|---|
| GetByKey | 2000 | 🏆 **PalORM** | 216.8 µs [1.00] / 3.0KB [1.00] | **210.8 µs [0.97] / 3.4KB [1.13]** | 213.3 µs [0.98] / 3.3KB [1.09] | 285.8 µs [1.32] / 12.1KB [**4.00**] |
| GetAllAsync | 2000 | Dapper | 662.1 µs [1.00] / 239KB [1.00] | 672.2 µs [1.02] / 226KB [0.94] | **659.1 µs [1.00] / 442KB [1.85]** | 806.5 µs [1.22] / 244KB [1.02] |
| QueryAll | 2000 | ADO | **657.7 µs [1.00] / 239KB [1.00]** | 674.4 µs [1.03] / 225KB [0.94] | 669.7 µs [1.02] / 442KB [**1.85**] | 773.4 µs [1.18] / 244KB [1.02] |
| Insert | 2000 | ADO | **1.36 ms [1.00] / 7.5KB [1.00]** | 1.37 ms [1.01] / 7.5KB [1.00] | 1.37 ms [1.00] / 7.2KB [0.96] | 1.45 ms [1.07] / 11.1KB [1.48] |
| Update | 2000 | Dapper | 1.40 ms [1.00] / 7.4KB [1.00] | 1.40 ms [1.00] / 7.9KB [1.07] | **1.39 ms [0.99] / 7.2KB [0.97]** | 1.44 ms [1.03] / 13.1KB [**1.77**] |
| GetByKey | 20000 | 🏆 **PalORM** | 213.1 µs [1.00] / 3.0KB [1.00] | **212.0 µs [0.99] / 3.4KB [1.13]** | 222.8 µs [1.05] / 3.3KB [1.10] | 287.9 µs [1.35] / 12.1KB [**4.03**] |
| GetAllAsync ⚠地板 | 20000 | ADO | **5.11 ms [1.00] / 2.5MB [1.00]** | 5.42 ms [1.06] / 2.2MB [0.86] | 5.35 ms [1.05] / 4.5MB [**1.79**] | 5.92 ms [1.16] / 2.5MB [1.00] |
| QueryAll ⚠地板 | 20000 | 🏆 **PalORM** | 5.57 ms [1.00] / 2.5MB [1.00] | **5.38 ms [0.97] / 2.2MB [0.86]** | 5.64 ms [1.01] / 4.5MB [**1.79**] | 5.91 ms [1.06] / 2.5MB [1.00] |
| Insert | 20000 | Dapper | 1.38 ms [1.00] / 7.5KB [1.00] | 1.38 ms [1.00] / 7.5KB [0.99] | **1.36 ms [0.98] / 7.2KB [0.96]** | 1.43 ms [1.04] / 11.0KB [1.47] |
| Update | 20000 | Dapper | 1.42 ms [1.00] / 7.4KB [1.00] | 1.42 ms [1.00] / 7.9KB [1.07] | **1.42 ms [1.00] / 7.1KB [0.96]** | 1.49 ms [1.04] / 13.2KB [**1.78**] |

## 表二：批量处理（三方言，两档）

> 胜场：PalORM 14 · ADO 9 · FreeSql 1 · Dapper 0

### 测试项说明

| 操作 | 测什么 | 总评 |
|---|---|---|
| BulkInsert | 批量插入（SQLite 行预编译 / PG COPY / MySQL BulkCopy） | PalORM 与 ADO 同形态贴平；Dapper/FreeSql 批量路径全方言偏慢，PG/MySQL 大档差距最大 |
| BulkUpdate | 批量更新（SQLite 逐条裹事务 / PG UPDATE FROM VALUES / MySQL CASE WHEN） | PalORM 三方言与 ADO 同形态贴平；Dapper/FreeSql 无集合式封装，明显偏慢 |
| BulkDelete | 批量删除（SQLite json_each 单参数 / PG = ANY 数组 / MySQL IN 分批） | PG 数组形态快于 ADO 的 IN 占位符；SQLite json_each 与 MySQL 分批接近地板 |
| UpsertBatch | 批量 UPSERT（ON CONFLICT / ON DUPLICATE KEY） | PG 数组形态快于 ADO 与其余臂；SQLite 四臂同形态贴平；MySQL Dapper/FreeSql 偏慢 |

**SQLite**

| 操作 | 档 | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql |
|---|---:|:-:|---|---|---|---|
| BulkInsert | 2000 | 🏆 **PalORM** | 2.66 ms [1.00] / 1.7MB [1.00] | **2.59 ms [0.97] / 1.7MB [1.00]** | 4.48 ms [**1.68**] / 4.3MB [**2.48**] | 10.81 ms [**4.07**] / 6.5MB [**3.78**] |
| BulkUpdate | 2000 | ADO | **2.35 ms [1.00] / 1.7MB [1.00]** | 2.41 ms [1.03] / 1.8MB [1.04] | 9.30 ms [**3.96**] / 6.3MB [**3.75**] | 13.63 ms [**5.81**] / 7.0MB [**4.15**] |
| BulkDelete ⚠地板 | 2000 | ADO | **846.5 µs [1.00] / 197KB [1.00]** | 860.2 µs [1.02] / 198KB [1.01] | 22.68 ms [**26.79**] / 1.6MB [**8.13**] | 1.62 ms [**1.92**] / 569KB [**2.89**] |
| UpsertBatch | 2000 | ADO | **2.45 ms [1.00] / 1.7MB [1.00]** | 2.53 ms [1.03] / 1.7MB [1.01] | 4.66 ms [**1.90**] / 4.2MB [**2.50**] | 3.73 ms [**1.52**] / 2.5MB [1.47] |
| BulkInsert | 20000 | 🏆 **PalORM** | 32.62 ms [1.00] / 17.2MB [1.00] | **29.92 ms [0.92] / 17.2MB [1.00]** | 49.63 ms [**1.52**] / 42.5MB [**2.48**] | 115.69 ms [**3.55**] / 66.1MB [**3.85**] |
| BulkUpdate | 20000 | ADO | **26.04 ms [1.00] / 17.0MB [1.00]** | 28.16 ms [1.08] / 17.6MB [1.04] | 99.12 ms [**3.81**] / 63.6MB [**3.74**] | 156.22 ms [**6.00**] / 73.4MB [**4.32**] |
| BulkDelete ⚠地板 | 20000 | 🏆 **PalORM** | 8.80 ms [1.00] / 2.0MB [1.00] | **8.42 ms [0.96] / 2.0MB [1.00]** | 227.92 ms [**25.90**] / 15.6MB [**7.72**] | 17.24 ms [**1.96**] / 6.0MB [**2.94**] |
| UpsertBatch | 20000 | ADO | **26.70 ms [1.00] / 17.0MB [1.00]** | 27.70 ms [1.04] / 17.1MB [1.01] | 50.49 ms [**1.89**] / 42.4MB [**2.49**] | 41.21 ms [**1.54**] / 25.8MB [**1.52**] |

**MySQL**

| 操作 | 档 | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql |
|---|---:|:-:|---|---|---|---|
| BulkInsert | 2000 | 🏆 **PalORM** | 12.47 ms [1.00] / 482KB [1.00] | **12.29 ms [0.99] / 484KB [1.00]** | 27.67 ms [**2.22**] / 6.7MB [**14.16**] | 18.04 ms [1.45] / 3.2MB [**6.87**] |
| BulkUpdate ⚠地板 | 2000 | FreeSql | 11.76 ms [1.00] / 4.6MB [1.00] | 11.09 ms [0.94] / 3.7MB [0.80] | 12.50 ms [1.06] / 6.5MB [1.41] | **11.01 ms [0.94] / 2.1MB [0.45]** |
| BulkDelete | 2000 | 🏆 **PalORM** | 13.13 ms [1.00] / 887KB [1.00] | **12.48 ms [0.95] / 811KB [0.92]** | 12.49 ms [0.95] / 1.3MB [**1.55**] | 18.53 ms [1.41] / 613KB [0.69] |
| UpsertBatch | 2000 | ADO | **8.33 ms [1.00] / 2.9MB [1.00]** | 8.45 ms [1.01] / 2.5MB [0.85] | 11.44 ms [1.37] / 6.6MB [**2.29**] | 9.46 ms [1.14] / 2.5MB [0.86] |
| BulkInsert ⚠地板 | 20000 | ADO | **63.85 ms [1.00] / 4.5MB [1.00]** | 64.45 ms [1.01] / 4.6MB [1.02] | 290.68 ms [**4.55**] / 66.6MB [**14.93**] | 158.37 ms [**2.48**] / 33.4MB [**7.48**] |
| BulkUpdate ⚠地板 | 20000 | 🏆 **PalORM** | 92.48 ms [1.00] / 14.6MB [1.00] | **89.46 ms [0.97] / 13.7MB [0.94]** | 153.62 ms [**1.66**] / 64.6MB [**4.41**] | 96.80 ms [1.05] / 21.8MB [1.49] |
| BulkDelete ⚠地板 | 20000 | 🏆 **PalORM** | 93.37 ms [1.00] / 8.5MB [1.00] | **80.49 ms [0.86] / 3.6MB [0.42]** | 95.94 ms [1.03] / 13.3MB [**1.57**] | 193.52 ms [**2.07**] / 6.3MB [0.74] |
| UpsertBatch ⚠地板 | 20000 | ADO | **77.35 ms [1.00] / 12.9MB [1.00]** | 81.43 ms [1.05] / 12.6MB [0.98] | 139.74 ms [**1.81**] / 66.2MB [**5.14**] | 125.62 ms [**1.62**] / 25.8MB [**2.00**] |

**PostgreSQL**

| 操作 | 档 | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql |
|---|---:|:-:|---|---|---|---|
| BulkInsert | 2000 | ADO | **4.79 ms [1.00] / 341KB [1.00]** | 4.90 ms [1.02] / 343KB [1.00] | 16.54 ms [**3.45**] / 6.7MB [**20.07**] | 13.05 ms [**2.72**] / 3.8MB [**11.54**] |
| BulkUpdate | 2000 | 🏆 **PalORM** | 5.58 ms [1.00] / 419KB [1.00] | **5.48 ms [0.98] / 422KB [1.01]** | 18.54 ms [**3.32**] / 6.5MB [**15.97**] | 14.13 ms [**2.53**] / 2.7MB [**6.63**] |
| BulkDelete | 2000 | 🏆 **PalORM** | 3.53 ms [1.00] / 785KB [1.00] | **2.46 ms [0.70] / 89.4KB [0.11]** | 4.05 ms [1.15] / 1.3MB [**1.71**] | 6.01 ms [**1.70**] / 572KB [0.73] |
| 📌UpsertBatch | 2000 | 🏆 **PalORM** | 15.13 ms [1.00] / 2.7MB [1.00] | **7.70 ms [0.51] / 446KB [0.16]** | 20.01 ms [1.32] / 6.7MB [**2.47**] | 16.26 ms [1.07] / 2.7MB [1.01] |
| BulkInsert ⚠地板 | 20000 | 🏆 **PalORM** | 25.60 ms [1.00] / 3.3MB [1.00] | **21.14 ms [0.83] / 3.3MB [0.99]** | 218.59 ms [**8.54**] / 67.0MB [**20.42**] | 117.03 ms [**4.57**] / 39.6MB [**12.08**] |
| BulkUpdate | 20000 | 🏆 **PalORM** | 67.24 ms [1.00] / 4.1MB [1.00] | **66.28 ms [0.99] / 4.1MB [1.00]** | 218.96 ms [**3.26**] / 65.4MB [**16.01**] | 132.19 ms [**1.97**] / 28.3MB [**6.93**] |
| 📌BulkDelete ⚠地板 | 20000 | 🏆 **PalORM** | 26.44 ms [1.00] / 7.6MB [1.00] | **10.35 ms [0.39] / 813KB [0.10]** | 30.05 ms [1.14] / 13.0MB [**1.71**] | 64.75 ms [**2.45**] / 6.0MB [0.79] |
| 📌UpsertBatch ⚠地板 | 20000 | 🏆 **PalORM** | 160.99 ms [1.00] / 11.6MB [1.00] | **91.61 ms [0.57] / 4.3MB [0.37]** | 248.49 ms [**1.54**] / 67.0MB [**5.76**] | 159.97 ms [0.99] / 28.4MB [**2.44**] |

## 表三：事务（三方言，仅最小档）

> 胜场：ADO 9 · PalORM 6 · Dapper 0 · FreeSql 0

### 测试项说明

| 操作 | 测什么 | 总评 |
|---|---|---|
| TxSingleInsert | 事务内单行插入 | 四臂持平（事务开销主导） |
| TxHundredInserts | 事务内 100 行逐条插入 | 联网方言接近；FreeSql SQLite 明显偏慢（逐行表达式开销放大） |
| TxBulkInsert | 事务内批量插入 | PalORM 贴地板；Dapper（multi-exec 无语句缓存复用）与 FreeSql 明显偏慢 |
| TxRollback | 事务回滚（更新后显式 Rollback） | 联网方言接近；SQLite FreeSql 明显偏慢 |
| EnumInserts | 枚举列事务内逐条插入（Status TEXT 列） | PalORM 贴平（SQLite 略高为每调用会话契约成本，已探针定案）；FreeSql 全方言明显偏慢，Dapper SQLite 偏慢 |

**SQLite**

| 操作 | 档 | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql |
|---|---:|:-:|---|---|---|---|
| TxSingleInsert | 2000 | 🏆 **PalORM** | 20.4 µs [1.00] / 3.9KB [1.00] | **20.1 µs [0.99] / 4.3KB [1.10]** | 21.0 µs [1.03] / 4.7KB [1.20] | 34.6 µs [**1.70**] / 7.0KB [**1.78**] |
| TxHundredInserts ⚠地板 | 2000 | ADO | **190.6 µs [1.00] / 137KB [1.00]** | 193.8 µs [1.02] / 92.7KB [0.68] | 261.1 µs [1.37] / 220KB [**1.61**] | 1.61 ms [**8.46**] / 541KB [**3.96**] |
| TxBulkInsert ⚠地板 | 2000 | ADO | **2.70 ms [1.00] / 1.7MB [1.00]** | 2.73 ms [1.01] / 1.7MB [1.00] | 4.42 ms [**1.64**] / 4.3MB [**2.48**] | 8.44 ms [**3.13**] / 3.5MB [**2.04**] |
| TxRollback | 2000 | ADO | **608.0 µs [1.00] / 435KB [1.00]** | 650.6 µs [1.07] / 437KB [1.01] | 2.13 ms [**3.51**] / 1.6MB [**3.74**] | 7.86 ms [**12.93**] / 3.5MB [**8.16**] |
| EnumInserts | 2000 | ADO | **132.9 µs [1.00] / 59.1KB [1.00]** | 154.8 µs [1.16] / 59.1KB [1.00] | 359.6 µs [**2.70**] / 223KB [**3.77**] | 1.30 ms [**9.79**] / 497KB [**8.42**] |

**MySQL**

| 操作 | 档 | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql |
|---|---:|:-:|---|---|---|---|
| TxSingleInsert ⚠地板 | 2000 | ADO | **4.51 ms [1.00] / 21.5KB [1.00]** | 4.55 ms [1.01] / 22.7KB [1.05] | 4.54 ms [1.01] / 21.4KB [0.99] | 4.85 ms [1.08] / 28.8KB [1.34] |
| TxHundredInserts ⚠地板 | 2000 | 🏆 **PalORM** | 27.04 ms [1.00] / 372KB [1.00] | **26.82 ms [0.99] / 309KB [0.83]** | 26.87 ms [0.99] / 353KB [0.95] | 30.63 ms [1.13] / 818KB [**2.20**] |
| TxBulkInsert | 2000 | ADO | **11.83 ms [1.00] / 478KB [1.00]** | 11.85 ms [1.00] / 495KB [1.04] | 20.18 ms [**1.71**] / 6.6MB [**14.25**] | 21.10 ms [**1.78**] / 3.4MB [**7.32**] |
| TxRollback | 2000 | 🏆 **PalORM** | 101.55 ms [1.00] / 1.4MB [1.00] | **99.69 ms [0.98] / 1.4MB [1.02]** | 105.64 ms [1.04] / 2.4MB [**1.76**] | 121.22 ms [1.19] / 4.9MB [**3.56**] |
| EnumInserts | 2000 | 🏆 **PalORM** | 26.48 ms [1.00] / 280KB [1.00] | **25.93 ms [0.98] / 288KB [1.03]** | 28.62 ms [1.08] / 436KB [**1.56**] | 31.07 ms [1.17] / 780KB [**2.78**] |

**PostgreSQL**

| 操作 | 档 | 🏆 | ADO.NET | **PalORM** | Dapper | FreeSql |
|---|---:|:-:|---|---|---|---|
| TxSingleInsert | 2000 | ADO | **1.52 ms [1.00] / 8.4KB [1.00]** | 1.58 ms [1.04] / 9.4KB [1.12] | 1.54 ms [1.02] / 8.2KB [0.97] | 1.61 ms [1.06] / 12.3KB [1.46] |
| TxHundredInserts | 2000 | 🏆 **PalORM** | 21.71 ms [1.00] / 317KB [1.00] | **21.40 ms [0.99] / 215KB [0.68]** | 21.58 ms [0.99] / 285KB [0.90] | 24.28 ms [1.12] / 704KB [**2.22**] |
| TxBulkInsert | 2000 | ADO | **4.82 ms [1.00] / 342KB [1.00]** | 4.94 ms [1.02] / 344KB [1.01] | 15.78 ms [**3.27**] / 6.7MB [**20.04**] | 21.58 ms [**4.48**] / 3.4MB [**10.27**] |
| TxRollback | 2000 | ADO | **103.15 ms [1.00] / 941KB [1.00]** | 103.91 ms [1.01] / 1.0MB [1.10] | 109.81 ms [1.06] / 1.7MB [**1.88**] | 125.58 ms [1.22] / 4.4MB [**4.81**] |
| EnumInserts | 2000 | 🏆 **PalORM** | 20.73 ms [1.00] / 166KB [1.00] | **20.68 ms [1.00] / 185KB [1.12]** | 21.45 ms [1.03] / 284KB [**1.71**] | 24.11 ms [1.16] / 663KB [**4.00**] |

## 附表：绝对时间（全量）

> 主表倍数以 ADO 地板为基准；本附表为绝对时间与分配，供外部引用。分配为 AllocatedBytesPerOp。

### 表一：CRUD 单行与读（三方言，两档）

| 操作 | 方言 | 档 | ADO.NET | PalORM | Dapper | FreeSql | 分配 A/P/D/F |
|---|---|---:|---:|---:|---:|---:|---|
| GetByKey | SQLite | 2000 | 7.6 µs | 6.0 µs | 9.9 µs | 34.7 µs | 2.1KB/2.1KB/2.8KB/10.2KB |
| GetAllAsync | SQLite | 2000 | 972.2 µs | 971.3 µs | 1.40 ms | 1.41 ms | 308KB/294KB/513KB/314KB |
| QueryAll | SQLite | 2000 | 981.9 µs | 983.1 µs | 1.41 ms | 1.42 ms | 308KB/293KB/513KB/314KB |
| Insert | SQLite | 2000 | 18.1 µs | 17.9 µs | 18.9 µs | 31.1 µs | 2.8KB/2.7KB/3.5KB/5.7KB |
| Update | SQLite | 2000 | 7.5 µs | 7.5 µs | 8.2 µs | 20.9 µs | 2.5KB/2.9KB/3.3KB/7.4KB |
| GetByKey | SQLite | 20000 | 6.5 µs | 6.8 µs | 7.7 µs | 31.4 µs | 2.1KB/2.2KB/2.8KB/10.4KB |
| GetAllAsync | SQLite | 20000 | 10.08 ms | 10.11 ms | 14.44 ms | 14.40 ms | 3.3MB/2.9MB/5.3MB/3.3MB |
| QueryAll | SQLite | 20000 | 10.14 ms | 10.05 ms | 14.25 ms | 14.66 ms | 3.3MB/2.9MB/5.3MB/3.3MB |
| Insert | SQLite | 20000 | 18.8 µs | 18.5 µs | 19.2 µs | 31.6 µs | 2.8KB/2.7KB/3.6KB/5.7KB |
| Update | SQLite | 20000 | 7.8 µs | 7.7 µs | 8.6 µs | 23.4 µs | 2.5KB/2.9KB/3.3KB/7.4KB |
| GetByKey | MySQL | 2000 | 208.2 µs | 204.8 µs | 209.0 µs | 455.0 µs | 4.6KB/4.9KB/5.9KB/16.7KB |
| GetAllAsync | MySQL | 2000 | 1.23 ms | 1.16 ms | 1.19 ms | 1.49 ms | 399KB/384KB/447KB/407KB |
| QueryAll | MySQL | 2000 | 1.16 ms | 1.16 ms | 1.21 ms | 1.48 ms | 399KB/384KB/447KB/407KB |
| Insert | MySQL | 2000 | 4.27 ms | 4.17 ms | 4.07 ms | 4.45 ms | 18.0KB/17.4KB/16.9KB/23.2KB |
| Update | MySQL | 2000 | 231.2 µs | 210.7 µs | 204.6 µs | 445.0 µs | 5.5KB/5.9KB/5.2KB/13.3KB |
| GetByKey | MySQL | 20000 | 240.6 µs | 224.8 µs | 236.8 µs | 521.2 µs | 4.6KB/5.0KB/5.9KB/17.1KB |
| GetAllAsync | MySQL | 20000 | 10.27 ms | 10.11 ms | 10.25 ms | 11.53 ms | 4.0MB/3.7MB/4.5MB/4.1MB |
| QueryAll | MySQL | 20000 | 10.01 ms | 9.90 ms | 10.14 ms | 11.80 ms | 4.0MB/3.7MB/4.5MB/4.1MB |
| Insert | MySQL | 20000 | 4.21 ms | 4.29 ms | 4.24 ms | 4.44 ms | 17.2KB/17.8KB/21.8KB/23.5KB |
| Update | MySQL | 20000 | 215.6 µs | 217.3 µs | 216.9 µs | 440.3 µs | 5.3KB/5.8KB/5.2KB/13.3KB |
| GetByKey | PostgreSQL | 2000 | 216.8 µs | 210.8 µs | 213.3 µs | 285.8 µs | 3.0KB/3.4KB/3.3KB/12.1KB |
| GetAllAsync | PostgreSQL | 2000 | 662.1 µs | 672.2 µs | 659.1 µs | 806.5 µs | 239KB/226KB/442KB/244KB |
| QueryAll | PostgreSQL | 2000 | 657.7 µs | 674.4 µs | 669.7 µs | 773.4 µs | 239KB/225KB/442KB/244KB |
| Insert | PostgreSQL | 2000 | 1.36 ms | 1.37 ms | 1.37 ms | 1.45 ms | 7.5KB/7.5KB/7.2KB/11.1KB |
| Update | PostgreSQL | 2000 | 1.40 ms | 1.40 ms | 1.39 ms | 1.44 ms | 7.4KB/7.9KB/7.2KB/13.1KB |
| GetByKey | PostgreSQL | 20000 | 213.1 µs | 212.0 µs | 222.8 µs | 287.9 µs | 3.0KB/3.4KB/3.3KB/12.1KB |
| GetAllAsync | PostgreSQL | 20000 | 5.11 ms | 5.42 ms | 5.35 ms | 5.92 ms | 2.5MB/2.2MB/4.5MB/2.5MB |
| QueryAll | PostgreSQL | 20000 | 5.57 ms | 5.38 ms | 5.64 ms | 5.91 ms | 2.5MB/2.2MB/4.5MB/2.5MB |
| Insert | PostgreSQL | 20000 | 1.38 ms | 1.38 ms | 1.36 ms | 1.43 ms | 7.5KB/7.5KB/7.2KB/11.0KB |
| Update | PostgreSQL | 20000 | 1.42 ms | 1.42 ms | 1.42 ms | 1.49 ms | 7.4KB/7.9KB/7.1KB/13.2KB |

### 表二：批量处理（三方言，两档）

| 操作 | 方言 | 档 | ADO.NET | PalORM | Dapper | FreeSql | 分配 A/P/D/F |
|---|---|---:|---:|---:|---:|---:|---|
| BulkInsert | SQLite | 2000 | 2.66 ms | 2.59 ms | 4.48 ms | 10.81 ms | 1.7MB/1.7MB/4.3MB/6.5MB |
| BulkUpdate | SQLite | 2000 | 2.35 ms | 2.41 ms | 9.30 ms | 13.63 ms | 1.7MB/1.8MB/6.3MB/7.0MB |
| BulkDelete | SQLite | 2000 | 846.5 µs | 860.2 µs | 22.68 ms | 1.62 ms | 197KB/198KB/1.6MB/569KB |
| UpsertBatch | SQLite | 2000 | 2.45 ms | 2.53 ms | 4.66 ms | 3.73 ms | 1.7MB/1.7MB/4.2MB/2.5MB |
| BulkInsert | SQLite | 20000 | 32.62 ms | 29.92 ms | 49.63 ms | 115.69 ms | 17.2MB/17.2MB/42.5MB/66.1MB |
| BulkUpdate | SQLite | 20000 | 26.04 ms | 28.16 ms | 99.12 ms | 156.22 ms | 17.0MB/17.6MB/63.6MB/73.4MB |
| BulkDelete | SQLite | 20000 | 8.80 ms | 8.42 ms | 227.92 ms | 17.24 ms | 2.0MB/2.0MB/15.6MB/6.0MB |
| UpsertBatch | SQLite | 20000 | 26.70 ms | 27.70 ms | 50.49 ms | 41.21 ms | 17.0MB/17.1MB/42.4MB/25.8MB |
| BulkInsert | MySQL | 2000 | 12.47 ms | 12.29 ms | 27.67 ms | 18.04 ms | 482KB/484KB/6.7MB/3.2MB |
| BulkUpdate | MySQL | 2000 | 11.76 ms | 11.09 ms | 12.50 ms | 11.01 ms | 4.6MB/3.7MB/6.5MB/2.1MB |
| BulkDelete | MySQL | 2000 | 13.13 ms | 12.48 ms | 12.49 ms | 18.53 ms | 887KB/811KB/1.3MB/613KB |
| UpsertBatch | MySQL | 2000 | 8.33 ms | 8.45 ms | 11.44 ms | 9.46 ms | 2.9MB/2.5MB/6.6MB/2.5MB |
| BulkInsert | MySQL | 20000 | 63.85 ms | 64.45 ms | 290.68 ms | 158.37 ms | 4.5MB/4.6MB/66.6MB/33.4MB |
| BulkUpdate | MySQL | 20000 | 92.48 ms | 89.46 ms | 153.62 ms | 96.80 ms | 14.6MB/13.7MB/64.6MB/21.8MB |
| BulkDelete | MySQL | 20000 | 93.37 ms | 80.49 ms | 95.94 ms | 193.52 ms | 8.5MB/3.6MB/13.3MB/6.3MB |
| UpsertBatch | MySQL | 20000 | 77.35 ms | 81.43 ms | 139.74 ms | 125.62 ms | 12.9MB/12.6MB/66.2MB/25.8MB |
| BulkInsert | PostgreSQL | 2000 | 4.79 ms | 4.90 ms | 16.54 ms | 13.05 ms | 341KB/343KB/6.7MB/3.8MB |
| BulkUpdate | PostgreSQL | 2000 | 5.58 ms | 5.48 ms | 18.54 ms | 14.13 ms | 419KB/422KB/6.5MB/2.7MB |
| BulkDelete | PostgreSQL | 2000 | 3.53 ms | 2.46 ms | 4.05 ms | 6.01 ms | 785KB/89.4KB/1.3MB/572KB |
| UpsertBatch | PostgreSQL | 2000 | 15.13 ms | 7.70 ms | 20.01 ms | 16.26 ms | 2.7MB/446KB/6.7MB/2.7MB |
| BulkInsert | PostgreSQL | 20000 | 25.60 ms | 21.14 ms | 218.59 ms | 117.03 ms | 3.3MB/3.3MB/67.0MB/39.6MB |
| BulkUpdate | PostgreSQL | 20000 | 67.24 ms | 66.28 ms | 218.96 ms | 132.19 ms | 4.1MB/4.1MB/65.4MB/28.3MB |
| BulkDelete | PostgreSQL | 20000 | 26.44 ms | 10.35 ms | 30.05 ms | 64.75 ms | 7.6MB/813KB/13.0MB/6.0MB |
| UpsertBatch | PostgreSQL | 20000 | 160.99 ms | 91.61 ms | 248.49 ms | 159.97 ms | 11.6MB/4.3MB/67.0MB/28.4MB |

### 表三：事务（三方言，仅最小档）

| 操作 | 方言 | 档 | ADO.NET | PalORM | Dapper | FreeSql | 分配 A/P/D/F |
|---|---|---:|---:|---:|---:|---:|---|
| TxSingleInsert | SQLite | 2000 | 20.4 µs | 20.1 µs | 21.0 µs | 34.6 µs | 3.9KB/4.3KB/4.7KB/7.0KB |
| TxHundredInserts | SQLite | 2000 | 190.6 µs | 193.8 µs | 261.1 µs | 1.61 ms | 137KB/92.7KB/220KB/541KB |
| TxBulkInsert | SQLite | 2000 | 2.70 ms | 2.73 ms | 4.42 ms | 8.44 ms | 1.7MB/1.7MB/4.3MB/3.5MB |
| TxRollback | SQLite | 2000 | 608.0 µs | 650.6 µs | 2.13 ms | 7.86 ms | 435KB/437KB/1.6MB/3.5MB |
| EnumInserts | SQLite | 2000 | 132.9 µs | 154.8 µs | 359.6 µs | 1.30 ms | 59.1KB/59.1KB/223KB/497KB |
| TxSingleInsert | MySQL | 2000 | 4.51 ms | 4.55 ms | 4.54 ms | 4.85 ms | 21.5KB/22.7KB/21.4KB/28.8KB |
| TxHundredInserts | MySQL | 2000 | 27.04 ms | 26.82 ms | 26.87 ms | 30.63 ms | 372KB/309KB/353KB/818KB |
| TxBulkInsert | MySQL | 2000 | 11.83 ms | 11.85 ms | 20.18 ms | 21.10 ms | 478KB/495KB/6.6MB/3.4MB |
| TxRollback | MySQL | 2000 | 101.55 ms | 99.69 ms | 105.64 ms | 121.22 ms | 1.4MB/1.4MB/2.4MB/4.9MB |
| EnumInserts | MySQL | 2000 | 26.48 ms | 25.93 ms | 28.62 ms | 31.07 ms | 280KB/288KB/436KB/780KB |
| TxSingleInsert | PostgreSQL | 2000 | 1.52 ms | 1.58 ms | 1.54 ms | 1.61 ms | 8.4KB/9.4KB/8.2KB/12.3KB |
| TxHundredInserts | PostgreSQL | 2000 | 21.71 ms | 21.40 ms | 21.58 ms | 24.28 ms | 317KB/215KB/285KB/704KB |
| TxBulkInsert | PostgreSQL | 2000 | 4.82 ms | 4.94 ms | 15.78 ms | 21.58 ms | 342KB/344KB/6.7MB/3.4MB |
| TxRollback | PostgreSQL | 2000 | 103.15 ms | 103.91 ms | 109.81 ms | 125.58 ms | 941KB/1.0MB/1.7MB/4.4MB |
| EnumInserts | PostgreSQL | 2000 | 20.73 ms | 20.68 ms | 21.45 ms | 24.11 ms | 166KB/185KB/284KB/663KB |


**表四：跨方言 PalORM/ADO 比值（批量族 + GetAllAsync；裸比值，越线 📌）**

| 操作 | 档 | SQLite | PostgreSQL | MySQL |
|---|---:|---:|---:|---:|
| BulkInsert | 2000 | 0.97 | 1.02 | 0.99 |
| BulkInsert | 20000 | 0.92 | 0.83 | 1.01 |
| BulkUpdate | 2000 | 1.03 | 0.98 | 0.94 |
| BulkUpdate | 20000 | 1.08 | 0.99 | 0.97 |
| BulkDelete | 2000 | 1.02 | 0.70 | 0.95 |
| BulkDelete | 20000 | 0.96 | 📌0.39 | 0.86 |
| UpsertBatch | 2000 | 1.03 | 📌0.51 | 1.01 |
| UpsertBatch | 20000 | 1.04 | 📌0.57 | 1.05 |
| GetAllAsync | 2000 | 1.00 | 1.02 | 0.95 |
| GetAllAsync | 20000 | 1.00 | 1.06 | 0.98 |
