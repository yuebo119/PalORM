#!/usr/bin/env bash
# assert-test-counts.sh（独立审计 M0-3）——CI"确实执行了"断言。
# 读取 TUnit 的 .tunit-report.json，对三项目断言：
#   ① passed ≥ 地板（bench/baselines/test-counts.json 为唯一真源）
#   ② skipped == 0（当前无跳过机制，任何跳过都是新引入的静默降级）
#   ③（仅 Integration）ExternalDatabase 分类用例数 ≥ 地板（真库面不许静默消失）
# 调用：assert-test-counts.sh <项目名> <tunit-report.json 路径>
# 陷阱（账本 M0-3）：不 grep stdout（编码/语言漂移）、不硬编码配置目录（从传入路径解析）。
set -euo pipefail

PROJECT="${1:?usage: assert-test-counts.sh <project> <report.json>}"
REPORT="${2:?usage: assert-test-counts.sh <project> <report.json>}"

if [ ! -f "$REPORT" ]; then
    echo "::error::报告不存在：$REPORT（测试未运行或路径错——本断言不接受'找不到报告即放行'）"
    exit 1
fi

FLOOR_FILE="$(dirname "$0")/../bench/baselines/test-counts.json"

node -e '
const fs = require("fs");
const [project, report, floorFile] = process.argv.slice(1);
const summary = JSON.parse(fs.readFileSync(report, "utf8")).summary;
const floors = JSON.parse(fs.readFileSync(floorFile, "utf8"));
const floor = floors[project];
if (!floor) { console.error(`::error::地板文件缺少项目 ${project} 的条目`); process.exit(1); }

const failures = [];
if (summary.passed < floor.passed)
    failures.push(`passed ${summary.passed} < 地板 ${floor.passed}（用例被删除或未运行？）`);
if (summary.skipped !== 0)
    failures.push(`skipped ${summary.skipped} ≠ 0（无跳过机制的套件出现跳过 = 静默降级）`);

if (floor.externalDatabase !== undefined) {
    const j = JSON.parse(fs.readFileSync(report, "utf8"));
    const external = j.groups.flatMap(g => g.tests).filter(t =>
        (t.customProperties || []).some(p => p.key === "Category" && p.value === "ExternalDatabase")
            && t.status === "passed").length;
    if (external < floor.externalDatabase)
        failures.push(`ExternalDatabase 通过数 ${external} < 地板 ${floor.externalDatabase}（真库用例静默消失？）`);
    console.log(`${project}: passed=${summary.passed} skipped=${summary.skipped} externalDb=${external}`);
} else {
    console.log(`${project}: passed=${summary.passed} skipped=${summary.skipped}`);
}

if (failures.length) {
    for (const f of failures) console.error(`::error::${project}: ${f}`);
    process.exit(1);
}
' "$PROJECT" "$REPORT" "$FLOOR_FILE"
