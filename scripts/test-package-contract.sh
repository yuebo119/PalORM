#!/usr/bin/env bash
# 验证最终 NuGet 包的依赖契约，而不是源码 ProjectReference 假象。

set -euo pipefail

ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
PACKAGES="$TMP/packages"
mkdir -p "$PACKAGES" "$TMP/consumer" "$TMP/analyzer-consumer"

for project in Core SourceGen Sqlite PostgreSql MySql Testing; do
    dotnet pack "src/PalORM.$project/PalORM.$project.csproj" -c Release -o "$PACKAGES" --nologo >/dev/null
done

nuspec=$(unzip -p "$PACKAGES/PalORM.Testing.5.0.0.nupkg" '*.nuspec')
for dependency in PalORM.Core PalORM.Sqlite PalORM.PostgreSql PalORM.MySql; do
    if ! grep -q "dependency id=\"$dependency\" version=\"5.0.0\"" <<< "$nuspec"; then
        printf 'FAIL PalORM.Testing 缺少包依赖：%s\n' "$dependency"
        exit 1
    fi
done

cat > "$TMP/consumer/Consumer.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="PalORM.Testing" Version="5.0.0" />
  </ItemGroup>
</Project>
XML

cat > "$TMP/consumer/Program.cs" <<'CS'
using PalORM.Testing;

global::System.Func<
    global::System.Threading.CancellationToken,
    global::System.Threading.Tasks.Task<
        PalORM.DataSession<PalORM.Sqlite.SqliteProvider>>> sqlite =
    TestDb.SqliteAsync;
global::System.Func<
    global::System.Threading.CancellationToken,
    global::System.Threading.Tasks.Task<
        PalORM.DataSession<PalORM.PostgreSql.PostgreSqlProvider>>> postgres =
    TestDb.PostgreSqlAsync;
global::System.Func<
    global::System.Threading.CancellationToken,
    global::System.Threading.Tasks.Task<
        PalORM.DataSession<PalORM.MySql.MySqlProvider>>> mysql =
    TestDb.MySqlAsync;

_ = sqlite;
_ = postgres;
_ = mysql;
CS

nuget_packages="$PACKAGES"
nuget_global_packages="$TMP/global-packages"
if command -v cygpath >/dev/null 2>&1; then
    nuget_packages=$(cygpath -w "$nuget_packages")
    nuget_global_packages=$(cygpath -w "$nuget_global_packages")
fi

cat > "$TMP/consumer/NuGet.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$nuget_global_packages" />
  </config>
  <packageSources>
    <clear />
    <add key="local" value="$nuget_packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="PalORM.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
XML

dotnet build "$TMP/consumer/Consumer.csproj" -c Release --configfile "$TMP/consumer/NuGet.config" --nologo
printf 'PASS PalORM.Testing NuGet 依赖与独立消费者契约\n'

cat > "$TMP/analyzer-consumer/AnalyzerConsumer.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="PalORM.Core" Version="5.0.0" />
    <PackageReference Include="PalORM.SourceGen" Version="5.0.0"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
XML

cat > "$TMP/analyzer-consumer/Models.cs" <<'CS'
[global::PalORM.Table("root_users")]
public sealed partial class RootUser
{
    [global::PalORM.Key]
    public long Id { get; set; }
}

namespace Alpha
{
    [global::PalORM.Table("alpha_users")]
    public sealed partial class User
    {
        [global::PalORM.Key]
        public long Id { get; set; }
    }
}

namespace Beta
{
    [global::PalORM.Table("beta_users")]
    public sealed partial class User
    {
        [global::PalORM.Key]
        public long Id { get; set; }
    }
}
CS

cat > "$TMP/analyzer-consumer/Program.cs" <<'CS'
internal static class Program
{
    private static int Main()
    {
        global::System.Type[] expected =
        [
            typeof(global::RootUser),
            typeof(global::Alpha.User),
            typeof(global::Beta.User)
        ];
        foreach (global::System.Type type in expected)
        {
            if (!global::PalORM.PalORM_Runtime.TableNames.ContainsKey(type))
                return 1;
        }

        return expected[1] == expected[2] ? 2 : 0;
    }
}
CS

cp "$TMP/consumer/NuGet.config" "$TMP/analyzer-consumer/NuGet.config"
dotnet run --project "$TMP/analyzer-consumer/AnalyzerConsumer.csproj" -c Release \
    --configfile "$TMP/analyzer-consumer/NuGet.config" --nologo
printf 'PASS SourceGen 包加载、禁用隐式 using 与实体身份契约\n'
