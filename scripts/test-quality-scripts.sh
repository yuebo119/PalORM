#!/usr/bin/env bash
# 质量脚本的固定回归夹具。验证成功与故障输入的退出码和计数。

set -euo pipefail

ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

printf '─── verify-action-items ───\n'
printf '# fixture\n`README.md`\n' > "$TMP/action-pass.md"
printf '# fixture\n`missing-file.yml`\n' > "$TMP/action-fail-file.md"
missing_symbol='PalOrmDefinitelyMissing'
missing_symbol+='Symbol'
printf '# fixture\n`%s`\n' "$missing_symbol" > "$TMP/action-fail-symbol.md"

bash scripts/verify-action-items.sh "$TMP/action-pass.md" > "$TMP/action-pass.log"
if bash scripts/verify-action-items.sh "$TMP/action-fail-file.md" > "$TMP/action-fail-file.log"; then
    printf 'FAIL 缺失文件未导致失败\n'
    exit 1
fi
if ! grep -q '缺失：1' "$TMP/action-fail-file.log"; then
    printf 'FAIL 缺失文件计数错误\n'
    exit 1
fi
if bash scripts/verify-action-items.sh "$TMP/action-fail-symbol.md" > "$TMP/action-fail-symbol.log"; then
    printf 'FAIL 缺失标识符未导致失败\n'
    exit 1
fi
if ! grep -q '缺失：1' "$TMP/action-fail-symbol.log"; then
    printf 'FAIL 缺失标识符计数错误\n'
    exit 1
fi
printf 'PASS verify-action-items\n'

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

printf '\n─── review-snapshot ───\n'
bash scripts/review-snapshot.sh --no-build > "$TMP/snapshot.log"
if ! grep -q '构建状态' "$TMP/snapshot.log" || ! grep -q '已跳过（--no-build）' "$TMP/snapshot.log"; then
    printf 'FAIL 快照无构建模式输出不完整\n'
    exit 1
fi
if grep -q '测试文件：5835' "$TMP/snapshot.log"; then
    printf 'FAIL 快照仍统计生成产物\n'
    exit 1
fi
printf 'PASS review-snapshot\n'

printf '\n─── gate-check G12 ───\n'
mkdir -p "$TMP/gate/src/Fixture"
printf '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework><IsAotCompatible>true</IsAotCompatible></PropertyGroup></Project>\n' \
    > "$TMP/gate/src/Fixture/Fixture.csproj"
printf 'using System.Collections.Generic; public static class Clean { public static List<int> Values() => []; }\n' \
    > "$TMP/gate/src/Fixture/Clean.cs"
git -C "$TMP/gate" init -q
git -C "$TMP/gate" add .
(
    cd "$TMP/gate"
    bash "$ROOT/scripts/gate-check.sh" > "$TMP/gate-pass.log"
)
if ! grep -q 'PASS G12: 禁止公开 static 可写状态' "$TMP/gate-pass.log"; then
    printf 'FAIL G12 干净夹具未通过\n'
    exit 1
fi
printf 'public static class Broken { public static int Value\n{\n    get;\n    set;\n} }\n' \
    > "$TMP/gate/src/Fixture/Broken.cs"
git -C "$TMP/gate" add .
if (
    cd "$TMP/gate"
    bash "$ROOT/scripts/gate-check.sh" > "$TMP/gate-fail.log"
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
    bash "$ROOT/scripts/gate-check.sh" > "$TMP/gate-collection-fail.log"
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
    bash "$ROOT/scripts/gate-check.sh" > "$TMP/gate-recovered.log"
)
if ! grep -q 'PASS G12: 禁止公开 static 可写状态' "$TMP/gate-recovered.log"; then
    printf 'FAIL G12 移除违规后未恢复\n'
    exit 1
fi
printf 'PASS gate-check G12 故障与恢复\n'

printf '\n─── verify-phase ───\n'
if bash scripts/verify-phase.sh invalid > "$TMP/phase.log" 2>&1; then
    printf 'FAIL 非法阶段参数未导致失败\n'
    exit 1
fi
if ! grep -q '用法：bash scripts/verify-phase.sh <phase-number>' "$TMP/phase.log"; then
    printf 'FAIL 非法阶段参数输出不完整\n'
    exit 1
fi
printf 'PASS verify-phase 参数失败传播\n'

printf '\n─── SDK pin ───\n'
current_sdk=$(dotnet --version)
if ! grep -Fq "\"version\": \"$current_sdk\"" global.json; then
    printf 'FAIL 当前 SDK 与 global.json 不一致\n'
    exit 1
fi
if grep -q 'dotnet-version: "11.0.x"' .github/workflows/ci.yml; then
    printf 'FAIL CI 仍使用浮动 .NET SDK\n'
    exit 1
fi
setup_count=$(grep -c 'uses: actions/setup-dotnet@v4' .github/workflows/ci.yml)
global_json_count=$(grep -c 'global-json-file: global.json' .github/workflows/ci.yml)
if [ "$setup_count" -ne "$global_json_count" ]; then
    printf 'FAIL CI setup-dotnet 未全部读取 global.json\n'
    exit 1
fi
printf 'PASS SDK 固定与 CI 一致性\n'

printf '\n─── doc-consistency ───\n'
if bash scripts/doc-consistency-check.sh > "$TMP/doc-pass.log"; then
    printf 'PASS doc-consistency 8/8\n'
else
    printf 'FAIL doc-consistency 未通过\n'
    exit 1
fi
# 故障夹具（ITM-427：不就地改 tracked 文件——异常中断会留下损坏态；
# 恒等替换防护——破坏值与当前值相同时换备选值，避免假失败）
cp docs/架构设计.md "$TMP/pristine-arch.md"
trap 'cp "$TMP/pristine-arch.md" docs/架构设计.md' EXIT
current_core=$(grep -oP 'Core\.Tests\s*\|\s*\K\d+/\d+' docs/架构设计.md)
broken="71/71"
[ "$current_core" = "$broken" ] && broken="72/72"
sed -i "s|${current_core}|${broken}|g" docs/架构设计.md
if bash scripts/doc-consistency-check.sh > "$TMP/doc-fail.log" 2>&1; then
    printf 'FAIL doc-consistency 过期计数未导致失败\n'
    exit 1
fi
cp "$TMP/pristine-arch.md" docs/架构设计.md
trap - EXIT
if ! bash scripts/doc-consistency-check.sh > "$TMP/doc-recover.log"; then
    printf 'FAIL doc-consistency 恢复后未通过\n'
    exit 1
fi
printf 'PASS doc-consistency 故障与恢复\n'

printf '\n质量脚本回归夹具全部通过。\n'
