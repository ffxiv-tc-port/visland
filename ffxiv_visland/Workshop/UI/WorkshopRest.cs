using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System.Numerics;
using visland.Helpers;

namespace visland.Workshop;

// 休日快速設定(參考 DailyRoutines MoreFlexibleMJIWorkdays 的功能形狀):
// 直接以 14 個核取方塊自由設定本週期/下週期的工房休息日,
// 不受遊戲原生介面「每週恰好兩天」的限制(0~4 天皆可,封包上限 4 天)。
// 寫入走 WorkshopUtils.SetRestCycles(遊戲自身的 agent 確認事件),與排程套用同一條路。
public class WorkshopRest {
    public unsafe void Draw() {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null) {
            ImGui.TextUnformatted("Workshop data not ready".Loc());
            return;
        }

        var data = agent->Data;
        var rest = data->RestCycles & 0x3FFFu;
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
            // 封包只有 4 個休日欄位,超過會被截斷 → 已達 4 天時禁止再勾
            var capReached = !isRest && totalRest >= 4;

            if (day > 0)
                ImGui.SameLine();
            using (ImRaii.Disabled(locked || capReached)) {
                var v = isRest;
                if (ImGui.Checkbox($"C{day + 1}##rest{bit}", ref v)) {
                    var newMask = rest ^ 1u << bit;
                    WorkshopUtils.SetRestCycles(newMask);
                }
            }
            if (locked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("This day is already done or in progress and cannot be changed.".Loc());
            if (capReached && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("At most 4 rest days total across both weeks.".Loc());
        }
    }
}
