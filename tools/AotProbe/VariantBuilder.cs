using System.Globalization;
using System.Text;

namespace PalORM.AotProbe;

internal enum ProbeGroup { A, B }

/// <summary>jit = 框架依赖基线（适配器正确性对照，显式不开 PublishAot——特性开关陷阱：
/// csproj 静态设 PublishAot=true 会向 runtimeconfig 注入 IsDynamicCodeSupported=false，
/// 连 JIT 构建也被模拟成 AOT，2026-10-28 实测 SqlSugar 误判之源）；aot = 主测定；
/// trimjit/root = 仅 AOT 失败后的诊断臂（条件升级阶梯）。</summary>
internal enum ProbeForm { Jit, Aot, TrimJit, Root }

/// <summary>生成"每库×组×形态一个独立变体工程"到 tmp/aotprobe/variants/。
/// <para>闭包隔离是硬约束：多库混进一个宿主会互相污染 ILC 裁剪与 B 组"实体仅 typeof"前提。
/// 变体是生成物（tmp/ 已 gitignore，不入库）；适配器源（adapters/*.cs）才是真源。</para></summary>
internal static class VariantBuilder
{
    internal sealed record BuiltVariant(string Directory, string ProjectPath, string BinaryPath, string RunViaDotnet);

    internal static string VariantsRoot(string repoRoot) => Path.Combine(repoRoot, "tmp", "aotprobe", "variants");

    /// <summary>中和文件：切断根 Directory.Build.props（Sonar/CPM/TreatWarningsAsErrors/版本号），
    /// 变体完全自含——第三方告警是采集数据，不是构建失败。</summary>
    private static void WriteNeutralProps(string repoRoot)
    {
        string probeRoot = Path.Combine(repoRoot, "tmp", "aotprobe");
        Directory.CreateDirectory(probeRoot);
        string neutral = Path.Combine(probeRoot, "Directory.Build.props");
        if (!File.Exists(neutral))
            File.WriteAllText(neutral, "<Project />\n");
    }

