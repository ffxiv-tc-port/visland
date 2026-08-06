using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using visland.Helpers;

namespace visland.Workshop;

// 補滿封存排程沒有給的生產日。
//
// 為什麼需要:Overseas Casuals 的封存每一季都只列 C2~C7,而且其中一天還是休息日 —— 實際只有 5 個生產日。
// 那是遊戲原生介面的規則(台服 Addon 15146「請為本週期和下週期及其後分別選擇2個生產日作為休息日」,7-2=5),
// 不是遊戲引擎的限制:AgentMJICraftSchedule.Data->RestCycles 是 14-bit mask、
// MJIManager.ScheduleCraft 的 cycle 參數吃 0~13,零休息日照排。
// 所以剩下的兩天用遊戲自己的資料在本機解就好,不需要任何外部資料源
// (封存 100 季全部都是 5 天格式,去找「7 天的封存」是找不到的)。
//
// 價值模型 —— 每一項都來自遊戲資料表,沒有寫死的魔術數字:
//   單次生產價值 = MJICraftworksObject.Value
//                × 受歡迎度%   (MJICraftworksPopularity[列] -> MJICraftworksPopularityType.Ratio)
//                × 市場需求%   (MJICraftworksSupplyDefine[級].Ratio,160/130/100/80/60)
//                × (與前一件同主題 ? 2 : 1)   (WorkshopSolver.IsLinked,遊戲的效率加成)
// 工房等級加成(MJICraftworksRankRatio)與熱度(groove)對所有候選是同一個乘數,
// 不影響排序,故不納入 —— 但也因此這裡算出來的是**相對值**,不是實際貝殼幣。
//
// 🔑 封存那 5 天造成的市場需求下降必須先累加進來,再解空的兩天,否則會重複挑同一批物品。
public static unsafe class WorkshopDayFiller {
    public const int HoursPerCycle = 24;
    private const int DefaultCraftsPerSupplyStep = 8;

    public sealed class Report {
        public readonly List<int> FilledCycles = [];
        public float AddedValue;            // 補上的生產日合計期望價值(相對單位)
        public float ArchiveValuePerDay;    // 封存生產日的平均(同一把尺,可直接比)
        public int ArchiveDays;
        public int Workshops;
        public string? SkipReason;          // 非 null = 這次沒有補,原因在這
        public bool Filled => FilledCycles.Count > 0;
    }

    private struct Craft {
        public uint Id;
        public int Time;
        public int Value;
    }

