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
        var agentData = AgentMJICraftSchedule.Instance()->Data;
        var cycle = agentData->CycleDisplayed;
        var minHour = cycle == agentData->CycleInProgress ? agentData->HourSinceCycleStart : 0;
        ApplyRecommendation(cycle, rec, minHour);
        WorkshopUtils.ResetCurrentCycleToRefreshUI();
    }

    public unsafe void ApplyRecommendations(WorkshopSolver.Recs recommendations, bool nextWeek) {
        var agentData = AgentMJICraftSchedule.Instance()->Data;
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

        var currentRestCycles = nextWeek ? agentData->RestCycles >> 7 : agentData->RestCycles & 0x7F;
        if ((currentRestCycles & recommendations.CyclesMask) != 0) {
            var freeCycles = ~recommendations.CyclesMask & 0x7F;
            if (freeCycles == 0)
                throw new Exception($"Schedule has crafts on all 7 days but rest days are set - clear a day or adjust rest days on the Rest days tab");

            // 休日直接從空閒日中挑選:最低位 + 最高位。
            // 當 C1 空閒時最低位就是 C1,結果與舊版「假設 C1 固定休」完全一致;
            // C1 排了生產時(任意休日形狀的排班)也能正確套用,不再直接拒絕。
            uint rest;
            if (BitOperations.PopCount(freeCycles) == 1) {
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
