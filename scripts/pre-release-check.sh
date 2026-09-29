#!/usr/bin/env bash
# pre-release-check.sh——发布前预检（发布规范 §4 的"模拟 CI 消费者路径"，2026-09-29 v6.1.0 发布事故固化）。
#
# 背景：v6.1.0 首次 tag 触发的发布流失败于 "NuGet Consumer Native AOT" job 的 Restore 步骤
# （run 36510390810）——消费者 csproj 残留 Version="6.0.0-placeholder"，本地四件套
# （build/测试/包契约/pack）都不覆盖"消费者 restore 走本地包源"这条 CI 路径，坏包未推出
# 但白烧一轮发布流并留下失败记录。本脚本把该路径复刻到发布前本地执行。
#
# 用法: bash scripts/pre-release-check.sh <新版本>
# 步骤（与 verify.yml "NuGet Consumer Native AOT" job 同序同参）:
#   0. 版本残留扫描（release-version-scan，含 placeholder 双判据）
#   1. pack 五包到本地目录（= CI "Pack local packages"，多含 PG/MySQL 两包）
#   2. 用消费者自己的 NuGet.config（clear + 本地源 PalORM.* 映射）做 dotnet restore
#      ——占位版本在这一步必然失败（本地源里没有该包），正是 CI 抓到的形态
#   3. 包契约脚本
# 退出码: 任一步失败即 1。发布 tag 前必跑；全绿才允许 push tag（SOP §5.1 第 4 步前）。
set -euo pipefail

VERSION="${1:?usage: pre-release-check.sh <new-version>}"
ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"
CONSUMER_DIR="test/PalORM.PackageConsumer.Aot"
PKG_DIR="artifacts/packages"   # 消费者 NuGet.config 的本地源路径（../../artifacts/packages）

step() { printf '\n═══ %s ═══\n' "$1"; }

fail=0

step "0/3 版本残留扫描（含 placeholder 双判据）"
# 旧版本号从 git tag 推导：语义 tag 里除本版本外最新的一个
OLD_VER=$(git tag --list 'v*' --sort=-v:refname | grep -vx "v${VERSION}" | head -1 | sed 's/^v//')
printf '推导旧版本: %s（git tag 序）\n' "${OLD_VER:-无}"
if [ -n "$OLD_VER" ]; then
    bash scripts/release-version-scan.sh "$OLD_VER" "$VERSION" || fail=1
else
    printf '::error::无法从 git tag 推导旧版本号\n'
    fail=1
fi

step "1/3 pack 五包到本地目录（$PKG_DIR）"
rm -rf "$PKG_DIR" && mkdir -p "$PKG_DIR"
for proj in Core SourceGen Sqlite PostgreSql MySql; do
    dotnet pack "src/PalORM.$proj/PalORM.$proj.csproj" -c Release -o "$PKG_DIR"
done
PKG_COUNT=$(ls "$PKG_DIR"/*.nupkg 2>/dev/null | wc -l)
printf '打包数: %s（应 5）\n' "$PKG_COUNT"
[ "$PKG_COUNT" -eq 5 ] || { printf '::error::pack 数量 %s ≠ 5\n' "$PKG_COUNT"; fail=1; }

step "2/3 消费者 restore 走本地包源（复刻 CI Restore package consumer）"
dotnet restore "$CONSUMER_DIR" --configfile "$CONSUMER_DIR/NuGet.config"
printf '消费者 restore OK——占位版本/缺包形态已在此步排除\n'

step "3/3 包契约"
bash scripts/test-package-contract.sh

if [ "$fail" -eq 0 ]; then
    printf '\nPASS 发布预检全绿（%s）——允许 push tag\n' "$VERSION"
else
    printf '\nFAIL 发布预检有失败项——禁止 push tag\n' >&2
    exit 1
fi
