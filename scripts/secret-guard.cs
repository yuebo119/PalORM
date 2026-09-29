// secret-guard.cs（自 secret-guard.sh 迁移，脚本 C# 化整改方案 T2-4）
// ═══════════════════════════════════════════════════════════════
// PalORM 敏感信息拦截器 v3（pre-commit hook）— 40 类检测
// 安装: git config core.hooksPath .githooks（见 .githooks/pre-commit）
//
// 三种运行模式:
//   默认（staged）      — 扫描暂存区文件（pre-commit 场景）
//   --range BASE..HEAD  — 扫描提交范围内的新增/修改文件（CI 场景，右侧为内容源）
//   --selftest          — 内置双向量自测（误报必须放行、真阳性必须拦截）
// 退出路径显式完备：selftest → exit self_fail；主流程 → FAIL>0 ? exit 1 : exit 0。
// ═══════════════════════════════════════════════════════════════
// 行为基线：与 .sh 版逐字节对拍（--selftest + 暂存夹具矩阵 + 全历史 range 双跑）。
// 唯一已知同构盲区（非差异）：git 默认 quotepath 下非 ASCII 路径两侧同样
// 静默跳过内容检查——该盲区的修复属独立变更，不混入迁移轮。
using System.Text;
using System.Text.RegularExpressions;

// 输出统一 UTF-8 无 BOM + LF（bash 对拍字节兼容，跨平台一致）
Console.OutputEncoding = new UTF8Encoding(false);
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });

return args.Length > 0 && args[0] == "--selftest" ? RunSelfTest() : RunMain(args);

static int RunSelfTest()
{
    var selfFail = 0;

    void Vec(Regex rule, string sample, int expected, bool ignoreCase)
    {
        var pattern = ignoreCase ? new Regex(rule.ToString(), RegexOptions.IgnoreCase) : new Regex(rule.ToString());
        var got = pattern.IsMatch(sample) ? 1 : 0;
        if (got != expected)
        {
            Console.WriteLine($"SELFTEST FAIL: [{sample}] 期望命中={expected} 实际={got}");
            selfFail = 1;
        }
    }

    // 误报向量（2026-08-22 实战样本，期望不命中）
    Vec(Rules.ConnStringPassword, "Host=...;Port=5432;Username=...;Password=...;Database=palorm_bench", 0, true);
    Vec(Rules.InternalDomain, "or Accessibility.Internal", 0, false);
    // 真阳性向量（期望命中）
    Vec(Rules.ConnStringPassword, "Host=prod.db;Port=5432;Password=realpass123", 1, true);
    Vec(Rules.ConnStringPassword, "Server=db1.internal;Password=Sup3rS3cret!", 1, true);
    Vec(Rules.InternalDomain, "connect to auth.service.internal now", 1, false);
    // 文件名豁免：根 NuGet.Config 放行、子路径 nuget.config 拦截
    if (Rules.FileBlacklist.IsMatch("NuGet.Config") && !Rules.NuGetConfigExact.IsMatch("NuGet.Config"))
    {
        Console.WriteLine("SELFTEST FAIL: 根 NuGet.Config 应豁免");
        selfFail = 1;
    }
    void VecFile(string path, int expected)
    {
        var got = Rules.FileBlacklist.IsMatch(path)
            && !Rules.NuGetConfigExact.IsMatch(path)
            && !Rules.EnvExample.IsMatch(path) ? 1 : 0;
        if (got != expected)
        {
            Console.WriteLine($"SELFTEST FAIL: 文件名 [{path}] 期望拦截={expected} 实际={got}");
            selfFail = 1;
        }
    }
    // 误报向量（实际触发过：修改 .env.test.example 被拦，阻断正常提交）
    VecFile(".env.test.example", 0);
    VecFile(".env.example", 0);
    VecFile("scripts/.env.local.example", 0);
    // 真阳性向量（豁免不得放宽到真实凭据文件）
    VecFile(".env", 1);
    VecFile(".env.production", 1);
    VecFile(".env.test", 1);
    VecFile("config/.env.local", 1);
    VecFile("secrets.yml", 1);
    VecFile("id_rsa", 1);
    if (!Rules.FileBlacklist.IsMatch("sub/dir/nuget.config"))
    {
        Console.WriteLine("SELFTEST FAIL: 子路径 nuget.config 应在黑名单");
        selfFail = 1;
    }
    // ── 白名单过滤层双向向量（B53：过滤层与规则层同等回归）──
    // 期望被过滤：uint64 LIMIT 常量行必须被 WHITELIST_FILTER 吸收（过滤后无残留）
    if (!Rules.WhitelistFilter.IsMatch("sb.Append(\", 18446744073709551615\");"))
    {
        Console.WriteLine("SELFTEST FAIL: uint64 LIMIT 常量行应被白名单过滤");
        selfFail = 1;
    }
    // 期望被过滤：尖括号占位符三形态（<pwd> 短形态是 quotepath 盲区修复后暴露的缺口，B53 增补）
    if (!Rules.WhitelistFilter.IsMatch("Host=<host>;Password=<pwd>;Database=<db>")
        || !Rules.WhitelistFilter.IsMatch("Host=<host>;Password=<password>")
        || !Rules.WhitelistFilter.IsMatch("Host=<host>;Password=<your-pwd-here>"))
    {
        Console.WriteLine("SELFTEST FAIL: 尖括号占位符连接串应被白名单过滤");
        selfFail = 1;
    }
    // 期望穿透白名单：真阳性连接串不得被白名单误滤（否则规则层永远看不到）
    if (Rules.WhitelistFilter.IsMatch("Host=prod.db;Port=5432;Password=realpass123"))
    {
        Console.WriteLine("SELFTEST FAIL: 真阳性连接串不应被白名单误滤");
        selfFail = 1;
    }
    if (selfFail == 0)
    {
        Console.WriteLine("SELFTEST PASS: secret-guard 自测全通过（2 误报 + 3 真阳性 + 内容白名单过滤层 B53 + 文件名向量 11：3 误报豁免 + 8 真阳性拦截）");
    }
    return selfFail;
}

