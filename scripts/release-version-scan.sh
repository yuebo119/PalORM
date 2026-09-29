#!/usr/bin/env bash
# release-version-scan.sh——升版本旧号残留机械扫描（发布规范 §1.3 的脚本化，2026-09-25）。
#
# 背景（教训 B91）：人肉 grep 清单漏了 GeneratedCodeMetadata 的 ToolVersion 字面量，
# v5.6.0 升版时靠 doc-consistency D11 才兜住没带病发布。本脚本把四类属性模式 +
# src 双引号版本字面量一次扫齐；"文档计数=实测"类校验（D10）仍归 doc-consistency，不重复造。
#
# 用法: bash scripts/release-version-scan.sh <旧版本> [新版本]
#   <旧版本>  升级前版本号（如 5.5.1）——扫描其残留
#   [新版本]  可选——正向核对其已落真源（Directory.Build.props 的 <Version>）
#
# 扫描面刻意排除 CHANGELOG / docs 历史叙述 / docs/review 审计——那些 5.x 字样是
# 历史记录与当时规划，改了是篡改审计史（B88 的分层纪律）。
#
# 退出码: 0 = 零残留（且新版本已落真源）；1 = 有残留或正向缺失（打印 ::error:: 清单）
set -euo pipefail

OLD="${1:?usage: release-version-scan.sh <old-version> [new-version]}"
NEW="${2:-}"
ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"

# 点号转义为字面量（grep BRE 里 . 通配）
OLD_RE=$(printf '%s' "$OLD" | sed 's/\./\\./g')
NEW_RE=$(printf '%s' "$NEW" | sed 's/\./\\./g')
fail=0

report() { # <描述> <匹配输出>
    if [ -n "$2" ]; then
        printf '::error::版本残留（%s）:\n%s\n' "$1" "$2"
        fail=1
    fi
}

report "props/csproj 的 <Version> 属性" \
    "$(grep -rn "<Version>${OLD_RE}</Version>" --include='*.props' --include='*.csproj' --exclude-dir=obj --exclude-dir=bin . 2>/dev/null || true)"

# 2026-09-28（v6.0.1 发布实测）：PackageReference 残留扫描收窄到 PalORM.* 包——vendored
# BDN 子树（bench/BenchmarkDotNet）的第三方依赖版本与 PalORM 版本号巧合（AsmResolver 6.0.0）
# 构成发布阻断误报；自家版本残留的载体恒为 PalORM.* 前缀引用。
report "PackageReference Version= 属性（PalORM.* 包）" \
    "$(grep -rnE "Include=\"PalORM\.[A-Za-z]+\"[^>]*Version=\"${OLD_RE}\"" --include='*.csproj' --exclude-dir=obj --exclude-dir=bin . 2>/dev/null || true)"

report "README badge version-" \
    "$(grep -n "version-${OLD_RE}" README.md 2>/dev/null || true)"

report "src 双引号版本字面量（含 ToolVersion）" \
    "$(grep -rn "\"${OLD_RE}\"" --include='*.cs' --exclude-dir=obj --exclude-dir=bin src/ 2>/dev/null || true)"

# 2026-09-29（v6.1.0 发布实测）：两道新判据，封堵 placeholder 残留这一发布失败形态——
# 升版本批次的占位符交换若对某一行落空（留下 Version="6.0.0-placeholder"），旧号扫描
# 抓不到它（含 placeholder 的字符串不匹配旧号），正向核对又只看 props 单点。CI 的
# NuGet Consumer Restore 会因该版本不存在而失败（run 36510390810，坏包未推出但白烧
# 一轮发布流）。
#
# 判据 A：消费者 csproj 的 PalORM.* 包引用必须精确等于新版本号——旧号残留与占位符
#   字符串两种形态一网打尽（版本比较而非模式匹配，对任意占位写法免疫）。
# 判据 B：全部 PalORM.* PackageReference 行中凡含 placeholder/TODO/FIXME 字样的行
#   ——兜住"新版本号写好了但行内带占位标记"的中间态提交。
CONSUMER_CSproj="test/PalORM.PackageConsumer.Aot/PalORM.PackageConsumer.Aot.csproj"
if [ -n "$NEW" ] && [ -f "$CONSUMER_CSproj" ]; then
    BAD_CONSUMER=$(grep -nE "Include=\"PalORM\.[A-Za-z]+\"[^>]*Version=\"" "$CONSUMER_CSproj" 2>/dev/null \
        | grep -v "Version=\"${NEW_RE}\"" || true)
    report "消费者 PackageReference 版本≠新版本 ${NEW}（判据 A）" "$BAD_CONSUMER"
fi
report "PalORM.* PackageReference 行含占位标记（判据 B）" \
    "$(grep -rnE "Include=\"PalORM\.[A-Za-z]+\"[^>]*Version=\"" --include='*.csproj' --exclude-dir=obj --exclude-dir=bin . 2>/dev/null \
        | grep -iE 'placeholder|todo|fixme' || true)"

if [ -n "$NEW" ]; then
    if ! grep -q "<Version>${NEW_RE}</Version>" Directory.Build.props; then
        printf '::error::正向核对失败：Directory.Build.props 未落新版本 %s\n' "$NEW"
        fail=1
    fi
fi

if [ "$fail" -eq 0 ]; then
    printf 'PASS 版本残留扫描（旧 %s%s）\n' "$OLD" "${NEW:+ → 新 $NEW}"
fi
exit "$fail"
