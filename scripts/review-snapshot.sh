#!/usr/bin/env bash
# 生成可复制到评审报告的仓库基线快照。

set -euo pipefail

NO_BUILD=false
if [ "${1:-}" = "--no-build" ]; then
    NO_BUILD=true
elif [ $# -gt 0 ]; then
    printf '用法：bash scripts/review-snapshot.sh [--no-build]\n' >&2
    exit 2
fi

printf '═══════ 评审基线快照 ═══════\n'
printf '时间：%s\n' "$(date '+%Y-%m-%d %H:%M:%S %Z')"
printf '分支：%s\n' "$(git branch --show-current)"
printf '提交：%s\n' "$(git rev-parse HEAD)"
printf '短提交：%s\n' "$(git rev-parse --short HEAD)"
printf 'SDK：%s\n' "$(dotnet --version)"
if [ -n "$(git status --porcelain)" ]; then
    printf '工作树：有未提交变更\n'
else
    printf '工作树：干净\n'
fi

printf '\n─── 最近一次提交变更 ───\n'
git diff --stat HEAD~1 HEAD 2>/dev/null || printf '(初始提交)\n'

printf '\n─── 最近 10 个提交 ───\n'
git log --oneline -10

printf '\n─── 项目结构 ───\n'
find src test -name '*.csproj' ! -path '*/bin/*' ! -path '*/obj/*' -print | sort

printf '\n─── 文件统计 ───\n'
source_count=$(git ls-files 'src/**/*.cs' | grep -v -E '(^|/)(bin|obj|Generated)/|\.g\.cs$' | wc -l | tr -d ' ')
test_count=$(git ls-files 'test/**/*.cs' | grep -v -E '(^|/)(bin|obj|Generated)/|\.g\.cs$' | wc -l | tr -d ' ')
doc_count=$(git ls-files 'docs/*.md' 'docs/**/*.md' | sort -u | wc -l | tr -d ' ')
printf 'C# 源文件：%s\n测试文件：%s\n文档文件：%s\n' "$source_count" "$test_count" "$doc_count"

if $NO_BUILD; then
    printf '\n─── 构建状态 ───\n已跳过（--no-build）\n'
else
    printf '\n─── 构建状态 ───\n'
    build_log=$(mktemp)
    trap 'rm -f "$build_log"' EXIT
    if dotnet build PalORM.slnx -c Release --no-incremental --nologo >"$build_log" 2>&1; then
        summary=$(grep -E '[0-9]+ (个警告|Warning\(s\))|[0-9]+ (个错误|Error\(s\))' "$build_log" | tail -2 || true)
        [ -n "$summary" ] && printf '%s\n' "$summary"
        printf '构建退出码：0\n'
    else
        code=$?
        tail -40 "$build_log"
        printf '构建退出码：%s\n' "$code"
        exit "$code"
    fi
fi

printf '\n═══════ 快照完成 ═══════\n'