    /// <summary>落盘一个变体并返回二进制路径约定。group=B 时同时确保 bentities 工程存在。
    /// extraDefines：efcore 的 AOT 形态在编译模型生成后二次重建时注入 EF_COMPILED。</summary>
    internal static BuiltVariant Create(
        string repoRoot, AotProbeManifest.Library lib, ProbeGroup group, ProbeForm form, string rid, string adapterFile,
        string? extraDefines = null)
    {
        string libDir = Path.Combine(VariantsRoot(repoRoot), lib.Id);
        string variantDir = Path.Combine(libDir, $"{(group == ProbeGroup.A ? "a" : "b")}-{FormName(form)}");
        Directory.CreateDirectory(variantDir);
        WriteNeutralProps(repoRoot);

        (string sharedCatalog, string adapterCommon, string? rawChannel, string adapterPath, string entitiesPath) =
            ResolveSources(repoRoot, lib, adapterFile);
        string hostFile = Path.Combine(variantDir, "Host.cs");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "templates", "VariantHost.cs"), hostFile, overwrite: true);

        var csproj = new StringBuilder();
        csproj.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        csproj.AppendLine("  <PropertyGroup>");
        csproj.AppendLine("    <OutputType>Exe</OutputType>");
        csproj.AppendLine("    <TargetFramework>net11.0</TargetFramework>");
        csproj.AppendLine("    <LangVersion>latest</LangVersion>");
        csproj.AppendLine("    <Nullable>enable</Nullable>");
        csproj.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        csproj.AppendLine("    <RootNamespace>AotProbe.Variant</RootNamespace>");
        csproj.AppendLine("    <AssemblyName>AotProbeVariant</AssemblyName>");
        csproj.AppendLine("    <IsPackable>false</IsPackable>");
        csproj.AppendLine("    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>");
        csproj.AppendLine("    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>");
        csproj.AppendLine("    <GenerateDocumentationFile>false</GenerateDocumentationFile>");
        csproj.AppendLine("    <AnalysisLevel>none</AnalysisLevel>");
        csproj.AppendLine("    <EnableNETAnalyzers>false</EnableNETAnalyzers>");
        // 与 PalORM 包消费先例（PackageConsumer.Aot）同轨：runtime-async 特性开关
        csproj.AppendLine("    <Features>runtime-async=on</Features>");
        if (group == ProbeGroup.B)
            csproj.AppendLine("    <DefineConstants>$(DefineConstants);AOTPROBE_B</DefineConstants>");
        if (extraDefines is not null)
            csproj.AppendLine(CultureInfo.InvariantCulture, $"    <DefineConstants>$(DefineConstants);{extraDefines}</DefineConstants>");
        AppendLibSpecific(csproj, lib.Id);
        csproj.AppendLine("  </PropertyGroup>");
        csproj.AppendLine("  <ItemGroup>");
        foreach (var pkg in lib.Packages)
        {
            string privateAssets = pkg.PrivateAssets ? " PrivateAssets=\"all\"" : "";
            csproj.AppendLine(CultureInfo.InvariantCulture,
                $"    <PackageReference Include=\"{pkg.Name}\" Version=\"{pkg.Version}\"{privateAssets} />");
        }
        csproj.AppendLine("  </ItemGroup>");
        csproj.AppendLine("  <ItemGroup>");
        csproj.AppendLine(CultureInfo.InvariantCulture, $"    <Compile Include=\"{sharedCatalog}\" Link=\"shared/ProbeCatalog.cs\" />");
        csproj.AppendLine(CultureInfo.InvariantCulture, $"    <Compile Include=\"{adapterCommon}\" Link=\"adapter/common/AdapterValues.cs\" />");
        if (rawChannel is not null)
            csproj.AppendLine(CultureInfo.InvariantCulture, $"    <Compile Include=\"{rawChannel}\" Link=\"adapter/common/RawChannel.cs\" />");
        csproj.AppendLine(CultureInfo.InvariantCulture, $"    <Compile Include=\"{adapterPath}\" Link=\"adapter/LibAdapter.cs\" />");
        if (group == ProbeGroup.A)
            csproj.AppendLine(CultureInfo.InvariantCulture, $"    <Compile Include=\"{entitiesPath}\" Link=\"adapter/Entities.cs\" />");
        // Host.cs 位于变体目录内，SDK 默认 glob 自动包含——显式 Include 会触发 NETSDK1022 重复项
        csproj.AppendLine("  </ItemGroup>");
        if (group == ProbeGroup.B)
        {
            csproj.AppendLine("  <ItemGroup>");
            csproj.AppendLine("    <ProjectReference Include=\"../bentities/BEntities.csproj\" />");
            csproj.AppendLine("  </ItemGroup>");
        }
        AppendRootBlock(csproj, lib, group, form);
        csproj.AppendLine("</Project>");

        string projectPath = Path.Combine(variantDir, "AotProbeVariant.csproj");
        File.WriteAllText(projectPath, csproj.ToString());

        if (group == ProbeGroup.B)
            CreateBEntities(libDir, lib, entitiesPath);

        bool frameworkDependent = form == ProbeForm.Jit;
        string exeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string binaryPath = frameworkDependent
            ? Path.Combine(variantDir, "bin", "Release", "net11.0", "AotProbeVariant.dll")
            : Path.Combine(variantDir, "publish", "AotProbeVariant" + exeSuffix);
        return new BuiltVariant(variantDir, projectPath, binaryPath, frameworkDependent ? "dotnet" : "");
    }

    /// <summary>B 组实体程序集（AotModels.First 先例：实体独立、源生成注册表跨程序集合并）。</summary>
    private static void CreateBEntities(string libDir, AotProbeManifest.Library lib, string entitiesPath)
    {
        string dir = Path.Combine(libDir, "bentities");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net11.0</TargetFramework>");
        sb.AppendLine("    <LangVersion>latest</LangVersion>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
        sb.AppendLine("    <RootNamespace>AotProbe.Adapters</RootNamespace>");
        sb.AppendLine("    <AssemblyName>BEntities</AssemblyName>");
        sb.AppendLine("    <IsPackable>false</IsPackable>");
        sb.AppendLine("    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>");
        sb.AppendLine("    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>");
        sb.AppendLine("    <GenerateDocumentationFile>false</GenerateDocumentationFile>");
        sb.AppendLine("    <AnalysisLevel>none</AnalysisLevel>");
        sb.AppendLine("    <EnableNETAnalyzers>false</EnableNETAnalyzers>");
        sb.AppendLine("    <Features>runtime-async=on</Features>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");
        foreach (var pkg in lib.Packages)
        {
            string privateAssets = pkg.PrivateAssets ? " PrivateAssets=\"all\"" : "";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    <PackageReference Include=\"{pkg.Name}\" Version=\"{pkg.Version}\"{privateAssets} />");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("  <ItemGroup>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    <Compile Include=\"{entitiesPath}\" Link=\"Entities.cs\" />");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");
        File.WriteAllText(Path.Combine(dir, "BEntities.csproj"), sb.ToString());
    }

    /// <summary>解析变体需链接的源资产路径（与 Create 拆分以满足 S3776 认知复杂度预算）。</summary>
    private static (string SharedCatalog, string AdapterCommon, string? RawChannel, string AdapterPath, string EntitiesPath)
        ResolveSources(string repoRoot, AotProbeManifest.Library lib, string adapterFile)
    {
        string toolDir = Path.Combine(repoRoot, "tools", "AotProbe");
        // raw 旁路（SQLite ADO）三类库不链接：litedb（无包）、postgres 库（自带 PG 旁路）、其余全链
        string? rawChannel = lib.Id == "litedb" || lib.Provider != "sqlite" ? null
            : Forward(Path.Combine(toolDir, "adapters", "common", "RawChannel.cs"));
        return (Forward(Path.Combine(toolDir, "shared", "ProbeCatalog.cs")),
            Forward(Path.Combine(toolDir, "adapters", "common", "AdapterValues.cs")),
            rawChannel,
            Forward(Path.Combine(toolDir, "adapters", adapterFile)),
            Forward(Path.Combine(toolDir, "adapters", $"{lib.Id}.Entities.cs")));
    }

    /// <summary>库特定 csproj 注入（独立小方法，也为 Create 的认知复杂度预算减负）。</summary>
    private static void AppendLibSpecific(StringBuilder csproj, string libId)
    {
        if (libId != "dapper-aot")
            return;
        // [assembly: DapperAot] 全量拦截走 C# 拦截器机制，须启用其命名空间（CS9137 提示，2026-10-28）
        csproj.AppendLine("    <InterceptorsNamespaces>$(InterceptorsNamespaces);Dapper.AOT</InterceptorsNamespaces>");
    }


    /// <summary>Root 对照臂块：B 组 root 实体程序集；A 组 root 宿主 + 库包程序集
    /// （TrimmerRootAssembly = 官方支持的保根形态，供判定模型 R5c"需配置"档）。</summary>
    private static void AppendRootBlock(StringBuilder csproj, AotProbeManifest.Library lib, ProbeGroup group, ProbeForm form)
    {
        if (form != ProbeForm.Root)
            return;
        csproj.AppendLine("  <ItemGroup>");
        if (group == ProbeGroup.B)
        {
            csproj.AppendLine("    <TrimmerRootAssembly Include=\"BEntities\" />");
        }
        else
        {
            csproj.AppendLine("    <TrimmerRootAssembly Include=\"AotProbeVariant\" />");
            foreach (var pkg in lib.Packages)
                csproj.AppendLine(CultureInfo.InvariantCulture,
                    $"    <TrimmerRootAssembly Include=\"{pkg.Name}\" />");
        }
        csproj.AppendLine("  </ItemGroup>");
    }

    internal static string FormName(ProbeForm form) => form switch
    {
        ProbeForm.Jit => "jit",
        ProbeForm.Aot => "aot",
        ProbeForm.TrimJit => "trimjit",
        _ => "root",
    };

    private static string Forward(string path) => path.Replace('\\', '/');
}
