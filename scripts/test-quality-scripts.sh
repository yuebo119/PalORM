#!/usr/bin/env bash
# 质量脚本的固定回归夹具。验证成功与故障输入的退出码和计数。
# AI 系统脚本在 .ai/scripts/（本地工具，不入仓库）：存在时全量回归；
# 不存在时（fresh clone / CI）跳过 AI 段、仍回归仓库内防线
# （stub-check / SDK pin / secret-guard 自测）——v7.2 B 系列接线收口：
# 此前 skip_ai 只打印不生效，后续段在 fresh clone 必挂。

set -euo pipefail

ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# AI 脚本路径（本地存在时测试，不存在时跳过对应段）
AI_SCRIPTS="$ROOT/.ai/scripts"
skip_ai() { [ ! -d "$AI_SCRIPTS" ]; }

if skip_ai; then
    printf 'SKIP: .ai/scripts/ not found — AI 段跳过，仅回归仓库内防线\n'
fi

if ! skip_ai; then
printf '─── verify-action-items ───\n'
printf '# fixture\n`README.md`\n' > "$TMP/action-pass.md"
printf '# fixture\n`missing-file.yml`\n' > "$TMP/action-fail-file.md"
missing_symbol='PalOrmDefinitelyMissing'
missing_symbol+='Symbol'
printf '# fixture\n`%s`\n' "$missing_symbol" > "$TMP/action-fail-symbol.md"

bash .ai/scripts/verify-action-items.sh "$TMP/action-pass.md" > "$TMP/action-pass.log"
if bash .ai/scripts/verify-action-items.sh "$TMP/action-fail-file.md" > "$TMP/action-fail-file.log"; then
    printf 'FAIL 缺失文件未导致失败\n'
    exit 1
fi
if ! grep -q '缺失：1' "$TMP/action-fail-file.log"; then
    printf 'FAIL 缺失文件计数错误\n'
    exit 1
fi
if bash .ai/scripts/verify-action-items.sh "$TMP/action-fail-symbol.md" > "$TMP/action-fail-symbol.log"; then
    printf 'FAIL 缺失标识符未导致失败\n'
    exit 1
fi
if ! grep -q '缺失：1' "$TMP/action-fail-symbol.log"; then
    printf 'FAIL 缺失标识符计数错误\n'
    exit 1
fi
printf 'PASS verify-action-items\n'
fi

printf '\n─── stub-check ───\n'
mkdir -p "$TMP/clean" "$TMP/stub"
printf 'internal sealed class Complete { int Value() { return 1; } }\n' > "$TMP/clean/Complete.cs"
printf 'internal sealed class Stub { object Route() => this; }\n' > "$TMP/stub/Stub.cs"
bash scripts/stub-check.sh "$TMP/clean" > "$TMP/stub-pass.log"
if bash scripts/stub-check.sh "$TMP/stub" > "$TMP/stub-fail.log"; then
    printf 'FAIL 空壳夹具未导致失败\n'
    exit 1
fi
if ! grep -q '发现 1 个' "$TMP/stub-fail.log"; then
    printf 'FAIL 空壳计数错误\n'
    exit 1
fi
printf 'PASS stub-check\n'

if ! skip_ai; then
printf '\n─── review-snapshot ───\n'
bash .ai/scripts/review-snapshot.sh --no-build > "$TMP/snapshot.log"
if ! grep -q '构建状态' "$TMP/snapshot.log" || ! grep -q '已跳过（--no-build）' "$TMP/snapshot.log"; then
    printf 'FAIL 快照无构建模式输出不完整\n'
    exit 1
fi
if grep -q '测试文件：5835' "$TMP/snapshot.log"; then
    printf 'FAIL 快照仍统计生成产物\n'
    exit 1
fi
printf 'PASS review-snapshot\n'
fi

if ! skip_ai; then
printf '\n─── gate-check G12 ───\n'
mkdir -p "$TMP/gate/src/Fixture"
printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><IsAotCompatible>true</IsAotCompatible></PropertyGroup></Project>\n' \
    > "$TMP/gate/src/Fixture/Fixture.csproj"
printf 'using System.Collections.Generic; public static class Clean { public static List<int> Values() => []; }\n' \
    > "$TMP/gate/src/Fixture/Clean.cs"
