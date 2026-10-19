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
// 為什麼要這一頁:那四個視窗都是 UIAttachedWindow,只有站到對應 NPC/建築旁邊、讓原生介面開起來,它們才會出現 —— 想改個「自動採集」得先跑到島上那個角落。
// 🔴 這裡**只鏡射設定,不鏡射即時資料,也不鏡射動作**。絕不畫一份看起來像真的假數字(三態原則:不知道要看得見)。
// 一鍵巡島是例外:它真的走過去、說話、開視窗(由 IslandTour 執行),這裡只放開始/停止與要走哪幾站。
// 設定物件本身是 Configuration.Get<T>() 的同一份實例(每個型別只有一個),所以這裡改完,建築旁那些視窗立刻是同一個值,不需要任何同步。
public sealed class AutomationTab {
    private readonly GranaryConfig _granary = Service.Config.Get<GranaryConfig>();
    private readonly FarmConfig _farm = Service.Config.Get<FarmConfig>();
    private readonly PastureConfig _pasture = Service.Config.Get<PastureConfig>();
    private readonly ExportConfig _export = Service.Config.Get<ExportConfig>();
    private readonly IslandTourConfig _tour = Service.Config.Get<IslandTourConfig>();

    public void Draw() {
        using (ImRaii.PushColor(ImGuiCol.Text, 0xff909090u))
            ImGui.TextWrapped("These are the same settings as the windows that pop up next to each building. Buttons and live status still need you to be standing there.".Loc());

        using var child = ImRaii.Child("automation");
        if (!child)
            return;

        DrawTour();

        Utils.DrawSection("Granary".Loc(), ImGuiColors.ParsedGold);
        if (UICombo.Enum("Auto Collect".Loc() + "###granarycollect", ref _granary.Collect))
            _granary.NotifyModified();
        if (UICombo.Enum("Auto Reassign".Loc(), ref _granary.Reassign))
            _granary.NotifyModified();
        ImGuiComponents.HelpMarker(HelpText.GranaryTopUpLowStock.Loc());
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
        if (ImGui.Checkbox(HelpText.ExportRespectWorkshopNeedsLabel.Loc(), ref _export.RespectWorkshopNeeds))
            _export.NotifyModified();
        ImGuiComponents.HelpMarker(HelpText.ExportRespectWorkshopNeedsHelp.Loc());
        DrawNeedsBuilding("Selling now needs the export window.".Loc());
    }

    private void DrawTour() {
        var tour = Service.IslandTour;
        Utils.DrawSection("Island tour".Loc(), ImGuiColors.ParsedGold, false);
        using (ImRaii.PushColor(ImGuiCol.Text, 0xff909090u))
            ImGui.TextWrapped("Walks to each ticked mammet, talks to them and opens their window; the automation settings below do the rest. A window it cannot finish (the workshop agenda, or anything still uncollected) is left open for you, and the tour carries on once you close it. YesAlready and TextAdvance are paused while it runs. Get onto your island yourself first.".Loc());

        using (ImRaii.Disabled(tour.Running)) {
            var changed = ImGui.Checkbox("Workshop agenda".Loc(), ref _tour.VisitWorkshop);
            ImGui.SameLine();
            changed |= ImGui.Checkbox("Granary".Loc() + "###tourgranary", ref _tour.VisitGranary);
            ImGui.SameLine();
            changed |= ImGui.Checkbox("Exports".Loc() + "###tourexport", ref _tour.VisitExport);
            ImGui.SameLine();
            changed |= ImGui.Checkbox("Pasture".Loc() + "###tourpasture", ref _tour.VisitPasture);
            ImGui.SameLine();
            changed |= ImGui.Checkbox("Farm".Loc() + "###tourfarm", ref _tour.VisitFarm);
            if (changed)
                _tour.NotifyModified();
        }

        var canStart = tour.CanStart(out var why);
        using (ImRaii.Disabled(!canStart)) {
            if (ImGui.Button("Start island tour".Loc()))
                tour.RequestStart();
        }
        if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(why);
        ImGui.SameLine();
        using (ImRaii.Disabled(!tour.Running)) {
            if (ImGui.Button("Stop island tour".Loc()))
                tour.RequestStop();
        }

        var line = tour.Running ? tour.Status : tour.LastResult;
        if (line.Length > 0) {
            using (ImRaii.PushColor(ImGuiCol.Text, tour.Running ? ImGui.GetColorU32(ImGuiCol.Text) : 0xff909090u))
                ImGui.TextWrapped(line);
        }
    }

    private static void DrawNeedsBuilding(string what) {
        using (ImRaii.PushColor(ImGuiCol.Text, 0xff909090u))
            ImGui.TextWrapped(what);
    }
}
