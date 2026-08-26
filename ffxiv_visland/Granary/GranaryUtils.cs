using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System.Runtime.CompilerServices;
using visland.Helpers;

namespace visland.Granary;

public static unsafe class GranaryUtils {
    public static MJIGranariesState* State() {
        var agent = AgentMJIGatheringHouse.Instance();
        return agent != null ? agent->GranariesState : null;
    }

    public static MJIGranaryState* GetGranaryState(int index) {
        var state = State();
        return state != null ? (MJIGranaryState*)Unsafe.AsPointer(ref state->Granary[index]) : null;
    }

    public static void Collect(int index) {
        var state = State();
        if (state != null) {
            Service.Log.Info($"Gathering from granary {index}");
            state->CollectResources((byte)index);
        }
    }

    // note: make sure to check that expedition is unlocked before calling this
    public static void SelectExpedition(byte granaryIndex, byte expeditionId, byte numDays) {
        var gstate = GetGranaryState(granaryIndex);
        if (gstate != null) {
            Service.Log.Info($"Selecting expedition {expeditionId} for {numDays} days at granary {granaryIndex}");
            // set current agent fields to emulate user interactions, so that messages are correct
            var confirm = CalculateConfirmation(gstate->ActiveExpeditionId, gstate->RemainingDays, expeditionId, numDays);
            if (confirm == AgentMJIGatheringHouse.Confirmation.None) {
                Service.Log.Info($"=> nothing to do, this is already active");
            }
            else if (numDays - gstate->RemainingDays > MaxDays()) {
                Service.Log.Info($"=> not enough cowries");
            }
            else {
                // 🔴 gstate 非 null 只證明「稍早那次 State() 取得到 agent」,這裡是重新取得的一次
                //    呼叫,而 AgentMJIGatheringHouse.Instance() 合法回 null(產生器本體即
                //    agentModule == null ? null : ...);下面 agent->Data->Expeditions 又是第二層
                //    裸讀,Data 只是普通指標欄位,資料未載入時同樣是 null。
                //    任一層是 null 就是 AccessViolationException —— corrupted-state,try/catch 攔不到。
                // fail-closed:取不到就不送這次遠征指派。維持現狀比在未知狀態下送指令安全。
                var agent = AgentMJIGatheringHouse.Instance();
                if (agent == null || agent->Data == null || agent->GranariesState == null) {
                    Service.Log.Information($"[Granary] SelectExpedition skipped: agent/data/state unavailable (agent={(nint)agent:X})");
                    return;
                }
                agent->CurGranaryIndex = granaryIndex;
                agent->CurActiveExpeditionId = gstate->ActiveExpeditionId;
                agent->CurActiveDays = gstate->RemainingDays;
                agent->CurHoveredExpeditionId = agent->CurSelectedExpeditionId = expeditionId;
                agent->CurSelectedDays = numDays;
                agent->CurExpeditionName.SetString(agent->Data->Expeditions[expeditionId].Name.ToString());
                agent->ConfirmType = confirm;
                agent->GranariesState->SelectExpeditionCommit(granaryIndex, expeditionId, numDays);
            }
        }
    }

    public static CollectResult CalculateGranaryCollectionState(int index) {
        var gstate = GetGranaryState(index);
        if (gstate == null)
            return CollectResult.NothingToCollect;

        var haveAnything = gstate->RareResourceCount > 0;
        var overcapSome = haveAnything && WillOvercap(gstate->RareResourcePouchId, gstate->RareResourceCount);
        var overcapAll = !haveAnything || overcapSome;
        for (var i = 0; i < gstate->NormalResourceCounts.Length; ++i) {
            if (gstate->NormalResourceCounts[i] > 0) {
                haveAnything = true;
                var overcap = WillOvercap(gstate->NormalResourcePouchIds[i], gstate->NormalResourceCounts[i]);
                overcapSome |= overcap;
                overcapAll &= overcap;
            }
        }
        return !haveAnything ? CollectResult.NothingToCollect : overcapAll ? CollectResult.EverythingCapped : overcapSome ? CollectResult.CanCollectWithOvercap : CollectResult.CanCollectSafely;
    }

    public static AgentMJIGatheringHouse.Confirmation CalculateConfirmation(byte curExpedition, byte curDays, byte newExpedition, byte newDays)
        => curExpedition == newExpedition && curDays >= newDays ? AgentMJIGatheringHouse.Confirmation.None
            : curExpedition == 0 && curDays == 0 ? AgentMJIGatheringHouse.Confirmation.Start
            : curExpedition != newExpedition && curDays < newDays ? AgentMJIGatheringHouse.Confirmation.ChangeExtend
            : curExpedition != newExpedition ? AgentMJIGatheringHouse.Confirmation.Change : AgentMJIGatheringHouse.Confirmation.Extend;

    public static int MaxDays() => Utils.NumCowries() / 50;

    private static bool WillOvercap(uint pouchId, int count) => Utils.NumItems(MJIItemPouch.GetRow(pouchId)!.Value.Item.RowId) + count > 999;
}
