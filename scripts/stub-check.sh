#!/usr/bin/env bash
# 扫描明确的占位实现。合法 fluent 方法必须改变状态，不能靠方法名排除。

set -euo pipefail

DIR="${1:-src/}"
COUNT=0

printf '=== PalORM 空壳扫描 ===\n'

scan() {
    local title="$1"
    local pattern="$2"
    local matches

    printf '\n--- %s ---\n' "$title"
    matches=$(grep -rn -E "$pattern" "$DIR" --include='*.cs' 2>/dev/null || true)
    if [ -n "$matches" ]; then
        printf '%s\n' "$matches"
        COUNT=$((COUNT + $(printf '%s\n' "$matches" | wc -l)))
    else
        printf 'PASS 零发现\n'
    fi
}

scan '直接返回 this 的表达式体' '=>[[:space:]]*this[[:space:]]*;'
scan 'NotImplementedException 占位' 'NotImplementedException'
scan 'TODO 占位异常' 'throw[[:space:]]+new[[:space:]]+NotSupportedException\("(TODO|Not implemented|Not yet implemented)'

printf '\n'
if [ "$COUNT" -gt 0 ]; then
    printf 'FAIL 发现 %s 个明确空壳或占位实现。\n' "$COUNT"
    exit 1
fi

printf 'PASS 无明确空壳或占位实现。\n'
