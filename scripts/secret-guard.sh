#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════
# PalORM 敏感信息拦截器 v3（pre-commit hook）— 40 类检测
# 安装: git config core.hooksPath .githooks（见 .githooks/pre-commit）
# 调试: SECRET_GUARD_DEBUG=1 bash .git/hooks/pre-commit
#
# 两种运行模式:
#   默认（staged）      — 扫描暂存区文件（pre-commit 场景）
#   --range BASE..HEAD  — 扫描提交范围内的新增/修改文件（CI 场景，右侧为内容源）
# 自测: bash scripts/secret-guard.sh --selftest
# ═══════════════════════════════════════════════════════════════
set -uo pipefail

RED='\033[0;31m'
NC='\033[0m'
FAIL=0
DEBUG="${SECRET_GUARD_DEBUG:-0}"
[ "$DEBUG" = "1" ] && echo "── DEBUG MODE ──"

# ─── 1. 文件名黑名单（扩展版）───
FILE_BLACKLIST='\.env$|\.env\.[^e]|\.pem$|\.key$|\.pfx$|\.p12$|\.jks$|\.keystore$|id_rsa|id_ed25519|id_ecdsa|id_dsa|\.ppk$|nuget\.config$|credentials$|apikey\.?|\.kube/config|\.aws/|\.gcp/|\.azure/|kubeconfig|\.htpasswd|\.netrc|\.git-credentials|secrets?\.(yml|yaml|json|txt)|\.dockercfg|\.npmrc|\.pypirc|\.gem/credentials|travis\.yml$|\.ssh/|known_hosts|authorized_keys|\.pgpass|\.my\.cnf|\.dbpass|\.vault-token|\.terraform$|\.terraformrc|\.awsvault|serviceaccount'

# 内容规则变量化（B41）：检查逻辑与 --selftest 共用同一正则，防双源漂移
RULE_CONNSTRING_PASSWORD='(Server|Host|Data[[:space:]]Source)[[:space:]]*=[[:space:]]*[^;]+;.*Password[[:space:]]*=[[:space:]]*[^;."]+'
RULE_INTERNAL_DOMAIN='\.(internal|local|corp|intranet|private)([:[:space:]]|$)'
# 白名单过滤正则变量化（B53）：与 --selftest 过滤层向量共用——此前内联于 check_content，
# 白名单改动无任何自测覆盖（本次 uint64 常量豁免即属该盲区）
WHITELIST_FILTER='Password=\*\*\*|Password=xxx|Password=<password>|Password=change-me|Password=\$\{|PALORM_.*_PASSWORD|pwd=\|connectionString|example|placeholder|sample|template|gate-check\.sh|secret-guard\.sh|安全红线|YOUR_.*_HERE|REPLACE_ME|INSERT_|TO_BE_|FIXME|TODO|18446744073709551615'

