using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using System;
using System.Linq;

namespace visland.Gathering.AutoGather;

// TODO: remove entirely? I don't think anyone uses this and it's totally scope creep
public sealed class AutoGatherController : IDisposable {
    private static readonly string[] _addonNames = ["Gathering", "GatheringMasterpiece"];

    public AutoGatherController() {
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, _addonNames, OnAddonSetup);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, _addonNames, OnAddonFinalize);
    }

    public void Dispose() {
        Service.AddonLifecycle.UnregisterListener(OnAddonSetup);
        Service.AddonLifecycle.UnregisterListener(OnAddonFinalize);
    }

    // 🔴 這兩個事件現在只是「addon 開了／關了」的提示，用來決定要不要建立/清掉包裝物件；
    // **正確性不依賴它們**。GatheringAddon 的兩個包裝類已經改成每次使用時用 addon 名稱重查，
    // 所以就算 PreFinalize 沒送達（addon 開著時外掛 reload 等），留下來的也只是一個
    // 查不到 addon 的空殼——會走既有的「讀不到」三態路徑，而不是解參考懸空指標。
    // ⚠️ 刻意**不**傳 args.Addon 進去：包裝類不接受、也不保存任何跨幀的原生指標。
    private void OnAddonSetup(AddonEvent type, AddonArgs args) {
        var exec = Service.RouteExec;
        switch (args.AddonName) {
            case "Gathering":
                exec.GatheringAM = new GatheringAddon.Gathering();
                if (exec.CurrentRoute != null) {
                    Service.TaskManager.Enqueue(() => exec.GatheringAM.Items.Any(x => x.ItemID != 0));
                    Service.TaskManager.Enqueue(() => {
                        exec.GatheredItem = exec.GatheringAM!.Items.FirstOrDefault(x => x.ItemID != 0 && x.ItemID == (uint)exec.CurrentRoute!.TargetGatherItem);
                        return exec.GatheredItem != null;
                    });
                }
                break;
            case "GatheringMasterpiece":
                exec.GatheringCollectableAM = new GatheringAddon.GatheringMasterpiece();
                break;
        }
    }

    private void OnAddonFinalize(AddonEvent type, AddonArgs args) {
        switch (args.AddonName) {
            case "Gathering":
                Service.RouteExec.GatheringAM = null;
                Service.RouteExec.GatheredItem = null;
                break;
            case "GatheringMasterpiece":
                Service.RouteExec.GatheringCollectableAM = null;
                break;
        }
    }
}
