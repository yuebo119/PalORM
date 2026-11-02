// 性能表-表二 生成器（v1.4，2026-11-02）：从 CrossBench 完整批生成 README 性能模块的
// 操作对照与事务对照（按方言分表，13 列，每格四行：时延/时延倍数/内存/内存倍数）。
// 用法：node scripts/table2-gen.mjs <crossbench-batch.json> [输出路径，默认 stdout]
// 规则对齐 docs/README性能表模板.md v1.3：着色只落 PalORM 列；基准格写纯 1.00；
// 🏆 = 时延最快组、💾 = 内存最省组（均含 ADO 基准）；≥2.0 或 ≤0.5 的极端值加粗。
import fs from 'node:fs';

const [, , batchPath, outPath] = process.argv;
if (!batchPath) {
  console.error('用法: node scripts/table2-gen.mjs <crossbench-batch.json> [out.md]');
  process.exit(1);
}

const rows = JSON.parse(fs.readFileSync(batchPath, 'utf8'));
const agg = new Map();
for (const x of rows) {
  const k = `${x.Dialect}|${x.Implementation}|${x.Operation}|${x.Rows}`;
  const a = agg.get(k) ?? [];
  a.push(x);
  agg.set(k, a);
}
const cells = [...agg.values()].map((a) => ({
  d: a[0].Dialect, arm: a[0].Implementation, op: a[0].Operation, rows: a[0].Rows,
  med: a.reduce((s, x) => s + x.MedianNs, 0) / a.length,
  alloc: a.reduce((s, x) => s + x.AllocatedBytesPerOp, 0) / a.length,
}));

// 列序（2026-11-02 模板 v1.3 冻结序，AGENTS.md 列固定条）：基准 → PalORM → Dapper.AOT
// → RepoDb → EF Core → Dapper → FreeSql → linq2db → SqlSugar。缺失的组整列省略，
// 新增组需同步模板与 AGENTS.md 的列固定条。
const ARMS = ['ADO_NET', 'PalORM', 'DapperAot', 'RepoDb', 'EfCore', 'Dapper', 'FreeSql', 'Linq2Db', 'SqlSugar'];
const DISPLAY = { ADO_NET: 'ADO', DapperAot: 'Dapper.AOT', EfCore: 'EF Core', Linq2Db: 'linq2db' };
const OPS = [
  ['GetByKey', 'crud'], ['GetAllAsync', 'crud'], ['QueryAll', 'crud'], ['Insert', 'crud'], ['Update', 'crud'],
  ['BulkInsert', 'bulk'], ['BulkUpdate', 'bulk'], ['BulkDelete', 'bulk'], ['UpsertBatch', 'bulk'],
  ['KeysetPage', 'extra'], ['WhereIn', 'extra'], ['Count', 'extra'],
  ['TxSingleInsert', 'tx'], ['TxHundredInserts', 'tx'], ['TxBulkInsert', 'tx'], ['TxRollback', 'tx'], ['EnumInserts', 'tx'],
];
const TIERS = [2000, 20000];

const idx = (d, o, t, arm) => cells.find((x) => x.d === d && x.op === o && x.rows === t && x.arm === arm);
const fmtT = (ns) => {
  const u = ns / 1000;
  if (u >= 1e6) return `${(u / 1e6).toFixed(2)}&nbsp;s`;
  if (u >= 1000) return `${(u / 1000).toFixed(2)}&nbsp;ms`;
  if (u >= 1) return `${u.toFixed(1)}&nbsp;µs`;
  return `${u.toFixed(3)}&nbsp;µs`;
};
const fmtA = (b) => {
  b /= 1024;
  if (b >= 1048576) return `${(b / 1024).toFixed(1)}MB`;
  if (b >= 100) return `${Math.round(b)}KB`;
  if (b >= 1) return `${b.toFixed(1)}KB`;
  return `${b.toFixed(0)}B`;
};
// 六档色值（模板 v1.3）
const COLOR = (v) => {
  if (v <= 0.70) return '#16a34a';
  if (v <= 0.90) return '#65a30d';
  if (v <= 0.99) return '#2563eb';
  if (Math.abs(v - 1) < 0.005) return '#6b7280';
  if (v <= 1.09) return '#d97706';
  if (v <= 1.29) return '#ea580c';
  return '#dc2626';
};
const PAINT = (arm, v) => {
  const s = v.toFixed(2);
  if (arm === 'ADO_NET') return '1.00';
  const bold = v >= 2.0 || v <= 0.5;
  if (arm === 'PalORM') return `$\\color{${COLOR(v)}}{${s}}$`;
  return bold ? `**${s}**` : s;
};