# ─── 自测模式（B41）：误报向量必须放行、真阳性必须拦截 ───
if [ "${1:-}" = "--selftest" ]; then
    self_fail=0
    st_vec() { # $1=规则 $2=样本 $3=期望（1=应命中拦截）$4=grep 大小写（i=忽略，空=敏感，须与实际调用一致）
        local flags="-nE"; [ "${4:-}" = "i" ] && flags="-inE"
        if printf '%s' "$2" | grep $flags "$1" > /dev/null 2>&1; then got=1; else got=0; fi
        if [ "$got" -ne "$3" ]; then
            echo "SELFTEST FAIL: [$2] 期望命中=$3 实际=$got"
            self_fail=1
        fi
    }
    # 误报向量（2026-08-22 实战样本，期望不命中）
    st_vec "$RULE_CONNSTRING_PASSWORD" 'Host=...;Port=5432;Username=...;Password=...;Database=palorm_bench' 0 i
    st_vec "$RULE_INTERNAL_DOMAIN" 'or Accessibility.Internal' 0
    # 真阳性向量（期望命中）
    st_vec "$RULE_CONNSTRING_PASSWORD" 'Host=prod.db;Port=5432;Password=realpass123' 1 i
    st_vec "$RULE_CONNSTRING_PASSWORD" 'Server=db1.internal;Password=Sup3rS3cret!' 1 i
    st_vec "$RULE_INTERNAL_DOMAIN" 'connect to auth.service.internal now' 1
    # 文件名豁免：根 NuGet.Config 放行、子路径 nuget.config 拦截
    if echo "NuGet.Config" | grep -qiE "$FILE_BLACKLIST" && ! echo "NuGet.Config" | grep -qxE 'NuGet\.Config'; then
        echo "SELFTEST FAIL: 根 NuGet.Config 应豁免"
        self_fail=1
    fi
    if ! echo "sub/dir/nuget.config" | grep -qiE "$FILE_BLACKLIST"; then
        echo "SELFTEST FAIL: 子路径 nuget.config 应在黑名单"
        self_fail=1
    fi
    # ── 白名单过滤层双向向量（B53：过滤层与规则层同等回归）──
    # 期望被过滤：uint64 LIMIT 常量行必须被 WHITELIST_FILTER 吸收（过滤后无残留）
    if printf '%s' 'sb.Append(", 18446744073709551615");' | grep -viE "$WHITELIST_FILTER" | grep -q .; then
        echo "SELFTEST FAIL: uint64 LIMIT 常量行应被白名单过滤"
        self_fail=1
    fi
    # 期望穿透白名单：真阳性连接串不得被白名单误滤（否则规则层永远看不到）
    if ! printf '%s' 'Host=prod.db;Port=5432;Password=realpass123' | grep -viE "$WHITELIST_FILTER" | grep -q .; then
        echo "SELFTEST FAIL: 真阳性连接串不应被白名单误滤"
        self_fail=1
    fi
    if [ "$self_fail" -eq 0 ]; then
        echo "SELFTEST PASS: secret-guard 自测全通过（2 误报 + 3 真阳性 + 2 文件名豁免 + 2 白名单过滤层 B53）"
    fi
    exit "$self_fail"
fi

# ─── 2. 内容检测（40 类）───
# CONTENT_REV：内容读取的 rev 前缀——staged 模式读索引（":"），range 模式读范围右侧提交。
# 与下方 check_content 的 `git show "$CONTENT_REV$file"` 配对，两模式共用同一检测函数。
CONTENT_REV=":"

