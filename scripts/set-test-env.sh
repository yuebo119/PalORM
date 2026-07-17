# PalORM 测试环境变量——从未跟踪的 .env.test 加载，不回显值。
# 用法: source scripts/set-test-env.sh  (或 . scripts/set-test-env.sh)
# 首次使用: cp scripts/.env.test.example .env.test 后填入真实连接串。

_palorm_env_file="$(dirname "${BASH_SOURCE[0]:-$0}")/../.env.test"

if [ ! -f "$_palorm_env_file" ]; then
    echo "未找到 .env.test——复制 scripts/.env.test.example 到仓库根目录的 .env.test 并填入连接串" >&2
    return 1 2>/dev/null || exit 1
fi

set -a
# shellcheck disable=SC1090
. "$_palorm_env_file"
set +a

for _v in PALORM_PG_CONNECTION PALORM_MYSQL_CONNECTION; do
    if [ -n "${!_v:-}" ]; then
        echo "$_v 已设置（值不回显）"
    else
        echo "警告：$_v 未在 .env.test 中定义" >&2
    fi
done
unset _v _palorm_env_file

echo "环境变量加载完成——运行 dotnet test 即可"
