#!/usr/bin/env bash
# PalORM 性能测评统一入口（docs/性能基准规范.md v2 §5）。
#
# 用法:
#   bash scripts/perf.sh smoke                    # 三套夹具最小档冒烟（SQLite，约 5 分钟）
#   bash scripts/perf.sh full                     # 全量（三方言）+ 门禁 + 唯一报告
#   bash scripts/perf.sh compare <基线worktree> <轮数> [选项]   # 交替 A/B（转发 perfhub-ab.sh）
#   bash scripts/perf.sh gate                     # 只跑门禁（BDN 基线 + 结果库基线）
#   bash scripts/perf.sh report                   # 只重建统一报告（不重跑夹具）
#   bash scripts/perf.sh index                    # 只重建跨夹具索引（单独查看用）
#
# 设计：本脚本只做编排，不改任何夹具的口径——每步都调用该夹具自己的既有入口，
# 保证"从统一入口跑"与"单独跑某套夹具"得到同样的结果。
# 报告只有一个产物：bench/reports/perf-report-<时间戳>.md（在全部夹具写完成结果库之后生成）。

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE_PROJECT="$ROOT_DIR/tools/PalORM.PerfGate"
BDN_RESULTS="$ROOT_DIR/BenchmarkDotNet.Artifacts/results"
BASELINE_BDN="$ROOT_DIR/bench/baselines/perf-baseline.json"
BASELINE_INDEX="$ROOT_DIR/bench/baselines/perfhub-index-baseline.json"

# 三方言凭证：与集成测试同一口径，从 gitignored 的 .env.test 读入（不回显值）。
# 缺文件时不中断——sqlite 档不需要凭证，pg/mysql 档由各自夹具自行报"未设置"。
if [ -f "$ROOT_DIR/scripts/set-test-env.sh" ]; then
    # shellcheck disable=SC1091
    . "$ROOT_DIR/scripts/set-test-env.sh" >/dev/null 2>&1 || echo "（.env.test 未就绪：pg/mysql 档将报缺凭证）"
fi

usage() {
    cat <<'EOF'
用法:
  bash scripts/perf.sh smoke                    # 三套夹具最小档冒烟（SQLite，约 5 分钟）
  bash scripts/perf.sh full                     # 全量（三方言）+ 门禁 + 唯一报告
  bash scripts/perf.sh compare <基线worktree> <轮数> [选项]   # 交替 A/B（转发 perfhub-ab.sh）
  bash scripts/perf.sh gate                     # 只跑门禁（BDN 基线 + 结果库基线）
  bash scripts/perf.sh report                   # 只重建统一报告（不重跑夹具）
  bash scripts/perf.sh index                    # 只重建跨夹具索引（单独查看用）

设计：本脚本只做编排，不改任何夹具的口径——每步都调用该夹具自己的既有入口，
保证"从统一入口跑"与"单独跑某套夹具"得到同样的结果。
报告只有一个产物：bench/reports/perf-report-<时间戳>.md（在全部夹具写完成结果库之后生成）。
EOF
}

step() {
    echo ""
    echo "═══════════════════════════════════════════"
    echo " $1"
    echo "═══════════════════════════════════════════"
}

# ── 实时进度与计时 ────────────────────────────────────────────────────────────
# 全量流程动辄半小时以上，"跑完了没有、还要多久、时间花在哪一步"是操作时最需要的信息。
# 每步跑完立即打印「本步耗时 + 累计」，末尾再汇总成表。
TOTAL_T0=$SECONDS
STEP_TOTAL=5
STEP_IDX=0
STEP_ROWS=()

fmt_dur() {
    local s="$1"
    if [ "$s" -ge 3600 ]; then printf '%dh%dm' $((s / 3600)) $(((s % 3600) / 60))
    elif [ "$s" -ge 60 ]; then printf '%dm%02ds' $((s / 60)) $((s % 60))
    else printf '%ds' "$s"; fi
}

