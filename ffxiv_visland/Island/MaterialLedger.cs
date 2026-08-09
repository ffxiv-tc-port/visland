using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using visland.Helpers;

namespace visland.Island;

// 無人島「缺料總表」的資料層:缺口 = 需求 - 庫存 - 在途,逐 MJIItemPouch 列。
// 純顯示,不做任何寫入或自動化。
//
// 三態很重要:分不出「真的是 0」與「這份資料還沒載入」的話,畫成 0 會直接誤導使用者。
// 所以每個來源各自帶一個 *Known 旗標,拿不到就在 UI 上畫 `?`。
public sealed class MaterialLedgerRow {
    public MaterialInfo Info = null!;
    public bool Unlocked;                  // 這個材料在收納袋裡是否已解鎖(採集/製作過一次)
    public readonly int[] Demand = new int[MaterialLedger.DemandEntryCount];
    public int Stock;
    public int Granary;
    public int Farm;
    public int Pasture;

    public int Incoming => Granary + Farm + Pasture;
}

public sealed unsafe class MaterialLedger {
    public const int DemandEntryCount = 3;

    // CS 的註解說 [0] = 本日、[1] = 本週、[2] = 本週+下週,**這個語意沒有實機驗證過**。
    // 所以 P0 把三個都留著,列上用哪一個由使用者選,tooltip 三個都顯示,
    // 另外把三個總和寫成 Information log —— 在週初/週末各看一次就能自然分辨誰是誰。
    public const int HorizonCycle = 0;
    public const int HorizonWeek = 1;
    public const int HorizonTwoWeeks = 2;

    public MaterialLedgerRow[] Rows = [];

    public bool IslandDataAvailable;   // MJIManager 拿得到
    public bool DemandKnown;           // AgentMJICraftSchedule 的排程資料讀過了
    public bool StockKnown;            // 收納袋數量可信
    public bool GranaryKnown;
    public bool FarmKnown;
    public bool PastureKnown;
    public bool StockFrozen;           // 切區中 / 讀到全 0,保留上一次的快照
    public bool OnIsland;              // 在途數量只有站在島上才讀得到
    public byte DemandCycle;
    public int IslandRank;

    private long _nextRefresh;
    private long _nextDemandLog;
    private (int, int, int) _lastLoggedDemand = (-1, -1, -1);
    private bool _everSawStock;
    private bool _loggedToolMismatch;

    private const int RefreshIntervalMs = 500;
    private const int DemandLogCooldownMs = 60_000;

    public void Refresh(bool force = false) {
        var now = Environment.TickCount64;
        if (!force && now < _nextRefresh)
            return;
        _nextRefresh = now + RefreshIntervalMs;

        var sources = MaterialSources.All;
        if (Rows.Length != sources.Length) {
            Rows = new MaterialLedgerRow[sources.Length];
            for (var i = 0; i < sources.Length; ++i)
                Rows[i] = new MaterialLedgerRow { Info = sources[i] };
        }
        if (Rows.Length == 0)
            return;

        // 🔴 這一頁掛在主視窗上,標題畫面/角色選擇時也可能開著。
        //    Utils.NumItems 是 InventoryManager.Instance()->GetInventoryItemCount(...) 且**沒有 null 檢查**
        //    (既有呼叫端全是 UIAttachedWindow,只在遊戲內畫,所以踩不到)。
        //    未登入時那個靜態指標是 null,呼叫下去就是 this = null 的原生函式 —— AVE,try/catch 攔不到。
        if (!Service.ClientState.IsLoggedIn) {
            IslandDataAvailable = DemandKnown = StockKnown = false;
            GranaryKnown = FarmKnown = PastureKnown = false;
            return;
        }

        var mji = MJIManager.Instance();
        IslandDataAvailable = mji != null;
        if (mji == null) {
            DemandKnown = StockKnown = GranaryKnown = FarmKnown = PastureKnown = false;
            return;
        }

        IslandRank = mji->IslandState.CurrentRank;
        ReadUnlocks(mji);
        ReadDemand();
        ReadStock(mji);
        ReadIncoming(mji);
    }

