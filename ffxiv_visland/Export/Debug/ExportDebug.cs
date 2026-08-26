using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using visland.Helpers;

namespace visland.Export;

public unsafe class ExportDebug {
    private readonly UITree _tree = new();

    public void Draw() {
        var agent = AgentMJIDisposeShop.Instance();
        // 第二參數是 UITree.Node 的 leaf（葉節點＝不可展開）。
        // 原本寫成 `agent == null && agent->Data != null`：agent 為 null 時 && 照樣要求值右邊，
        // 等於對 null 解參考 +0x28；agent 非 null 時又短路成 false，展開體反而在零守衛下跑。
        // 正解是 ||：只要 agent 或 Data 任一為 null 就畫成葉節點，
        // 「節點展得開」這件事本身就成為展開體的守衛（進得去代表兩層都非 null）。
        foreach (var n1 in _tree.Node($"Agent: {(nint)agent:X}", agent == null || agent->Data == null)) {
            // 進到這裡 agent 與 agent->Data 都保證非 null；收斂成一次解參考，
            // 順帶消掉同一次繪製裡對 +0x28 讀十幾次的 TOCTOU。
            var data = agent->Data;

            var opHandler = *(AgentInterface**)((nint)agent + 0x18); // it's really an atkeventlistener pointer, but atkeventlistener in CS doesn't define vfuncs...
            // opHandler 在 agent 還沒完全初始化時是 null，原本零檢查就解 VirtualTable(+0x00) 與 +0x10。
            if (opHandler != null)
                _tree.LeafNode($"OpHandler: {(nint)opHandler:X}, vtable=+{(nint)opHandler->AtkEventInterface.VirtualTable - Service.SigScanner.Module.BaseAddress:X}, obj={*(nint*)((nint)opHandler + 0x10):X}");
            else
                _tree.LeafNode("OpHandler: ?"); // 讀不到就畫 ?，不要畫成 0
            _tree.LeafNode($"Unk2C: {data->u2C}");
            _tree.LeafNode($"Init: stage={data->InitializationState}, data-init={data->DataInitialized}, dirty={data->AddonDirty}");
            _tree.LeafNode($"Currency 0: {data->CurrencyItemIds[0]} '{Item.GetRow(data->CurrencyItemIds[0])?.Name}': {data->CurrencyCounts[0]}/{data->CurrencyStackSizes[0]}");
            _tree.LeafNode($"Currency 1: {data->CurrencyItemIds[1]} '{Item.GetRow(data->CurrencyItemIds[1])?.Name}': {data->CurrencyCounts[1]}/{data->CurrencyStackSizes[1]}");

            // CategoryNames 是 StdVector，長度在讀完 excel（DataInitialized）之前是 0，
            // 而 CurSelectedCategory 是 byte（0..255）、NumCategories 是編譯期常數 4 ——
            // 兩個索引來源都跟這個 vector 的實際長度無關，索引前一律先驗。
            var catNames = data->CategoryNames.AsSpan();
            var curCat = data->CurSelectedCategory;
            _tree.LeafNode($"Cur category: {curCat} '{(curCat < catNames.Length ? catNames[curCat].ToString() : "?")}'");
            _tree.LeafNode($"Cur ship item: {data->CurShipItemIndex} qty={data->CurShipQuantity}");
            _tree.LeafNode($"Cur ship bulk: limit={data->CurBulkShiptLimit} stage={data->CurBulkShipCheckStage}");

            foreach (var n2 in _tree.Node($"All items ({data->Items.LongCount})")) {
                foreach (ref readonly var a in data->Items.AsSpan()) {
                    _tree.LeafNode($"{a.ItemIndex}: {a.ItemId} '{a.Name}', shop-row={a.ShopItemRowId}, count={a.CountInInventory}");
                }
            }

            for (var i = 0; i < AgentMJIDisposeShop.AgentData.NumCategories; ++i) {
                var catName = i < catNames.Length ? catNames[i].ToString() : "?";
                foreach (var n2 in _tree.Node($"Category {catName}: {data->PerCategoryItems[i].LongCount} items")) {
                    foreach (var item in data->PerCategoryItems[i].AsSpan()) {
                        // 這個 vector 裝的是 Pointer<ItemData>（指標的指標），元素本身可能是 null。
                        // 畫成一列 "?" 而不是整列吞掉：讓「有這筆但讀不到」在列上看得見。
                        if (item.Value == null) {
                            _tree.LeafNode("?: ? '?' count=?");
                            continue;
                        }
                        _tree.LeafNode($"{item.Value->ItemIndex}: {item.Value->ItemId} '{item.Value->Name}' count={item.Value->CountInInventory}");
                    }
                }
            }
        }
    }
}
