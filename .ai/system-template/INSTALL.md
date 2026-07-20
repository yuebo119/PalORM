# PalORM AI 质量系统——安装指南

## 快速安装

```bash
# 1. 复制模板到新项目
cp -r .ai/system-template/ /path/to/new-project/.ai-system/

# 2. 运行安装脚本
cd /path/to/new-project
bash .ai-system/install-ai-system.sh
```

安装脚本会：
- 询问是否启用
- 复制 AGENTS.md → 项目根目录（ZCode 自动加载）
- 复制 .ai/ → 项目根目录（lessons.md 等）
- 复制 scripts/tech-debt-scan.sh → scripts/
- 复制 .github/PULL_REQUEST_TEMPLATE.md → .github/

## 手动配置（安装脚本不自动处理）

### 1. .editorconfig

从 PalORM 仓库复制 `.editorconfig`，按项目语言/框架调整：
- C# → 保持 SonarAnalyzer 规则（需安装 NuGet 包）
- TypeScript → 调整为 ESLint 规则
- Python → 调整为 ruff/flake8 规则

### 2. SonarAnalyzer.CSharp（C# 项目）

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="SonarAnalyzer.CSharp" Version="10.29.0.143774" />

<!-- Directory.Build.props -->
<ItemGroup>
  <PackageReference Include="SonarAnalyzer.CSharp">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

### 3. CI 集成

`.github/workflows/ci.yml` 添加 gate job 步骤：

```yaml
- name: Run tech debt scan
  run: bash scripts/tech-debt-scan.sh
```

### 4. 按项目调整

- `.ai/lessons.md` 的铁律（C# 特有项改为项目语言）
- `scripts/tech-debt-scan.sh` 的检查项
- `AGENTS.md` 的构建命令

## 模板文件清单

| 文件 | 用途 |
|------|------|
| `AGENTS.md.template` | 项目级 AGENTS.md（ZCode 自动加载） |
| `.ai-template/lessons.md` | AI 规范系统手册（精简通用版） |
| `tech-debt-scan.sh.template` | 技术债扫描脚本（通用版） |
| `PULL_REQUEST_TEMPLATE.md.template` | PR 检查清单 |
| `INSTALL.md` | 本文件 |
| `install-ai-system.sh` | 引导安装脚本 |
