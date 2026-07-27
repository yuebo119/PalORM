#!/usr/bin/env bash
# 验证最终 NuGet 包的依赖契约，而不是源码 ProjectReference 假象。
# 仅验证公开发布的包（PalORM.Testing 是内部测试库，IsPackable=false，不在验证范围）。

set -euo pipefail

ROOT=$(git rev-parse --show-toplevel)
cd "$ROOT"

# 从 Directory.Build.props 动态读取版本号（与包版本同源，避免硬编码漂移）
VERSION=$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props | head -1)
if [ -z "$VERSION" ]; then
    printf 'FAIL 无法从 Directory.Build.props 读取 Version\n' >&2
    exit 1
fi
printf 'INFO 检测到版本 %s\n' "$VERSION"

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
PACKAGES="$TMP/packages"
mkdir -p "$PACKAGES" "$TMP/analyzer-consumer"

# 打包公开发布的 5 个项目（Testing 是内部库不打包）
for project in Core SourceGen Sqlite PostgreSql MySql; do
    dotnet pack "src/PalORM.$project/PalORM.$project.csproj" -c Release -o "$PACKAGES" --nologo >/dev/null
done

cat > "$TMP/analyzer-consumer/AnalyzerConsumer.csproj" <<XML
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
    <PackageReference Include="PalORM.Core" Version="$VERSION" />
    <PackageReference Include="PalORM.SourceGen" Version="$VERSION"
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
    [global::PalORM.Column("name")] public string Name { get; set; } = "";
}

namespace Alpha
{
    [global::PalORM.Table("alpha_users")]
    public sealed partial class User
    {
        [global::PalORM.Key]
        public long Id { get; set; }
        [global::PalORM.Column("name")] public string Name { get; set; } = "";
    }
}

namespace Beta
{
    [global::PalORM.Table("beta_users")]
    public sealed partial class User
    {
        [global::PalORM.Key]
        public long Id { get; set; }
        [global::PalORM.Column("name")] public string Name { get; set; } = "";
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

nuget_packages="$PACKAGES"
nuget_global_packages="$TMP/global-packages"
if command -v cygpath >/dev/null 2>&1; then
    nuget_packages=$(cygpath -w "$nuget_packages")
    nuget_global_packages=$(cygpath -w "$nuget_global_packages")
fi

cat > "$TMP/analyzer-consumer/NuGet.config" <<XML
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

dotnet run --project "$TMP/analyzer-consumer/AnalyzerConsumer.csproj" -c Release \
    --configfile "$TMP/analyzer-consumer/NuGet.config" --nologo
printf 'PASS SourceGen 包加载、禁用隐式 using 与实体身份契约\n'
