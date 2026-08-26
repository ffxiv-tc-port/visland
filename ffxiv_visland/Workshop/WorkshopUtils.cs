using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;
using visland.Helpers;

namespace visland.Workshop;

public static unsafe class WorkshopUtils {
    public static (long index, DateTime startTime) CurrentWeek() {
        var cycleData = CycleTime.GetRow(2)!;
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var index = (now - cycleData.Value.FirstCycle) / cycleData.Value.Cycle;
        var startTime = cycleData.Value.FirstCycle + cycleData.Value.Cycle * index;
        return (index, DateTime.UnixEpoch.AddSeconds(startTime));
    }

    public static bool CurrentCycleIsEmpty() {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null)
            return false;
        foreach (ref var w in agent->Data->WorkshopSchedules)
            if (w.NumScheduleEntries != 0)
                return false;
        return true;
    }

    // 🔴 AgentMJICraftSchedule.Data 是短命的:製作預定表關掉之後 agent 還在、Data 變 null,
    //    而換區/登入前連 agent 本身都拿不到(Instance() 走 AgentModule.Instance(),
    //    UIModule 未建立時回 null)。下面這批 public static 方法原本全靠呼叫端
    //    WorkshopWindow.PreOpenCheck 那棵樹擋著 —— 那是**別處建立的前提**,擋不住未來新增的
    //    呼叫點,而且 Utils.SynthesizeEvent 會對 receiver 做虛擬呼叫,null 進去就是攔不到的 AVE。
    //    一律改成本地判空,失敗形式=不動作並留一行 Information(使用者跑 LogLevel 2 看得到)。
    public static void ClearCurrentCycleSchedule() {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null) {
            Service.Log.Information("Not clearing cycle schedule: craft schedule agent/data unavailable");
            return;
        }
        Service.Log.Info($"Clearing current cycle schedule");
        Utils.SynthesizeEvent(&agent->AgentInterface, 6, [new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 }]);
    }

    public static void ScheduleItemToWorkshop(uint objId, int startingHour, int cycle, int workshop) {
        var mji = MJIManager.Instance();
        if (mji == null) {
            Service.Log.Information($"Not adding schedule ({objId} @ {startingHour}/{cycle}/{workshop}): MJIManager unavailable");
            return;
        }
        Service.Log.Info($"Adding schedule: {objId} @ {startingHour}/{cycle}/{workshop}");
        mji->ScheduleCraft((ushort)objId, (byte)((startingHour + 17) % 24), (byte)cycle, (byte)workshop);
    }

    // this is what the game uses to refresh the ui after adding schedules
    public static void ResetCurrentCycleToRefreshUI() {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null) {
            Service.Log.Information("Not refreshing craft schedule UI: craft schedule agent/data unavailable");
            return;
        }
        Service.Log.Info($"Resetting current cycle");
        agent->SetDisplayedCycle(agent->Data->CycleDisplayed);
        agent->Data->Flags1 |= AgentMJICraftSchedule.DataFlags1.MaterialsUpdated; // ensure material assignment addon is updated
    }

    public static void SetCurrentCycle(int cycle) {
        // SetDisplayedCycle 是 agent 的成員函式,只需要 agent 本身非 null;
        // 這裡刻意不連 Data 一起擋,以免多擋掉原本會成功的路徑。
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null) {
            Service.Log.Information($"Not setting cycle {cycle}: craft schedule agent unavailable");
            return;
        }
        Service.Log.Info($"Setting cycle: {cycle}");
        agent->SetDisplayedCycle(cycle);
    }

    public static void SetRestCycles(uint mask) {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null) {
            Service.Log.Information($"Not setting rest mask 0x{mask:X}: craft schedule agent/data unavailable");
            return;
        }
        Service.Log.Info($"Setting rest: {mask:X}");
        agent->Data->NewRestCycles = mask;
        Utils.SynthesizeEvent(&agent->AgentInterface, 5, [new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 }]);
    }

    // 放寬「本週期」多出來的休息日,讓封存排程的第二個休息日可以拿來生產。
    //
    // 🔴 舊版寫死 mask 0x2081,那是 本週C1(bit0) + 下週C1(bit7) + 下週C7(bit13) 三個位元。
    //    它從 WorkshopWindow.OnOpen 無條件呼叫(FavourMode == MinMaxFreeRestDay 時),
    //    等於每開一次工房視窗就把**下週**的休息日覆寫成 C1+C7,
    //    而變更休息日會刪光那些生產日已排的生產計畫(台服 Addon 15151)。
    //    現在:下週期的位元原封不動,只碰本週期的低 7 位。
    // 🔴 而且只做「減少休息日」,絕不新增 —— 零休息日的玩家不該因為開個視窗就多出休息日。
    //    (只移除休息日時,新的休息日集合是舊集合的子集,不會有任何生產日被清空。)
    public static bool RelaxSecondRestThisWeek() {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null)
            return false;

        var current = agent->Data->RestCycles;
        var thisWeek = current & 0x7Fu;
        if (BitOperations.PopCount(thisWeek) < 2)
            return false; // 已經 0 或 1 天休息,沒有「第二個」可以放掉

        var keep = 1u << BitOperations.TrailingZeroCount(thisWeek); // 保留最早的那一天
        var dropped = thisWeek & ~keep;

        // 已完成/進行中的生產日不能改(與 Rest days 分頁同一條規則:day <= CycleInProgress)
        var locked = (1u << (agent->Data->CycleInProgress + 1)) - 1;
        if ((dropped & locked) != 0) {
            Service.Log.Information($"Not relaxing rest days: {ScheduleApplier.FormatCycleMask(dropped & locked)} already done or in progress");
            return false;
        }

        var target = (current & 0x3F80u) | keep;
        Service.Log.Information($"Relaxing this week's extra rest day(s) {ScheduleApplier.FormatCycleMask(dropped)}: rest mask 0x{current:X} -> 0x{target:X} (next week untouched)");
        SetRestCycles(target);
        return true;
    }

    // 🔴 判斷休息日一律讀這個 14-bit mask(本週低 7 位、下週高 7 位)。
    // 不要用 MJIManager.CraftworksRestDays —— 那是 4 個 byte 的「休息日編號清單」(0~13),
    // 「完全沒有休息日」與「C1 是休息日」都會讀成 0,兩者分不出來。
    public static uint GetRestCycleMask() {
        var agent = AgentMJICraftSchedule.Instance();
        return agent == null || agent->Data == null ? 0 : agent->Data->RestCycles & 0x3FFFu;
    }

    public static void RequestDemandFavours() {
        var mji = MJIManager.Instance();
        if (mji == null) {
            Service.Log.Information("Not fetching demand & favours: MJIManager unavailable");
            return;
        }
        Service.Log.Info("Fetching demand & favours");
        mji->RequestDemandFull();
        mji->RequestFavorData();
    }

    public static int GetMaxWorkshops() {
        var mji = MJIManager.Instance();
        return mji == null ? 0 : mji->IslandState.CurrentRank switch {
            < 3 => 0,
            < 6 => 1,
            < 8 => 2,
            < 14 => 3,
            _ => 4,
        };
    }

    // ⚠️ 這支回傳的是 MJIManager 那份原始的「休息日編號清單」,只給 Debug 分頁原樣顯示用。
    // 要判斷某一天是不是休息日請用 GetRestCycleMask() —— 見上面的說明。
    // 讀不到 MJIManager 時回空清單(不是 [0,0,0,0]) —— 呼叫端據此顯示「讀不到」而不是假的「全 0」。
    public static List<int> GetCurrentRestCycles() {
        var mji = MJIManager.Instance();
        if (mji == null)
            return [];
        var restDays1 = mji->CraftworksRestDays[0];
        var restDays2 = mji->CraftworksRestDays[1];
        var restDays3 = mji->CraftworksRestDays[2];
        var restDays4 = mji->CraftworksRestDays[3];

        return [restDays1, restDays2, restDays3, restDays4];
    }

    // 改讀 RestCycles mask:原本用 GetCurrentRestCycles() 的清單,
    // 「沒有休息日」與「C1 休息」都是 0,零休的玩家會被誤判成 C1 休息而跳過第一天。
    public static int GetNextNonRestCycle(int cycle) {
        var mask = GetRestCycleMask();
        while (cycle is >= 0 and < 14 && (mask & (1u << cycle)) != 0)
            cycle++;
        return cycle;
    }
}