    // 解算封存排程沒填的生產日。傳入的 recs 不會被就地修改;回傳新的一份。
    public static WorkshopSolver.Recs Fill(WorkshopSolver.Recs recs, bool nextWeek, ExcelSheet<MJICraftworksObject> sheet, out Report report) {
        report = new Report();
        if (recs.Empty) {
            report.SkipReason = "no schedule loaded";
            return recs;
        }

        var missingMask = ~recs.CyclesMask & 0x7Fu;
        if (missingMask == 0) {
            report.SkipReason = "schedule already covers all 7 cycles";
            return recs;
        }

        var mji = MJIManager.Instance();
        if (mji == null) {
            report.SkipReason = "island data not available";
            return recs;
        }
        // DemandDirty == true 時 CurrentPopularity/NextPopularity/SupplyAndDemandShifts 全部是未初始化的值。
        // 拿它們去解算會靜默給出爛答案,所以寧可不補。
        if (mji->DemandDirty) {
            report.SkipReason = "demand/popularity data not fetched yet";
            return recs;
        }

        var maxWorkshops = WorkshopUtils.GetMaxWorkshops();
        if (maxWorkshops <= 0) {
            report.SkipReason = "no workshops built";
            return recs;
        }
        report.Workshops = maxWorkshops;

        var catalog = BuildCatalog(sheet, mji->IslandState.CurrentRank);
        if (catalog.Length == 0) {
            report.SkipReason = "no craftable objects unlocked";
            return recs;
        }

        var popularity = new WorkshopSolver.Popularity();
        popularity.Set(nextWeek ? mji->NextPopularity : mji->CurrentPopularity);

        var supplyRatios = ReadSupplyRatios(out var craftsPerStep);

        // 陣列以「整張表的最大列號」開,不是以目錄的最大列號 —— 海島等級不夠時目錄會被過濾掉一截,
        // 但封存排程裡仍可能出現那些物品,用目錄的上界去數會靜默漏算它們造成的市場需求。
        var maxId = 0u;
        foreach (var row in sheet)
            maxId = Math.Max(maxId, row.RowId);
        var counts = new int[maxId + 1];
        var baseBucket = new int[maxId + 1];
        for (var i = 0u; i <= maxId; ++i)
            baseBucket[i] = i < 91 ? (int)mji->GetSupplyForCraftwork(i) : 2;

        // 主題連結表(2 倍效率加成的判定),81x81 個 bool,建一次就好
        var linked = BuildLinkTable(catalog, sheet);
        var popMult = new float[catalog.Length];
        for (var i = 0; i < catalog.Length; ++i)
            popMult[i] = catalog[i].Value * popularity.Multiplier(catalog[i].Id);

        // ① 先把封存排程的產量累加進市場需求
        var days = new Dictionary<int, WorkshopSolver.DayRec>();
        foreach (var (cycle, day) in recs.Enumerate()) {
            days[cycle] = day;
            foreach (var (_, w) in day.Enumerate(maxWorkshops))
                foreach (var slot in w.Slots)
                    if (slot.CraftObjectId <= maxId)
                        counts[slot.CraftObjectId]++;
        }
        report.ArchiveDays = days.Count;

        // ② 空的生產日逐日解算(工房之間也依序累加,避免四間工房排出一模一樣的東西之後才發現需求崩了)
        var newDays = new List<(int cycle, WorkshopSolver.DayRec day)>();
        for (var cycle = 1; cycle <= 7; ++cycle) {
            if ((missingMask & (1u << (cycle - 1))) == 0)
                continue;
            var day = new WorkshopSolver.DayRec();
            for (var w = 0; w < maxWorkshops; ++w) {
                var plan = SolveWorkshop(catalog, popMult, linked, counts, baseBucket, supplyRatios, craftsPerStep);
                if (plan.Slots.Count == 0)
                    break;
                day.Workshops.Add(plan);
                foreach (var slot in plan.Slots)
                    counts[slot.CraftObjectId]++;
            }
            if (day.Empty)
                continue;
            days[cycle] = day;
            newDays.Add((cycle, day));
            report.FilledCycles.Add(cycle);
        }

        if (newDays.Count == 0) {
            report.SkipReason = "solver produced no schedule";
            return recs;
        }

        // ③ 評分:兩邊都用「整週最終的市場需求」這把同一把尺,所以數字可以直接比。
        //    (補上去的那幾天先吃到自己造成的需求下降,方向是保守的。)
        float archiveTotal = 0;
        foreach (var (cycle, day) in days) {
            var v = DayValue(day, maxWorkshops, sheet, popularity, counts, baseBucket, supplyRatios, craftsPerStep);
            if (report.FilledCycles.Contains(cycle))
                report.AddedValue += v;
            else
                archiveTotal += v;
        }
        report.ArchiveValuePerDay = report.ArchiveDays > 0 ? archiveTotal / report.ArchiveDays : 0;

        var result = new WorkshopSolver.Recs();
        foreach (var (cycle, day) in days.OrderBy(kv => kv.Key))
            result.Add(cycle, day);
        return result;
    }

    private static Craft[] BuildCatalog(ExcelSheet<MJICraftworksObject> sheet, byte islandRank) {
        var list = new List<Craft>();
        foreach (var row in sheet) {
            // CraftingTime == 0 是佔位列(第 0 列與未使用列);LevelReq 是海島等級門檻,
            // 沒解鎖的東西排下去 ScheduleCraft 不會生效,是靜默的少一格。
            if (row.CraftingTime == 0 || row.Value == 0)
                continue;
            if (row.LevelReq > islandRank)
                continue;
            list.Add(new Craft { Id = row.RowId, Time = row.CraftingTime, Value = row.Value });
        }
        return [.. list];
    }

    private static bool[,] BuildLinkTable(Craft[] catalog, ExcelSheet<MJICraftworksObject> sheet) {
        var n = catalog.Length;
        var rows = new MJICraftworksObject[n];
        for (var i = 0; i < n; ++i)
            sheet.TryGetRow(catalog[i].Id, out rows[i]);
        var table = new bool[n, n];
        for (var a = 0; a < n; ++a)
            for (var b = 0; b < n; ++b)
                table[a, b] = WorkshopSolver.IsLinked(rows[a], rows[b]);
        return table;
    }

    // MJICraftworksSupplyDefine:Ratio = 該供需級別的價值百分比(160/130/100/80/60),
    // Supply = 進入該級別的門檻(18/10/2/-6/-999)。相鄰門檻差 8 → 每 8 次生產掉一級。
    // 門檻是從表算出來的,不寫死;算不出合理值才退回 8。
    private static int[] ReadSupplyRatios(out int craftsPerStep) {
        craftsPerStep = DefaultCraftsPerSupplyStep;
        var ratios = new List<int>();
        var thresholds = new List<int>();
        try {
            foreach (var row in MJICraftworksSupplyDefine.Get()) {
                ratios.Add(row.Ratio);
                thresholds.Add(row.Supply);
            }
        }
        catch (Exception ex) {
            Service.Log.Warning($"Could not read MJICraftworksSupplyDefine ({ex.Message}); supply model disabled");
        }
        if (ratios.Count == 0)
            return [100];

        var gaps = new List<int>();
        for (var i = 0; i + 1 < thresholds.Count; ++i) {
            var gap = thresholds[i] - thresholds[i + 1];
            if (gap > 0 && gap < 100) // 最後一列是 -999 的哨兵,跳過
                gaps.Add(gap);
        }
        if (gaps.Count > 0)
            craftsPerStep = gaps.Min();
        return [.. ratios];
    }

