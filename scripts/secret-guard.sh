#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# PalORM 敏感信息拦截器 v2（pre-commit hook）
# 安装: cp scripts/secret-guard.sh .git/hooks/pre-commit && chmod +x .git/hooks/pre-commit
# 调试: SECRET_GUARD_DEBUG=1 bash .git/hooks/pre-commit
# ═══════════════════════════════════════════════════════════════
set -uo pipefail  # 不用 -e：单条 grep 无匹配返回 1 会杀整个脚本

RED='\033[0;31m'
NC='\033[0m'
FAIL=0
DEBUG="${SECRET_GUARD_DEBUG:-0}"
[ "$DEBUG" = "1" ] && echo "── DEBUG MODE ──"

# ─── 1. 文件名黑名单 ───
FILE_BLACKLIST='\.env$|\.env\.[^e]|\.pem$|\.key$|\.pfx$|\.p12$|id_rsa|id_ed25519|id_ecdsa|\.ppk$|nuget\.config$|credentials$|apikey\.?'

# ─── 2. 内容模式（每行独立 grep -E，兼容 POSIX ERE）───
check_content() {
    local file="$1"
    local content
    content=$(git show ":$file" 2>/dev/null | grep -viE \
        'Password=\*\*\*|Password=xxx|Password=<password>|Password=change-me|Password=\$\{|PALORM_.*_PASSWORD|pwd=\|connectionString|example|placeholder|sample|template|gate-check\.sh|secret-guard\.sh|安全红线' \
        || true)
    [ -z "$content" ] && return 0
    [ "$DEBUG" = "1" ] && echo "  [debug] content after whitelist: $(echo "$content" | head -1)"

    # 密码（连接串）
    echo "$content" | grep -inE '(Password|Pwd|passwd)[[:space:]]*=[[:space:]]*[^$*<[[:space:]|;][^;[:space:]|"]{5,}' > /dev/null 2>&1 && { echo "  → 密码泄露"; return 1; }
    # GitHub Token
    echo "$content" | grep -inE '(ghp_|gho_|ghu_|ghs_|github_pat_)[A-Za-z0-9_]{20,}' > /dev/null 2>&1 && { echo "  → GitHub Token"; return 1; }
    # API Key（20+ 字符）
    echo "$content" | grep -inE '(api[_-]?key|apikey)[[:space:]]*[=:][[:space:]]*["'"'"']?[A-Za-z0-9_-]{20,}' > /dev/null 2>&1 && { echo "  → API Key"; return 1; }
    # AWS Access Key
    echo "$content" | grep -inE 'AKIA[0-9A-Z]{16}' > /dev/null 2>&1 && { echo "  → AWS Key"; return 1; }
    # Bearer Token
    echo "$content" | grep -inE 'Bearer[[:space:]]+[A-Za-z0-9_.-]{20,}' > /dev/null 2>&1 && { echo "  → Bearer Token"; return 1; }
    # Azure 连接串
    echo "$content" | grep -inE 'DefaultEndpointsProtocol.*AccountKey=' > /dev/null 2>&1 && { echo "  → Azure Key"; return 1; }
    # 私钥
    echo "$content" | grep -inE -- '-----BEGIN[[:space:]]+(RSA[[:space:]]+)?PRIVATE[[:space:]]+KEY-----' > /dev/null 2>&1 && { echo "  → 私钥"; return 1; }
    # NuGet API Key
    echo "$content" | grep -inE '(nuget|npm|pypi|crates)[_:][[:space:]]*[A-Za-z0-9_-]{30,}' > /dev/null 2>&1 && { echo "  → 包管理器 Key"; return 1; }
    # 内网 IP
    echo "$content" | grep -inE '(Host|Server)[[:space:]]*=[[:space:]]*(192[.]168|10[.][0-9]+)[.][0-9]+[.][0-9]+' > /dev/null 2>&1 && { echo "  → 内网 IP"; return 1; }
    return 0
}

echo "═══ 敏感信息拦截器 ═══"

# 获取 staged 文件
STAGED=$(git diff --cached --name-only --diff-filter=ACM 2>/dev/null)
[ -z "$STAGED" ] && { echo "✓ 无 staged 文件"; exit 0; }
[ "$DEBUG" = "1" ] && echo "  [debug] staged: $STAGED"

for file in $STAGED; do
    [ "$DEBUG" = "1" ] && echo "  [debug] checking: $file"

    # 文件名检查
    if echo "$file" | grep -qiE "$FILE_BLACKLIST" 2>/dev/null; then
        echo -e "${RED}✗ 文件名违规${NC}: $file"
        FAIL=$((FAIL+1))
        continue
    fi

    # 跳过二进制
    if file "$file" 2>/dev/null | grep -q "binary"; then
        [ "$DEBUG" = "1" ] && echo "  [debug] skip binary: $file"
        continue
    fi

    # 跳过检测脚本自身（含模式文本，必然自匹配）
    case "$file" in
        scripts/secret-guard.sh|.git/hooks/pre-commit) continue ;;
    esac

    # 内容检查
    REASON=$(check_content "$file")
    if [ $? -ne 0 ]; then
        echo -e "${RED}✗ 内容违规${NC}: $file $REASON"
        FAIL=$((FAIL+1))
    fi
done

if [ $FAIL -gt 0 ]; then
    echo ""
    echo -e "${RED}═══ 拦截: $FAIL 个文件含敏感信息 ═══${NC}"
    echo "修复后重新 git add && git commit"
    echo "确认误报: git commit --no-verify"
    exit 1
else
    echo "✓ 敏感信息检查通过"
fi
