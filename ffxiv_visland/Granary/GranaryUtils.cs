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
                // 🔴 Utf8String.ToString() 是 Encoding.UTF8.GetString(AsSpan())，raw 解碼、
                //    不剝 SeString payload。遠征名稱是遊戲自己組的文字，帶圖示或連結
                //    payload 時那些控制位元組會被解成 U+FFFD，再編碼回 UTF-8 就不是
                //    原來的位元組了。而這個值是寫回代理人的 CurExpeditionName，
                //    遊戲接下來拿它組確認框的文字，變形了也不會有任何訊息。
                // 🔑 改成把原始位元組直接交給同一個原生 SetString（以 C 字串指標
                //    為參數的那個多載；原本的 string 多載是產生器包出來的，最後呼叫
                //    的是同一個遊戲函式），中間不再經過受管理 string。
                //    純文字的情況下結果與改動前逐位元組相同。
                ref var expedition = ref agent->Data->Expeditions[expeditionId];
                // StringPtr 是 null 代表那一格的 Utf8String 還沒被建構過。沒有證據說
                // 原生 SetString 收 null，而舊碼在這種情況下送的是空字串（ToString()
                // 對空 span 回 string.Empty），所以這裡沿用舊行為。
                if (expedition.Name.StringPtr.HasValue)
                    agent->CurExpeditionName.SetString(expedition.Name.StringPtr);
                else
                    agent->CurExpeditionName.SetString(string.Empty);
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
