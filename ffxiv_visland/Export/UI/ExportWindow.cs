using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using visland.Helpers;
using visland.Island;

namespace visland.Export;

unsafe class ExportWindow : UIAttachedWindow {
    private readonly ExportConfig _config;
    private readonly ExportDebug _debug = new();
    private readonly Throttle _exportThrottle = new(); // export seems to close & reopen window?..

    public ExportWindow() : base("Exports Automation".Loc(), "MJIDisposeShop", new(400, 600)) {
        _config = Service.Config.Get<ExportConfig>();
    }

    public override void PreOpenCheck() {
        base.PreOpenCheck();
        var agent = AgentMJIDisposeShop.Instance();
        IsOpen &= agent != null && agent->Data != null && agent->Data->DataInitialized;
    }

    public override void OnOpen() {
        if (_config.AutoSell) {
            _exportThrottle.Exec(AutoExport, 2);
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
        if (ImGui.Checkbox("Auto Export".Loc(), ref _config.AutoSell))
            _config.NotifyModified();
        ImGui.PushItemWidth(150);
        ImGui.SliderInt("Sell normal above".Loc(), ref _config.NormalLimit, 0, 999);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _config.NotifyModified();
        ImGui.SliderInt("Sell granary above".Loc(), ref _config.GranaryLimit, 0, 999);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _config.NotifyModified();
        ImGui.SliderInt("Sell farm above".Loc(), ref _config.FarmLimit, 0, 999);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _config.NotifyModified();
        ImGui.SliderInt("Sell pasture above".Loc(), ref _config.PastureLimit, 0, 999);
        if (ImGui.IsItemDeactivatedAfterEdit())
            _config.NotifyModified();
        ImGui.PopItemWidth();

        if (ImGui.Checkbox(HelpText.ExportRespectWorkshopNeedsLabel.Loc(), ref _config.RespectWorkshopNeeds))
            _config.NotifyModified();
        ImGuiComponents.HelpMarker(HelpText.ExportRespectWorkshopNeedsHelp.Loc());

        if (ImGui.Button("Sell everything above configured limits".Loc()))
            AutoExport();
    }

    // 🔴 資料讀不到就退回原本的行為,不要改成「不敢賣」——
    //    在未知狀態下擋賣是把行為往錯的方向改。只記一次 Information 讓使用者回報得出來。
    private bool _loggedLedgerUnavailable;
    private bool _loggedAgentUnavailable;

    private void AutoExport() {
        try {
            if (_config.RespectWorkshopNeeds) {
                Service.Materials.Refresh(true);
                if (!Service.Materials.DemandKnown || !Service.Materials.IncomingKnown) {
                    if (!_loggedLedgerUnavailable) {
                        _loggedLedgerUnavailable = true;
                        Service.Log.Information($"[Export] workshop reserve is enabled but the material ledger is unavailable (demand={Service.Materials.DemandKnown}, demandLive={Service.Materials.DemandLive}, incoming={Service.Materials.IncomingKnown}); falling back to the plain limits for this sale");
                    }
                }
            }
            // 🔴 AgentMJIDisposeShop.Instance() 是產生器產出的取得子,本體就是
            //    「agentModule == null ? null : GetAgentByInternalId(...)」—— AgentModule 還沒建好、
            //    或這個 agent 格還沒建立時合法回 null;Data 只是普通指標欄位,商店資料未載入時也是 null。
            //    外面這圈 try/catch 對此完全無效:解參考 null 是 AccessViolationException,
            //    在 .NET Core 屬 corrupted-state exception,catch (Exception) 攔不到。
            //    而且這條路徑不是「只在視窗開著時」跑 —— OnOpen 是 _exportThrottle.Exec(AutoExport, 2),
            //    會延到之後某一幀才執行,PreOpenCheck 當時驗過的狀態到那時已不保證成立。
            // fail-closed:拿不到就這次不賣。少賣一次下次還能賣,崩潰不能重來。
            var agent = AgentMJIDisposeShop.Instance();
            var data = agent == null ? null : agent->Data;
            if (data == null) {
                if (!_loggedAgentUnavailable) {
                    _loggedAgentUnavailable = true;
                    Service.Log.Information($"[Export] dispose shop agent/data unavailable (agent={(nint)agent:X}), skipping this export run");
                }
                return;
            }
            int seafarerCowries = data->CurrencyCounts[0], islanderCowries = data->CurrencyCounts[1];
            AutoExportCategory(0, _config.NormalLimit, ref seafarerCowries, ref islanderCowries);
            AutoExportCategory(1, _config.GranaryLimit, ref seafarerCowries, ref islanderCowries);
            AutoExportCategory(2, _config.FarmLimit, ref seafarerCowries, ref islanderCowries);
            AutoExportCategory(3, _config.PastureLimit, ref seafarerCowries, ref islanderCowries);
        }
        catch (Exception ex) {
            Service.Log.Error($"Error: {ex}");
            Service.ChatGui.PrintError("Auto export error: ??".Loc(ex.Message));
        }
    }

    private void AutoExportCategory(int category, int limit, ref int seafarerCowries, ref int islanderCowries) {
        if (limit >= 999)
            return;
        // 同上兩層都可能合法為 null。目前唯一呼叫端 AutoExport 已判過,但 agent 是每次重新取得的、
        // 不跨呼叫沿用 —— 各自判空,將來多一個呼叫端時才不會靜默退化成裸讀。
        var agent = AgentMJIDisposeShop.Instance();
        var data = agent == null ? null : agent->Data;
        if (data == null)
            return;
        List<AtkValue> args =
        [
            new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt },
            new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, Int = limit }
        ];
        var numItems = 0;
        foreach (var item in data->PerCategoryItems[category].AsSpan()) {
            var count = Utils.NumItems(item.Value->ItemId);

            // 「賣到剩 N」-> 「賣到剩 max(N, 兩週需求 - 在途)」。
            // 讀不到需求/在途時 TryGetWorkshopReserve 回 false,effectiveLimit 就是原本的 limit,
            // 也就是完全的舊行為。
            var effectiveLimit = limit;
            if (_config.RespectWorkshopNeeds && TryGetWorkshopReserve(item.Value->ItemId, out var reserve))
                effectiveLimit = Math.Max(limit, reserve);

            if (count <= effectiveLimit)
                continue;

            var export = count - effectiveLimit;
            var value = item.Value->CowriesPerItem * export;
            if (item.Value->UseIslanderCowries) {
                islanderCowries += value;
                if (islanderCowries > data->CurrencyStackSizes[1])
                    throw new Exception("Islander cowries would overcap".Loc());
            }
            else {
                seafarerCowries += value;
                if (seafarerCowries > data->CurrencyStackSizes[0])
                    throw new Exception("Seafarer cowries would overcap".Loc());
            }

            args.Add(new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = item.Value->ShopItemRowId });
            args.Add(new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, Int = export });
            if (++numItems > 64)
                throw new Exception("Too many items to export, please report this as a bug!".Loc());
        }
        // 📌 args[1] 刻意維持原本的 limit,不跟著 effectiveLimit 走。
        //    每個項目的實際出售量是後面那串 (ShopItemRowId, 數量) 配對決定的;
        //    萬一遊戲其實是拿 args[1] 自己重算而忽略配對,結果就是「保留量沒生效」——
        //    也就是退回今天的行為,不會比現況多賣。這個方向的失敗是可以接受的。
        var argsSpan = CollectionsMarshal.AsSpan(args);
        argsSpan[0].Int = numItems;

        Service.Log.Info($"Exporting {numItems} items above {limit} limit (workshop reserve={_config.RespectWorkshopNeeds})...");
        var listener = *(AgentInterface**)((nint)agent + 0x18);
        Utils.SynthesizeEvent(listener, 0, argsSpan);
    }

    // Item 列號 -> 收納袋列號 -> 「至少要留多少」。任何一步讀不到就回 false,呼叫端退回舊行為。
    private static bool TryGetWorkshopReserve(uint itemId, out int reserve) {
        reserve = 0;
        return MaterialSources.TryGetPouchIdByItemId(itemId, out var pouchId)
            && Service.Materials.TryGetReserve(pouchId, MaterialLedger.HorizonTwoWeeks, out reserve);
    }
}
