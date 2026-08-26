using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using visland.Helpers;
using visland.Island;

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
// ⚠️ 上面那句「沒有寫死的魔術數字」有一個例外:**過剩材料偏好**
//    (WorkshopConfig.SurplusPreferencePercent)是使用者參數,不是遊戲資料。
//    它只乘進**挑選用**的分數,不進 Report 的價值評分 —— 評分那把尺仍然是純貝殼幣相對值,
//    所以偏好付出的代價會如實顯示在報告與 log 裡,而不是被自己的偏好粉飾掉。
//    預設 0 = 完全不偏好。
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

        // 過剩材料偏好的狀態。🔴 刻意不塞進 SkipReason:那個代表「整個沒補」,
        //    而偏好用不了的時候補天照常進行,只是退回純價值解 —— 兩件事混在一起 UI 會講錯話。
        public int SurplusPreferencePercent;     // 使用者設定的強度(0 = 沒開)
        public bool SurplusApplied;              // 偏好真的生效了
        public string? SurplusUnavailableReason; // 非 null = 想偏好但材料資料讀不到

        public bool Filled => FilledCycles.Count > 0;
    }

    private struct Craft {
        public uint Id;
        public int Time;
        public int Value;
    }

    // 解算封存排程沒填的生產日。傳入的 recs 不會被就地修改;回傳新的一份。
    public static WorkshopSolver.Recs Fill(WorkshopSolver.Recs recs, bool nextWeek, ExcelSheet<MJICraftworksObject> sheet, int surplusPreferencePercent, out Report report) {
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

        // 過剩材料偏好的準備。🔴 三態:庫存或需求任一讀不到就完全不偏好(照原本的純價值解),
        //    並把原因寫進 Report 讓 UI 畫得出來 —— 絕不把「不知道」當成「沒有需求所以全是過剩」,
        //    那會解出一份看起來很合理、其實建立在猜測上的排程。
        report.SurplusPreferencePercent = surplusPreferencePercent;
        int[]? surplusBudget = null;
        (uint PouchId, int Amount)[][]? craftMats = null;
        if (surplusPreferencePercent > 0) {
            surplusBudget = BuildSurplusBudget(out var surplusReason);
            report.SurplusUnavailableReason = surplusReason;
            report.SurplusApplied = surplusBudget != null;
            if (surplusBudget != null)
                craftMats = BuildCraftMaterials(sheet, maxId);
        }
        var surplusStrength = surplusBudget != null ? surplusPreferencePercent / 100f : 0f;

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
                foreach (var slot in w.Slots) {
                    if (slot.CraftObjectId <= maxId)
                        counts[slot.CraftObjectId]++;
                    // 🔑 封存那 5 天已經排定要吃掉的材料先扣掉 —— 方向與上面「封存產量先累加進
                    //    市場需求」完全一致:不先扣就會把同一批材料當成還能再用一次。
                    if (craftMats != null && surplusBudget != null && slot.CraftObjectId < craftMats.Length)
                        ConsumeSurplus(craftMats[slot.CraftObjectId], surplusBudget);
                }
        }
        report.ArchiveDays = days.Count;

        // ② 空的生產日逐日解算(工房之間也依序累加,避免四間工房排出一模一樣的東西之後才發現需求崩了)
        var newDays = new List<(int cycle, WorkshopSolver.DayRec day)>();
        for (var cycle = 1; cycle <= 7; ++cycle) {
            if ((missingMask & (1u << (cycle - 1))) == 0)
                continue;
            var day = new WorkshopSolver.DayRec();
            for (var w = 0; w < maxWorkshops; ++w) {
                // 逐工房重算:第一間工房吃掉的過剩額度,第二間解算時就不該再看得到。
                var surplusMult = BuildSurplusMultipliers(catalog, craftMats, surplusBudget, surplusStrength);
                var plan = SolveWorkshop(catalog, popMult, linked, counts, baseBucket, supplyRatios, craftsPerStep, surplusMult);
                if (plan.Slots.Count == 0)
                    break;
                day.Workshops.Add(plan);
                foreach (var slot in plan.Slots) {
                    counts[slot.CraftObjectId]++;
                    if (craftMats != null && surplusBudget != null && slot.CraftObjectId < craftMats.Length)
                        ConsumeSurplus(craftMats[slot.CraftObjectId], surplusBudget);
                }
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

    // 一整週排程在「同一把尺」下的相對價值,用途是把兩份排程放在一起比(例如「有放請求」vs「沒放請求」)。
    // 與 Fill 的評分不同的地方:這裡**逐生產日累加**市場需求,所以先生產的享有較高的需求倍率 ——
    // 「排在哪一天」本身就會影響分數,而這正是要量的東西。
    // ⚠️ 一格算 1 次產量(與 Fill 的計法一致)。主題連結的 2 倍效率其實會產出 2 份、因而多吃一級需求;
    //    兩邊用的是同一個近似,所以差值仍然可以直接比,絕對值不要當成貝殼幣。
    // ⚠️ 工房等級加成(MJICraftworksRankRatio)與熱度同樣沒有納入 —— 對所有候選是同一個乘數。
    public sealed class WeekScore {
        public bool Valid;
        public string? SkipReason;          // 非 null = 算不出來,呼叫端要畫「?」不要畫 0
        public float Total;
        public readonly Dictionary<int, float> PerCycle = [];
    }

    public static WeekScore ScoreWeek(WorkshopSolver.Recs recs, bool nextWeek, ExcelSheet<MJICraftworksObject> sheet) {
        var score = new WeekScore();
        var mji = MJIManager.Instance();
        if (mji == null) {
            score.SkipReason = "island data not available";
            return score;
        }
        if (mji->DemandDirty) {
            score.SkipReason = "demand/popularity data not fetched yet";
            return score;
        }
        var maxWorkshops = WorkshopUtils.GetMaxWorkshops();
        if (maxWorkshops <= 0) {
            score.SkipReason = "no workshops built";
            return score;
        }

        var popularity = new WorkshopSolver.Popularity();
        popularity.Set(nextWeek ? mji->NextPopularity : mji->CurrentPopularity);
        var supplyRatios = ReadSupplyRatios(out var craftsPerStep);

        var maxId = 0u;
        foreach (var row in sheet)
            maxId = Math.Max(maxId, row.RowId);
        var counts = new int[maxId + 1];
        var baseBucket = new int[maxId + 1];
        for (var i = 0u; i <= maxId; ++i)
            baseBucket[i] = i < 91 ? (int)mji->GetSupplyForCraftwork(i) : 2;

        foreach (var (cycle, day) in recs.Enumerate()) {
            float dayTotal = 0;
            foreach (var (_, w) in day.Enumerate(maxWorkshops)) {
                MJICraftworksObject? prev = null;
                foreach (var slot in w.Slots) {
                    if (!sheet.TryGetRow(slot.CraftObjectId, out var row))
                        continue;
                    var eff = prev != null && WorkshopSolver.IsLinked(prev.Value, row) ? 2f : 1f;
                    var supply = slot.CraftObjectId < counts.Length
                        ? SupplyMultiplier(slot.CraftObjectId, counts, baseBucket, supplyRatios, craftsPerStep)
                        : 1f;
                    dayTotal += row.Value * popularity.Multiplier(row.RowId) * supply * eff;
                    if (slot.CraftObjectId < counts.Length)
                        counts[slot.CraftObjectId]++;
                    prev = row;
                }
            }
            score.PerCycle[cycle] = dayTotal;
            score.Total += dayTotal;
        }
        score.Valid = true;
        return score;
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
    private static WorkshopSolver.WorkshopRec SolveWorkshop(Craft[] catalog, float[] popMult, bool[,] linked, int[] counts, int[] baseBucket, int[] supplyRatios, int craftsPerStep, float[]? surplusMult) {
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
            slotValue[i] = popMult[i] * SupplyMultiplier(catalog[i].Id, counts, baseBucket, supplyRatios, craftsPerStep)
                * (surplusMult != null ? surplusMult[i] : 1f);

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

    // 過剩 = 收納袋現有 − 工坊排程兩週需求,取正值。與屯貨倉庫派遣、缺料總表共用同一把尺。
    // 🔴 庫存或需求任一讀不到就回 null,呼叫端完全不偏好 —— 把「不知道」當 0 會讓每一種材料
    //    都看起來過剩,那比不偏好糟得多。
    private static int[]? BuildSurplusBudget(out string? reason) {
        var ledger = Service.Materials;
        // Fill 是按鈕觸發的一次性解算,不在每幀路徑上,而且可能從非繪製路徑(_pendingActions)被呼叫。
        // Refresh() 自己第一件事就是 IsLoggedIn 閘門,未登入時直接把所有 *Known 清成 false 就返回,
        // 所以在這裡強制刷新是安全的 —— 資料讀不到會表現成下面三個 reason 之一,不是崩潰。
        ledger.Refresh(true);
        if (ledger.Rows.Length == 0) {
            reason = "island material table could not be built";
            return null;
        }
        if (!ledger.StockKnown) {
            reason = "pouch counts could not be read";
            return null;
        }
        if (!ledger.DemandKnown) {
            reason = "workshop agenda has not been read";
            return null;
        }
        var budget = new int[ledger.Rows.Length];
        for (var i = 0; i < budget.Length; ++i)
            budget[i] = Math.Max(0, ledger.NetStockOf((uint)i, MaterialLedger.HorizonTwoWeeks));
        reason = null;
        return budget;
    }

    // 每個產品吃哪些材料。
    // 🔴 MJICraftworksObject.Material[] **直接就是 MJIItemPouch 列號**(與 MJIRecipe.Material[] 不同 ——
    //    那個指向 MJIRecipeMaterial,多一層轉接,照抄會得到一份看起來合理但完全錯誤的配方表)。
    // 🔴 判「有沒有這個材料」一律看 Amount > 0,不能看 Material != 0:pouch 第 0 列是真材料(棕櫚葉)。
    private static (uint PouchId, int Amount)[][] BuildCraftMaterials(ExcelSheet<MJICraftworksObject> sheet, uint maxId) {
        var table = new (uint PouchId, int Amount)[maxId + 1][];
        foreach (var row in sheet) {
            if (row.RowId > maxId)
                continue;
            var list = new List<(uint, int)>();
            for (var i = 0; i < row.Material.Count && i < row.Amount.Count; ++i) {
                var amount = row.Amount[i];
                if (amount <= 0)
                    continue;
                list.Add((row.Material[i].RowId, amount));
            }
            table[row.RowId] = [.. list];
        }
        return table;
    }

    // 這個產品的材料裡,有多少比例(按數量加權)是目前的過剩額度吃得下的。
    private static float SurplusFraction((uint PouchId, int Amount)[]? mats, int[] budget) {
        if (mats == null || mats.Length == 0)
            return 0f;
        var total = 0;
        var surplus = 0;
        foreach (var (pouchId, amount) in mats) {
            total += amount;
            if (pouchId < budget.Length && budget[pouchId] >= amount)
                surplus += amount;
        }
        return total > 0 ? (float)surplus / total : 0f;
    }

    private static void ConsumeSurplus((uint PouchId, int Amount)[]? mats, int[] budget) {
        if (mats == null)
            return;
        foreach (var (pouchId, amount) in mats)
            if (pouchId < budget.Length)
                budget[pouchId] = Math.Max(0, budget[pouchId] - amount);
    }

    // 偏好 = 候選分數 × (1 + 強度 × 過剩比例);完全用過剩材料的產品在 50% 時拿到 1.5 倍。
    // 🔑 刻意用乘法而不是加法:加法會讓一個低價值產品只因為「材料剛好過剩」就壓過高價值產品,
    //    乘法則只在價值相近時才改變順序,代價可控且與強度成比例。
    private static float[]? BuildSurplusMultipliers(Craft[] catalog, (uint PouchId, int Amount)[][]? craftMats, int[]? budget, float strength) {
        if (craftMats == null || budget == null || strength <= 0)
            return null;
        var mult = new float[catalog.Length];
        for (var i = 0; i < catalog.Length; ++i) {
            var id = catalog[i].Id;
            mult[i] = 1f + strength * SurplusFraction(id < craftMats.Length ? craftMats[id] : null, budget);
        }
        return mult;
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
