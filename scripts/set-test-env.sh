#!/usr/bin/env bash
# PalORM 测试环境变量——从未跟踪的 .env.test 加载，不回显值。
#
# 用法: source scripts/set-test-env.sh  (或 . scripts/set-test-env.sh)
# 首次使用: cp .env.test.example .env.test 后填入真实连接串。
#
# 配置体系（双层覆盖）：
#   1. .env.test（本脚本加载，gitignored）—— 设置 PALORM_PG_* / PALORM_MYSQL_* 系列变量
#   2. appsettings.test.json（git 跟踪）—— 引用上述变量做占位符替换
#   3. 优先级：PALORM_*_CONNECTION 整串 > JSON 模板占位符
#
# 详见 .env.test.example 与 appsettings.test.json 注释。

_palorm_env_file="$(dirname "${BASH_SOURCE[0]:-$0}")/../.env.test"

if [ ! -f "$_palorm_env_file" ]; then
    echo "未找到 .env.test——复制 .env.test.example 到仓库根目录的 .env.test 并填入凭据" >&2
    echo "  cp .env.test.example .env.test" >&2
    return 1 2>/dev/null || exit 1
fi

set -a
# shellcheck disable=SC1090
. "$_palorm_env_file"
set +a

# 校验：完整连接串（PALORM_*_CONNECTION）或拆分项（PALORM_*_HOST 等）二选一，至少要有一种
_check_pg() {
    if [ -n "${PALORM_PG_CONNECTION:-}" ]; then
        echo "PALORM_PG_CONNECTION 已设置（整串覆盖，JSON 模板将被绕过）"
    elif [ -n "${PALORM_PG_HOST:-}" ]; then
        echo "PALORM_PG_HOST 已设置——appsettings.test.json 模板占位符将被解析"
    else
        echo "警告：PG 凭据未配置——需 PALORM_PG_CONNECTION 或 PALORM_PG_* 拆分项" >&2
    fi
}
_check_mysql() {
    if [ -n "${PALORM_MYSQL_CONNECTION:-}" ]; then
        echo "PALORM_MYSQL_CONNECTION 已设置（整串覆盖，JSON 模板将被绕过）"
    elif [ -n "${PALORM_MYSQL_HOST:-}" ]; then
        echo "PALORM_MYSQL_HOST 已设置——appsettings.test.json 模板占位符将被解析"
    else
        echo "警告：MySQL 凭据未配置——需 PALORM_MYSQL_CONNECTION 或 PALORM_MYSQL_* 拆分项" >&2
    fi
}
_check_pg
_check_mysql
unset -f _check_pg _check_mysql _palorm_env_file

echo "环境变量加载完成——运行 dotnet test 即可"
