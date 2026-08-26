using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using System;
using System.Collections.Generic;
using System.Linq;
using visland.Helpers;

namespace visland.Workshop;

internal unsafe class FavourReader(List<string> botNames) {
    public WorkshopSolver.FavourState ReadFavourState(bool nextWeek) {
        var mji = MJIManager.Instance();
        if (mji == null || !mji->IsPlayerInSanctuary)
            throw new Exception("Favour data requires being on your island");
        var state = new WorkshopSolver.FavourState();
        var offset = nextWeek ? 6 : 3;
        for (var i = 0; i < 3; ++i) {
            state.CraftObjectIds[i] = mji->FavorState->CraftObjectIds[i + offset];
            state.CompletedCounts[i] = mji->FavorState->NumDelivered[i + offset] + mji->FavorState->NumScheduled[i + offset];
        }
        if (!mji->DemandDirty)
            state.Popularity.Set(nextWeek ? mji->NextPopularity : mji->CurrentPopularity);
        if (state.CraftObjectIds.Any(id => id == 0))
            throw new Exception("Favour craft IDs not available yet");
        return state;
    }

    public string CreateFavourRequestCommand(bool nextWeek) {
        // MJIManager 是 isPointer 的靜態位址,登入前/不在無人島時是 null(同檔 ReadFavourState 已有同樣的判空)。
        var mji = MJIManager.Instance();
        if (mji == null)
            throw new Exception("Favour data requires being on your island");
        var state = mji->FavorState;
        // ⚠️ 原本的錯誤訊息在 state == null 時會解參考 state->UpdateState —— 報錯路徑自己再炸一次。
        if (state == null)
            throw new Exception("Favour data not available yet");
        if (state->UpdateState != 2)
            throw new Exception($"Favour data not available: {state->UpdateState}");

        var res = "/favors";
        var offset = nextWeek ? 6 : 3;
        for (var i = 0; i < 3; ++i) {
            var id = state->CraftObjectIds[offset + i];
            // botNames comes from the embedded english-name map (see WorkshopOCImport); the game
            // sheets can't provide English names on clients without English EXD (TC), and the OC
            // Discord bot only understands English names.
            var name = id < botNames.Count ? botNames[id] : string.Empty;
            if (!string.IsNullOrEmpty(name))
                res += $" favor{i + 1}:{name.Replace("\'", "")}";
        }
        return res;
    }

    public void EnsureDemandFavoursAvailable(List<Func<bool>> pendingActions) {
        // 讀不到 MJIManager 就什麼都不排:後續的 ReadFavourState 會擲出「requires being on your island」,
        // 由呼叫端既有的 try/catch 顯示。DemandDirty 為 false 時的行為與原本完全相同。
        var mji = MJIManager.Instance();
        if (mji == null || !mji->DemandDirty)
            return;
        WorkshopUtils.RequestDemandFavours();
        // ⚠️ 這個輪詢 lambda 由 WorkshopOCImport.Draw() 的 TakeWhile 執行,外面沒有 try/catch。
        // 讀不到就回 false(＝繼續等),不要解參考 —— 島上資料就緒後會自己接上。
        pendingActions.Add(() => {
            var m = MJIManager.Instance();
            return m != null && !m->DemandDirty && m->FavorState != null && m->FavorState->UpdateState == 2;
        });
    }

    public List<WorkshopSolver.WorkshopRec> SolveRecOverrides(bool nextWeek)
        => new WorkshopSolverFavourSheet(ReadFavourState(nextWeek)).Recs;
}
