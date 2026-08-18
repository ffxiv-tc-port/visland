using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace visland.Workshop;

internal class ScheduleApplier {
    public bool IgnoreFourthWorkshop { get; set; }

    public unsafe int ApplyRecommendation(int cycle, WorkshopSolver.DayRec rec, int minStartingHour = 0) {
        var maxWorkshops = WorkshopUtils.GetMaxWorkshops();
        var scheduled = 0;
        foreach (var w in rec.Enumerate(maxWorkshops))
            if (!IgnoreFourthWorkshop || w.workshop < maxWorkshops - 1)
                foreach (var r in w.rec.Slots) {
                    if (r.Slot < minStartingHour)
                        continue;
                    WorkshopUtils.ScheduleItemToWorkshop(r.CraftObjectId, r.Slot, cycle, w.workshop);
                    scheduled++;
                }
        return scheduled;
    }

    public unsafe void ApplyRecommendationToCurrentCycle(WorkshopSolver.DayRec rec) {
        // 🔴 原本是 AgentMJICraftSchedule.Instance()->Data 兩層裸讀。agent 在 UIModule 未建立時
        //    回 null,Data 則在製作預定表關掉之後變 null。這個 public 方法目前唯一的呼叫端
        //    (WorkshopOCImport 的「Set on Active Cycle」按鈕)沒有 try/catch,而且上游的保護
        //    是 WorkshopWindow.PreOpenCheck —— 那是別處建立的前提,新的呼叫點可以繞過去。
        //    取不到就安靜返回(留一行 Information),不動作而不是崩潰。
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null) {
            Service.Log.Information("Not applying recommendation: craft schedule agent/data unavailable");
            return;
        }

