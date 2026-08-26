using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using visland.Helpers;

namespace visland.Gathering;

internal class CompatModule {
    public static unsafe void EnsureCompatibility(GatherRouteDB RouteDB) {
        if (RouteDB.GatherModeOnStart) {
            // MJIManager 是 isPointer 的靜態位址,登入前/不在無人島時是 null。
            // 目前 Player.IsOnIsland 內部已含判空而靠短路擋住,但那是間接保證;
            // 這裡顯式判一次,免得日後動到 IsOnIsland 就靜默變成裸解參考。
            var mji = MJIManager.Instance();
            if (Player.IsOnIsland && mji != null && mji->CurrentMode != 1) {
                AtkCallback.Fire("MJIHud", false, 11, 0);
                AtkCallback.Fire("ContextIconMenu", true, 0, 1, 82042, 0, 0);
            }

            if (Player.IsOnIsland)
                AtkCallback.Fire("ContextIconMenu", true, -1);
        }

        if (!PurificationManager.ListenersActive)
            PurificationManager.EnableListeners();

        OverrideAFK.ResetTimers();
    }

    public static void RestoreChanges() {
        PurificationManager.DisableListeners();
    }
}