    private static float SupplyMultiplier(uint id, int[] counts, int[] baseBucket, int[] supplyRatios, int craftsPerStep) {
        var bucket = baseBucket[id] + counts[id] / craftsPerStep;
        bucket = Math.Clamp(bucket, 0, supplyRatios.Length - 1);
        return 0.01f * supplyRatios[bucket];
    }

    // 單一工房的 24 小時排程 —— 狀態是(已用時數, 上一件物品),轉移是「再排一件」。
    // 之所以只要記「上一件」,是因為效率加成只看相鄰兩件是否同主題。
    // 規模:25 小時 x (81+1) 個上一件 x 81 個轉移 ≈ 16 萬次,按一次按鈕跑一次,不在每幀路徑上。
    private static WorkshopSolver.WorkshopRec SolveWorkshop(Craft[] catalog, float[] popMult, bool[,] linked, int[] counts, int[] baseBucket, int[] supplyRatios, int craftsPerStep) {
        var n = catalog.Length;
        var width = n + 1; // 0 = 還沒排任何東西
        var states = (HoursPerCycle + 1) * width;
        var best = new float[states];
        var prevState = new int[states];
        var prevItem = new int[states];
        Array.Fill(best, float.NegativeInfinity);
        Array.Fill(prevState, -1);
        best[0] = 0;

        var slotValue = new float[n];
        for (var i = 0; i < n; ++i)
            slotValue[i] = popMult[i] * SupplyMultiplier(catalog[i].Id, counts, baseBucket, supplyRatios, craftsPerStep);

        for (var hour = 0; hour < HoursPerCycle; ++hour) {
            for (var last = 0; last < width; ++last) {
                var from = hour * width + last;
                var cur = best[from];
                if (float.IsNegativeInfinity(cur))
                    continue;
                for (var i = 0; i < n; ++i) {
                    var next = hour + catalog[i].Time;
                    if (next > HoursPerCycle)
                        continue;
                    var gain = cur + slotValue[i] * (last > 0 && linked[last - 1, i] ? 2f : 1f);
                    var to = next * width + i + 1;
                    if (gain > best[to]) {
                        best[to] = gain;
                        prevState[to] = from;
                        prevItem[to] = i;
                    }
                }
            }
        }

        // 優先取剛好填滿 24 小時的解;真的填不滿才退而求其次(4/6/8 三種時長一定填得滿,這裡只是保險)
        var endBase = HoursPerCycle * width;
        var bestState = -1;
        for (var last = 1; last < width; ++last)
            if (best[endBase + last] > (bestState < 0 ? float.NegativeInfinity : best[bestState]))
                bestState = endBase + last;
        if (bestState < 0) {
            for (var s = 1; s < states; ++s)
                if (!float.IsNegativeInfinity(best[s]) && (bestState < 0 || best[s] > best[bestState]))
                    bestState = s;
        }

        var rec = new WorkshopSolver.WorkshopRec();
        if (bestState < 0)
            return rec;

        var picked = new List<int>();
        for (var s = bestState; s > 0 && prevState[s] >= 0; s = prevState[s])
            picked.Add(prevItem[s]);
        picked.Reverse();

        var slot = 0;
        foreach (var i in picked) {
            rec.Add(slot, catalog[i].Id);
            slot += catalog[i].Time;
        }
        return rec;
    }

    private static float DayValue(WorkshopSolver.DayRec day, int maxWorkshops, ExcelSheet<MJICraftworksObject> sheet, WorkshopSolver.Popularity popularity, int[] counts, int[] baseBucket, int[] supplyRatios, int craftsPerStep) {
        float total = 0;
        foreach (var (_, w) in day.Enumerate(maxWorkshops)) {
            MJICraftworksObject? prev = null;
            foreach (var slot in w.Slots) {
                if (!sheet.TryGetRow(slot.CraftObjectId, out var row))
                    continue;
                var eff = prev != null && WorkshopSolver.IsLinked(prev.Value, row) ? 2f : 1f;
                var supply = slot.CraftObjectId < counts.Length
                    ? SupplyMultiplier(slot.CraftObjectId, counts, baseBucket, supplyRatios, craftsPerStep)
                    : 1f;
                total += row.Value * popularity.Multiplier(row.RowId) * supply * eff;
                prev = row;
            }
        }
        return total;
    }

    public static string FormatCycles(IEnumerable<int> cycles) => string.Join(", ", cycles.Select(c => $"C{c}"));
}
