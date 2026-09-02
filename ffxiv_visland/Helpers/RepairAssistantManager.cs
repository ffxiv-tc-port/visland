using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System;

namespace visland.Helpers;

internal unsafe class RepairAssistantManager {
    public static bool UseRepair() {
        // 🔴 通用動作 6 是 toggle:修理視窗開著時這一下等於「關窗」。登記成「正在關閉」,
        //    否則下面這條路徑擋不住:第 N 次嘗試逾時後窗還開著 → 第 N+1 次的第一步把它 toggle 關掉
        //    → RepairWindowOpen 對淡出中的窗仍回 true → 下一 tick 對關閉中的窗送 ButtonClick ⇒ 原生 AVE。
        //    (上一次的「按過」紀錄那時早就超過逃生口、冷掉了,只有這個關閉標記擋得住。)
        var repair = Service.GameGui.GetAddonByName("Repair").Address;
        if (repair != 0)
            AddonPressGuard.MarkClosing("Repair", repair);
        return ActionManager.Instance()->UseAction(ActionType.GeneralAction, 6);
    }

    internal static bool HasDarkMatterOrBetter(uint darkMatterID) => ItemRepairResource.Any(r => r.Item.RowId >= darkMatterID && InventoryManager.Instance()->GetInventoryItemCount(r.Item.RowId) > 0);

    internal static bool CanRepairAny(float repairPercent = 0) {
        var equipment = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        for (var i = 0; i < equipment->Size; i++) {
            var item = equipment->GetInventorySlot(i);
            if (item != null && item->ItemId > 0)
                if (CanRepairItem(item->ItemId) && item->Condition / 300 < (repairPercent > 0 ? repairPercent : 100))
                    return true;
        }
        return false;
    }

    internal static bool CanRepairItem(uint ItemId) {
        if (Item.GetRow(ItemId) is { ClassJobCategory.RowId: > 0, ClassJobRepair.RowId: > 0 } row) {
            var repairItem = row.ItemRepair.Value!.Item;

            if (!HasDarkMatterOrBetter(repairItem.RowId))
                return false;

            var jobLevel = Player.JobLevel(row.ClassJobRepair.RowId);
            if (Math.Max(row.LevelEquip - 10, 1) <= jobLevel)
                return true;
        }

        return false;
    }

    internal static bool RepairWindowOpen() => AddonUtils.TryGetAddonByName<AddonRepair>("Repair", out _);
    internal static bool ProcessRepair() {
        Service.TaskManager.Enqueue(UseRepair);
        Service.TaskManager.Enqueue(RepairWindowOpen);
        Service.TaskManager.Enqueue(() => {
            if (AddonUtils.TryGetAddonByName<AddonRepair>("Repair", out var repairAddon)) {
                // 🔴 ClickButton 是對按鈕自己的 vtable 合成 ButtonClick(比送 callback 更早踩到關閉中的窗),
                //    所以同樣要過守衛。被擋下回 false ＝「這一輪沒按到,下一 tick 再來」,
                //    與既有「視窗還沒出現」同一條路徑;逃生口 90 幀遠早於 TaskManager 的 20 秒逾時,不會死鎖。
                if (!AddonPressGuard.TryBeginPress("Repair", (nint)repairAddon, "RepairAll"))
                    return false;
                AddonUtils.ClickButton(repairAddon->RepairAllButton);
            }
            return true;
        });
        Service.TaskManager.Enqueue(() => !CanRepairAny());
        Service.TaskManager.Enqueue(UseRepair);
        return true;
    }
}