static int RunMain(string[] args)
{
    var fail = 0;
    var debug = Environment.GetEnvironmentVariable("SECRET_GUARD_DEBUG") == "1";
    if (debug)
    {
        Console.WriteLine("── DEBUG MODE ──");
    }

    Console.WriteLine("═══ 敏感信息拦截器 v3（40 类检测）═══");

    // 运行模式解析：staged（默认，pre-commit 场景）/ range（CI 场景，右侧为内容源）
    string mode = "staged";
    string range = "";
    string contentRev = ":";
    if (args.Length > 0 && args[0] == "--range")
    {
        if (args.Length < 2 || args[1].Length == 0)
        {
            Console.Error.WriteLine("用法: secret-guard.cs --range BASE..HEAD");
            return 2;
        }
        range = args[1];
        mode = "range";
        // 内容源 = 范围右侧提交（a..b 取 b）；--diff-filter=ACM 已排除删除文件，右侧必存在
        contentRev = range[(range.LastIndexOf("..", StringComparison.Ordinal) + 2)..] + ":";
    }

    string staged;
    if (mode == "staged")
    {
        staged = Git("diff --cached --name-only --diff-filter=ACM");
        if (staged.Length == 0)
        {
            Console.WriteLine("✓ 无 staged 文件");
            return 0;
        }
    }
    else
    {
        staged = Git($"diff --name-only --diff-filter=ACM \"{range}\"");
        if (staged.Length == 0)
        {
            Console.WriteLine($"✓ 范围内无新增/修改文件: {range}");
            return 0;
        }
    }
    if (debug)
    {
        Console.WriteLine($"  [debug] mode={mode} files: {staged.Replace("\n", " ")}");
    }

    foreach (var file in staged.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        if (debug)
        {
            Console.WriteLine($"  [debug] checking: {file}");
        }

        if (Rules.FileBlacklist.IsMatch(file)
            && !Rules.NuGetConfigExact.IsMatch(file)
            && !Rules.EnvExample.IsMatch(file))
        {
            // 豁免一：仓库根的 NuGet.Config 是版本化受控配置（仅源映射，无凭据——G32 门禁对象）；
            // 子目录或其他机器的 nuget.config（可能含 packageSourceCredentials）仍拦截。
            // 豁免二：.env 家族的 *.example 是占位模板（凭据位为 change-me/YOUR_..._HERE），内容规则照常执行。
            Console.WriteLine($"{Rules.Red}✗ 文件名违规{Rules.Nc}: {file}");
            fail++;
            continue;
        }

        if (IsBinary(file))
        {
            continue;
        }

        // 自豁免：门禁脚本与其回归夹具按设计包含检测向量（真阳性样本），不适用内容检查
        if (file is "scripts/secret-guard.cs" or "scripts/secret-guard.sh"
            or "scripts/test-quality-scripts.cs" or "scripts/test-quality-scripts.sh"
            or ".git/hooks/pre-commit")
        {
            continue;
        }

        var content = GitShowFiltered(contentRev, file);
        if (content.Length == 0)
        {
            continue;
        }

        foreach (var (reason, pattern) in Rules.Content)
        {
            if (pattern.IsMatch(content))
            {
                Console.WriteLine($"{Rules.Red}✗ 内容违规{Rules.Nc}: {file} {reason}");
                fail++;
                break;
            }
        }
    }

    if (fail > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"{Rules.Red}═══ 拦截: {fail} 个文件含敏感信息 ═══{Rules.Nc}");
        Console.WriteLine("修复后重新 git add && git commit");
        Console.WriteLine("确认误报: git commit --no-verify");
        return 1;
    }
    Console.WriteLine("✓ 敏感信息检查通过（40 类）");
    return 0;

    static bool IsBinary(string file)
    {
        // 与 .sh 的 `file "$file" | grep -q binary` 对齐的 NUL 探测（git 同款启发式：首 8000 字节）
        try
        {
            using var fs = File.OpenRead(file);
            Span<byte> buf = stackalloc byte[8000];
            var read = fs.Read(buf);
            return buf[..read].Contains((byte)0);
        }
        catch (IOException)
        {
            return false; // 读不了按非二进制处理，交由内容检查（与 .sh 静默行为一致）
        }
    }

    static string GitShowFiltered(string contentRev, string file)
    {
        // git show 内容 → 白名单逐行过滤（B53）；git show 失败（.sh 侧 2>/dev/null）等同空内容。
        // 18446744073709551615（MySQL uint64 LIMIT 常量）等占位/示例行整行剔除后的空内容 = 干净
        var raw = Git($"show \"{contentRev}{file}\"");
        if (raw.Length == 0)
        {
            return "";
        }
        var kept = raw.Split('\n').Where(line => !Rules.WhitelistFilter.IsMatch(line));
        var filtered = string.Join('\n', kept);
        return filtered.Trim('\n').Length == 0 ? "" : filtered;
    }

    static string Git(string arguments)
    {
        // core.quotepath=false：git 默认把非 ASCII 路径转义为八进制形态（"\346\226\207..."），
        // 拿转义名去 git show / 读盘必然失败 → 内容检查静默跳过（盲区：中文路径文件从不被
        // 内容扫描，真阳性泄漏可绕过——2026-09-30 scratch 仓复现实锤后修复）。
        // StandardOutputEncoding=UTF8：本机 Console 编码 GBK 会把 git 输出的 UTF-8 中文路径解码成乱码。
        var psi = new System.Diagnostics.ProcessStartInfo("git", $"-c core.quotepath=false {arguments}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return stdout;
    }
}

