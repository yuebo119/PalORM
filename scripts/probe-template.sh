#!/usr/bin/env bash
# 探针骨架生成器——review 系统 probe-first 的基建。
# 用法: bash scripts/probe-template.sh <探针名> [provider: sqlite|pg|mysql]
# 生成 /tmp/palorm-probe-<名>/ 最小工程（引用本仓库 Core+Provider+SourceGen），
# 写 Program.cs 后 dotnet run 即可。成本 ~30 秒，替代每次手搓（~5 分钟）。

set -euo pipefail
cd "$(dirname "$0")/.."
REPO="$(pwd)"

NAME="${1:?用法: probe-template.sh <探针名> [sqlite|pg|mysql]}"
PROVIDER="${2:-sqlite}"

case "$PROVIDER" in
    sqlite) PRJ="PalORM.Sqlite"; USING="PalORM.Sqlite"; PROV="SqliteProvider"; CS='Data Source=:memory:' ;;
    pg)     PRJ="PalORM.PostgreSql"; USING="PalORM.PostgreSql"; PROV="PostgreSqlProvider"; CS='$ENV:PALORM_PG_CONNECTION' ;;
    mysql)  PRJ="PalORM.MySql"; USING="PalORM.MySql"; PROV="MySqlProvider"; CS='$ENV:PALORM_MYSQL_CONNECTION' ;;
    *) echo "未知 provider: $PROVIDER（sqlite|pg|mysql）"; exit 1 ;;
esac

DIR="/tmp/palorm-probe-$NAME"
mkdir -p "$DIR"

cat > "$DIR/probe.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <AnalysisLevel>none</AnalysisLevel>
    <NoWarn>\$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$REPO/src/PalORM.Core/PalORM.Core.csproj" />
    <ProjectReference Include="$REPO/src/$PRJ/$PRJ.csproj" />
    <ProjectReference Include="$REPO/src/PalORM.SourceGen/PalORM.SourceGen.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
EOF

if [ ! -f "$DIR/Program.cs" ]; then
cat > "$DIR/Program.cs" <<EOF
using PalORM;
using $USING;

// 探针: $NAME —— 断言写在下方，结论打印到 stdout（证实/证伪一行说清）
[Table("probe_entity")]
public partial class ProbeEntity
{
    [Key] public long Id { get; set; }
    [Column("name")] public string Name { get; set; } = "";
}

internal static class Program
{
    private static async Task Main()
    {
        await using var db = await DataSession<$PROV>.CreateAsync(
            new DbOptions { ConnectionString = "$CS" });
        await db.MigrateAsync();
        // TODO: 探针主体
        Console.WriteLine("probe $NAME: TODO");
    }
}
EOF
fi

echo "探针工程: $DIR"
echo "编辑 $DIR/Program.cs 后: cd $DIR && dotnet run"
