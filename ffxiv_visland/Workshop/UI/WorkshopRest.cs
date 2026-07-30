using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System;
using System.Numerics;
using visland.Helpers;

namespace visland.Workshop;

// 休日快速設定(參考 DailyRoutines MoreFlexibleMJIWorkdays 的功能形狀):
// 直接以 14 個核取方塊自由設定本週期/下週期的工房休息日,
// 不受遊戲原生介面「每週恰好兩天」的限制(0~4 天皆可,含 0 休)。
// 寫入走 WorkshopUtils.SetRestCycles(遊戲自身的 agent 確認事件,event 5),與排程套用同一條路。
//
// ⚠️ 零休(mask==0)的已知風險:agent 以 NewRestCycles==0 代表「尚無變更」
// (DR 的 MoreFlexibleMJIWorkdays 就有 `if NewRestCycles==0 → 用 RestCycles 回填` 的 sentinel 處理),
// event 5 對零 mask 可能被當 no-op、或被伺服器拒絕——離線無法驗證。
// 因此每次寫入後追蹤 RestCycles 是否在時限內變成目標值,不生效時明確標示,讓實機一測就有判定。
public class WorkshopRest {
    private uint? _pendingMask;
    private DateTime _pendingSince;
    private uint? _rejectedMask;

    private const double ConfirmTimeoutSeconds = 5.0;

    public unsafe void Draw() {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null) {
            ImGui.TextUnformatted("Workshop data not ready".Loc());
            return;
        }

        var data = agent->Data;
        var actualRest = data->RestCycles & 0x3FFFu;

        // 寫入結果追蹤:RestCycles 變成目標值=已生效;逾時未變=遊戲/伺服器未接受
        if (_pendingMask != null) {
            if (actualRest == _pendingMask.Value) {
                _pendingMask = null;
                _rejectedMask = null;
            }
            else if ((DateTime.UtcNow - _pendingSince).TotalSeconds > ConfirmTimeoutSeconds) {
                _rejectedMask = _pendingMask;
                _pendingMask = null;
                Service.Log.Warning($"Rest-day write not accepted by the game: requested 0x{_rejectedMask:X}, still 0x{actualRest:X}");
            }
        }

        // 顯示用 mask:寫入等待期間顯示目標值,避免勾了又跳回的錯覺
        var rest = _pendingMask ?? actualRest;
        var cycleInProgress = data->CycleInProgress;
        var totalRest = BitOperations.PopCount(rest);

        ImGui.TextWrapped("Freely toggle workshop rest days below. The game UI forces exactly two rest days per week; here 0-4 total rest days are allowed. Days already finished or in progress cannot be changed.".Loc());
        ImGui.Spacing();

        var sheet = Service.DataManager.GetExcelSheet<Addon>();
        DrawWeek(sheet.GetRow(15107).Text.ExtractText(), 0, rest, cycleInProgress, totalRest);
        ImGui.Spacing();
        DrawWeek(sheet.GetRow(15108).Text.ExtractText(), 7, rest, cycleInProgress, totalRest);

        ImGui.Spacing();
        ImGui.TextUnformatted("Rest days set: ??/4".Loc(totalRest));
        if (_pendingMask != null)
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), "Applying...".Loc());
        else if (_rejectedMask != null)
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Last rest-day change was NOT accepted by the game (no effect after ?? seconds). Zero rest days may be rejected - please report this result.".Loc((int)ConfirmTimeoutSeconds));
        if (totalRest == 0)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "Note: currently no rest days are set for either week.".Loc());
    }

    private unsafe void DrawWeek(string label, int bitOffset, uint rest, int cycleInProgress, int totalRest) {
        ImGui.TextUnformatted(label);
        for (var day = 0; day < 7; ++day) {
            var bit = bitOffset + day;
            var isRest = (rest & 1u << bit) != 0;
            // 本週期:已完成或進行中的日子不可改;下週期全開放
            var locked = bitOffset == 0 && day <= cycleInProgress;
            // 封包只有 4 個休日欄位,超過會被截斷 → 已達 4 天時禁止再勾(最少 0 天,不設下限)
            var capReached = !isRest && totalRest >= 4;

            if (day > 0)
                ImGui.SameLine();
            using (ImRaii.Disabled(locked || capReached || _pendingMask != null)) {
                var v = isRest;
                if (ImGui.Checkbox($"C{day + 1}##rest{bit}", ref v)) {
                    var newMask = rest ^ 1u << bit;
                    WorkshopUtils.SetRestCycles(newMask);
                    _pendingMask = newMask;
                    _pendingSince = DateTime.UtcNow;
                    _rejectedMask = null;
                }
            }
            if (locked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("This day is already done or in progress and cannot be changed.".Loc());
            if (capReached && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("At most 4 rest days total across both weeks.".Loc());
        }
    }
}