// ─── 规则真源（B41：检查逻辑与 --selftest 共用同一正则，防双源漂移）───
internal static class Rules
{
    public const string Red = "\x1b[0;31m";
    public const string Nc = "\x1b[0m";

    // 文件名黑名单（扩展版；-i 大小写不敏感，单行输入）
    public static readonly Regex FileBlacklist = new(
        @"\.env$|\.env\.[^e]|\.pem$|\.key$|\.pfx$|\.p12$|\.jks$|\.keystore$|id_rsa|id_ed25519|id_ecdsa|id_dsa|\.ppk$|nuget\.config$|credentials$|apikey\.?|\.kube/config|\.aws/|\.gcp/|\.azure/|kubeconfig|\.htpasswd|\.netrc|\.git-credentials|secrets?\.(yml|yaml|json|txt)|\.dockercfg|\.npmrc|\.pypirc|\.gem/credentials|travis\.yml$|\.ssh/|known_hosts|authorized_keys|\.pgpass|\.my\.cnf|\.dbpass|\.vault-token|\.terraform$|\.terraformrc|\.awsvault|serviceaccount",
        RegexOptions.Compiled);

    // 根 NuGet.Config 豁免（-x 全串匹配，大小写敏感——与 .sh 的 grep -qxE 一致）
    public static readonly Regex NuGetConfigExact = new(@"^NuGet\.Config$", RegexOptions.Compiled);

