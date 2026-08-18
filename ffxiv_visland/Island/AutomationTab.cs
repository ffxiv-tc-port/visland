using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using visland.Export;
using visland.Farm;
using visland.Granary;
using visland.Helpers;
using visland.Pasture;

namespace visland.Island;

// 把倉庫/耕地/牧場/產品交易四塊的**設定**鏡射到 /visland 主視窗。
//
// 為什麼要這一頁:那四個視窗都是 UIAttachedWindow,只有站到對應 NPC/建築旁邊、
// 讓原生介面開起來,它們才會出現 —— 想改個「自動採集」得先跑到島上那個角落。
//
// 🔴 這裡**只鏡射設定,不鏡射即時資料,也不鏡射動作**。
//    「立即套用」「賣掉超過門檻的」這類按鈕要 agent 活著才有意義,
//    倉庫剩幾天、耕地有什麼可收更是要 agent 資料;主窗拿不到就明講拿不到,
//    絕不畫一份看起來像真的假數字(三態原則:不知道要看得見)。
//
// 設定物件本身是 Configuration.Get<T>() 的同一份實例(每個型別只有一個),
// 所以這裡改完,建築旁那些視窗立刻是同一個值,不需要任何同步。
public sealed class AutomationTab {
    private readonly GranaryConfig _granary = Service.Config.Get<GranaryConfig>();
    private readonly FarmConfig _farm = Service.Config.Get<FarmConfig>();
    private readonly PastureConfig _pasture = Service.Config.Get<PastureConfig>();
    private readonly ExportConfig _export = Service.Config.Get<ExportConfig>();

    public void Draw() {
        using (ImRaii.PushColor(ImGuiCol.Text, 0xff909090u))
            ImGui.TextWrapped("These are the same settings as the windows that pop up next to each building. Buttons and live status still need you to be standing there.".Loc());

        using var child = ImRaii.Child("automation");
        if (!child)
            return;

        Utils.DrawSection("Granary".Loc(), ImGuiColors.ParsedGold, false);
        if (UICombo.Enum("Auto Collect".Loc() + "###granarycollect", ref _granary.Collect))
            _granary.NotifyModified();
        if (UICombo.Enum("Auto Reassign".Loc(), ref _granary.Reassign))
            _granary.NotifyModified();
        ImGuiComponents.HelpMarker("\"Top up low stock\" ranks the materials a granary can actually bring by pouch stock minus the workshop agenda's two-week demand, so a material the workshop is about to eat counts as scarcer than its raw count suggests. Materials the workshop does not use keep their plain stock and are still ranked. The first granary is sent wherever it can restock the scarcest one; the second gets whatever the first does not cover. It only counts whether a material is covered, not how much arrives - daily yields are not in the game data. If the workshop agenda has not been read yet, ranking falls back to plain pouch stock. Incoming granary and farm deliveries are deliberately not subtracted.".Loc());
        DrawNeedsBuilding("Reassigning now, and the per-expedition table, need the granary window.".Loc());

        Utils.DrawSection("Farm".Loc(), ImGuiColors.ParsedGold);
        if (UICombo.Enum("Auto Collect".Loc() + "###farmcollect", ref _farm.Collect))
            _farm.NotifyModified();
        DrawNeedsBuilding("Collecting, entrusting and dismissing need the cropland window.".Loc());

        Utils.DrawSection("Pasture".Loc(), ImGuiColors.ParsedGold);
        if (UICombo.Enum("Auto Collect".Loc() + "###pasturecollect", ref _pasture.Collect))
            _pasture.NotifyModified();
        DrawNeedsBuilding("Collecting leavings needs the pasture window.".Loc());

        Utils.DrawSection("Exports".Loc(), ImGuiColors.ParsedGold);
        if (ImGui.Checkbox("Auto Export".Loc(), ref _export.AutoSell))
            _export.NotifyModified();
        ImGui.PushItemWidth(150);
        ImGui.SliderInt("Sell normal above".Loc(), ref _export.NormalLimit, 0, 999);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _export.NotifyModified();
        ImGui.SliderInt("Sell granary above".Loc(), ref _export.GranaryLimit, 0, 999);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _export.NotifyModified();
        ImGui.SliderInt("Sell farm above".Loc(), ref _export.FarmLimit, 0, 999);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _export.NotifyModified();
        ImGui.SliderInt("Sell pasture above".Loc(), ref _export.PastureLimit, 0, 999);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _export.NotifyModified();
        ImGui.PopItemWidth();
        if (ImGui.Checkbox("Keep what the workshop agenda still needs".Loc(), ref _export.RespectWorkshopNeeds))
            _export.NotifyModified();
        ImGuiComponents.HelpMarker("Raises each limit to whatever the workshop agenda still needs: sells down to the larger of the limit above and (two-week requirement minus what is already inbound from granary, farm and pasture). If that requirement cannot be read, the plain limit is used - a missing reading never blocks a sale.".Loc());
        DrawNeedsBuilding("Selling now needs the export window.".Loc());
    }

    private static void DrawNeedsBuilding(string what) {
        using (ImRaii.PushColor(ImGuiCol.Text, 0xff909090u))
            ImGui.TextWrapped(what);
    }
}