    private void ReadUnlocks(MJIManager* mji) {
        // IslandState.LockedPouchItems 是以 MJIItemPouch 列號直接索引的 byte 陣列(非 0 = 未解鎖)。
        // 刻意讀欄位而不是呼叫 MJIManager.IsPouchItemLocked ——
        // 那是 [MemberFunction],特徵碼在台服失配時是「載入照常、首次呼叫才擲例外」的靜默失敗。
        var locked = mji->IslandState.LockedPouchItems;
        var n = Math.Min(Rows.Length, locked.Length);
        for (var i = 0; i < n; ++i)
            Rows[i].Unlocked = locked[i] == 0;

        if (!_loggedToolMismatch) {
            // 一致性檢查:材料已解鎖就代表當初一定拿得到對應工具。
            // CS 對 IslandState._unlockedKeyItems 的散文註解說 index = RowId - 1,
            // 但它自己的 IsKeyItemUnlocked 用的是 RowId —— 兩者矛盾且我們無法離線分辨。
            // 這裡不猜,直接拿「已解鎖的材料」當已知真值去對,不一致就記一筆 Information。
            for (var i = 0; i < n; ++i) {
                var g = Rows[i].Info.Gather;
                if (g == null || g.ToolKeyItemRow == 0 || !Rows[i].Unlocked)
                    continue;
                if (mji->IsKeyItemUnlocked((ushort)g.ToolKeyItemRow))
                    continue;
                Service.Log.Information($"[Materials] key item unlock mismatch: pouch {i} '{Rows[i].Info.Name}' is unlocked but MJIKeyItem {g.ToolKeyItemRow} ('{g.ToolName}') reads locked - key item bit index is probably off by one");
                _loggedToolMismatch = true;
                break;
            }
        }
    }

    private void ReadDemand() {
        var agent = AgentMJICraftSchedule.Instance();
        var data = agent != null ? agent->Data : null;
        if (data == null) {
            DemandKnown = false;
            return;
        }

        DemandKnown = true;
        DemandCycle = data->MaterialUse.Cycle;
        var entries = data->MaterialUse.Entries;
        var numEntries = Math.Min(DemandEntryCount, entries.Length);
        Span<int> sums = stackalloc int[DemandEntryCount];

        for (var e = 0; e < numEntries; ++e) {
            ref var entry = ref entries[e];
            var used = entry.UsedAmounts;
            // 🔴 兩邊都取小:UsedAmounts 是 FixedSizeArray109,而 Rows 的長度來自表列數。
            //    表加列時 109 不會跟著長,只照表列數跑會直接讀到結構外(AVE 攔不到)。
            var n = Math.Min(Rows.Length, used.Length);
            var sum = 0;
            for (var i = 0; i < n; ++i) {
                var v = used[i];
                Rows[i].Demand[e] = v;
                sum += v;
            }
            for (var i = n; i < Rows.Length; ++i)
                Rows[i].Demand[e] = 0;
            sums[e] = sum;
        }
        for (var e = numEntries; e < DemandEntryCount; ++e)
            for (var i = 0; i < Rows.Length; ++i)
                Rows[i].Demand[e] = 0;

        var triple = (sums[0], sums[1], sums[2]);
        var now = Environment.TickCount64;
        if (triple != _lastLoggedDemand && now >= _nextDemandLog) {
            _lastLoggedDemand = triple;
            _nextDemandLog = now + DemandLogCooldownMs;
            var monotonic = sums[0] <= sums[1] && sums[1] <= sums[2];
            Service.Log.Information($"[Materials] MaterialUse cycle={DemandCycle} totals=[{sums[0]}, {sums[1]}, {sums[2]}] monotonic(0<=1<=2)={monotonic} - CS calls them cycle/week/week+next, unverified");
        }
    }

