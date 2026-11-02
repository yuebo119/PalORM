using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

namespace PalORM.PerfHub;

/// <summary>环境预检：方言连通的认证级验证 + 容器自愈 + MySQL local_infile 校验。
/// <para><b>为什么必须有</b>：原先的"可用"探测只解析连接串（<see cref="Connections.Resolve"/>），
/// 从不建连接。串能解析、TCP 能通，都不代表认证与参数可用（B193 同族）。2026-11-02 实测：
/// mysql84/pg186 全部 Exited 时 PerfHub 照旧打印"三方言可用"，跑完 4 分半 BDN 与 2 分半 SQLite
/// 段之后才在 MySQL 连接处整段失败，416 项只产出 140 项，整轮作废。</para>
/// <para><b>自愈</b>：连接失败时按端口映射到本机 wslc 容器并执行 <c>wslc container start</c>，
/// 之后按 3 秒间隔重试最多 60 秒；MySQL 的 <c>local_infile</c> 为 0 时置 1（容器重启后归 0，
/// 不设会让批量走多值 INSERT 慢路径，分配涨 2.7 倍）。</para>
/// <para><b>口径</b>：预检不写任何测量数据，不产生信封；它只回答"这台机器现在能不能测"。</para></summary>
internal static class Preflight
{
    /// <summary>端口 → 本机容器名，与 ~/.zcode/scripts/wslc-start-containers.cmd 同源。</summary>
    private static readonly Dictionary<int, string> PortToContainer = new()
    {
        [3306] = "mysql84",
        [5432] = "pg186",
        [55432] = "pgbench",
    };

    private static readonly TimeSpan RetryWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    /// <summary>预检单方言。返回 true 表示可用于测量。</summary>
    public static async Task<bool> CheckAsync(
        DialectInfo info, bool autoFix, TextWriter output, CancellationToken ct = default)
    {
        string cs;
        try
        {
            cs = Connections.Resolve(info);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException
            or IOException or ArgumentException)
        {
            output.WriteLine($"[预检] {info.DisplayName,-11} 跳过 — 连接串不可用：{ex.Message}");
            return false;
        }

        if (info.Dialect == Dialect.Sqlite)
        {
            // SQLite 是进程内库：建连接即结论，无服务器依赖
            try
            {
                await using DbConnection conn = info.OpenConnection(cs);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                output.WriteLine($"[预检] {info.DisplayName,-11} 可用（进程内）");
                return true;
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                output.WriteLine($"[预检] {info.DisplayName,-11} 不可用 — {ex.Message}");
                return false;
            }
        }

        bool ok = await TryOpenAsync(info, cs, ct).ConfigureAwait(false);
        if (!ok && autoFix && TryStartContainerFor(cs, info.DisplayName, output))
        {
            ok = await WaitForOpenAsync(info, cs, output, ct).ConfigureAwait(false);
        }

        if (!ok)
        {
            output.WriteLine(
                $"[预检] {info.DisplayName,-11} 不可用 — 认证级连接失败（容器未启动或凭据不符）");
            return false;
        }

        if (info.Dialect == Dialect.MySql)
        {
            return await CheckLocalInfileAsync(info, cs, autoFix, output, ct).ConfigureAwait(false);
        }

        output.WriteLine($"[预检] {info.DisplayName,-11} 可用");
        return true;
    }

    /// <summary>三方言预检汇总。任一方言不可用即返回 false（调用方据此在跑测前中止）。</summary>
    public static async Task<bool> CheckAllAsync(
        IReadOnlyList<DialectInfo> dialects, bool autoFix, TextWriter output, CancellationToken ct = default)
    {
        bool all = true;
        foreach (DialectInfo info in dialects)
        {
            all &= await CheckAsync(info, autoFix, output, ct).ConfigureAwait(false);
        }

        return all;
    }

    private static async Task<bool> TryOpenAsync(DialectInfo info, string cs, CancellationToken ct)
    {
        try
        {
            await using DbConnection conn = info.OpenConnection(cs);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            _ = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForOpenAsync(
        DialectInfo info, string cs, TextWriter output, CancellationToken ct)
    {
        output.WriteLine($"[预检] {info.DisplayName,-11} 容器启动中，最多等 {RetryWindow.TotalSeconds:F0} 秒…");
        var deadline = DateTime.UtcNow + RetryWindow;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(RetryInterval, ct).ConfigureAwait(false);
            if (await TryOpenAsync(info, cs, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>按端口找容器并启动。端口无法映射到已知容器时返回 false（不做盲猜）。</summary>
    private static bool TryStartContainerFor(string connectionString, string displayName, TextWriter output)
    {
        int port = ResolvePort(connectionString);
        if (!PortToContainer.TryGetValue(port, out string? container))
        {
            output.WriteLine($"[预检] {displayName,-11} 端口 {port} 无对应容器映射，不自愈");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("wslc.exe", $"container start {container}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                output.WriteLine($"[预检] {displayName,-11} wslc 启动 {container} 超时");
                return false;
            }

            output.WriteLine($"[预检] {displayName,-11} 已尝试启动容器 {container}（exit={process.ExitCode}）");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output.WriteLine($"[预检] {displayName,-11} 无法调用 wslc：{ex.Message}");
            return false;
        }
    }

    private static int ResolvePort(string connectionString)
    {
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (string key in new[] { "Port", "port" })
            {
                if (builder.TryGetValue(key, out object? value)
                    && int.TryParse(
                        Convert.ToString(value, CultureInfo.InvariantCulture),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
                {
                    return port;
                }
            }
        }
        catch (ArgumentException)
        {
            // 连接串形态异常时退回默认端口（MySQL 3306 / PG 5432）
        }

        return connectionString.Contains("3306", StringComparison.Ordinal) ? 3306 : 5432;
    }

    /// <summary>MySQL 的 local_infile：为 0 时批量路径退回多值 INSERT（分配涨 2.7 倍）。
    /// 容器重启后服务端变量归 0，故每次跑测前都要看一次；置位需要 SUPER 或 SYSTEM_VARIABLES_ADMIN。</summary>
    private static async Task<bool> CheckLocalInfileAsync(
        DialectInfo info, string cs, bool autoFix, TextWriter output, CancellationToken ct)
    {
        try
        {
            await using DbConnection conn = info.OpenConnection(cs);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            bool on = await ReadLocalInfileAsync(conn, ct).ConfigureAwait(false);
            if (on)
            {
                output.WriteLine($"[预检] {info.DisplayName,-11} 可用（local_infile=1）");
                return true;
            }

            if (!autoFix)
            {
                output.WriteLine($"[预检] {info.DisplayName,-11} 可用，但 local_infile=0（批量走慢路径）");
                return true;
            }

            await using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SET GLOBAL local_infile=1";
                _ = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            on = await ReadLocalInfileAsync(conn, ct).ConfigureAwait(false);
            output.WriteLine(on
                ? $"[预检] {info.DisplayName,-11} 可用（local_infile 已置 1）"
                : $"[预检] {info.DisplayName,-11} 可用，但 local_infile 置位失败（批量走慢路径）");
            return true;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            output.WriteLine($"[预检] {info.DisplayName,-11} local_infile 检查失败：{ex.Message}");
            return false;
        }
    }

    private static async Task<bool> ReadLocalInfileAsync(DbConnection conn, CancellationToken ct)
    {
        await using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@local_infile";
        object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
    }
}
