# ADR-G：MySQL AllowLoadLocalInfile 安全策略

> 状态：已裁决（2026-08-15，评审 ITM-612/EVAL-1）
> 关联：MySqlProvider.CreateConnection · MySqlBulkCopyInserter（v5.0 阶段 4.2 引入）

## 背景

MySqlBulkCopy 路径依赖 `AllowLoadLocalInfile=true`（LOAD DATA LOCAL INFILE 协议）。
MySqlConnector 默认 `false` 出于安全考虑：恶意/被攻陷的 MySQL 服务端可借 LOAD DATA LOCAL
请求读取**客户端**本地文件（已知攻击向量）。当前 `MySqlProvider.CreateConnection` 对
`false`（无论用户显式设置还是驱动默认）无条件提升为 `true`——builder 层无法区分两种
false（ITM-612 实证），用户的安全加固意图被静默覆盖。

## 决策

维持"性能优先"现状（自动提升为 true），但以三层约束兜底：

1. **文档登记攻击面**（本 ADR + MySqlProvider 注释已做）——使用者知情。
2. **连接串显式意图优先的通道保留**：用户以 `AllowLoadLocalInfile=false` + 不使用
   BulkCopy（local_infile 检测走 SHOW VARIABLES，服务端 OFF 时自动回退多值 INSERT）
   的组合可获得"不开启本地文件读取"的运行态——服务端变量 local_infile=OFF 即禁用
   BulkCopy 路径，客户端标志不再单独构成攻击面。
3. **不做启发式检测**（连接串文本扫描 "AllowLoadLocalInfile" 等）——脆弱且易误判。

## 后果与 revisit 条件

- 多租户/不可信服务端场景的用户应自担评估；如需硬关闭，设服务端 local_infile=OFF。
- **Revisit 触发**：若 DbOptions 增加 `MySqlBulkCopyMode { Auto, Always, Never }`
  类显式开关的需求出现（用户反馈/issue），升级为显式 opt-in 默认 Never——届时本 ADR
  作废并重裁决。攻击面利用在依赖方被实际报告时立即 revisit。

## 被否方案

- 检测连接串文本关键字：无法覆盖等价写法，误报风险。
- 默认 Never + BulkCopy 需显式 opt-in：性能路径（4.84× 基准）对多数内部服务端场景
  是安全收益为零的损失——留待真实需求驱动。
