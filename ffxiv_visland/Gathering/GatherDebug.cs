using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using System.Linq;
using visland.Helpers;

namespace visland.Gathering;

public unsafe class GatherDebug(GatherRouteExec exec) {
    private readonly GatherRouteExec exec = exec;

    // 放在 Player.Available 閘門之前：掃描開著時，停止鈕任何時候都要按得到。
    private static void DrawIslandScan() {
        var scan = Service.IslandScan;
        Utils.DrawSection("Island Gathering Point Scan".Loc(), ImGuiColors.ParsedGold);
        ImGui.TextWrapped("Once per second, records every island gathering point in range (name, position, whether it can be gathered) and which materials each gather added to your pouch. Read-only; it never moves or gathers for you. Walk or run your routes around the island while it is on, then stop it. The result is saved as a separate file in the plugin config folder; your routes and settings are not touched.".Loc());
        if (ImGui.Button(scan.Running ? "Stop Scan".Loc() : "Start Scan".Loc())) {
            if (scan.Running)
                scan.Stop();
            else
                scan.Start();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(scan.Running ? "Scanning".Loc() : "Off".Loc());
        if (scan.Running && Player.Territory != 1055)
            ImGui.TextDisabled("Not on your island - the scan waits until you are.".Loc());
        if (scan.FileName != null) {
            ImGui.TextUnformatted("Points seen: ??, gathers recorded: ??, pouch gains with no point nearby: ??".Loc(scan.NodeCount, scan.YieldCount, scan.Unattributed));
            ImGui.TextUnformatted("File: ??".Loc(scan.FileName));
        }
        if (scan.LastSaveError != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, "Save failed: ??".Loc(scan.LastSaveError));
    }

    public void Draw() {
        using var child = ImRaii.Child("child");
        if (!child) return;
        DrawIslandScan();
        if (!Player.Available) return;

        if (exec.RouteDB.AutoRetainerIntegration) {
            Utils.DrawSection("AutoRetainer Integration", ImGuiColors.ParsedGold);
            Utils.DrawSection($"Conditions to Begin", ImGuiColors.ParsedGold, drawSeparator: false);
            ImGui.Text($"One of:");
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.Text($"Subs Ready: {Service.Retainers.HasSubsReady}");
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.Text($"Retainers Ready: {Service.Retainers.HasRetainersReady}");
            ImGui.Text($"Preferred Character == Current Character: {Service.Retainers.GetPreferredCharacter() == Player.CID}");
            Utils.DrawSection($"Conditions to End", ImGuiColors.ParsedGold, drawSeparator: false);
            ImGui.Text($"Route paused: {exec.Paused}");
            ImGui.Text($"Retainers finished: {Service.Retainers.Finished}");
            ImGui.Text($"All of the below:");
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.Text($"MultiMode Enabled: {Service.AutoRetainer.GetMultiEnabled()}");
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.Text($"Not Busy: {!Service.AutoRetainer.IsBusy()}");
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.Text($"Current Character == Starting Character: {Player.CID == Service.Retainers.StartingCharacter}");
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.Text($"No Retainers Ready: {!Service.Retainers.HasRetainersReady}");
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.Text($"No Subs Ready: {!Service.Retainers.HasSubsReady}");
            ImGui.Text($"Preferred Character == Current Character: {Service.Retainers.GetPreferredCharacter() == Player.CID}");
        }

        if (Service.TargetManager.Target != null) {
            Utils.DrawSection("Target", ImGuiColors.ParsedGold);
            var t = Service.TargetManager.Target;
            if (GatheringPoint.GetRow(t.BaseId) is { } gp) {
                ImGui.Text($"IsNode: {gp}");
                if (gp.GatheringPointBase.IsValid)
                    ImGui.Text($"GatheringType: {gp.GatheringPointBase.Value.GatheringType.RowId}");
            }
            else
                ImGui.Text($"Not a GatheringPoint (BaseId={t.BaseId})");
        }
        if (exec.CurrentRoute is { TargetGatherItem: not 0 } && Item.GetRow((uint)exec.CurrentRoute.TargetGatherItem) is { } item) {
            Utils.DrawSection("Target Item", ImGuiColors.ParsedGold);
            var wp = exec.CurrentRoute.Waypoints[exec.CurrentWaypoint];
            ImGui.Text($"[{exec.CurrentRoute.TargetGatherItem}] {item.Name}");
            ImGui.Text($"Waypoint: IsNode: {wp.IsNode} Type: {wp.GatheringType} NodeJob: {wp.NodeJob}");
        }
        if (exec.GatheringAM != null) {
            Utils.DrawSection("Gathering Addon", ImGuiColors.ParsedGold);
            // 讀不到就畫 "?"：把「不知道」畫成 0 會讓人以為完整度真的是 0。
            ImGui.Text($"Integrity: {exec.GatheringAM.CurrentIntegrityOrUnknown?.ToString() ?? "?"}/{exec.GatheringAM.TotalIntegrityOrUnknown?.ToString() ?? "?"}");
            foreach (var gatherable in exec.GatheringAM.Items.Where(x => x.IsEnabled)) {
                ImGui.TextV($@"[{gatherable.ItemID}] Lv{gatherable.ItemLevel} {gatherable.GatherChance}% {gatherable.ItemName} {(gatherable.IsCollectable ? SeIconChar.Collectible : string.Empty)}");
                ImGui.SameLine();
                if (ImGui.IconButton(Dalamud.Interface.FontAwesomeIcon.BoreHole, $"###{gatherable.ItemID}"))
                    gatherable.Gather();
            }
        }
        if (exec.GatheredItem != null) {
            Utils.DrawSection("Gathered Item", ImGuiColors.ParsedGold);
            ImGui.Text($"[{exec.GatheredItem.ItemID}] {exec.GatheredItem.ItemName} {(exec.GatheredItem.IsCollectable ? SeIconChar.Collectible : string.Empty)}");
        }
        if (exec.GatheringCollectableAM != null) {
            Utils.DrawSection("Gathering Collectable Addon", ImGuiColors.ParsedGold);
            ImGui.Text($"Item: [{exec.GatheringCollectableAM.ItemID}] {exec.GatheringCollectableAM.ItemName}");
            // 同上：AtkValues 讀不到時畫 "?"，不要畫成 0。
            ImGui.Text($"Integrity: {exec.GatheringCollectableAM.CurrentIntegrityOrUnknown?.ToString() ?? "?"}/{exec.GatheringCollectableAM.TotalIntegrityOrUnknown?.ToString() ?? "?"}");
            ImGui.Text($"Collectability: {exec.GatheringCollectableAM.CurrentCollectabilityOrUnknown?.ToString() ?? "?"}/{exec.GatheringCollectableAM.MaxCollectabilityOrUnknown?.ToString() ?? "?"}");
            ImGui.Text($"Scour: {exec.GatheringCollectableAM.ScourPower} Brazen: {exec.GatheringCollectableAM.BrazenPowerMin}/{exec.GatheringCollectableAM.BrazenPowerMax} Meticulous: {exec.GatheringCollectableAM.MeticulousPower}");
        }
    }
}
