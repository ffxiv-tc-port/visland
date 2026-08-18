using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using System.Text;
using visland.Helpers;
using visland.Island;

namespace visland.Farm;

public unsafe class FarmWindow : UIAttachedWindow {
    private readonly FarmConfig _config;
    private readonly FarmDebug _debug = new();

    // 與缺料總表(MaterialLedgerTab)刻意同色 —— 「缺口」與「不知道」在兩個視窗必須長得一樣,
    // 這裡不引入新的顏色系統。
    private static readonly uint ColShortage = 0xff4f53d9; // ABGR:紅
    private static readonly uint ColUnknown = 0xff909090;  // 灰:代表「不知道」,不是 0

    public FarmWindow() : base("Farm Automation".Loc(), "MJIFarmManagement", new(400, 600)) {
        _config = Service.Config.Get<FarmConfig>();
    }

    public override void OnOpen() {
        if (_config.Collect != CollectStrategy.Manual) {
            var state = CalculateCollectResult();
            if (state == CollectResult.CanCollectSafely || _config.Collect == CollectStrategy.FullAuto && state == CollectResult.CanCollectWithOvercap) {
                CollectAll();
            }
        }
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

    private void DrawMain() {
        if (UICombo.Enum("Auto Collect".Loc(), ref _config.Collect))
            _config.NotifyModified();
        ImGui.Separator();

        var mji = MJIManager.Instance();
        var agent = AgentMJIFarmManagement.Instance();
        if (mji == null || mji->FarmState == null || mji->IslandState.Farm.EligibleForCare == 0 || agent == null) {
            ImGui.TextUnformatted("Mammets not available!".Loc());
            return;
        }

        DrawGlobalOperations();
        ImGui.Separator();
        DrawPlotOperations();
    }

    private void DrawGlobalOperations() {
        var res = CalculateCollectResult();
        if (res != CollectResult.NothingToCollect) {
            // if there's uncollected stuff - propose to collect everything
            using (ImRaii.Disabled(res == CollectResult.EverythingCapped)) {
                if (ImGui.Button("Collect all".Loc()))
                    CollectAll();
                if (res != CollectResult.CanCollectSafely) {
                    ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xff0000ff))
                        ImGui.TextV(res == CollectResult.EverythingCapped ? "Inventory is full!".Loc() : "Warning: some resources will overcap!".Loc());
                }
            }
        }
        else {
            bool canDismiss = false, canEntrust = false;
            var agent = AgentMJIFarmManagement.Instance();
            for (var i = 0; i < agent->NumSlots; ++i) {
                var cared = agent->Slots[i].UnderCare;
                canDismiss |= cared;
                canEntrust |= !cared && agent->Slots[i].SeedItemId != 0;
            }

            using (ImRaii.Disabled(!canDismiss))
                if (ImGui.Button("Dismiss all".Loc()))
                    DismissAll();
            ImGui.SameLine();
            using (ImRaii.Disabled(!canEntrust))
                if (ImGui.Button("Entrust all".Loc()))
                    EntrustAll();
        }
    }

    private void DrawPlotOperations() {
        // 🔴 這份材料資料只有「有人呼叫 Refresh」才會更新,而既有的呼叫端(缺料總表 / 屯貨倉庫 /
        //    產品交易)全都在別的視窗裡。耕地視窗單獨開著時若不自己叫一次,需求欄會停在一份
        //    從未載入過的空資料上 —— 而那看起來就只是「需求是 0」。
        //    節流(500ms)在 Refresh() 內部,每幀呼叫無妨。
        Service.Materials.Refresh();

        using var table = ImRaii.Table("table", 3);
        if (table) {
            ImGui.TableSetupColumn("Slot".Loc());
            ImGui.TableSetupColumn("Need".Loc(), ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Operations".Loc());
            ImGui.TableHeadersRow();

            var agent = AgentMJIFarmManagement.Instance();
            for (var i = 0; i < agent->NumSlots; ++i) {
                ref var slot = ref agent->Slots[i];
                var inventory = Utils.NumItems(slot.YieldItemId);
                var overcap = inventory + slot.YieldAvailable > 999;
                var full = inventory == 999;

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, full ? 0xff0000ff : 0xff00ffff, overcap))
                    ImGui.TextV($"{slot.YieldName}: {inventory} + {slot.YieldAvailable} / 999");

                ImGui.TableNextColumn();
                DrawDemand(slot.YieldItemId);

                ImGui.TableNextColumn();
                if (slot.YieldAvailable > 0) {
                    using (ImRaii.Disabled(full)) {
                        if (ImGui.Button("Collect".Loc() + $"##{i}"))
                            CollectOne(i, false);
                        ImGui.SameLine();
                        if (ImGui.Button("Collect & dismiss".Loc() + $"##{i}"))
                            CollectOne(i, true);
                    }
                }
                else if (slot.UnderCare) {
                    if (ImGui.Button("Dismiss".Loc() + $"##{i}"))
                        DismissOne(i);
                }
                else if (slot.SeedItemId != 0) {
                    if (slot.WasUnderCare || Utils.NumCowries() >= 5) {
                        if (ImGui.Button("Entrust".Loc() + $"##{i}"))
                            EntrustOne(i, slot.SeedItemId);
                    }
                    // else: not enough cowries
                }
                // TODO: else - choose what to plant?
            }
        }
    }

    // 開拓工坊排程對這個作物的需求量。決定要種什麼看的是消耗端,只看產量會種出一堆沒人吃的東西。
    // 🔴 三態必須分得出來:排程還沒讀到 -> 灰色 `?`,絕不畫 0。
    //    把「不知道」畫成 0 等於告訴使用者「這個沒人要」,而那正好是會被剷掉的那一格。
    private static void DrawDemand(uint yieldItemId) {
        if (yieldItemId == 0)
            return; // 空格:沒種東西就沒有消耗端可談

        var ledger = Service.Materials;
        if (ledger.Rows.Length == 0) {
            // 材料表沒建起來(Excel 讀不到)。這不是「不需要」,是「答不出來」。
            using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown))
                ImGui.TextUnformatted("?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The island material table could not be built.".Loc());
            return;
        }

        var row = ledger.RowByItemId(yieldItemId);
        if (row == null) {
            // 收成物不在 MJIItemPouch 裡 = 工坊配方吃不到它。這是「確定不需要」,不是「不知道」,
            // 所以畫破折號而不是問號。
            using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown))
                ImGui.TextUnformatted("-");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The workshop agenda never uses this item.".Loc());
            return;
        }

        if (!ledger.DemandKnown) {
            using (ImRaii.PushColor(ImGuiCol.Text, ColUnknown))
                ImGui.TextUnformatted("?");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(HelpText.AgendaNotReadYet.Loc());
            return;
        }

        // 缺口的定義與缺料總表 / 屯貨倉庫共用同一把尺(需求 - 收納袋 - 在途),
        // 不要在這裡另外算一個 —— 同一個詞在兩個視窗算出不同答案比不顯示更糟。
        var shortage = ledger.GapKnown(row) && ledger.Gap(row, MaterialLedger.HorizonTwoWeeks) > 0;
        using (ImRaii.PushColor(ImGuiCol.Text, ColShortage, shortage))
            ImGui.TextUnformatted(row.Demand[MaterialLedger.HorizonTwoWeeks].ToString());
        if (ImGui.IsItemHovered())
            DrawDemandTooltip(row);
    }

    // 列上只放一個數字(兩週口徑);三個口徑的明細與「為什麼是紅的」放這裡。
    private static void DrawDemandTooltip(MaterialLedgerRow row) {
        var ledger = Service.Materials;
        var sb = new StringBuilder();
        sb.Append(row.Info.Name).Append('\n');
        sb.Append(HelpText.DemandBreakdown.Loc(row.Demand[0], row.Demand[1], row.Demand[2])).Append('\n');
        sb.Append(ledger.StockKnown
            ? "In pouch: ??".Loc(row.Stock)
            : "Pouch count unknown.".Loc()).Append('\n');
        sb.Append(HelpText.IncomingBreakdown.Loc(
            ledger.GranaryKnown ? row.Granary : "?",
            ledger.FarmKnown ? row.Farm : "?",
            ledger.PastureKnown ? row.Pasture : "?")).Append('\n');
        sb.Append('\n');
        sb.Append("The number on the row is the two-week demand. The three bucket names come from client struct comments and have not been verified in game yet.".Loc()).Append('\n');
        sb.Append("It turns red while the workshop still needs more than your pouch holds plus everything already on its way.".Loc());
        // 新鮮度是「為什麼」不是「有沒有問題」 -> 住 tooltip。
        if (ledger.DescribeDemandFreshness() is { } freshness)
            sb.Append('\n').Append('\n').Append(freshness);
        ImGui.SetTooltip(sb.ToString());
    }

    private CollectResult CalculateCollectResult() {
        var agent = AgentMJIFarmManagement.Instance();
        var mji = MJIManager.Instance();
        if (agent == null || agent->TotalAvailableYield <= 0 || mji == null || mji->FarmState == null)
            return CollectResult.NothingToCollect;

        var perCropYield = new int[MJICropSeed.Get().Count];
        for (var i = 0; i < 20; ++i) {
            var seed = mji->FarmState->SeedType[i];
            if (seed != 0) {
                perCropYield[seed] += mji->FarmState->GardenerYield[i];
            }
        }

        var anyOvercap = false;
        var allFull = true;
        for (var i = 1; i < perCropYield.Length; ++i) {
            if (perCropYield[i] == 0)
                continue;

            var inventory = Utils.NumItems(MJICropSeed.GetRow((uint)i)!.Value.Item.RowId);
            allFull &= inventory >= 999;
            anyOvercap |= inventory + perCropYield[i] > 999;
        }
        return allFull ? CollectResult.EverythingCapped : anyOvercap ? CollectResult.CanCollectWithOvercap : CollectResult.CanCollectSafely;
    }

    private void CollectOne(int slot, bool dismissAfter) {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null) {
            Service.Log.Info($"Collecting slot {slot}, dismiss={dismissAfter}");
            if (dismissAfter)
                mji->FarmState->CollectSingleAndDismiss((uint)slot);
            else
                mji->FarmState->CollectSingle((uint)slot);
        }
    }

    private void CollectAll() {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null) {
            Service.Log.Info("Collecting everything from farm");
            mji->FarmState->UpdateExpectedTotalYield();
            mji->FarmState->CollectAll(true);
        }
    }

    private void DismissOne(int slot) {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null) {
            Service.Log.Info($"Dismissing slot {slot}");
            mji->FarmState->Dismiss((uint)slot);
        }
    }

    private void DismissAll() {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null) {
            Service.Log.Info($"Dismissing all");
            for (var i = 0; i < 20; ++i) {
                if (mji->FarmState->FarmSlotFlags[i].HasFlag(FarmSlotFlags.UnderCare))
                    mji->FarmState->Dismiss((uint)i);
            }
        }
    }

    private void EntrustOne(int slot, uint seedId) {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null) {
            Service.Log.Info($"Entrusting slot {slot}, planting {seedId}");
            mji->FarmState->Entrust((uint)slot, seedId);
        }
    }

    private void EntrustAll() {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null) {
            Service.Log.Info($"Entrusting all");
            for (var i = 0; i < 20; ++i) {
                var seed = mji->FarmState->SeedType[i];
                if (seed != 0 && !mji->FarmState->FarmSlotFlags[i].HasFlag(FarmSlotFlags.UnderCare)) {
                    mji->FarmState->Entrust((uint)i, mji->FarmState->SeedItemIds.AsSpan()[seed]);
                }
            }
        }
    }
}
