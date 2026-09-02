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
                // MJIHud 是常駐 HUD、不會因為被按而關,所以它的守衛逃生口是「多次互動」等級
                // (15 幀);原本這裡是每幀重發直到 CurrentMode 變 1,現在改成每 15 幀一次。
                AtkCallback.Fire("MJIHud", false, "OpenGatherModeMenu", 11, 0);
                // ContextIconMenu 選一項之後即關 ⇒ 守衛把它當單答終結窗,
                // 同一個位址上「選項」與下面那發「取消」互擋,免得對淡出中的選單再送一次。
                AtkCallback.Fire("ContextIconMenu", true, "SelectGatherMode", 0, 1, 82042, 0, 0);
            }

            if (Player.IsOnIsland)
                AtkCallback.Fire("ContextIconMenu", true, "CloseMenu", -1);
        }

        if (!PurificationManager.ListenersActive)
            PurificationManager.EnableListeners();

        OverrideAFK.ResetTimers();
    }

    public static void RestoreChanges() {
        PurificationManager.DisableListeners();
    }
}