# 打印累计进度表——每步结束后调用，让"跑到哪了"随时可见。
# 表格只有 ASCII 列参与宽度填充，且不设表头——两个原因：
#  ① bash 在 C locale 下按**字节**计宽（一个中文 3 字节、✓ 也 3 字节），对含中文或 ✓ 的列
#     做 %-Ns 填充必然错位（实测过）；
#  ② 不设表头就不需要"表头也要对齐"这件事——列自带"本步/累计"字样，语义自解释。
# 中文的步骤名放最后、不填充；状态用 ASCII 的 OK/FAIL。
print_progress_table() {
    local i
    echo ""
    echo "┌ 进度 ────────────────────────────────────────────────────────────────"
    for ((i = 0; i < ${#STEP_ROWS[@]}; i++)); do
        IFS='|' read -r name status secs cum <<< "${STEP_ROWS[$i]}"
        printf '│ %-4s 本步 %-8s 累计 %-9s %s
' "$status" "$(fmt_dur "$secs")" "$(fmt_dur "$cum")" "$name"
    done
    echo "└──────────────────────────────────────────────────────────────────────"
}

# 末尾汇总表——把五步的耗时与占比一次列全，方便回看"时间花在哪一步"。
print_final_table() {
    local total=$((SECONDS - TOTAL_T0)) i pct
    echo ""
    echo "╭─ 全量跑测汇总 $(date '+%Y-%m-%d %H:%M') ──────────────────────────────"
    for ((i = 0; i < ${#STEP_ROWS[@]}; i++)); do
        IFS='|' read -r name status secs cum <<< "${STEP_ROWS[$i]}"
        pct="-"
        [ "$total" -gt 0 ] && pct="$((secs * 100 / total))%"
        printf '│ %-4s %-9s %-5s 累计 %-9s %s
' "$status" "$(fmt_dur "$secs")" "$pct" "$(fmt_dur "$cum")" "$name"
    done
    echo "│"
    printf '│ 总计 %s（OK=通过 FAIL=失败；报告：bench/reports/perf-report-<时间戳>.md）
' "$(fmt_dur "$total")"
    echo "╰──────────────────────────────────────────────────────────────────────"
}

gate() {
    step "[门禁 1/2] 微基准 BDN 基线"
    if [ -f "$BASELINE_BDN" ]; then
        dotnet run --project "$GATE_PROJECT" -c Release -- \
            check --results "$BDN_RESULTS" --baseline "$BASELINE_BDN"
    else
        echo "跳过：缺 $BASELINE_BDN"
    fi

    step "[门禁 2/2] 结果库基线（PerfHub 能力矩阵 + DapperSuite 哨兵）"
    if [ -f "$BASELINE_INDEX" ]; then
        dotnet run --project "$GATE_PROJECT" -c Release -- \
            check-index --baseline "$BASELINE_INDEX"
    else
        echo "跳过：缺 $BASELINE_INDEX（先跑 record-index 录制）"
    fi
}

index() {
    step "[索引] 从结果库重建 bench/reports/perf-index.md"
    dotnet run --project "$GATE_PROJECT" -c Release -- index
}

# 唯一报告产物（规范 §6）：BDN 门禁明细 + 负载/内存 + 跨夹具批次登记 + 维度总览，一份文件。
# 负载/内存 JSON 与启动量具结果由 run-full-perf.sh 留在固定位置，此处按最新取用。
report() {
    step "[报告] 统一报告（BDN 门禁明细 + 负载/内存 + 跨夹具批次登记 + 维度总览）"
    local bench_bin="$ROOT_DIR/bench/PalORM.Benchmarks/bin/Release/net11.0"
    local workload memory startup
    workload="$(ls -t "$bench_bin"/workload-sqlite-*.json 2>/dev/null | head -1 || true)"
    memory="$(ls -t "$bench_bin"/memory-sqlite.json 2>/dev/null | head -1 || true)"
    startup="$(cat "$ROOT_DIR/bench/reports/last-startup-status.txt" 2>/dev/null || true)"
    mkdir -p "$ROOT_DIR/bench/reports"
    local out="$ROOT_DIR/bench/reports/perf-report-$(date +%Y%m%d-%H%M%S).md"

    local args=(--results "$BDN_RESULTS" --baseline "$BASELINE_BDN"
        --envelopes "$ROOT_DIR/bench/results" --index-baseline "$BASELINE_INDEX"
        --out "$out")
    [ -n "$workload" ] && args+=(--workload "$workload")
    [ -n "$memory" ] && args+=(--memory "$memory")
    [ -n "$startup" ] && args+=(--startup "$startup")

    # 报告步不因门禁有回归而失败——报告的价值恰恰在于如实呈现回归；退出码由 gate 步负责
    set +e
    dotnet run --project "$GATE_PROJECT" -c Release -- report "${args[@]}"
    set -e
    echo "报告: $out"
}

cmd="${1:-}"
if [ -n "$cmd" ]; then shift; fi

case "$cmd" in
    smoke)
        step "[1/4] 微基准冒烟（单点查询，BDN）"
        dotnet run --project "$ROOT_DIR/bench/PalORM.Benchmarks" -c Release -- \
            --filter '*ADO_NET_GetByKey*'

        step "[2/4] PerfHub 冒烟（SQLite 2000 档，quick）"
        dotnet run --project "$ROOT_DIR/bench/PalORM.PerfHub" -c Release -- \
            run --dialects sqlite --tiers 2000 --quick

        step "[3/4] DapperSuite 冒烟（SQLite 单行，官方形状）"
        DAPPER_SUITE_DIALECT=sqlite dotnet run --project "$ROOT_DIR/bench/PalORM.DapperSuite" -c Release -- \
            --filter '*SqlCommand*' --join

        step "[4/4] 索引报告"
        index
        ;;
    full)
        STEP_FAILED=()
        run_step() {
            local name="$1"
            shift
            STEP_IDX=$((STEP_IDX + 1))
            step "[$STEP_IDX/$STEP_TOTAL] $name"
            local t0=$SECONDS status="OK"
            # 单步失败不中断全量：全量的价值是最大覆盖，每步的结果库登记与健康度会如实反映状态；
            # 失败汇总到末尾并以非零退出（不静默吞掉）。
            if ! "$@"; then
                status="FAIL"
                STEP_FAILED+=("$name")
            fi
            local secs=$((SECONDS - t0)) cum=$((SECONDS - TOTAL_T0))
            STEP_ROWS+=("[$STEP_IDX/$STEP_TOTAL] $name|$status|$secs|$cum")
            # 实时反馈：本步耗时与累计，不等流程结束就能判断"是不是卡住了"
            echo ""
            echo "  $status 本步 $(fmt_dur "$secs")，累计 $(fmt_dur "$cum")"
            print_progress_table
        }

        # 步骤 1 跳过其内部报告：统一报告必须等三套夹具都写完结果库信封后再生成
        #（否则报告里的跨夹具节只含上一轮的批次——顺序错了就会得出"PerfHub 未采集"的假缺口）。
        run_step "微基准全量 + 负载 + 内存 + 启动 + BDN 门禁" \
            env SKIP_REPORT=1 bash "$ROOT_DIR/scripts/run-full-perf.sh"
        run_step "PerfHub 三方言全量（含并发扩展档）" \
            dotnet run --project "$ROOT_DIR/bench/PalORM.PerfHub" -c Release -- \
            run --dialects sqlite,mysql,pg --tiers 2000,20000 --concurrency --threads 1,4,8
        # DapperSuite 只跑 SQLite：它的定位是"与 Dapper 官方数字可对照的外部锚点"，
        # 而官方数字是单机 SQLite 的——跑 PG/MySQL 得不到可对照的外部锚点，只是白花 2/3 时间
        #（2026-09-23 精简）。哨兵目的（换驱动/换大版本时复测）一个方言足够。
        run_step "DapperSuite SQLite（官方形状锚点）" \
            bash "$ROOT_DIR/scripts/dappersuite-run.sh" sqlite
        run_step "门禁（BDN 基线 + 结果库基线）" gate
        # [5/5] 唯一报告产物：BDN 门禁明细 + 负载/内存 + 跨夹具批次登记 + 维度总览
        run_step "统一报告" report

        if [ ${#STEP_FAILED[@]} -gt 0 ]; then
            echo ""
            echo "全量跑测有失败步骤：${STEP_FAILED[*]}" >&2
            print_final_table
            exit 1
        fi
        echo ""
        echo "全量跑测完成：五步全通过，总耗时 $(fmt_dur "$((SECONDS - TOTAL_T0))")。"
        print_final_table
        ;;
    compare)
        # 交替 A/B 是跨版本对比的唯一可信方式（规范 §5），直接转发编排器
        bash "$ROOT_DIR/scripts/perfhub-ab.sh" "$@"
        ;;
    gate)
        gate
        ;;
    report)
        report
        ;;
    index)
        index
        ;;
    *)
        usage
        exit 1
        ;;
esac
