#!/usr/bin/env bash
# PalORM 仓库门禁。每条规则独立执行，最终统一返回结果。

set -euo pipefail

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
# localhost 豁免收窄（ITM-417：Password=x;Host=localhost 曾被整行放行）——
# localhost 行仅在不含非空密码时豁免；.ai 报告与本脚本的自述文本不计
connections=$(printf '%s\n' "$connections" | grep -v -i -E '(example|sample|placeholder|<password>|\$\{[^}]+\}|\.\.\.|change-me|USER.*PASS|palorm_bench)' || true)
connections=$(printf '%s\n' "$connections" | grep -v -E '^(\.ai/|scripts/gate-check\.sh|\.github/PULL_REQUEST_TEMPLATE\.md|CONTRIBUTING\.md)' || true)
# 无密码的 localhost 连接串（测试用 DbOptions / README 示例）豁免
connections=$(printf '%s\n' "$connections" | grep -v -i -E 'localhost.*Database=test' || true)
connections=$(printf '%s\n' "$connections" | grep -v -i -E 'Host=(primary|replica)' || true)
connections=$(printf '%s\n' "$connections" | awk 'BEGIN{IGNORECASE=1} !(/localhost/ && $0 !~ /(Password|Pwd)=[^;"[:space:]]/)' || true)
[ -z "$connections" ] && connection_count=0 || connection_count=$(printf '%s\n' "$connections" | wc -l | tr -d ' ')
check_zero G9 '受跟踪文件零硬编码连接凭据' "$connection_count"

# G10/G15/G16 由警告级升为阻断（2026-07-19：警告级从未产生行动=门禁死项；当前全 0，升级零成本）
check_zero G10 'DataSession 不得长期持有（字段/属性缓存会话）' "$(count_matches 'DataSession.*(_db|field|property)' 'src/**/*.cs')"
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
check_zero G15 '实体禁用裸 DateTime（用 DateTimeOffset）' "$(count_matches 'public[[:space:]]+DateTime[?[:space:]]' 'src/**/*.cs')"
check_zero G16 '级联删除必须显式启用（默认 NO ACTION）' "$(count_matches 'OnDelete.*Cascade|Cascade.*Delete' 'src/**/*.cs')"
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

check_zero G20 '禁止同步阻塞异步操作' "$(count_matches '\.Result([^A-Za-z0-9_]|$)|\.Wait\(|GetAwaiter\(\)\.GetResult\(' 'src/**/*.cs')"
check_zero G21 '生成器不得输出 blanket pragma' "$(count_matches '#pragma warning disable(\\n|\")' 'src/PalORM.SourceGen/**/*.cs')"
check_zero G22 '禁止抑制裁剪与 AOT 警告' "$(count_matches '(NoWarn|pragma warning disable).*(IL2[0-9]{3}|IL3[0-9]{3})|UnconditionalSuppressMessage' 'src/**/*.cs' '**/*.csproj' 'Directory.Build.props')"
aot_generated=$(grep -rn -E 'Compile[[:space:]]+(Include|Remove)=.*\.g\.cs' test/PalORM.AotTest* --include='*.csproj' 2>/dev/null || true)
[ -z "$aot_generated" ] && aot_generated_count=0 || aot_generated_count=$(printf '%s\n' "$aot_generated" | wc -l | tr -d ' ')
check_zero G23 'AOT 项目不得手工编译生成文件' "$aot_generated_count"

