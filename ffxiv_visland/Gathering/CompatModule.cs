using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using visland.Helpers;

namespace visland.Gathering;

internal class CompatModule {
    // visland 這條路線自己送過「開採集模式選單」、而那扇選單還沒收掉。只在這段期間替選單送「關閉」,
    // 使用者自己開的 ContextIconMenu 不碰。只在框架執行緒讀寫(EnsureCompatibility／RestoreChanges)。
    private static bool _ownsGatherModeMenu;
    private static bool _reportedUserMenu;

    public static unsafe void EnsureCompatibility(GatherRouteDB RouteDB) {
        if (!RouteDB.GatherModeOnStart)
            _ownsGatherModeMenu = false;
        else {
            // MJIManager 是 isPointer 的靜態位址,登入前/不在無人島時是 null。
            // 目前 Player.IsOnIsland 內部已含判空而靠短路擋住,但那是間接保證;
            // 這裡顯式判一次,免得日後動到 IsOnIsland 就靜默變成裸解參考。
            var mji = MJIManager.Instance();
            if (Player.IsOnIsland && mji != null && mji->CurrentMode != 1) {
                // MJIHud 是常駐 HUD、不會因為被按而關,所以它的守衛逃生口是「多次互動」等級:
                // 這裡每幀都會走到,實際送出是每 15 幀一次,直到 CurrentMode 變 1。
                if (AtkCallback.Fire("MJIHud", false, "OpenGatherModeMenu", 11, 0))
                    _ownsGatherModeMenu = true;
                // ContextIconMenu 選一項之後即關 ⇒ 守衛把它當單答終結窗,
                // 同一個位址上「選項」與下面那發「取消」互擋,免得對淡出中的選單再送一次。
                AtkCallback.Fire("ContextIconMenu", true, "SelectGatherMode", 0, 1, 82042, 0, 0);
            }

            if (Player.IsOnIsland) {
                var menuVisible = AddonUtils.TryGetAddonByName("ContextIconMenu", out var menu) && menu->IsVisible;
                // 模式已切好、選單也收了 ⇒ 這扇是自己開的那扇已經處理完;之後再出現的選單是使用者的。
                if (_ownsGatherModeMenu && mji != null && mji->CurrentMode == 1 && !menuVisible)
                    _ownsGatherModeMenu = false;

                if (_ownsGatherModeMenu)
                    AtkCallback.Fire("ContextIconMenu", true, "CloseMenu", -1);
                else if (menuVisible && !_reportedUserMenu) {
                    _reportedUserMenu = true;
                    Service.Log.Information("[CompatModule] 採集路線進行中看到不是 visland 開的 ContextIconMenu,不替你關閉(這一行每條路線只寫一次)。");
                }
            }
        }

        if (!PurificationManager.ListenersActive)
            PurificationManager.EnableListeners();

        OverrideAFK.ResetTimers();
    }

    public static void RestoreChanges() {
        _ownsGatherModeMenu = false;
        _reportedUserMenu = false;
        PurificationManager.DisableListeners();
    }
}