check_content() {
    local file="$1"
    local content
    # 白名单过滤（占位符/示例/环境变量引用）——正则来自 WHITELIST_FILTER（B53 变量化）
    # 18446744073709551615：MySQL LIMIT 裸 OFFSET 的 uint64 上限惯用常量（QueryBuilder.cs），
    # 20 位连续数字会误触规则 38 身份证号——按行豁免；同行混入其他敏感内容时整行走白名单，
    # 属已接受的窄边界（2026-08-25 提交实证）
    content=$(git show "${CONTENT_REV}${file}" 2>/dev/null | grep -viE "$WHITELIST_FILTER" \
        || true)
    [ -z "$content" ] && return 0

    # ═══ 凭据类 ═══
    # 1. 密码（连接串/配置）
    echo "$content" | grep -inE '(Password|Pwd|passwd|passphrase|secret)[[:space:]]*=[[:space:]]*[^$*<[[:space:]|;][^;[:space:]|"]{5,}' > /dev/null 2>&1 && { echo "→ 密码"; return 1; }
    # 2. API Key
    echo "$content" | grep -inE '(api[_-]?key|apikey|access[_-]?key)[[:space:]]*[=:][[:space:]]*["'"'"']?[A-Za-z0-9_-]{20,}' > /dev/null 2>&1 && { echo "→ API Key"; return 1; }
    # 3. GitHub Token
    echo "$content" | grep -inE '(ghp_|gho_|ghu_|ghs_|github_pat_)[A-Za-z0-9_]{20,}' > /dev/null 2>&1 && { echo "→ GitHub Token"; return 1; }
    # 4. GitLab Token
    echo "$content" | grep -inE 'glpat-[A-Za-z0-9_-]{20,}' > /dev/null 2>&1 && { echo "→ GitLab Token"; return 1; }
    # 5. Bearer Token
    echo "$content" | grep -inE 'Bearer[[:space:]]+[A-Za-z0-9_.-]{20,}' > /dev/null 2>&1 && { echo "→ Bearer Token"; return 1; }
    # 6. JWT（base64 header 特征）
    echo "$content" | grep -inE 'eyJ[A-Za-z0-9_-]{15,}' > /dev/null 2>&1 && { echo "→ JWT"; return 1; }

    # ═══ 云平台凭据 ═══
    # 7. AWS Access Key
    echo "$content" | grep -inE '(AKIA|ASIA)[0-9A-Z]{16}' > /dev/null 2>&1 && { echo "→ AWS Key"; return 1; }
    # 8. AWS Secret Key（40字符 base64）
    echo "$content" | grep -inE 'aws_secret_access_key.*[A-Za-z0-9/+=]{40}' > /dev/null 2>&1 && { echo "→ AWS Secret"; return 1; }
    # 9. Azure 连接串
    echo "$content" | grep -inE 'DefaultEndpointsProtocol.*AccountKey=' > /dev/null 2>&1 && { echo "→ Azure Key"; return 1; }
    # 10. GCP Service Account
    echo "$content" | grep -inE '"type":.*"service_account"|private_key_id' > /dev/null 2>&1 && { echo "→ GCP SA"; return 1; }

    # ═══ 私钥/证书 ═══
    # 11. RSA/EC/DSA 私钥
    echo "$content" | grep -inE -- '-----BEGIN[[:space:]]+(RSA[[:space:]]+|EC[[:space:]]+|DSA[[:space:]]+|OPENSSH[[:space:]]+)?PRIVATE[[:space:]]+KEY-----' > /dev/null 2>&1 && { echo "→ 私钥"; return 1; }
    # 12. SSH 公钥（含真实密钥数据）
    echo "$content" | grep -inE 'ssh-(rsa|ed25519|ecdsa-sha2-nistp256)[[:space:]]+AAA[A-Za-z0-9+/=]{20,}' > /dev/null 2>&1 && { echo "→ SSH 密钥"; return 1; }
    # 13. 证书内容
    echo "$content" | grep -inE -- '-----BEGIN[[:space:]]+CERTIFICATE-----' > /dev/null 2>&1 && { echo "→ 证书"; return 1; }

    # ═══ 数据库连接 ═══
    # 14. 完整连接串（含密码）——密码值须含非点字符：`Password=...`（占位符文档示例）不算泄漏
    echo "$content" | grep -inE "$RULE_CONNSTRING_PASSWORD" > /dev/null 2>&1 && { echo "→ 连接串含密码"; return 1; }
    # 15. MongoDB URI
    echo "$content" | grep -inE 'mongodb(\+srv)?://[^@[:space:]]+:[^@[:space:]]+@' > /dev/null 2>&1 && { echo "→ MongoDB URI"; return 1; }
    # 16. Redis URL
    echo "$content" | grep -inE 'rediss?://:([^@]+)@|redis://[^@]+:[^@]+@' > /dev/null 2>&1 && { echo "→ Redis URL"; return 1; }
    # 17. PostgreSQL URL
    echo "$content" | grep -inE 'postgres(ql)?://[^@]+:[^@]+@' > /dev/null 2>&1 && { echo "→ PG URL"; return 1; }
    # 18. MySQL URL
    echo "$content" | grep -inE 'mysql://[^@]+:[^@]+@' > /dev/null 2>&1 && { echo "→ MySQL URL"; return 1; }

    # ═══ 第三方服务 ═══
    # 19. Slack Token
    echo "$content" | grep -inE '(xoxb|xoxp|xoxa|xoxs)-[A-Za-z0-9-]{10,}|hooks\.slack\.com/services/' > /dev/null 2>&1 && { echo "→ Slack Token"; return 1; }
    # 20. Discord Webhook
    echo "$content" | grep -inE 'discord(\.gg/|\.com/api/webhooks/)' > /dev/null 2>&1 && { echo "→ Discord"; return 1; }
    # 21. Telegram Bot Token
    echo "$content" | grep -inE 'api\.telegram\.org/bot[0-9]+:' > /dev/null 2>&1 && { echo "→ Telegram"; return 1; }
    # 22. SendGrid Key
    echo "$content" | grep -inE 'SG\.[A-Za-z0-9_-]{20,}' > /dev/null 2>&1 && { echo "→ SendGrid"; return 1; }
    # 23. Twilio SID
    echo "$content" | grep -inE '(AC|SK)[0-9a-f]{32}' > /dev/null 2>&1 && { echo "→ Twilio"; return 1; }
    # 24. Stripe Key
    echo "$content" | grep -inE '(sk|pk|rk)_live_[A-Za-z0-9]{20,}' > /dev/null 2>&1 && { echo "→ Stripe Key"; return 1; }
    # 25. PayPal
    echo "$content" | grep -inE 'client_secret.*[A-Za-z0-9]{20,}' > /dev/null 2>&1 && { echo "→ PayPal Secret"; return 1; }

    # ═══ 包管理器 ═══
    # 26. NuGet / NPM / PyPI / Cargo
    echo "$content" | grep -inE '(nuget|npm|pypi|crates)[_:][[:space:]]*[A-Za-z0-9_-]{30,}' > /dev/null 2>&1 && { echo "→ 包管理器 Key"; return 1; }
    # 27. NPM Token（npm_ 前缀）
    echo "$content" | grep -inE 'npm_[A-Za-z0-9]{30,}' > /dev/null 2>&1 && { echo "→ NPM Token"; return 1; }
    # 28. PyPI Token
    echo "$content" | grep -inE 'pypi-AgEIcHlwaS5vcmc' > /dev/null 2>&1 && { echo "→ PyPI Token"; return 1; }

    # ═══ 容器/编排 ═══
    # 29. Docker auth
    echo "$content" | grep -inE '"auth":.*"auth":|dockerconfigjson' > /dev/null 2>&1 && { echo "→ Docker Auth"; return 1; }
    # 30. K8s Secret
    echo "$content" | grep -inE 'kind:[[:space:]]*Secret|kubectl.*secret' > /dev/null 2>&1 && { echo "→ K8s Secret"; return 1; }

    # ═══ 网络 ═══
    # 31. 内网 IP
    echo "$content" | grep -inE '(Host|Server)[[:space:]]*=[[:space:]]*(192[.]168|10[.][0-9]+)[.][0-9]+[.][0-9]+' > /dev/null 2>&1 && { echo "→ 内网 IP"; return 1; }
    # 32. 内部域名——大小写敏感匹配（域名惯例小写；C# 限定符如 `Accessibility.Internal` 不算域名）
    echo "$content" | grep -nE "$RULE_INTERNAL_DOMAIN" > /dev/null 2>&1 && { echo "→ 内部域名"; return 1; }
    # 33. 非标端口+凭据组合
    echo "$content" | grep -inE ':(5432|3306|6379|27017|9200)@' > /dev/null 2>&1 && { echo "→ 端口+凭据"; return 1; }

    # ═══ OAuth/加密 ═══
    # 34. OAuth client_secret
    echo "$content" | grep -inE 'client[_-]?secret["'"'"']?[:=]["'"'"']?[A-Za-z0-9_-]{20,}' > /dev/null 2>&1 && { echo "→ OAuth Secret"; return 1; }
    # 35. 加密密钥
    echo "$content" | grep -inE '(encryption|signing|aes|hmac)[_-]?key["'"'"']?[:=]["'"'"']?[A-Fa-f0-9]{32,}' > /dev/null 2>&1 && { echo "→ 加密密钥"; return 1; }

    # ═══ CI/CD 环境变量 ═══
    # 36. CI Token（硬编码值）
    echo "$content" | grep -inE '(GITHUB_TOKEN|GITLAB_TOKEN|JENKINS_|CI_TOKEN|BUILD_TOKEN)["'"'"']?[:=]["'"'"']?[A-Za-z0-9_-]{20,}' > /dev/null 2>&1 && { echo "→ CI Token"; return 1; }

    # ═══ PII（个人身份信息）═══
    # 37. 邮箱地址（含密码场景）
    echo "$content" | grep -inE '[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}.*[Pp]ass' > /dev/null 2>&1 && { echo "→ 邮箱+密码"; return 1; }
    # 38. 身份证号（18位）
    echo "$content" | grep -inE '[0-9]{17}[0-9Xx]' > /dev/null 2>&1 && { echo "→ 身份证号"; return 1; }
    # 39. 银行卡号（16-19位连续数字）
    echo "$content" | grep -inE '"[0-9]{16,19}"|[[:space:]][0-9]{16,19}[[:space:]]' > /dev/null 2>&1 && { echo "→ 银行卡号"; return 1; }

    # ═══ Web/Session ═══
    # 40. Session ID / Cookie 值
    echo "$content" | grep -inE '(session[_-]?id|session[_-]?key|csrf[_-]?token)["'"'"']?[:=]["'"'"']?[A-Fa-f0-9]{32,}' > /dev/null 2>&1 && { echo "→ Session ID"; return 1; }

    return 0
}

