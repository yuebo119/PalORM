#!/usr/bin/env bash
# 验证行动项中的文件路径和源码标识符是否存在。

set -euo pipefail

if [ $# -ne 1 ]; then
    printf '用法：bash scripts/verify-action-items.sh <action-items-file>\n' >&2
    exit 2
fi

ACTION_FILE="$1"
if [ ! -f "$ACTION_FILE" ]; then
    printf '错误：文件不存在：%s\n' "$ACTION_FILE" >&2
    exit 2
fi

printf '═══════ Action Items 验证 ═══════\n'
printf '文件：%s\n\n' "$ACTION_FILE"

MISSING=0
FOUND=0
SKIPPED=0

is_path() {
    [[ "$1" == */* ]] || [[ "$1" =~ \.(cs|csproj|slnx|md|sh|yml|yaml|props|targets|json|xml)$ ]]
}

is_ignored_token() {
    [[ "$1" =~ ^(P[0-3]|AUD-[0-9]+|ITM(-[0-9]+)?|PASS|FAIL|WARN|SKIP|urgent|near|future|assess)$ ]]
}

while IFS= read -r identifier; do
    [ -z "$identifier" ] && continue
    if is_ignored_token "$identifier"; then
        SKIPPED=$((SKIPPED + 1))
        continue
    fi

    if is_path "$identifier"; then
        if [ -e "$identifier" ]; then
            FOUND=$((FOUND + 1))
        else
            printf 'FAIL 文件不存在：%s\n' "$identifier"
            MISSING=$((MISSING + 1))
        fi
        continue
    fi

    if git grep -F -q -- "$identifier" src test scripts docs .github 2>/dev/null; then
        FOUND=$((FOUND + 1))
    else
        printf 'FAIL 标识符未找到：%s\n' "$identifier"
        MISSING=$((MISSING + 1))
    fi
done < <(grep -oE '`[^`]+`' "$ACTION_FILE" | tr -d '`' | sort -u || true)

printf '\n找到：%s  缺失：%s  跳过：%s\n' "$FOUND" "$MISSING" "$SKIPPED"
printf '═══════ 验证完成 ═══════\n'

[ "$MISSING" -eq 0 ]