        var agentData = agent->Data;
        var cycle = agentData->CycleDisplayed;
        var minHour = cycle == agentData->CycleInProgress ? agentData->HourSinceCycleStart : 0;
        ApplyRecommendation(cycle, rec, minHour);
        WorkshopUtils.ResetCurrentCycleToRefreshUI();
    }

    public unsafe void ApplyRecommendations(WorkshopSolver.Recs recommendations, bool nextWeek) {
        // 同 ApplyRecommendationToCurrentCycle:本地判空,不依賴呼叫端的 PreOpenCheck。
        // agentData 底下被無條件用到(CycleInProgress / HourSinceCycleStart / RestCycles),
        // 所以擋在方法開頭;取不到就安靜返回,不丟例外 —— 這條路徑的呼叫端雖然有 try/catch,
        // 但「介面沒開」不是使用者做錯了什麼,不值得跳一則紅字錯誤。
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null) {
            Service.Log.Information("Not applying recommendations: craft schedule agent/data unavailable");
            return;
        }

        var agentData = agent->Data;
        var restDaysCount = BitOperations.PopCount(~recommendations.CyclesMask & 0x7F);
        if (recommendations.Schedules.Count + restDaysCount > 7)
            throw new Exception($"Too many days in recs: {recommendations.Schedules.Count} crafts + {restDaysCount} rest > 7");

        var cycleInProgress = nextWeek ? -1 : agentData->CycleInProgress;
        var hourSinceStart = nextWeek ? 0 : agentData->HourSinceCycleStart;
        var completedCycles = cycleInProgress > 0 ? (1u << cycleInProgress) - 1 : 0u;
        var skippedMask = recommendations.CyclesMask & completedCycles;
        if (skippedMask != 0) {
            var skipped = FormatCycleMask(skippedMask);
            Service.Log.Info($"Skipping completed cycles: {skipped}");
            Service.ChatGui.Print("Skipping completed cycles: ??".Loc(skipped), "visland");
        }

        var hasApplicable = false;
        foreach ((var c, var r) in recommendations.Enumerate()) {
            if ((completedCycles & (1u << (c - 1))) != 0)
                continue;
            if (c - 1 == cycleInProgress)
                hasApplicable |= r.Workshops.Any(w => w.Slots.Any(s => s.Slot >= hourSinceStart));
            else
                hasApplicable = true;
        }
        if (!hasApplicable)
            throw new Exception("No remaining cycles to apply — the whole schedule is already done or in progress");

        // 🔴 順序:一定要「先改休息日、再排程」。
        // 台服 Addon 15151:「確定要變更休息日的預定時間嗎?制定在以下生產日的生產計畫將被全部刪除」——
        // 反過來做會把剛排好的計畫清掉。
        // 🔴 休息日一律讀 agent 的 RestCycles mask(本週低 7 位、下週高 7 位),
        // 不要用 MJIManager.CraftworksRestDays(分不出「零休息日」與「C1 休息」)。
        var currentRestCycles = (nextWeek ? agentData->RestCycles >> 7 : agentData->RestCycles) & 0x7Fu;
        if ((currentRestCycles & recommendations.CyclesMask) != 0) {
            var freeCycles = ~recommendations.CyclesMask & 0x7F;

            // 休日直接從空閒日中挑選:最低位 + 最高位。
            // 當 C1 空閒時最低位就是 C1,結果與舊版「假設 C1 固定休」完全一致;
            // C1 排了生產時(任意休日形狀的排班)也能正確套用,不再直接拒絕。
            uint rest;
            if (freeCycles == 0) {
                // 7 天全排(補滿空生產日之後的正常情形)。遊戲引擎沒有「每週兩天休息」的限制,
                // 那是原生介面的規則,所以這裡把該週的休息日清空即可,不必拒絕整份排程。
                // ⚠️ 兩週都歸零時 mask 會是 0,而 agent 用 NewRestCycles==0 當「尚無變更」的哨兵,
                // 這種情況下寫入可能被當成 no-op —— 下面會回讀確認並在沒生效時明講。
                rest = 0;
                Service.Log.Information($"Schedule covers all 7 cycles; clearing {(nextWeek ? "next" : "this")} week's rest days (was {FormatCycleMask(currentRestCycles)})");
            }
            else if (BitOperations.PopCount(freeCycles) == 1) {
                rest = freeCycles;
            }
            else {
                rest = (1u << (31 - BitOperations.LeadingZeroCount(freeCycles))) | (1u << BitOperations.TrailingZeroCount(freeCycles));
                if (BitOperations.PopCount(rest) != 2)
                    throw new Exception($"Something went wrong, failed to determine rest days");
            }

            var changedRest = rest ^ currentRestCycles;
            if ((changedRest & completedCycles) != 0) {
                Service.Log.Warning("Skipping rest-day adjustment: would affect cycles already done or in progress");
                Service.ChatGui.Print("Skipping rest-day adjustment for this week — set rest days manually if needed".Loc(), "visland");
            }
            else {
                var newRest = nextWeek ? (rest << 7) | (agentData->RestCycles & 0x7F) : (agentData->RestCycles & 0x3F80) | rest;
                WorkshopUtils.SetRestCycles(newRest);
                // 寫入是走 agent 事件、可能要等伺服器,所以同一幀讀回來通常還是舊值 —— 這不是錯誤。
                // 留下「要求值 vs 當下值」這一對數字,是為了讓實機 log 能區分
                // 「休息日沒改成功所以那幾天沒排到」與「排程本身有問題」這兩種完全不同的故障。
                Service.Log.Information($"Rest mask requested 0x{newRest & 0x3FFFu:X}, mask at write time 0x{agentData->RestCycles & 0x3FFFu:X} (write is async; Rest days tab shows the settled value)");
            }
        }

        var appliedCycles = 0;
        var appliedSlots = 0;
        foreach ((var c, var r) in recommendations.Enumerate()) {
            if ((completedCycles & (1u << (c - 1))) != 0)
                continue;
            var minHour = c - 1 == cycleInProgress ? hourSinceStart : 0;
            var scheduled = ApplyRecommendation(c - 1 + (nextWeek ? 7 : 0), r, minHour);
            if (scheduled > 0) {
                appliedCycles++;
                appliedSlots += scheduled;
            }
            else if (c - 1 == cycleInProgress && minHour > 0)
                Service.Log.Info($"Cycle {c}: no remaining slots after hour {minHour}");
        }

        if (appliedSlots == 0)
            throw new Exception("No cycles were applied");

        WorkshopUtils.ResetCurrentCycleToRefreshUI();
        if (skippedMask != 0 || cycleInProgress >= 0 && hourSinceStart > 0)
            Service.ChatGui.Print("Applied ?? craft(s) across ?? cycle(s)".Loc(appliedSlots, appliedCycles), "visland");
    }

    public static string FormatCycleMask(uint mask) {
        var cycles = new List<int>();
        for (var c = 1; c <= 7; ++c) {
            if ((mask & (1u << (c - 1))) != 0)
                cycles.Add(c);
        }
        return string.Join(", ", cycles.Select(c => $"C{c}"));
    }
}
