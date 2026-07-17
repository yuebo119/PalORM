#!/usr/bin/env bash
# PalORM 仓库门禁。每条规则独立执行，最终统一返回结果。

set -uo pipefail

PASSED=0
FAILED=0
WARNED=0
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
NC='\033[0m'

count_matches() {
    local pattern="$1"
    shift
    local output
    output=$(git grep -n -E "$pattern" -- "$@" 2>/dev/null || true)
    if [ -z "$output" ]; then
        printf '0\n'
    else
        printf '%s\n' "$output" | wc -l | tr -d ' '
    fi
}

count_public_static_state() {
    local count=0
    local matches
    while IFS= read -r file; do
        matches=$(perl -0777 -ne '
            $count += () = /public\s+static\s+(?:(?:readonly\s+)?(?:Dictionary|List|HashSet|ConcurrentDictionary)<[^;=(){}]+>\s+[A-Za-z_]\w*\s*(?:[;=]|\{[^{}]*\})|[^{;=()]+?\s+[A-Za-z_]\w*\s*\{[^{}]*\bset\s*;[^{}]*\})/g;
            END { print $count }
        ' "$file")
        count=$((count + matches))
    done < <(git ls-files 'src/**/*.cs')
    printf '%s\n' "$count"
}

pass() {
    printf "${GREEN}PASS %s: %s${NC}\n" "$1" "$2"
    PASSED=$((PASSED + 1))
}

fail() {
    printf "${RED}FAIL %s: %s（违规数：%s）${NC}\n" "$1" "$2" "$3"
    FAILED=$((FAILED + 1))
}

warn() {
    printf "${YELLOW}WARN %s: %s（待检查：%s）${NC}\n" "$1" "$2" "$3"
    WARNED=$((WARNED + 1))
}

check_zero() {
    if [ "$3" -eq 0 ]; then
        pass "$1" "$2"
    else
        fail "$1" "$2" "$3"
    fi
}

warn_zero() {
    if [ "$3" -eq 0 ]; then
        pass "$1" "$2"
    else
        warn "$1" "$2" "$3"
    fi
}

printf '═══════ PalORM 门禁扫描 ═══════\n'
printf '时间：%s\n' "$(date '+%Y-%m-%d %H:%M:%S')"
printf '规范：docs/编码规范.md\n\n'

# PalORMException 是有意设计的公共基类；具体异常必须 sealed 或 abstract。
exceptions=$(git grep -n -E 'public[[:space:]]+(class|record class)[[:space:]]+[A-Za-z0-9_]*Exception' -- 'src/**/*.cs' 2>/dev/null || true)
exceptions=$(printf '%s\n' "$exceptions" | grep -v -E 'sealed|abstract|class PalORMException' || true)
[ -z "$exceptions" ] && exception_count=0 || exception_count=$(printf '%s\n' "$exceptions" | wc -l | tr -d ' ')
check_zero G1 '具体异常类型 sealed' "$exception_count"

check_zero G2 'Core 零外部 ORM 依赖' "$(count_matches 'using[[:space:]]+(Dapper|Microsoft\.EntityFrameworkCore|NHibernate|Newtonsoft)' 'src/PalORM.Core/**/*.cs')"
check_zero G3 '运行时零泛型构造' "$(count_matches 'MakeGeneric(Type|Method)' 'src/PalORM.Core/**/*.cs' 'src/PalORM.Sqlite/**/*.cs' 'src/PalORM.PostgreSql/**/*.cs' 'src/PalORM.MySql/**/*.cs')"
check_zero G4 '运行时零反射发现' "$(count_matches '(Type|Assembly)\.GetType|\.Get(Method|Property|Field|Constructor)\(' 'src/PalORM.Core/**/*.cs' 'src/PalORM.Sqlite/**/*.cs' 'src/PalORM.PostgreSql/**/*.cs' 'src/PalORM.MySql/**/*.cs')"
check_zero G5 '运行时零 Expression.Compile' "$(count_matches 'Expression\.Compile|\.Compile\(\)' 'src/PalORM.Core/**/*.cs' 'src/PalORM.Sqlite/**/*.cs' 'src/PalORM.PostgreSql/**/*.cs' 'src/PalORM.MySql/**/*.cs')"
check_zero G6 '运行时零 Activator.CreateInstance' "$(count_matches 'Activator\.CreateInstance' 'src/PalORM.Core/**/*.cs' 'src/PalORM.Sqlite/**/*.cs' 'src/PalORM.PostgreSql/**/*.cs' 'src/PalORM.MySql/**/*.cs')"
check_zero G7 '运行时零 dynamic' "$(count_matches '(^|[^A-Za-z0-9_])dynamic([^A-Za-z0-9_]|$)|DynamicParameters' 'src/PalORM.Core/**/*.cs' 'src/PalORM.Sqlite/**/*.cs' 'src/PalORM.PostgreSql/**/*.cs' 'src/PalORM.MySql/**/*.cs')"
check_zero G8 '零 string.Format 拼接 SQL' "$(count_matches 'string\.Format.*(SELECT|INSERT|UPDATE|DELETE)' 'src/**/*.cs')"

# 扫描所有受跟踪文本，而不是只扫描 src。示例只能使用明显占位符。
connections=$(git grep -n -i -E '(Password|Pwd)=[^;$"'"'"'[:space:]]+|connectionString[[:space:]]*=[[:space:]]*"(Server|Host)=' -- ':!docs/**' ':!**/bin/**' ':!**/obj/**' 2>/dev/null || true)
connections=$(printf '%s\n' "$connections" | grep -v -i -E '(example|sample|placeholder|<password>|\$\{[^}]+\})' || true)
[ -z "$connections" ] && connection_count=0 || connection_count=$(printf '%s\n' "$connections" | wc -l | tr -d ' ')
check_zero G9 '受跟踪文件零硬编码连接凭据' "$connection_count"

warn_zero G10 'DataSession 生命周期需人工复核' "$(count_matches 'DataSession.*(_db|field|property)' 'src/**/*.cs')"
check_zero G11 '零 virtual 导航属性' "$(count_matches 'public.*virtual|virtual.*public' 'src/**/*.cs')"
check_zero G12 '禁止公开 static 可写状态' "$(count_public_static_state)"

cross_provider=0
for pair in \
    'src/PalORM.PostgreSql/**/*.cs|PalORM\.Sqlite|PalORM\.MySql' \
    'src/PalORM.Sqlite/**/*.cs|PalORM\.PostgreSql|PalORM\.MySql' \
    'src/PalORM.MySql/**/*.cs|PalORM\.PostgreSql|PalORM\.Sqlite'; do
    IFS='|' read -r path first second <<< "$pair"
    cross_provider=$((cross_provider + $(count_matches "$first|$second" "$path")))
done
check_zero G13 'Provider 不跨引用' "$cross_provider"

check_zero G14 'SourceGen 不引用运行时 Provider' "$(count_matches 'using[[:space:]]+PalORM\.(Sqlite|PostgreSql|MySql|Testing)' 'src/PalORM.SourceGen/**/*.cs')"
warn_zero G15 '实体优先使用 DateTimeOffset' "$(count_matches 'public[[:space:]]+DateTime[?[:space:]]' 'src/**/*.cs')"
warn_zero G16 '级联删除必须显式启用' "$(count_matches 'OnDelete.*Cascade|Cascade.*Delete' 'src/**/*.cs')"
check_zero G17 '禁止 async void' "$(count_matches 'async[[:space:]]+void' 'src/**/*.cs')"
check_zero G18 '禁止 TransactionScope' "$(count_matches 'TransactionScope' 'src/**/*.cs')"

# SourceGen 是编译期分析器，不是 Native AOT 运行时库。属性可由 Directory.Build.props 继承。
aot_missing=0
while IFS= read -r project; do
    evaluated=$(dotnet msbuild "$project" -nologo -getProperty:IsAotCompatible 2>/dev/null || true)
    if [ "$evaluated" != "true" ]; then
        printf 'IsAotCompatible 未评估为 true：%s（实际：%s）\n' "$project" "${evaluated:-<空>}"
        aot_missing=$((aot_missing + 1))
    fi
done < <(find src -name '*.csproj' ! -path 'src/PalORM.SourceGen/*' -print | sort)
check_zero G19 '运行时项目声明 IsAotCompatible=true' "$aot_missing"

check_zero G20 '禁止同步阻塞异步操作' "$(count_matches '\.Result([^A-Za-z0-9_]|$)|\.Wait\(' 'src/**/*.cs')"
check_zero G21 '生成器不得输出 blanket pragma' "$(count_matches '#pragma warning disable(\\n|\")' 'src/PalORM.SourceGen/**/*.cs')"
check_zero G22 '禁止抑制裁剪与 AOT 警告' "$(count_matches '(NoWarn|pragma warning disable).*(IL2[0-9]{3}|IL3[0-9]{3})' '**/*.cs' '**/*.csproj' 'Directory.Build.props')"
aot_generated=$(grep -rn -E 'Compile[[:space:]]+(Include|Remove)=.*\.g\.cs' test/PalORM.AotTest* --include='*.csproj' 2>/dev/null || true)
[ -z "$aot_generated" ] && aot_generated_count=0 || aot_generated_count=$(printf '%s\n' "$aot_generated" | wc -l | tr -d ' ')
check_zero G23 'AOT 项目不得手工编译生成文件' "$aot_generated_count"

printf '\n通过：%s  警告：%s  失败：%s  总计：%s\n' "$PASSED" "$WARNED" "$FAILED" "$((PASSED + WARNED + FAILED))"
printf '═══════ 扫描完成 ═══════\n'

if [ "$FAILED" -gt 0 ]; then
    exit 1
fi