git -C "$TMP/gate" init -q
git -C "$TMP/gate" add .
# G33 工作树脏检查：临时仓库必须 commit，否则 staged 未提交文件必然 FAIL（预存断裂根因）
git -C "$TMP/gate" -c user.name=fixture -c user.email=fixture@test.local commit -qm 'fixture init'
if ! (
    cd "$TMP/gate"
    bash "$ROOT/.ai/scripts/gate-check.sh" > "$TMP/gate-pass.log"
); then
    printf 'FAIL G12 干净夹具 gate-check 非零退出（查 gate-pass.log）'
    exit 1
fi
if ! grep -q 'PASS G12: 禁止公开 static 可写状态' "$TMP/gate-pass.log"; then
    printf 'FAIL G12 干净夹具未通过\n'
    exit 1
fi
printf 'public static class Broken { public static int Value\n{\n    get;\n    set;\n} }\n' \
    > "$TMP/gate/src/Fixture/Broken.cs"
git -C "$TMP/gate" add .
if (
    cd "$TMP/gate"
    bash "$ROOT/.ai/scripts/gate-check.sh" > "$TMP/gate-fail.log"
); then
    printf 'FAIL G12 多行可写属性未导致失败\n'
    exit 1
fi
if ! grep -q 'FAIL G12: 禁止公开 static 可写状态（违规数：1）' "$TMP/gate-fail.log"; then
    printf 'FAIL G12 多行属性计数错误\n'
    exit 1
fi
printf 'using System.Collections.Generic; public static class Broken { public static List<int> Items { get; } = []; }\n' \
    > "$TMP/gate/src/Fixture/Broken.cs"
git -C "$TMP/gate" add .
if (
    cd "$TMP/gate"
    bash "$ROOT/.ai/scripts/gate-check.sh" > "$TMP/gate-collection-fail.log"
); then
    printf 'FAIL G12 可变集合属性未导致失败\n'
    exit 1
fi
if ! grep -q 'FAIL G12: 禁止公开 static 可写状态（违规数：1）' "$TMP/gate-collection-fail.log"; then
    printf 'FAIL G12 可变集合属性计数错误\n'
    exit 1
fi
rm "$TMP/gate/src/Fixture/Broken.cs"
git -C "$TMP/gate" add -A
(
    cd "$TMP/gate"
    bash "$ROOT/.ai/scripts/gate-check.sh" > "$TMP/gate-recovered.log"
)
if ! grep -q 'PASS G12: 禁止公开 static 可写状态' "$TMP/gate-recovered.log"; then
    printf 'FAIL G12 移除违规后未恢复\n'
    exit 1
fi
printf 'PASS gate-check G12 故障与恢复\n'
fi

if ! skip_ai; then
printf '\n─── verify-phase ───\n'
if bash .ai/scripts/verify-phase.sh invalid > "$TMP/phase.log" 2>&1; then
    printf 'FAIL 非法阶段参数未导致失败\n'
    exit 1
fi
if ! grep -q '用法' "$TMP/phase.log" || ! grep -q 'phase-number' "$TMP/phase.log"; then
    printf 'FAIL 非法阶段参数输出不完整\n'
    exit 1
fi
printf 'PASS verify-phase 参数失败传播\n'
fi

printf '\n─── SDK pin ───\n'
# rollForward: latestMinor 语义——global.json 是下限锚点，实跑 SDK 允许同 band 更高版本
# 提取用 grep+sed POSIX 形式而非 grep -oP \K：PCRE \K 依赖 GNU/特定实现
#（Windows 侧 ugrep 不支持会直接挂在 set -e——本机夹具实测；sed BRE 两边皆通）
pin_band=$(grep '"version"' global.json | head -1 | sed 's/[^0-9]*\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\).*/\1/')
sdk_band=$(dotnet --version | sed 's/\([0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*\).*/\1/')
if [ -z "$pin_band" ] || [ "$pin_band" != "$sdk_band" ]; then
    printf 'FAIL 当前 SDK band(%s) 与 global.json band(%s) 不一致\n' "$sdk_band" "$pin_band"
    exit 1
fi
# ci.yml 已收敛为调用 verify.yml 的 14 行空壳（job 全部在 verify.yml）——SDK 断言改查
# verify.yml（真源）；grep -c 无匹配 exit 1 会沿 set -e 杀脚本（B38 同款坑），
# || true 守卫保 stdout 的 "0" 计数交给下方数量对账。
if grep -q 'dotnet-version: "11.0.x"' .github/workflows/verify.yml; then
    printf 'FAIL CI 仍使用浮动 .NET SDK\n'
    exit 1
