using Dalamud.Bindings.ImGui;
using visland.Helpers;
using visland.Island;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace visland.Granary;

unsafe class GranaryWindow : UIAttachedWindow {
    private readonly GranaryConfig _config;
    private readonly GranaryDebug _debug;

    // 「補缺口」策略與表格的第 4 欄共用的暫存,避免每幀配置。
    private readonly HashSet<uint> _shortages = [];
    private readonly HashSet<uint> _scratch = [];

    public GranaryWindow() : base("Granary Automation".Loc(), "MJIGatheringHouse", new(400, 600)) {
        _config = Service.Config.Get<GranaryConfig>();
        _debug = new();
    }

    public override void PreOpenCheck() {
        base.PreOpenCheck();
        var agent = AgentMJIGatheringHouse.Instance();
        IsOpen &= agent != null && agent->Data != null && agent->Data->Initialized;
    }

    public override void Draw() {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs) {
            using (var tab = ImRaii.TabItem("Main".Loc()))
                if (tab)
                    DrawMain();
            using (var tab = ImRaii.TabItem("Debug"))
                if (tab)
                    _debug.Draw();
        }
    }

    public override void OnOpen() {
        if (_config.Reassign != GranaryConfig.UpdateStrategy.Manual) {
            uint reassignMask = 0;
            for (var i = 0; i < 2; ++i)
                if (TryAutoCollect(i) && GranaryUtils.GetGranaryState(i)->RemainingDays < 7)
                    reassignMask |= 1u << i;

            if (reassignMask != 0)
                ReassignImpl(reassignMask);
        }
    }

    private unsafe void DrawMain() {
        if (UICombo.Enum("Auto Collect".Loc(), ref _config.Collect))
            _config.NotifyModified();
        if (UICombo.Enum("Auto Reassign".Loc(), ref _config.Reassign))
            _config.NotifyModified();
        ImGuiComponents.HelpMarker("\"Cover shortages\" scores each expedition by how many of your currently short materials it can bring, and gives the second granary whatever the first one does not already cover. It only counts whether a material is covered, not how much of it arrives - daily yields are not in the game data. Shortages are measured against the workshop agenda's two-week material requirement.".Loc());
        if (ImGui.Button("Apply!".Loc()))
            ForceReassign();

        ImGui.Separator();
        DrawTable();
    }

    private void DrawTable() {
        CollectResult[] collectStates = [GranaryUtils.CalculateGranaryCollectionState(0), GranaryUtils.CalculateGranaryCollectionState(1)];

        Service.Materials.Refresh();
        Service.Materials.CollectShortages(MaterialLedger.HorizonTwoWeeks, _shortages);
        // 🔴 需求/庫存讀不到時「可補 0 種」與「真的一種都補不到」是兩件事,
        //    畫成 0 會直接誤導 —— 這種時候畫 ? 。
        var shortagesKnown = Service.Materials.DemandKnown && Service.Materials.StockKnown;

        using var table = ImRaii.Table("table", 4);
        if (table) {
            ImGui.TableSetupColumn("Expedition".Loc());
            ImGui.TableSetupColumn("Covers shortages".Loc(), ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Granary 1".Loc(), ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Granary 2".Loc(), ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableHeadersRow();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            for (var i = 0; i < 2; ++i) {
                ImGui.TableNextColumn();
                using (ImRaii.Disabled(collectStates[i] is CollectResult.NothingToCollect or CollectResult.EverythingCapped))
                    if (ImGui.Button("Collect".Loc() + $"##{i}"))
                        GranaryUtils.Collect(i);
            }

            var agent = AgentMJIGatheringHouse.Instance();
            for (var e = agent->Data->Expeditions.First; e != agent->Data->Expeditions.Last; ++e) {
                if (!agent->IsExpeditionUnlocked(e))
                    continue;

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{e->Name} ({Utils.NumItems(e->RareItemId)}/999)");

                ImGui.TableNextColumn();
                CollectExpeditionMaterials(e, _scratch);
                if (!shortagesKnown) {
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xff909090u))
                        ImGui.TextUnformatted("?");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Shortages are unknown - open the Isleworks agenda once so the material requirement can be read.".Loc());
                }
                else {
                    var covered = 0;
                    foreach (var p in _scratch)
                        if (_shortages.Contains(p))
                            ++covered;
                    ImGui.TextUnformatted("?? short".Loc(covered));
                    if (covered > 0 && ImGui.IsItemHovered())
                        ImGui.SetTooltip(DescribeCovered(_scratch));
                }

                for (var i = 0; i < 2; ++i) {
                    ImGui.TableNextColumn();
                    var curDest = GranaryUtils.GetGranaryState(i)->ActiveExpeditionId;
                    var curDays = GranaryUtils.GetGranaryState(i)->RemainingDays;
                    var maxDays = (byte)Math.Min(7, curDays + GranaryUtils.MaxDays());
                    using (ImRaii.Disabled(collectStates[i] != CollectResult.NothingToCollect || curDest == e->ExpeditionId && curDays == maxDays))
                        if (ImGui.Button((curDest == e->ExpeditionId ? "Max" : "Reassign").Loc() + $"##{i}_{e->ExpeditionId}"))
                            GranaryUtils.SelectExpedition((byte)i, e->ExpeditionId, maxDays);
                }
            }
        }
    }

    // 遠征地 -> 它會帶回來的材料(MJIItemPouch 列號)。
    // 🔑 直接讀 agent 的 ExpeditionData(裡面是 Item 列號)再轉成收納袋列號,
    //    刻意不走 MJIStockyardManagementArea —— 那樣就得先確定 ExpeditionId 是不是那張表的列號,
    //    而 CalculateConfirmation 把 `curExpedition == 0` 當成「還沒開始」,
    //    收納袋列號 0 又是真材料(棕櫚葉),這種地方差一了完全看不出來。
    //    走 agent 的話這個問題根本不存在。
    private static void CollectExpeditionMaterials(AgentMJIGatheringHouse.ExpeditionData* e, HashSet<uint> into) {
        into.Clear();
        var n = Math.Min(e->NumNormalItems, e->NormalItemIds.Length);
        for (var i = 0; i < n; ++i)
            if (MaterialSources.TryGetPouchIdByItemId(e->NormalItemIds[i], out var pouchId))
                into.Add(pouchId);
        if (e->RareItemId != 0 && MaterialSources.TryGetPouchIdByItemId(e->RareItemId, out var rarePouchId))
            into.Add(rarePouchId);
    }

    private string DescribeCovered(HashSet<uint> materials) {
        var sb = new StringBuilder();
        sb.Append("Shortages this expedition can bring:".Loc()).Append('\n');
        var shown = 0;
        foreach (var pouchId in materials) {
            if (!_shortages.Contains(pouchId))
                continue;
            if (shown++ >= 12) {
                sb.Append("  ...");
                break;
            }
            sb.Append("  ").Append(MaterialSources.ByPouchId(pouchId)?.Name ?? $"#{pouchId}").Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private readonly record struct ExpeditionCandidate(byte Id, int RareCount, HashSet<uint> Materials);

    // 把遠征地整批攤成 managed 物件再挑 —— 不跨呼叫保存任何原生指標。
    private List<ExpeditionCandidate> BuildCandidates(AgentMJIGatheringHouse* agent) {
        List<ExpeditionCandidate> candidates = [];
        for (var e = agent->Data->Expeditions.First; e != agent->Data->Expeditions.Last; ++e) {
            if (!agent->IsExpeditionUnlocked(e))
                continue;
            HashSet<uint> mats = [];
            CollectExpeditionMaterials(e, mats);
            candidates.Add(new ExpeditionCandidate(e->ExpeditionId, Utils.NumItems(e->RareItemId), mats));
        }
        return candidates;
    }

    // 評分只算「有沒有覆蓋到」不算「會拿多少」—— 每日產量不在 EXD 裡,算不出來就不要假裝算得出來。
    private static int Score(ExpeditionCandidate c, HashSet<uint> remaining) {
        var n = 0;
        foreach (var pouchId in c.Materials)
            if (remaining.Contains(pouchId))
                ++n;
        return n;
    }

    private static ExpeditionCandidate? PickBest(List<ExpeditionCandidate> candidates, HashSet<uint> remaining) {
        ExpeditionCandidate? best = null;
        var bestScore = -1;
        foreach (var c in candidates) {
            var score = Score(c, remaining);
            // 平手時沿用既有策略的精神:稀有材料存量少的優先;再平手就取 id 小的求穩定。
            if (score > bestScore
                || score == bestScore && best is { } b && (c.RareCount < b.RareCount || c.RareCount == b.RareCount && c.Id < b.Id)) {
                best = c;
                bestScore = score;
            }
        }
        return best;
    }

    private bool TryAutoCollect(int i) {
        switch (GranaryUtils.CalculateGranaryCollectionState(i)) {
            case CollectResult.NothingToCollect:
                return true;
            case CollectResult.CanCollectSafely:
                if (_config.Collect != CollectStrategy.Manual) {
                    GranaryUtils.Collect(i);
                    return true;
                }
                break;
            case CollectResult.CanCollectWithOvercap:
                if (_config.Collect == CollectStrategy.FullAuto) {
                    GranaryUtils.Collect(i);
                    return true;
                }
                break;
        }
        return false;
    }

    private void ForceReassign() {
        uint reassignMask = 0;
        for (var i = 0; i < 2; ++i)
            if (GranaryUtils.CalculateGranaryCollectionState(i) == CollectResult.NothingToCollect)
                reassignMask |= 1u << i;
        ReassignImpl(reassignMask);
    }

    private void ReassignImpl(uint allowedMask) {
        byte[] currentDestinations = [GranaryUtils.GetGranaryState(0)->ActiveExpeditionId, GranaryUtils.GetGranaryState(1)->ActiveExpeditionId];
        byte[] newDestinations = [currentDestinations[0], currentDestinations[1]];
        var agent = AgentMJIGatheringHouse.Instance();
        if (_config.Reassign is GranaryConfig.UpdateStrategy.BestDifferent or GranaryConfig.UpdateStrategy.BestSame) {
            List<(byte id, int count)> destinations = [];
            for (var e = agent->Data->Expeditions.First; e != agent->Data->Expeditions.Last; ++e)
                if (agent->IsExpeditionUnlocked(e))
                    destinations.Add((e->ExpeditionId, Utils.NumItems(e->RareItemId)));
            destinations.SortBy(e => e.count);

            if (destinations.Count > 0) {
                newDestinations[0] = destinations[0].id;
                newDestinations[1] = destinations.Count > 1 && _config.Reassign == GranaryConfig.UpdateStrategy.BestDifferent ? destinations[1].id : destinations[0].id;
                if (newDestinations[0] == currentDestinations[1] || newDestinations[1] == currentDestinations[0])
                    Utils.Swap(ref newDestinations[0], ref newDestinations[1]); // don't reassign needlessly
            }
        }
        else if (_config.Reassign == GranaryConfig.UpdateStrategy.CoverShortages) {
            // 需求基準用兩週檔(MaterialUse.Entries[2])。
            // ⚠️ 那三個檔的語意取自 CS 註解、尚未實機驗證 —— 說明文字裡有寫明是「工坊排程兩週需求」,
            //    萬一語意是別的,使用者對照得出來。
            Service.Materials.Refresh(true);
            Service.Materials.CollectShortages(MaterialLedger.HorizonTwoWeeks, _shortages);

            var candidates = BuildCandidates(agent);
            if (candidates.Count > 0) {
                // 缺口讀不到時 _shortages 是空的 -> 每個遠征地都得 0 分 ->
                // 退回平手規則(稀有材料存量最少者),行為接近既有策略,不會亂跳。
                HashSet<uint> remaining = [.. _shortages];
                var first = PickBest(candidates, remaining);
                if (first is { } f) {
                    newDestinations[0] = f.Id;
                    // 貪婪:第二個倉庫只看第一個沒覆蓋到的那些。
                    foreach (var pouchId in f.Materials)
                        remaining.Remove(pouchId);
                    var second = PickBest(candidates, remaining);
                    newDestinations[1] = second?.Id ?? f.Id;
                    if (newDestinations[0] == currentDestinations[1] || newDestinations[1] == currentDestinations[0])
                        Utils.Swap(ref newDestinations[0], ref newDestinations[1]); // 覆蓋集合的聯集不變,順手少送一次指令
                }
            }
        }

        var max = GranaryUtils.MaxDays();
        for (var i = 0; i < 2; ++i) {
            if ((allowedMask & (1u << i)) == 0)
                continue; // this granary can't be reassigned
            var curDays = GranaryUtils.GetGranaryState(i)->RemainingDays;
            var newDays = (byte)Math.Min(7, curDays + max);
            if (currentDestinations[i] == newDestinations[i] && curDays == newDays)
                continue; // this is the best already
            GranaryUtils.SelectExpedition((byte)i, newDestinations[i], newDays);
        }
    }
}