    // .env 家族 *.example 豁免（部分匹配，大小写敏感）
    public static readonly Regex EnvExample = new(@"(^|/)\.env(\..+)?\.example$", RegexOptions.Compiled);

    // 白名单过滤层（B53：与 --selftest 过滤层向量共用；-viE 逐行剔除）
    // Password=<...>：连接串模板的尖括号占位符整体豁免（<pwd>/<password>/<your-pwd> 同族；
    // quotepath 盲区修复后 docs/AOT部署指南.md 的 <pwd> 形态首次进入扫描面暴露此缺口——
    // 真实凭据不会包尖括号，泛化安全）
    public static readonly Regex WhitelistFilter = new(
        @"Password=\*\*\*|Password=<[^;>]+>|Password=xxx|Password=change-me|Password=\$\{|PALORM_.*_PASSWORD|pwd=\|connectionString|example|placeholder|sample|template|gate-check\.sh|secret-guard\.sh|安全红线|YOUR_.*_HERE|REPLACE_ME|INSERT_|TO_BE_|FIXME|TODO|18446744073709551615",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 连接串含密码（规则 14，变量化共用）
    public static readonly Regex ConnStringPassword = new(
        @"(Server|Host|Data\s+Source)\s*=\s*[^;]+;.*Password\s*=\s*[^;.""]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 内部域名（规则 32，大小写敏感；Multiline 使 $ 回到逐行语义对齐 grep 逐行输入）
    public static readonly Regex InternalDomain = new(
        @"\.(internal|local|corp|intranet|private)([:\s]|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // 身份证号（规则 38；PCRE 前后向断言 .NET 原生支持；边界防长数字串误判）
    public static readonly Regex IdNumber = new(@"(?<![0-9.])[0-9]{17}[0-9Xx](?![0-9.])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 40 类内容规则（顺序敏感：首中即停；除规则 32 外全部大小写不敏感对齐 grep -i）
    public static readonly (string Reason, Regex Pattern)[] Content = Build();

    private static (string Reason, Regex Pattern)[] Build() =>
    [
        ("→ 密码", new Regex(@"(Password|Pwd|passwd|passphrase|secret)\s*=\s*[^\s$*<|;][^\s;|""]{5,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ API Key", new Regex(@"(api[_-]?key|apikey|access[_-]?key)\s*[=:]\s*[""']?[A-Za-z0-9_-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ GitHub Token", new Regex(@"(ghp_|gho_|ghu_|ghs_|github_pat_)[A-Za-z0-9_]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ GitLab Token", new Regex(@"glpat-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ Bearer Token", new Regex(@"Bearer\s+[A-Za-z0-9_.-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ JWT", new Regex(@"eyJ[A-Za-z0-9_-]{15,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ AWS Key", new Regex(@"(AKIA|ASIA)[0-9A-Z]{16}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ AWS Secret", new Regex(@"aws_secret_access_key.*[A-Za-z0-9/+=]{40}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ Azure Key", new Regex(@"DefaultEndpointsProtocol.*AccountKey=", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ GCP SA", new Regex(@"""type"":.*""service_account""|private_key_id", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ 私钥", new Regex(@"-----BEGIN\s+(RSA\s+|EC\s+|DSA\s+|OPENSSH\s+)?PRIVATE\s+KEY-----", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ SSH 密钥", new Regex(@"ssh-(rsa|ed25519|ecdsa-sha2-nistp256)\s+AAA[A-Za-z0-9+/=]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ 证书", new Regex(@"-----BEGIN\s+CERTIFICATE-----", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ 连接串含密码", ConnStringPassword),
        ("→ MongoDB URI", new Regex(@"mongodb(\+srv)?://[^\s@]+:[^\s@]+@", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ Redis URL", new Regex(@"rediss?://:([^@]+)@|redis://[^@]+:[^@]+@", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ PG URL", new Regex(@"postgres(ql)?://[^@]+:[^@]+@", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ MySQL URL", new Regex(@"mysql://[^@]+:[^@]+@", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ Slack Token", new Regex(@"(xoxb|xoxp|xoxa|xoxs)-[A-Za-z0-9-]{10,}|hooks\.slack\.com/services/", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ Discord", new Regex(@"discord(\.gg/|\.com/api/webhooks/)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ Telegram", new Regex(@"api\.telegram\.org/bot[0-9]+:", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ SendGrid", new Regex(@"SG\.[A-Za-z0-9_-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ Twilio", new Regex(@"(AC|SK)[0-9a-f]{32}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ Stripe Key", new Regex(@"(sk|pk|rk)_live_[A-Za-z0-9]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ PayPal Secret", new Regex(@"client_secret.*[A-Za-z0-9]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ 包管理器 Key", new Regex(@"(nuget|npm|pypi|crates)[_:]\s*[A-Za-z0-9_-]{30,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ NPM Token", new Regex(@"npm_[A-Za-z0-9]{30,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ PyPI Token", new Regex(@"pypi-AgEIcHlwaS5vcmc", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ Docker Auth", new Regex(@"""auth"":.*""auth"":|dockerconfigjson", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ K8s Secret", new Regex(@"kind:\s*Secret|kubectl.*secret", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ 内网 IP", new Regex(@"(Host|Server)\s*=\s*(192[.]168|10[.][0-9]+)[.][0-9]+[.][0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ 内部域名", InternalDomain),
        ("→ 端口+凭据", new Regex(@":(5432|3306|6379|27017|9200)@", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ OAuth Secret", new Regex(@"client[_-]?secret[""']?[:=][""']?[A-Za-z0-9_-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ 加密密钥", new Regex(@"(encryption|signing|aes|hmac)[_-]?key[""']?[:=][""']?[A-Fa-f0-9]{32,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ CI Token", new Regex(@"(GITHUB_TOKEN|GITLAB_TOKEN|JENKINS_|CI_TOKEN|BUILD_TOKEN)[""']?[:=][""']?[A-Za-z0-9_-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ 邮箱+密码", new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}.*[Pp]ass", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ 身份证号", IdNumber),
        ("→ 银行卡号", new Regex(@"""[0-9]{16,19}""|\s[0-9]{16,19}\s", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("→ Session ID", new Regex(@"(session[_-]?id|session[_-]?key|csrf[_-]?token)[""']?[:=][""']?[A-Fa-f0-9]{32,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];
}