fi
setup_count=$(grep -cE 'uses: actions/setup-dotnet' .github/workflows/verify.yml || true)
global_json_count=$(grep -c 'global-json-file: global.json' .github/workflows/verify.yml || true)
if [ "$setup_count" -ne "$global_json_count" ]; then
    printf 'FAIL CI setup-dotnet 未全部读取 global.json\n'
    exit 1
fi
printf 'PASS SDK 固定与 CI 一致性\n'

printf '\n─── 脚本可移植性（无 PCRE grep 依赖）───\n'
# B87：PCRE 专用语法（-oP 的 \K/lookahead）跨 grep 实现不兼容——Windows 侧 ugrep 实测挂死，
# 且夹具/断言脚本是要在 CI（GNU）与本地（可能 ugrep）双侧跑的防线，必须两边都活。
# 模式要求 -oP 后跟空白（真实调用形态）；豁免注释行（注释提及历史写法是合法的，
# 可移植性问题只在"实际调用"——三重防自指：printf 说明文本、模式空格后缀、注释豁免）
pcre_hits=$(grep -rn 'grep -oP[[:space:]]' scripts/ .github/workflows/ 2>/dev/null \
    | grep -v -e ':[[:space:]]*#' || true)
if [ -n "$pcre_hits" ]; then
    printf 'FAIL 存在 grep -oP（PCRE 语法跨实现不兼容，换 POSIX grep+sed）:\n%s\n' "$pcre_hits"
    exit 1
fi
printf 'PASS scripts/ 与 workflows/ 无 grep -oP（PCRE）依赖\n'

if ! skip_ai; then
printf '\n─── doc-consistency ───\n'
if bash .ai/scripts/doc-consistency-check.sh > "$TMP/doc-pass.log"; then
    printf 'PASS doc-consistency 8/8\n'
else
    printf 'FAIL doc-consistency 未通过\n'
    exit 1
fi
# 故障夹具（ITM-427：不就地改 tracked 文件——异常中断会留下损坏态；
# 恒等替换防护——破坏值与当前值相同时换备选值，避免假失败；
# B38 变体：命令替换 grep 无匹配返回 1 会沿 set -e 无声杀死夹具——必须 || true + 空值守卫）
cp docs/架构设计.md "$TMP/pristine-arch.md"
trap 'cp "$TMP/pristine-arch.md" docs/架构设计.md' EXIT
# D10 实际校验的是 273 行附近的 "| **N 项 [Test] 声明**" 总声明（旧 Core.Tests N/N 表早已改版删除）
# 同上：POSIX sed 取首个满足 "| **N 项" 的 N（等价原 grep -oP \K/lookahead + head -1；
# sed -n 对每行尝试、p 全部命中、head -1 取首——跨行搜索语义，不依赖 PCRE）
current_total=$(sed -n 's/.*| \*\*\([0-9][0-9]*\) 项.*/\1/p' docs/架构设计.md | head -1 || true)
if [ -z "$current_total" ]; then
    printf 'FAIL 故障注入目标（总声明计数）缺失——文档格式再变更时同步本夹具\n'
    exit 1
fi
broken=$((current_total - 1))
sed -i "s/| \\*\\*${current_total} 项/| **${broken} 项/" docs/架构设计.md
if bash .ai/scripts/doc-consistency-check.sh > "$TMP/doc-fail.log" 2>&1; then
    printf 'FAIL doc-consistency 过期计数未导致失败\n'
    exit 1
fi
cp "$TMP/pristine-arch.md" docs/架构设计.md
trap - EXIT
if ! bash .ai/scripts/doc-consistency-check.sh > "$TMP/doc-recover.log"; then
    printf 'FAIL doc-consistency 恢复后未通过\n'
    exit 1
fi
printf 'PASS doc-consistency 故障与恢复\n'
fi


printf '─── secret-guard 自测（B41：误报/真阳性双向向量回归）───\n'
if bash scripts/secret-guard.sh --selftest; then
    printf 'PASS secret-guard 自测\n'
else
    printf 'FAIL secret-guard 自测未通过\n'
    exit 1
fi

printf '\n质量脚本回归夹具全部通过。\n'
