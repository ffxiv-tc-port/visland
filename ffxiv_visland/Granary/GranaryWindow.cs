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
    private readonly HashSet<uint> _shortages = [];          // 工坊排程算出來的缺口(次要項)
    private readonly Dictionary<uint, int> _scarcityRanks = []; // 收納袋絕對庫存最低的前 N 名(主導項)
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
            // GetGranaryState() 會回 null,原本是裸解參考。刻意維持原本的求值順序:
            // TryAutoCollect(i) 先跑(它有副作用,而且收成後 RemainingDays 才是最新的),
            // 之後才取狀態。fail-closed:讀不到就不把這座倉庫放進重新指派名單。
            for (var i = 0; i < 2; ++i)
                if (TryAutoCollect(i)) {
                    var gstate = GranaryUtils.GetGranaryState(i);
                    if (gstate != null && gstate->RemainingDays < 7)
                        reassignMask |= 1u << i;
                }

            if (reassignMask != 0)
                ReassignImpl(reassignMask);
        }
    }

    private unsafe void DrawMain() {
        if (UICombo.Enum("Auto Collect".Loc(), ref _config.Collect))
            _config.NotifyModified();
        if (UICombo.Enum("Auto Reassign".Loc(), ref _config.Reassign))
            _config.NotifyModified();
        ImGuiComponents.HelpMarker(HelpText.GranaryTopUpLowStock.Loc());
        if (ImGui.Button("Apply!".Loc()))
            ForceReassign();

        ImGui.Separator();
        DrawTable();
    }

    private void DrawTable() {
        CollectResult[] collectStates = [GranaryUtils.CalculateGranaryCollectionState(0), GranaryUtils.CalculateGranaryCollectionState(1)];

        Service.Materials.Refresh();
        var agentForScan = AgentMJIGatheringHouse.Instance();
        var candidates = BuildCandidates(agentForScan);
        // 這一欄的語意與策略必須是同一把尺:算的是
        //「收納袋現有 − 工坊排程兩週需求 最低的前 N 種,這個遠征地能補幾種」。
        // 🔴 庫存讀不到時「可補 0 種」與「一種都補不到」是兩件事,畫成 0 會直接誤導 -> 畫 ?。
        var scarcityKnown = Service.Materials.TryRankByNetStock(EligibleMaterials(candidates), TopScarce, MaterialLedger.HorizonTwoWeeks, _scarcityRanks, out var demandApplied);
        Service.Materials.CollectShortages(MaterialLedger.HorizonTwoWeeks, _shortages);

        // 🔴 用的是哪一把尺必須看得見:需求讀不到時排序整個退回純庫存,
        //    不講的話使用者會以為「先扣消耗量」已經生效 —— 那正是他要求這個功能的目的。
        if (!demandApplied) {
            using (ImRaii.PushColor(ImGuiCol.Text, 0xff909090u))
                ImGui.TextUnformatted(HelpText.DemandFallbackToPlainStock.Loc());
        }

        using var table = ImRaii.Table("table", 4);
        if (table) {
            ImGui.TableSetupColumn("Expedition".Loc());
            ImGui.TableSetupColumn("Tops up low stock".Loc(), ImGuiTableColumnFlags.WidthFixed, 120);
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

            var agent = agentForScan;
            for (var e = agent->Data->Expeditions.First; e != agent->Data->Expeditions.Last; ++e) {
                if (!agent->IsExpeditionUnlocked(e))
                    continue;

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{e->Name} ({Utils.NumItems(e->RareItemId)}/999)");

                ImGui.TableNextColumn();
                CollectExpeditionMaterials(e, _scratch);
                if (!scarcityKnown) {
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xff909090u))
                        ImGui.TextUnformatted("?");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Pouch counts could not be read, so the lowest-stock ranking is unknown.".Loc());
                }
                else {
                    var covered = 0;
                    foreach (var p in _scratch)
                        if (_scarcityRanks.ContainsKey(p))
                            ++covered;
                    ImGui.TextUnformatted("?? of ??".Loc(covered, _scarcityRanks.Count));
                    // 覆蓋 0 種時也給 tooltip:排序基準本身要隨手查得到,不能只在有命中時才說。
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(DescribeCovered(_scratch, demandApplied));
                }

                for (var i = 0; i < 2; ++i) {
                    ImGui.TableNextColumn();
                    // GetGranaryState() 會回 null(agent / GranariesState 還沒好),原本是裸解參考。
                    // fail-closed:這一格畫不出來就整格跳過,不要拿 0 當「第 0 號遠征地、剩 0 天」——
                    // 那會讓按鈕的啟用狀態與標籤都變成假的。
                    var gstate = GranaryUtils.GetGranaryState(i);
                    if (gstate == null)
                        continue;
                    var curDest = gstate->ActiveExpeditionId;
                    var curDays = gstate->RemainingDays;
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

    private string DescribeCovered(HashSet<uint> materials, bool demandApplied) {
        var sb = new StringBuilder();
        // 第一行永遠是「用的是哪一把尺」—— 數字要能被解釋,不然使用者只會覺得排序莫名其妙。
        sb.Append(demandApplied
            ? "Ranked by pouch stock minus the workshop agenda's two-week demand.".Loc()
            : HelpText.DemandFallbackToPlainStock.Loc()).Append('\n');

        // 依排名用的那把尺由少到多列,使用者才對得上上面那一欄的順序。
        List<(uint PouchId, int Net)> listed = [];
        foreach (var pouchId in materials)
            if (_scarcityRanks.ContainsKey(pouchId))
                listed.Add((pouchId, Service.Materials.NetStockOf(pouchId, MaterialLedger.HorizonTwoWeeks)));
        if (listed.Count == 0)
            return sb.ToString().TrimEnd('\n');
        listed.Sort((a, b) => a.Net != b.Net ? a.Net.CompareTo(b.Net) : a.PouchId.CompareTo(b.PouchId));

        sb.Append('\n').Append("This expedition can top up:".Loc()).Append('\n');
        var shown = 0;
        foreach (var (pouchId, net) in listed) {
            if (shown++ >= 12) {
                sb.Append("  ...").Append('\n');
                break;
            }
            var name = MaterialSources.ByPouchId(pouchId)?.Name ?? $"#{pouchId}";
            var stock = Service.Materials.StockOf(pouchId);
            sb.Append("  ").Append(demandApplied
                ? "?? (have ??, ?? after demand)".Loc(name, stock, net)
                : "?? (have ??)".Loc(name, stock));
            if (_shortages.Contains(pouchId))
                sb.Append(' ').Append("[workshop needs this]".Loc());
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    // 讓實機回報「它為什麼派這裡」有得對照 —— 使用者跑 LogLevel 2,所以寫 Information。
    private string DescribeScarcest() {
        List<(uint PouchId, int Rank)> ordered = [];
        foreach (var kv in _scarcityRanks)
            ordered.Add((kv.Key, kv.Value));
        ordered.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        var sb = new StringBuilder();
        foreach (var (pouchId, _) in ordered) {
            if (sb.Length > 0)
                sb.Append(", ");
            sb.Append(MaterialSources.ByPouchId(pouchId)?.Name ?? $"#{pouchId}").Append('=')
                .Append(Service.Materials.StockOf(pouchId)).Append("->")
                .Append(Service.Materials.NetStockOf(pouchId, MaterialLedger.HorizonTwoWeeks));
        }
        return sb.Length > 0 ? sb.ToString() : "(stock unavailable)";
    }

    private readonly record struct ExpeditionCandidate(byte Id, int RareCount, HashSet<uint> Materials);

    // 把遠征地整批攤成 managed 物件再挑 —— 不跨呼叫保存任何原生指標。
    private List<ExpeditionCandidate> BuildCandidates(AgentMJIGatheringHouse* agent) {
        List<ExpeditionCandidate> candidates = [];
        // 🔴 兩個呼叫端傳進來的都是 AgentMJIGatheringHouse.Instance() 的原樣回傳值,而它合法回 null
        //    (產生器本體即 agentModule == null ? null : ...);Data 也只是普通指標欄位,可能還沒載入。
        //    在被呼叫端判空,兩個呼叫端就一起被涵蓋,將來多一個也不會漏。
        // fail-closed:回空清單。呼叫端對「candidates.Count > 0」都有分支,空清單＝不改變遠征地。
        if (agent == null || agent->Data == null)
            return candidates;
        for (var e = agent->Data->Expeditions.First; e != agent->Data->Expeditions.Last; ++e) {
            if (!agent->IsExpeditionUnlocked(e))
                continue;
            HashSet<uint> mats = [];
            CollectExpeditionMaterials(e, mats);
            candidates.Add(new ExpeditionCandidate(e->ExpeditionId, Utils.NumItems(e->RareItemId), mats));
        }
        return candidates;
    }

    // 全部未解鎖遠征地能帶回的材料聯集 —— 稀缺排名只在這個集合裡排,
    // 否則種子/作物/畜牧產物那些倉庫根本帶不回來的東西會佔滿前幾名。
    private HashSet<uint> EligibleMaterials(List<ExpeditionCandidate> candidates) {
        HashSet<uint> eligible = [];
        foreach (var c in candidates)
            foreach (var pouchId in c.Materials)
                eligible.Add(pouchId);
        return eligible;
    }

    // 評分只算「有沒有覆蓋到」不算「會拿多少」—— 每日產量不在 EXD 裡,算不出來就不要假裝算得出來。
    //
    // 🔑 主導項是「收納袋現有 − 工坊兩週需求 最低」:權重 1 << (TopScarce-1-名次),
    //    所以較低名次全部加起來也贏不過任何一個更高的名次
    //    (512 > 256+128+...+1 = 511)—— 覆蓋到最缺那一項的遠征地必定勝出。
    //    這正是使用者要的語意:最缺鐵礦就該派往「山」,而不是被一堆中等材料的廣度蓋過去。
    // 🔑 需求 0 的材料鍵值就等於它的庫存,照樣參與排名 —— 所以「鐵礦沒被排程吃到就被無視」
    //    那個舊 bug 不會因為扣需求而回來(它的成因是拿缺口當篩選器,不是扣需求本身)。
    // 工坊缺口保留為次要項:主導項雖然已經把需求吃進去了,但缺口多扣了在途、且不受前 N 名限制,
    //    所以在主導項完全平手(例如兩個遠征地都沒覆蓋到前 N 名)時仍有鑑別力。
    // ⚠️ 兩項是嚴格字典序比較(見 PickBest):次要項只在主導項相等時才看 -> 不會雙重計分。
    private const int TopScarce = 10;

    private int ShortageScore(ExpeditionCandidate c, HashSet<uint> remaining) {
        var n = 0;
        foreach (var pouchId in c.Materials)
            if (remaining.Contains(pouchId))
                ++n;
        return n;
    }

    private ExpeditionCandidate? PickBest(List<ExpeditionCandidate> candidates, HashSet<uint> remainingScarce, HashSet<uint> remainingShortages) {
        ExpeditionCandidate? best = null;
        (int Scarcity, int Shortage) bestScore = (-1, -1);
        foreach (var c in candidates) {
            var scarcity = 0;
            foreach (var pouchId in c.Materials)
                if (remainingScarce.Contains(pouchId) && _scarcityRanks.TryGetValue(pouchId, out var rank))
                    scarcity += 1 << (TopScarce - 1 - rank);
            var score = (scarcity, ShortageScore(c, remainingShortages));

            var better = score.Item1 > bestScore.Scarcity
                || score.Item1 == bestScore.Scarcity && score.Item2 > bestScore.Shortage
                // 兩項都平手時沿用既有策略的精神:稀有材料存量少的優先;再平手取 id 小的求穩定。
                || score.Item1 == bestScore.Scarcity && score.Item2 == bestScore.Shortage && best is { } b
                    && (c.RareCount < b.RareCount || c.RareCount == b.RareCount && c.Id < b.Id);
            if (better) {
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
        // 🔴 GranaryUtils.GetGranaryState() 自己就會回 null(agent 或 GranariesState 還沒好),
        //    原本這一行是對它的回傳值直接裸解參考;AgentMJIGatheringHouse.Instance() 同樣合法回 null,
        //    而 agent->Data 是第二層裸讀。任一層是 null 就是 AccessViolationException ——
        //    corrupted-state exception,try/catch 攔不到,遊戲直接被帶走。
        // fail-closed:讀不到就這次不重新指派,遠征維持現狀;下次開窗或按 Apply 會再試一次。
        var state0 = GranaryUtils.GetGranaryState(0);
        var state1 = GranaryUtils.GetGranaryState(1);
        var agent = AgentMJIGatheringHouse.Instance();
        if (state0 == null || state1 == null || agent == null || agent->Data == null) {
            Service.Log.Information($"[Granary] reassign skipped: granary state or agent unavailable (s0={(nint)state0:X}, s1={(nint)state1:X}, agent={(nint)agent:X})");
            return;
        }
        byte[] currentDestinations = [state0->ActiveExpeditionId, state1->ActiveExpeditionId];
        byte[] newDestinations = [currentDestinations[0], currentDestinations[1]];
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
            Service.Materials.Refresh(true);
            var candidates = BuildCandidates(agent);
            if (candidates.Count > 0) {
                // 主導項:收納袋現有 − 工坊兩週需求 最低的前 N 種(只在倉庫真的帶得回來的材料裡排)。
                // 🔴 需求讀不到時 demandApplied=false,排序自動退回純庫存(加功能前的行為),
                //    絕不把「不知道」當 0 去扣。
                Service.Materials.TryRankByNetStock(EligibleMaterials(candidates), TopScarce, MaterialLedger.HorizonTwoWeeks, _scarcityRanks, out var demandApplied);
                // 次要項:工坊排程算出來的缺口,只在稀缺分數平手時才有作用。
                // ⚠️ 需求檔的語意(兩週)取自 CS 註解、尚未實機驗證。
                Service.Materials.CollectShortages(MaterialLedger.HorizonTwoWeeks, _shortages);

                // 庫存讀不到時 _scarcityRanks 是空的、缺口也是空的 -> 兩項都 0 分 ->
                // 退回平手規則(稀有材料存量最少者),也就是既有策略的行為,不會亂跳。
                HashSet<uint> remainingScarce = [.. _scarcityRanks.Keys];
                HashSet<uint> remainingShortages = [.. _shortages];
                var first = PickBest(candidates, remainingScarce, remainingShortages);
                if (first is { } f) {
                    newDestinations[0] = f.Id;
                    // 貪婪:第二個倉庫只看第一個沒覆蓋到的那些。
                    foreach (var pouchId in f.Materials) {
                        remainingScarce.Remove(pouchId);
                        remainingShortages.Remove(pouchId);
                    }
                    var second = PickBest(candidates, remainingScarce, remainingShortages);
                    newDestinations[1] = second?.Id ?? f.Id;
                    if (newDestinations[0] == currentDestinations[1] || newDestinations[1] == currentDestinations[0])
                        Utils.Swap(ref newDestinations[0], ref newDestinations[1]); // 覆蓋集合的聯集不變,順手少送一次指令

                    Service.Log.Information($"[Granary] cover-shortages picked {newDestinations[0]}/{newDestinations[1]}; demandApplied={demandApplied}; scarcest(stock->net)={DescribeScarcest()}");
                }
            }
        }

        var max = GranaryUtils.MaxDays();
        for (var i = 0; i < 2; ++i) {
            if ((allowedMask & (1u << i)) == 0)
                continue; // this granary can't be reassigned
            // GetGranaryState() 會回 null;上面已經判過一次,但這是重新取得的一次呼叫,各自判空。
            var gstate = GranaryUtils.GetGranaryState(i);
            if (gstate == null)
                continue;
            var curDays = gstate->RemainingDays;
            var newDays = (byte)Math.Min(7, curDays + max);
            if (currentDestinations[i] == newDestinations[i] && curDays == newDays)
                continue; // this is the best already
            GranaryUtils.SelectExpedition((byte)i, newDestinations[i], newDays);
        }
    }
}
