using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace visland.Workshop;

// 「貓耳小員的請求」放進排程之後的事後分析:排在哪幾個生產日、佔掉多少產能、整週價值差多少。
//
// 為什麼是事後比對而不是讓 FavourIntegration 自己回報:四種 FavourMode 放置的方式完全不同
// (取代最後一份排程 / 同時長替換 / 整天覆寫),但「最後這份排程裡有多少請求產品」是一致的問題,
// 掃一次結果就能答,不必每個模式各寫一份帳。
//
// ⚠️ 這裡的價值是 WorkshopDayFiller.ScoreWeek 的**相對值**,不是實際貝殼幣
//    (少了工房等級加成與熱度,那兩者對所有候選是同一個乘數)。要看的是百分比差,不是絕對值。
public sealed class FavourPlacementReport {
    public bool Valid;
    public string? SkipReason;                          // 非 null = 算不出來;呼叫端要畫「?」不要畫 0

    public readonly List<int> Cycles = [];              // 有請求產品的生產日
    public int OccupiedWorkshopDays;                    // 被請求產品佔用的「工房 × 生產日」格數
    public int TotalWorkshopDays;                       // 這份排程一共有幾格
    public int? CompleteByCycle;                        // 三項全部達標的那個生產日;null = 這份排程做不完
    public readonly int[] Targets = FavourIntegration.FavourTargets;
    public readonly int[] Total = new int[3];           // 整週合計的請求產量(含主題連結的 2 倍)
    public readonly List<(int cycle, int[] cumulative)> Progress = [];
    public bool EarliestFirst;

    public bool ValueKnown;
    public string? ValueSkipReason;
    public float ValueWithout;                          // 完全不放請求的同一份排程
    public float ValueWith;                             // 實際要套用的排程
    public readonly Dictionary<int, float> PerCycleDelta = [];

    // 正數 = 為了請求犧牲掉的比例。ValueKnown == false 時不要顯示這個數字。
    public float LossPercent => ValueWithout > 0 ? 100f * (ValueWithout - ValueWith) / ValueWithout : 0;

    public bool AllMet => CompleteByCycle != null;
    public string CyclesText => Cycles.Count > 0 ? WorkshopDayFiller.FormatCycles(Cycles) : "-";
    public string LossText => ValueKnown ? $"{-LossPercent:+0.00;-0.00;0.00}%" : "?";
    public string CompleteByText => CompleteByCycle is { } c ? $"C{c}" : "?";

    public static FavourPlacementReport Build(
        WorkshopSolver.Recs recs,
        WorkshopSolver.FavourState favours,
        ExcelSheet<MJICraftworksObject> sheet,
        int maxWorkshops,
        WorkshopDayFiller.WeekScore without,
        WorkshopDayFiller.WeekScore with,
        bool earliestFirst) {
        var r = new FavourPlacementReport { EarliestFirst = earliestFirst };
        if (maxWorkshops <= 0) {
            r.SkipReason = "no workshops built";
            return r;
        }
        if (recs.Empty) {
            r.SkipReason = "no schedule loaded";
            return r;
        }

        var running = new int[3];
        foreach (var (cycle, day) in recs.Enumerate()) {
            var perCycle = new int[3];
            var occupied = 0;
            foreach (var (_, w) in day.Enumerate(maxWorkshops)) {
                var before = (int[])perCycle.Clone();
                FavourIntegration.CreditWorkshop(w, favours.CraftObjectIds, perCycle, sheet);
                if (!perCycle.SequenceEqual(before))
                    ++occupied;
            }
            r.TotalWorkshopDays += maxWorkshops;
            if (occupied == 0)
                continue;

            r.Cycles.Add(cycle);
            r.OccupiedWorkshopDays += occupied;
            for (var i = 0; i < 3; ++i) {
                running[i] += perCycle[i];
                r.Total[i] += perCycle[i];
            }
            r.Progress.Add((cycle, (int[])running.Clone()));
            if (r.CompleteByCycle == null && Met(running, r.Targets))
                r.CompleteByCycle = cycle;
        }

        if (without.Valid && with.Valid) {
            r.ValueKnown = true;
            r.ValueWithout = without.Total;
            r.ValueWith = with.Total;
            foreach (var cycle in without.PerCycle.Keys.Union(with.PerCycle.Keys).OrderBy(c => c)) {
                var a = without.PerCycle.TryGetValue(cycle, out var va) ? va : 0;
                var b = with.PerCycle.TryGetValue(cycle, out var vb) ? vb : 0;
                r.PerCycleDelta[cycle] = b - a;
            }
        }
        else {
            r.ValueSkipReason = without.SkipReason ?? with.SkipReason ?? "unknown";
        }

        r.Valid = true;
        return r;
    }

    private static bool Met(int[] have, int[] targets) {
        for (var i = 0; i < targets.Length; ++i)
            if (have[i] < targets[i])
                return false;
        return true;
    }

    // 每個生產日結束時的累計進度,例如 "C1 8/8 0/6 0/8 | C2 8/8 6/6 8/8"
    public string ProgressText() => string.Join(" | ", Progress.Select(p =>
        $"C{p.cycle} " + string.Join(" ", p.cumulative.Select((v, i) => $"{v}/{Targets[i]}"))));

    // 🔴 使用者跑 LogLevel 2,診斷一律 Information。
    public string LogLine(FavourMode mode) {
        if (!Valid)
            return $"[favour-placement] not computed: {SkipReason}";
        var head = $"[favour-placement] mode={mode} earliest-first={(EarliestFirst ? "on" : "off")}: " +
            $"requests on {CyclesText} ({OccupiedWorkshopDays}/{TotalWorkshopDays} workshop-days), " +
            $"all targets met by {CompleteByText}, totals {string.Join("/", Total.Select((v, i) => $"{v}:{Targets[i]}"))}";
        var progress = Progress.Count > 0 ? $"; progress {ProgressText()}" : "";
        var value = ValueKnown
            ? $"; week value {ValueWithout:F0} without requests -> {ValueWith:F0} with ({LossText}), " +
              $"per-cycle {string.Join(" ", PerCycleDelta.Where(kv => Math.Abs(kv.Value) >= 1).Select(kv => $"C{kv.Key}{kv.Value:+0;-0}"))}"
            : $"; week value unknown ({ValueSkipReason})";
        return head + progress + value;
    }
}