echo "═══ 敏感信息拦截器 v3（40 类检测）═══"

# ─── 3. 运行模式解析 ───
# staged（默认）：pre-commit 场景，扫暂存区；range：CI 场景，扫提交范围右侧。
# CI 此前"构造 staged 环境 + 直接运行 hook"恒空转（git diff --cached 在 CI 无暂存）——
# range 模式让同一套 40 类规则真正消费 CI 的差异文件集。
MODE="staged"
RANGE=""
if [ "${1:-}" = "--range" ]; then
    if [ -z "${2:-}" ]; then
        echo "用法: secret-guard.sh --range BASE..HEAD" >&2
        exit 2
    fi
    RANGE="$2"
    MODE="range"
    # 内容源 = 范围右侧提交（a..b 取 b）；--diff-filter=ACM 已排除删除文件，右侧必存在
    CONTENT_REV="${RANGE##*..}:"
fi

if [ "$MODE" = "staged" ]; then
    STAGED=$(git diff --cached --name-only --diff-filter=ACM 2>/dev/null)
    [ -z "$STAGED" ] && { echo "✓ 无 staged 文件"; exit 0; }
else
    STAGED=$(git diff --name-only --diff-filter=ACM "$RANGE" 2>/dev/null)
    [ -z "$STAGED" ] && { echo "✓ 范围内无新增/修改文件: $RANGE"; exit 0; }
fi
[ "$DEBUG" = "1" ] && echo "  [debug] mode=$MODE files: $STAGED"

for file in $STAGED; do
    [ "$DEBUG" = "1" ] && echo "  [debug] checking: $file"

    if echo "$file" | grep -qiE "$FILE_BLACKLIST" 2>/dev/null \
        && ! echo "$file" | grep -qxE 'NuGet\.Config'; then
        # 豁免：仓库根的 NuGet.Config 是版本化受控配置（仅源映射，无凭据——G32 门禁对象）；
        # 子目录或其他机器的 nuget.config（可能含 packageSourceCredentials）仍拦截
        echo -e "${RED}✗ 文件名违规${NC}: $file"
        FAIL=$((FAIL+1))
        continue
    fi

    if file "$file" 2>/dev/null | grep -q "binary"; then
        continue
    fi

    case "$file" in
        # 自豁免：门禁脚本与其回归夹具按设计包含检测向量（真阳性样本），不适用内容检查
        scripts/secret-guard.sh|scripts/test-quality-scripts.sh|.git/hooks/pre-commit) continue ;;
    esac

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
    echo "✓ 敏感信息检查通过（40 类）"
fi