    private void ReadStock(MJIManager* mji) {
        // 🔑 離線反組譯確認(台服 7.20):AgentMJIPouch::GetPouchItemCount 自己就是去呼叫
        //    InventoryManager::GetInventoryItemCount(pouchItem->ItemId, false, true, true, 0),
        //    而 GetInventoryItemCount 對收納袋道具會跳過一般背包、改問無人島那個管理器。
        //    ⇒ Utils.NumItems() 就是遊戲自己那條路,不需要開過收納袋 UI。
        // ⚠️ 但它在資料還沒載入時是**靜默回 0**(ICE 在 BetweenAreas 踩過)。
        //    所以切區中不更新,而且整份讀成 0 時保留上一次的快照。
        if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51]) {
            StockFrozen = StockKnown;
            return;
        }

        var total = 0;
        var n = Rows.Length;
        var fresh = new int[n];
        for (var i = 0; i < n; ++i) {
            var itemId = Rows[i].Info.ItemId;
            var count = itemId != 0 ? Utils.NumItems(itemId) : 0;
            fresh[i] = count;
            total += count;
        }

        if (total == 0 && _everSawStock) {
            // 曾經讀到過東西,現在整份是 0 —— 幾乎一定是資料沒載入而不是真的清空。
            StockFrozen = true;
            return;
        }

        for (var i = 0; i < n; ++i)
            Rows[i].Stock = fresh[i];
        StockKnown = true;
        StockFrozen = false;
        if (total > 0)
            _everSawStock = true;
    }

    private void ReadIncoming(MJIManager* mji) {
        foreach (var r in Rows)
            r.Granary = r.Farm = r.Pasture = 0;

        // 🔴 GranariesState / FarmState / PastureHandler 三個都是**島上才存在的物件**
        //    (PastureHandler 甚至是 EventHandler 衍生型別,離島時很可能是懸空指標)。
        //    既有的 Farm/Pasture 視窗是 UIAttachedWindow,只有原生介面開著才讀,所以踩不到;
        //    這一頁隨時都畫得到,不能沿用同樣的假設。
        //    「這個指標離島後還有效嗎」離線證不了 ⇒ 不猜:不在島上就一律當作讀不到(畫 ?),
        //    而不是賭它是 null。切區中同理。
        OnIsland = mji->IsPlayerInSanctuary
            && !Service.Condition[ConditionFlag.BetweenAreas]
            && !Service.Condition[ConditionFlag.BetweenAreas51];
        if (!OnIsland) {
            GranaryKnown = FarmKnown = PastureKnown = false;
            return;
        }

        // --- 屯貨倉庫(遠征):精確到材料 ---
        var granaries = mji->GranariesState;
        GranaryKnown = IsPlausible(granaries);
        if (GranaryKnown) {
            var numGranaries = Math.Min(MJIGranariesState.MaxGranaries, granaries->Granary.Length);
            for (var gi = 0; gi < numGranaries; ++gi) {
                ref var g = ref granaries->Granary[gi];
                // 🔴 pouch 列號 0 是真材料(棕櫚葉),判有沒有東西一律看數量。
                if (g.RareResourceCount > 0)
                    Add(g.RareResourcePouchId, g.RareResourceCount, static (r, v) => r.Granary += v);
                var slots = Math.Min(g.NormalResourceCounts.Length, g.NormalResourcePouchIds.Length);
                for (var i = 0; i < slots; ++i) {
                    var count = g.NormalResourceCounts[i];
                    if (count > 0)
                        Add(g.NormalResourcePouchIds[i], count, static (r, v) => r.Granary += v);
                }
            }
        }

        // --- 農園:每格的 SeedType -> MJICropSeed -> 收成物 ---
        var farm = mji->FarmState;
        FarmKnown = IsPlausible(farm);
        if (FarmKnown) {
            var slots = Math.Min(farm->SeedType.Length, farm->GardenerYield.Length);
            for (var i = 0; i < slots; ++i) {
                var seed = farm->SeedType[i];
                var yield = farm->GardenerYield[i];
                if (seed == 0 || yield <= 0)
                    continue;
                var crop = Lumina.Excel.Sheets.MJICropSeed.GetRow(seed);
                if (crop == null)
                    continue;
                if (MaterialSources.TryGetPouchIdByItemId(crop.Value.Item.RowId, out var pouchId))
                    Add(pouchId, yield, static (r, v) => r.Farm += v);
            }
        }

        // --- 牧場:魔法人偶已收集但還沒領的產物(鍵是 Item 列號,不是收納袋列號) ---
        var pasture = mji->PastureHandler;
        PastureKnown = IsPlausible(pasture);
        if (PastureKnown) {
            foreach (var (itemId, count) in pasture->AvailableMammetLeavings) {
                if (count <= 0)
                    continue;
                if (MaterialSources.TryGetPouchIdByItemId(itemId, out var pouchId))
                    Add(pouchId, count, static (r, v) => r.Pasture += v);
            }
        }
    }

    private void Add(uint pouchId, int amount, Action<MaterialLedgerRow, int> apply) {
        if (pouchId < Rows.Length)
            apply(Rows[(int)pouchId], amount);
    }

    // 只是把「CS 的欄位偏移在台服對不上 -> 讀到一個很小的垃圾值」變成不做事,
    // 而不是拿去解參考。這不是防護,是把一整類靜默錯誤擋在門外。
    private static bool IsPlausible(void* p) => (nint)p > 0x10000;

    public bool GapKnown(MaterialLedgerRow row) => DemandKnown && StockKnown;
    public bool IncomingKnown => GranaryKnown && FarmKnown && PastureKnown;
    public int Gap(MaterialLedgerRow row, int horizon) => row.Demand[horizon] - row.Stock - row.Incoming;

    /// <summary>
    /// 目前有缺口的材料(MJIItemPouch 列號)。刻意填進呼叫端給的集合而不是回傳新集合 ——
    /// UI 每幀都會叫一次。需求或庫存任一未知時集合會是空的:不知道就不要宣稱有缺口。
    /// </summary>
    public void CollectShortages(int horizon, HashSet<uint> into) {
        into.Clear();
        if (horizon < 0 || horizon >= DemandEntryCount)
            return;
        foreach (var row in Rows)
            if (GapKnown(row) && Gap(row, horizon) > 0)
                into.Add(row.Info.PouchId);
    }

    /// <summary>
    /// 依「收納袋現有數量」由少到多排名,只看 <paramref name="eligible"/> 裡且已解鎖的材料,
    /// 取最少的前 <paramref name="topN"/> 名填進 <paramref name="ranks"/>(pouch 列號 -> 名次,0 = 最少)。
    ///
    /// 🔑 這是「使用者心智模型」的那把尺:他看的是開拓包上的絕對數量,
    ///    不是工坊排程算出來的缺口 —— 沒被排程吃到的材料(例如無人島鐵礦)在需求驅動的缺口裡是 0,
    ///    但在他眼裡那正是最缺的東西。實機回報就是這樣派錯地的。
    /// ⚠️ 刻意不扣在途:使用者看的就是收納袋上那個數字,扣掉就對不起來了。
    /// 🔴 庫存讀不到時回 false —— 呼叫端要畫 ?,不可以拿全 0 去排名(那會排出一份亂序)。
    /// </summary>
    public bool TryRankByStock(HashSet<uint> eligible, int topN, Dictionary<uint, int> ranks) {
        ranks.Clear();
        if (!StockKnown || topN <= 0)
            return false;

        List<(uint PouchId, int Stock)> pool = [];
        foreach (var row in Rows) {
            // 未解鎖的材料一律排除:它們的庫存必然是 0,會把前幾名整個佔滿而且毫無資訊。
            if (!row.Unlocked || row.Info.ItemId == 0 || !eligible.Contains(row.Info.PouchId))
                continue;
            pool.Add((row.Info.PouchId, row.Stock));
        }
        if (pool.Count == 0)
            return false;

        pool.Sort((a, b) => a.Stock != b.Stock ? a.Stock.CompareTo(b.Stock) : a.PouchId.CompareTo(b.PouchId));
        var n = Math.Min(topN, pool.Count);
        for (var i = 0; i < n; ++i)
            ranks[pool[i].PouchId] = i;
        return true;
    }

    public int StockOf(uint pouchId) => pouchId < Rows.Length ? Rows[(int)pouchId].Stock : 0;

    /// <summary>
    /// 這個材料「至少要留多少在收納袋裡」= 需求 - 在途。
    /// 🔴 需求或在途任一讀不到就回 false —— 呼叫端必須退回原本的行為,
    ///    不可以拿「不知道」當 0 去算:那會安靜地把保留量算成整個需求,結果是少賣。
    /// </summary>
    public bool TryGetReserve(uint pouchId, int horizon, out int reserve) {
        reserve = 0;
        if (!DemandKnown || !IncomingKnown || horizon < 0 || horizon >= DemandEntryCount)
            return false;
        if (pouchId >= Rows.Length)
            return false;
        reserve = Math.Max(0, Rows[(int)pouchId].Demand[horizon] - Rows[(int)pouchId].Incoming);
        return true;
    }
}
