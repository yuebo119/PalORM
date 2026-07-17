#!/usr/bin/env bash
# 阶段完成验证。所有自动检查均真实阻断，不输出未经测量的结论。

set -uo pipefail

PHASE="${1:-}"
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        NATIVE_RID=win-x64
        NATIVE_SUFFIX=.exe
        ;;
    Linux*)
        NATIVE_RID=linux-x64
        NATIVE_SUFFIX=
        ;;
    *)
        printf '不支持的 Native AOT 验证平台：%s\n' "$(uname -s)" >&2
        exit 2
        ;;
esac

if [[ ! "$PHASE" =~ ^[0-9]+$ ]]; then
    printf '用法：bash scripts/verify-phase.sh <phase-number>\n' >&2
    exit 2
fi

FAIL=0
run_step() {
    local title="$1"
    shift
    printf '\n--- %s ---\n' "$title"
    if "$@"; then
        printf 'PASS %s\n' "$title"
    else
        local code=$?
        printf 'FAIL %s（退出码：%s）\n' "$title" "$code"
        FAIL=1
    fi
}

printf '═══════════ 阶段 %s 完成验证 ═══════════\n' "$PHASE"
run_step 'Release 全量构建' dotnet build PalORM.slnx -c Release --no-incremental --nologo
run_step '空壳扫描' bash scripts/stub-check.sh src/
run_step '严格 AOT 与规范门禁' bash scripts/gate-check.sh
run_step 'Core 测试' dotnet test test/PalORM.Core.Tests/PalORM.Core.Tests.csproj -c Release --no-restore
run_step 'SourceGen 测试' dotnet test test/PalORM.SourceGen.Tests/PalORM.SourceGen.Tests.csproj -c Release --no-restore

case "$PHASE" in
    0|1)
        ;;
    2|3|4|5|6|7|8)
        run_step 'SQLite 集成测试' dotnet test test/PalORM.Integration.Tests/PalORM.Integration.Tests.csproj -c Release --no-restore -- --treenode-filter '/*/*/*/*[Category!=ExternalDatabase]'
        ;;
    9|10)
        run_step 'SQLite 集成测试' dotnet test test/PalORM.Integration.Tests/PalORM.Integration.Tests.csproj -c Release --no-restore -- --treenode-filter '/*/*/*/*[Category!=ExternalDatabase]'
        run_step 'SQLite Native AOT 发布' dotnet publish test/PalORM.AotTest/PalORM.AotTest.csproj -c Release -r "$NATIVE_RID" --self-contained true -p:PublishAot=true -p:PublishTrimmed=true -p:JsonSerializerIsReflectionEnabledByDefault=false -o artifacts/verify-phase/sqlite
        run_step 'SQLite Native AOT 运行' "artifacts/verify-phase/sqlite/PalORM.AotTest${NATIVE_SUFFIX}"
        run_step '打包 SourceGen' dotnet pack src/PalORM.SourceGen/PalORM.SourceGen.csproj -c Release -o artifacts/packages
        run_step '打包 Core' dotnet pack src/PalORM.Core/PalORM.Core.csproj -c Release -o artifacts/packages
        run_step '打包 SQLite' dotnet pack src/PalORM.Sqlite/PalORM.Sqlite.csproj -c Release -o artifacts/packages
        run_step '验证 NuGet 包契约' bash scripts/test-package-contract.sh
        run_step '还原 NuGet consumer' dotnet restore test/PalORM.PackageConsumer.Aot/PalORM.PackageConsumer.Aot.csproj --configfile test/PalORM.PackageConsumer.Aot/NuGet.config
        run_step 'NuGet consumer Native AOT 发布' dotnet publish test/PalORM.PackageConsumer.Aot/PalORM.PackageConsumer.Aot.csproj -c Release -r "$NATIVE_RID" --self-contained true --no-restore -p:PublishAot=true -p:PublishTrimmed=true -p:JsonSerializerIsReflectionEnabledByDefault=false -o artifacts/verify-phase/package-consumer
        run_step 'NuGet consumer Native AOT 运行' "artifacts/verify-phase/package-consumer/PalORM.PackageConsumer.Aot${NATIVE_SUFFIX}"
        ;;
    *)
        printf 'FAIL 未定义阶段：%s\n' "$PHASE"
        FAIL=1
        ;;
esac

printf '\n'
if [ "$FAIL" -eq 0 ]; then
    printf '═══════ 阶段 %s 验证通过 ═══════\n' "$PHASE"
else
    printf '═══════ 阶段 %s 验证失败 ═══════\n' "$PHASE"
fi

exit "$FAIL"
