using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace visland.Helpers;

public static unsafe class AddonUtils {
    public static bool TryGetAddonByName<T>(string name, out T* addon) where T : unmanaged {
        addon = (T*)Service.GameGui.GetAddonByName(name).Address;
        return addon != null;
    }

    public static bool TryGetAddonByName(string name, out AtkUnitBase* addon) {
        addon = (AtkUnitBase*)Service.GameGui.GetAddonByName(name).Address;
        return addon != null;
    }

    public static bool IsOccupied()
        => Service.Condition[ConditionFlag.OccupiedInQuestEvent] ||
        Service.Condition[ConditionFlag.OccupiedInEvent] ||
        Service.Condition[ConditionFlag.OccupiedSummoningBell] ||
        Service.Condition[ConditionFlag.Occupied39] ||
        Service.Condition[ConditionFlag.Crafting] ||
        Service.Condition[ConditionFlag.PreparingToCraft] ||
        Service.Condition[ConditionFlag.ExecutingGatheringAction];

    public static bool IsAddonReady(AtkUnitBase* addon) => addon != null && addon->IsVisible && addon->IsReady;

    // TC/old-API-gen note: FFXIVClientStructs on this client doesn't have a RepairManager type; simulate a
    // real click on an AtkComponentButton by synthesizing a ButtonClick event through its own vtable instead.
    public static void ClickButton(AtkComponentButton* button) {
        if (button == null) return;
        var vtbl = (AtkComponentButton.AtkComponentButtonVirtualTable*)button->AtkComponentBase.VirtualTable;
        AtkEvent evt = default;
        AtkEventData data = default;
        vtbl->ReceiveEvent(button, AtkEventType.ButtonClick, 0, &evt, &data);
    }
}

public static unsafe class AtkCallback {
    /// <summary>
    /// 對指定名稱的視窗送 callback。
    /// <para>🔴 一律先問過 <see cref="AddonPressGuard"/>:對「正在關閉中」的視窗送第二發 callback
    /// 是攔不到的原生存取違規(AVE 在 .NET Core 是 corrupted-state exception,<c>try</c>/<c>catch</c> 無效)。
    /// 被擋下時回 <c>false</c>,語意與「視窗還沒出現」完全相同 —— 呼叫端下一輪再來,控制流不變。</para>
    /// </summary>
    /// <param name="pressKey">按法名稱(＝參數組的代稱)。同一扇窗的不同按法互不阻擋;
    /// 「回答一次即終結」的窗則由守衛把所有按法併成同一個 key。</param>
    /// <returns>真的送出去了才回 <c>true</c>。</returns>
    public static bool Fire(string addonName, bool checkVisibility, string pressKey, params int[] values)
        => AddonUtils.TryGetAddonByName<AtkUnitBase>(addonName, out var addon)
           && Fire(addonName, addon, checkVisibility, pressKey, values);

    /// <inheritdoc cref="Fire(string, bool, string, int[])"/>
    public static bool Fire(string addonName, AtkUnitBase* addon, bool checkVisibility, string pressKey, params int[] values) {
        if (addon == null)
            return false;
        if (checkVisibility && !addon->IsVisible)
            return false;
        // 🔴 位址只交給守衛做等值比較,守衛內部永遠不解參考它。
        if (!AddonPressGuard.TryBeginPress(addonName, (nint)addon, pressKey))
            return false;

        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; ++i) {
            atkValues[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            atkValues[i].Int = values[i];
        }

        addon->FireCallback((uint)values.Length, atkValues);
        return true;
    }
}