function buildRow(dialect, op, tier, present = ARMS) {
  const base = idx(dialect, op, tier, 'ADO_NET');
  let bestT = null, bestA = null;
  for (const arm of present) {
    const c = idx(dialect, op, tier, arm);
    if (!c) continue;
    if (!bestT || c.med < bestT.med) bestT = { arm, med: c.med };
    if (!bestA || c.alloc < bestA.alloc) bestA = { arm, alloc: c.alloc };
  }
  const mark = `${bestT ? `🏆&nbsp;${DISPLAY[bestT.arm] ?? bestT.arm}` : ''}${bestA ? `${bestT ? '<br>' : ''}💾&nbsp;${DISPLAY[bestA.arm] ?? bestA.arm}` : ''}`;
  const tds = present.map((arm) => {
    const c = idx(dialect, op, tier, arm);
    if (!c) return '—';
    const tag = `${bestT?.arm === arm ? '🏆&nbsp;' : ''}${bestA?.arm === arm ? '💾&nbsp;' : ''}`;
    return `${tag}${fmtT(c.med)}<br>${PAINT(arm, c.med / base.med)}<br>${fmtA(c.alloc)}<br>${PAINT(arm, c.alloc / base.alloc)}`;
  });
  return `| ${op} | ${tier.toLocaleString('en-US')} | ${mark} | ${tds.join(' | ')} |`;
}

function buildTable(dialect, ops, tiers) {
  const present = ARMS.filter((a) => idx(dialect, ops[0][0], tiers[0], a));
  const header = `| 操作 | 档 | 🏆&nbsp;/&nbsp;💾 | ${present.map((a) => DISPLAY[a] ?? a).join(' | ')} |`;
  const sep = `|---|---:|---|${'---:|'.repeat(present.length)}`;
  const lines = [header, sep];
  for (const [op] of ops) {
    for (const t of tiers) {
      if (idx(dialect, op, t, 'ADO_NET')) lines.push(buildRow(dialect, op, t, present));
    }
  }
  return lines.join('\n');
}

const out = [];
out.push(`> 数据源：\`${batchPath.replace(/\\/g, '/')}\`（9 组 × 三方言 × 两档，两遍 Average，比值分母 = 同批 ADO 基准）。`);
out.push('');
out.push('### 操作对照（时延 [倍数] 与内存 [相对基准]，颜色同百分比六档）');
out.push('');
out.push('> 🏆 = 时延最快组 · 💾 = 内存最省组（均含 ADO 基准）');
for (const [dialect, title] of [['SQLite', 'SQLite'], ['MySQL', 'MySQL'], ['PostgreSQL', 'PostgreSQL']]) {
  out.push('');
  out.push(`#### ${title}（2000 与 20000 档）`);
  out.push('');
  out.push(buildTable(dialect, OPS.filter(([, g]) => g !== 'tx'), TIERS));
}
out.push('');
out.push('### 事务对照（仅 2000 档）');
out.push('');
for (const dialect of ['SQLite', 'MySQL', 'PostgreSQL']) {
  out.push(`#### ${dialect}`);
  out.push('');
  out.push(buildTable(dialect, OPS.filter(([, g]) => g === 'tx'), [2000]));
  out.push('');
}

const text = out.join('\n');
if (outPath) {
  fs.writeFileSync(outPath, text);
  console.log(`written: ${outPath}`);
} else {
  console.log(text);
}