# G24：库代码每个 await 必须 ConfigureAwait(false)。跨行统计（await 与 ConfigureAwait 可能不同行）。
# 例外：await using / await foreach（作用于资源与流，无 ConfigureAwait 位点）、await Task.Yield()（YieldAwaitable 无该重载）。
ca_missing=0
while IFS= read -r file; do
    diff=$(perl -0777 -ne '
        $c += () = /\bawait\s+(?!using\b|foreach\b|Task\.Yield\(\))/gs;
        $cf += () = /ConfigureAwait\(false\)/g;
        END { print $c - $cf }
    ' "$file")
    if [ "$diff" -gt 0 ]; then
        printf 'ConfigureAwait 缺失 %s 处：%s\n' "$diff" "$file"
        ca_missing=$((ca_missing + diff))
    fi
done < <(git ls-files 'src/**/*.cs')
check_zero G24 '库代码 await 必须 ConfigureAwait(false)' "$ca_missing"

# G25：公共 async API 必须带 CancellationToken 参数（跨行签名，Dispose 系与显式接口实现豁免）。
ct_missing=0
while IFS= read -r file; do
    matches=$(perl -0777 -ne '
        while (/\bpublic\s+(?:static\s+)?(?:async\s+)?(?:Task|ValueTask)(?:<[^;{()]*>)?\s+([A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*\(([^)]*)\)/gs) {
            my ($name, $params) = ($1, $2);
            next if $name =~ /^(DisposeAsync|DisposeAsyncCore)$/;
            # StopAsync：2.0.1 已发布签名，加可选 ct 为 binary-breaking；3.0 对齐 IHostedService 惯例（见整改账本 API-001）
            next if $name eq "StopAsync";
            next if $params =~ /CancellationToken/;
            print "$name\n";
        }
    ' "$file")
    if [ -n "$matches" ]; then
        while IFS= read -r m; do
            printf 'CancellationToken 缺失：%s 中的 %s\n' "$file" "$m"
            ct_missing=$((ct_missing + 1))
        done <<< "$matches"
    fi
done < <(git ls-files 'src/**/*.cs')
check_zero G25 '公共 async API 必须带 CancellationToken' "$ct_missing"

# G26：QueryBuilder 必须保持 struct（候补 C1 脚本化——class 化即高 QPS 堆分配回退，
# 写时复制语义随之失效）。声明形态改变（class/record class）计为违规。
qb_struct=$(grep -cE '^public struct QueryBuilder<T>' src/PalORM.Core/QueryBuilder.cs 2>/dev/null || true)
# 文件缺失（最小化测试夹具仓库）不计违规；文件存在但非 struct 声明才违规
if [ -f src/PalORM.Core/QueryBuilder.cs ]; then
    check_zero G26 'QueryBuilder 保持 struct 声明' "$((1 - qb_struct))"
else
    check_zero G26 'QueryBuilder 保持 struct 声明' 0
fi

# G27：CS1591 豁免防回退（C3 编译期强制的守卫）——src/ 生效的全局 NoWarn 不得
# 重新纳入 CS1591；豁免仅允许出现在测试/AotModels/Benchmarks 的条件 PropertyGroup。
# 文件缺失（最小化测试夹具仓库）按 0 处理。
if [ -f Directory.Build.props ]; then
    cs1591_global=$(awk '/<PropertyGroup>/,/<\/PropertyGroup>/' Directory.Build.props | grep -c 'CS1591' || true)
else
    cs1591_global=0
fi
check_zero G27 'src/ 全域 CS1591 强制不回退' "$cs1591_global"

# G28：禁止裸 (int)…CommandTimeout/TotalSeconds 截断（ITM-501 下沉）——亚秒超时会塌缩为 0，
# ADO.NET CommandTimeout=0 语义是无限等待。必须走 DbOptions.CommandTimeoutSeconds /
# ToCommandTimeoutSeconds（向上取整）。
timeout_trunc=$(grep -rnE '\(int\)[^;]*(CommandTimeout|_commandTimeout|_timeout)\.TotalSeconds' \
    src --include='*.cs' 2>/dev/null | grep -v '/obj/' | grep -vc 'ToCommandTimeoutSeconds' || true)
check_zero G28 '禁止裸 CommandTimeout.TotalSeconds 截断（用 CommandTimeoutSeconds）' "$timeout_trunc"

# G29：执行型命令必须有 CommandTimeout（ITM-557 下沉）——DataSession.CreateCommand 工厂已集中
# 设置；本检查守护"绕过工厂直接 conn.CreateCommand() 并执行"的路径（暂存/探测命令不执行，豁免）。
# 检测：conn.CreateCommand() 后 20 行内出现 Execute*Async 且其前无 CommandTimeout 赋值。
timeout_missing=0
for f in src/PalORM.Core/*.cs src/PalORM.Sqlite/*.cs src/PalORM.MySql/*.cs src/PalORM.PostgreSql/*.cs; do
    misses=$(awk '/conn\.CreateCommand\(\)|Connection\.CreateCommand\(\)/{ln=FNR; found=0}
        ln && FNR<=ln+20 && /CommandTimeout/{found=1; ln=0}
        ln && FNR<=ln+20 && /Execute(Reader|Scalar|NonQuery)Async/ && !found{cnt++; ln=0}
        END{print cnt+0}' "$f" 2>/dev/null || echo 0)
    timeout_missing=$((timeout_missing + misses))
done
check_zero G29 '绕过 CreateCommand 工厂的执行型命令必须设 CommandTimeout（ITM-557）' "$timeout_missing"

# G30：PalORMAnalyzer 基类链口径统一（ITM-607 下沉）——所有需走基类链的属性枚举必须用
# SourceGenerationValidation.EnumerateMappedProperties，不得用 type.GetMembers().OfType<IPropertySymbol>()。
# ITM-587/588/601/607 同根因类四例：分析器与 TableModel.GetMappableProperties 口径漂移致派生类
# 继承基类列时误报或漏报。
base_chain_violations=$(grep -c 'type\.GetMembers()\.OfType<IPropertySymbol>' \
    src/PalORM.SourceGen/PalORMAnalyzer.cs 2>/dev/null)
[ -z "$base_chain_violations" ] && base_chain_violations=0
check_zero G30 'PalORMAnalyzer 基类链口径统一（用 EnumerateMappedProperties，ITM-607）' "$base_chain_violations"

printf '\n通过：%s  警告：%s  失败：%s  总计：%s\n' "$PASSED" "$WARNED" "$FAILED" "$((PASSED + WARNED + FAILED))"
printf '═══════ 扫描完成 ═══════\n'

if [ "$FAILED" -gt 0 ]; then
    exit 1
fi
