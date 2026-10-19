using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Threading;

namespace visland.Helpers;

/// <summary>「同一扇視窗的同一個按法,按過就不要再按,直到它真的收掉」的共用閘門。visland 對 addon 的所有按法(<see cref="AtkCallback.Fire(string, bool, string, int[])"/>、<c>AddonUtils.ClickButton</c>、直呼 <c>FireCallback</c>)都要先問過<see cref="TryBeginPress(string, nint, string)"/>。</summary>
/// <remarks>🔴 <b>存在的理由是原生 AccessViolation</b>:「按下即關」的窗被按下之後有「正在關閉中」的幾幀,這段期間 <c>AddonUtils.IsAddonReady</c> 三關仍全過;此時再送 callback／輸入事件就是攔不到的 AVE —— <b>唯一的防護是「不要送第二次」</b>。節流不是防護。
/// 🔑 <b>粒度＝(位址, 按法)</b>,位址<b>只做等值比較,永遠不解參考</b>;只有<b>回答一次即終結</b>的窗(<see cref="SingleAnswerAddons"/>)才把所有按法併成同一個 key。
/// 解除點:位址從同名清單消失(每幀輪詢)、同位址 <see cref="AddonEvent.PostSetup"/>(這一幀才登記的不清)、常駐窗連續隱藏、逃生口。
/// <see cref="AddonEvent.PreFinalize"/> 是<b>上鎖點</b>,鎖到位址消失或同位址重建。只在主執行緒使用;任何不確定一律不放行。</remarks>
internal static unsafe class AddonPressGuard {
    /// <summary>
    /// 單答終結窗(按下即關)的逃生口。遠大於關閉所需的幀數,只在「按了但窗沒收也沒重建」
    /// 這種異常狀況下放行補按,免得呼叫端卡到 <c>TaskManager</c> 的 20 秒逾時把整條佇列清掉。
    /// </summary>
    internal const int DefaultEscapeFrames = 90;

    /// <summary>
    /// 多次互動窗(按一次翻一頁／採一格,窗<b>不會</b>因為被按而消失)的逃生口。
    /// 走逃生口是<b>常態</b>,所以只寫 Debug、不洗版。關閉中的危險窗口 &lt; 10 幀,15 幀不落在裡面。
    /// </summary>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>「我們主動要求這扇窗關閉」的紀錄用的按法名稱(見 <see cref="MarkClosing"/>)。</summary>
    internal const string ClosePressKey = "Close";

    /// <summary>同名實例最多掃到第幾個(遊戲同名實例上限);掃到第一個空的就提早停。</summary>
    private const int MaxAddonIndex = 256;

    /// <summary>常駐視窗按下後,要連續觀察到「還在清單裡、但已隱藏」這麼多幀才解除;看到可見就歸零重數。</summary>
    private const int HiddenReleaseFrames = 20;

    /// <summary>超過這麼多幀沒再按過、也沒上鎖的位址紀錄,定期回收(早就過了逃生口,不改變任何判定)。</summary>
    private const int SweepMinAgeFrames = 3600;

    private const int SweepIntervalFrames = 60;

    /// <summary>
    /// 「回答一次即終結」的窗:按下去就開始關閉,所以<b>任何</b>按法對同一個位址都只准一次。
    /// ⚠️ 這是分類表不是按點清單 —— <c>SelectYesno</c> visland 目前沒有按它,
    /// 列在這裡是為了日後有人加按點時預設就是對的。
    /// </summary>
    private static readonly HashSet<string> SingleAnswerAddons = new(StringComparer.Ordinal) {
        "MaterializeDialog",    // 精製確認框:按 0 之後即關
        "ContextIconMenu",      // 無人島 HUD 的環狀選單:選一項或送 -1 之後即關
        "PurifyResult",         // 精選結果框:按 0 之後即關
        "SelectYesno",
        "SelectString",         // 巡島選小員選單:選一項之後即關
    };

    /// <summary>
    /// 多次互動窗:按一次做一件事、窗本身<b>不會</b>因此消失,所以逃生口用
    /// <see cref="RoutineRePressEscapeFrames"/> 而不是 <see cref="DefaultEscapeFrames"/>。
    /// </summary>
    private static readonly HashSet<string> RoutineAddons = new(StringComparer.Ordinal) {
        "MJIHud",       // 常駐 HUD,CompatModule 會每幀重發直到 CurrentMode 變 1
        "Materialize",  // 精製主視窗:按一次抽一顆魔晶石,窗留著
        "Gathering",    // 採集視窗:按一次採一格,窗留到採完才由遊戲自己收
        "Talk",         // 巡島推進小員的對話:按一次翻一句,窗通常留著
    };

    /// <summary>
    /// 常駐視窗:關閉只是隱藏,位址整場留在清單裡,PreFinalize/PostSetup 都不會再發生。
    /// 這些窗改由「按下後連續隱藏 <see cref="HiddenReleaseFrames"/> 幀」解除,而且送出前必須看得見(兩者互為反面)。
    /// ⚠️ 只收實機證實常駐的名字:把會被銷毀的窗誤收進來,等於在它拆除途中提早解除封鎖。
    /// </summary>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal) {
        "ContextIconMenu",
        "ContextMenu",
        "Talk",
    };

    /// <param name="Frame">按下時的守衛幀號。</param>
    /// <param name="EscapeFrames">登記當時的逃生口;判「這筆還熱著」用它。</param>
    private readonly record struct PressRecord(long Frame, int EscapeFrames);

    private sealed class AddressRecords(string addonName) {
        public string AddonName { get; set; } = addonName;
        public Dictionary<string, PressRecord> ByKey { get; } = new(StringComparer.Ordinal);
        public long LastFrame { get; set; }

        /// <summary>連續觀察到「還在清單裡、但已隱藏」的幀數;只有常駐窗會累加,看得見或重新按下就歸零。</summary>
        public int HiddenFrames { get; set; }

        /// <summary>登記時(或之後的輪詢)在同名清單裡找得到這個位址;只有它成立才准用「從清單消失」解除。</summary>
        public bool Listed { get; set; }

        /// <summary>看過 <see cref="AddonEvent.PreFinalize"/>:在位址消失或同位址重建之前,任何按法都不准,也沒有逃生口。</summary>
        public bool Finalizing { get; set; }
    }

    /// <summary>這一幀在同名清單裡對某個位址的觀察。零值必須是「不解除」的答案。</summary>
    private enum AddonPresence {
        /// <summary>查詢不成立(名稱空、位址 0、同名實例超過掃描上限)。零值。</summary>
        Unknown = 0,
        NotListed = 1,
        Visible = 2,
        Hidden = 3,
    }

    /// <summary>位址 → 這個實例被按過的按法。位址只當字典鍵,從不解參考。</summary>
    private static readonly Dictionary<nint, AddressRecords> Pressed = [];

    /// <summary>曾經守護過的窗名:這些名字的實例一進 <see cref="AddonEvent.PreFinalize"/> 就上鎖(即使還沒按過)。</summary>
    private static readonly HashSet<string> GuardedNames = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, long> LogNextFrame = new(StringComparer.Ordinal);

    // 可重用緩衝:沒有任何窗被記著時,每幀的成本就是一次整數比較,不配置任何東西。
    private static readonly List<nint> ReleaseBuf = [];
    private static readonly List<string> StaleKeysBuf = [];

    /// <summary>「這個窗名第一次走隱藏解除」只寫一行 Information。</summary>
    private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

    /// <summary>守衛自己的時鐘,遞增點在 <see cref="OnFrameworkUpdate"/> 的第一行、前面不可以有任何條件。</summary>
    /// <remarks>🔴 絕對不能用 <c>UiBuilder.FrameCount</c>:它在隱藏 UI(過場動畫、隱藏 UI 熱鍵、GPose)時完全不前進,逃生口永不到期。</remarks>
    private static long frameCount;

    /// <summary>🔴 用 <see cref="Interlocked.CompareExchange(ref int, int, int)"/> 而不是 <c>bool</c>:重複訂閱會讓時鐘一個 tick 前進 2,逃生口對半砍。</summary>
    private static int clockSubscribed;

    private static IAddonLifecycle.AddonEventDelegate? lifecycleHandler;

    /// <summary>
    /// 在 <c>Service.Init</c> 一拿到服務就叫一次,讓時鐘排在本外掛 <c>Framework.Update</c> 多播委派的<b>最前面</b>
    /// (同一個外掛的處理常式共用一個 try/catch,排前面的擲例外會讓後面整個 tick 不被呼叫)。
    /// </summary>
    internal static void Initialize() => EnsureClock();

    /// <summary>
    /// 問「現在可以對這扇窗送這個按法嗎」。可以就<b>就地登記</b>並回 <c>true</c>;
    /// 回 <c>false</c> 代表「這一輪沒按到,下一輪再來」—— 與既有「addon 還沒出現」走同一條路徑。
    /// </summary>
    /// <param name="addonName">視窗名稱(生命週期監聽與掃描都靠它)。</param>
    /// <param name="addon">視窗指標,<b>只取位址做等值比較</b>。</param>
    /// <param name="pressKey">按法名稱(參數組的代稱)。單答終結窗會忽略它、併成同一個 key。</param>
    internal static bool TryBeginPress(string addonName, AtkUnitBase* addon, string pressKey = "")
        => TryBeginPress(addonName, (nint)addon, pressKey);

    /// <inheritdoc cref="TryBeginPress(string, AtkUnitBase*, string)"/>
    internal static bool TryBeginPress(string addonName, nint address, string pressKey = "") {
        // 🔴 時鐘的後援訂閱點,放在所有 early return 之前(CompareExchange 保證只會生效一次)。
        EnsureClock();

        if (address == 0 || string.IsNullOrEmpty(addonName))
            return false;

        var singleAnswer = SingleAnswerAddons.Contains(addonName);
        if (singleAnswer && pressKey != ClosePressKey)
            pressKey = string.Empty;

        var escapeFrames = EscapeFramesFor(addonName);
        var routine = escapeFrames <= RoutineRePressEscapeFrames;
        var frame = frameCount;

        // 常駐窗的解除條件是「連續隱藏」,這裡是它的反面:隱藏中(或這一幀查不到)的常駐窗一律不送。
        if (PersistentAddons.Contains(addonName) && LookUp(addonName, address, readVisibility: true) != AddonPresence.Visible)
            return false;

        GuardedNames.Add(addonName);
        if (Pressed.TryGetValue(address, out var records)) {
            if (records.Finalizing) {
                LogFinalizingHold(addonName, address, pressKey);
                return false;
            }

            if (FindBlocking(records, pressKey, singleAnswer, frame, out var blockingKey)) {
                // 🔴 對關閉中的窗再按一次會崩潰的就是這一幀:擋下。
                LogHold(addonName, address, pressKey, blockingKey, routine);
                return false;
            }

            if (records.ByKey.TryGetValue(pressKey, out var same))
                LogEscape(addonName, address, pressKey, frame - same.Frame, routine);
        }
        else {
            records = new AddressRecords(addonName);
            Pressed[address] = records;
        }

        BindListed(records, addonName, address);
        records.ByKey[pressKey] = new PressRecord(frame, escapeFrames);
        records.LastFrame = frame;
        // 🔴 承重:隱藏解除是「從最後一次按下起算」連續隱藏幾幀;漏掉這一行,前一次累到一半的幀數會被沿用。
        records.HiddenFrames = 0;
        // 跨外掛重按診斷：只在真的送出按壓時記一行，刻意不節流。
        Service.Log.Verbose($"[按窗診斷] plugin=visland addon={addonName} addr=0x{address:X} key={pressKey}");
        return true;
    }

    /// <summary>
    /// 登記「我們剛剛主動要求這扇窗關閉」(例如用 toggle 型的通用動作把它關掉)。
    /// 之後對<b>同一個位址</b>的任何按法都會被擋到它真的消失,或撐過 <see cref="DefaultEscapeFrames"/> 為止。
    /// <para><c>Repair</c> 與 <c>Materialize</c> 都是靠通用動作 toggle 關閉的,窗不會因為「被按」而關 ——
    /// 沒有這一步的話,「上一輪把窗關掉、這一輪對淡出中的它再按一次」那條路徑上,按下紀錄早就冷掉了,擋不住。</para>
    /// </summary>
    internal static void MarkClosing(string addonName, nint address) {
        EnsureClock();

        if (address == 0 || string.IsNullOrEmpty(addonName))
            return;

        GuardedNames.Add(addonName);
        if (!Pressed.TryGetValue(address, out var records)) {
            records = new AddressRecords(addonName);
            Pressed[address] = records;
        }

        BindListed(records, addonName, address);
        records.ByKey[ClosePressKey] = new PressRecord(frameCount, DefaultEscapeFrames);
        records.LastFrame = frameCount;
        records.HiddenFrames = 0;
    }

    /// <summary>外掛卸載時拆乾淨(本 pin 其實會自動拆,但顯式拆掉才不會留下半死的狀態)。</summary>
    internal static void ForceTeardown() {
        if (Interlocked.Exchange(ref clockSubscribed, 0) == 1) {
            if (Service.Framework != null)
                Service.Framework.Update -= OnFrameworkUpdate;
            if (Service.AddonLifecycle != null && lifecycleHandler != null) {
                Service.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, lifecycleHandler);
                Service.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, lifecycleHandler);
            }
        }

        lifecycleHandler = null;
        Pressed.Clear();
        GuardedNames.Clear();
        LogNextFrame.Clear();
    }

    private static void EnsureClock() {
        if (Interlocked.CompareExchange(ref clockSubscribed, 1, 0) != 0)
            return;
        Service.Framework.Update += OnFrameworkUpdate;
        // 📌 從非框架執行緒註冊是安全的:服務端把實際的清單異動延到 RunOnTick。
        lifecycleHandler = OnAddonLifecycle;
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, lifecycleHandler);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, lifecycleHandler);
    }

    private static void OnFrameworkUpdate(IFramework framework) {
        frameCount++;   // 🔴 第一行。前面不可以有任何條件。
        PollRecords();
        if (frameCount % SweepIntervalFrames == 0)
            SweepColdRecords();
    }

    /// <summary>
    /// 每幀的解除路徑:①找得到過、現在從同名清單消失 ⇒ 解除(含 PreFinalize 鎖);
    /// ②常駐窗連續隱藏 <see cref="HiddenReleaseFrames"/> 幀 ⇒ 解除。
    /// </summary>
    /// <remarks>判準刻意不用「還可不可見」(常駐窗例外):拆除途中那幾幀三關全過。只有「找到了而且看不見」才累加隱藏幀。</remarks>
    private static void PollRecords() {
        if (Pressed.Count == 0)
            return;

        ReleaseBuf.Clear();
        foreach (var (address, records) in Pressed) {
            var persistent = PersistentAddons.Contains(records.AddonName);
            var presence = LookUp(records.AddonName, address, readVisibility: persistent);
            var present = presence != AddonPresence.NotListed;
            if (!present) {
                if (records.Listed)
                    ReleaseBuf.Add(address);
                continue;
            }

            if (presence != AddonPresence.Unknown)
                records.Listed = true;
            if (!persistent || records.Finalizing)
                continue;

            if (presence != AddonPresence.Hidden) {
                records.HiddenFrames = 0;
                continue;
            }

            if (++records.HiddenFrames < HiddenReleaseFrames)
                continue;

            ReleaseBuf.Add(address);
            ReportHiddenRelease(records.AddonName);
        }

        foreach (var address in ReleaseBuf)
            Pressed.Remove(address);
        ReleaseBuf.Clear();
    }

    private static void SweepColdRecords() {
        if (Pressed.Count == 0)
            return;

        ReleaseBuf.Clear();
        foreach (var (address, records) in Pressed) {
            if (records.Finalizing || frameCount - records.LastFrame < SweepMinAgeFrames)
                continue;
            ReleaseBuf.Add(address);
        }

        foreach (var address in ReleaseBuf)
            Pressed.Remove(address);
        ReleaseBuf.Clear();
    }

    private static int EscapeFramesFor(string addonName)
        => RoutineAddons.Contains(addonName) ? RoutineRePressEscapeFrames : DefaultEscapeFrames;

    private static void BindListed(AddressRecords records, string addonName, nint address) {
        if (records.Listed || LookUp(addonName, address, readVisibility: false) != AddonPresence.Visible)
            return;
        records.AddonName = addonName;
        records.Listed = true;
    }

    /// <summary>在同名的所有實例裡找 <paramref name="address"/>;查不到一律不當成「看得見」。</summary>
    /// <remarks>讀的是 <c>GetAddonByName</c> 這一幀交回來的實例,<paramref name="address"/> 本身只做等值比較。不讀可見性時找到一律回 Visible。</remarks>
    private static AddonPresence LookUp(string addonName, nint address, bool readVisibility) {
        if (string.IsNullOrEmpty(addonName) || address == 0)
            return AddonPresence.Unknown;

        for (var i = 1; i <= MaxAddonIndex; i++) {
            var unit = Service.GameGui.GetAddonByName(addonName, i);
            if (unit.IsNull)
                return AddonPresence.NotListed;
            if (unit.Address != address)
                continue;
            if (!readVisibility)
                return AddonPresence.Visible;
            return unit.IsVisible ? AddonPresence.Visible : AddonPresence.Hidden;
        }
        return AddonPresence.Unknown;
    }

    /// <summary>隱藏解除第一次接上時記一行(每個窗名每次遊戲一行),實機 log 靠它確認這條路真的走得到。</summary>
    private static void ReportHiddenRelease(string addonName) {
        if (!HiddenReleaseReported.Add(addonName))
            return;
        Service.Log.Information($"[AddonPressGuard] 「{addonName}」是常駐視窗（隱藏而不銷毀），按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個視窗名每次遊戲只寫一次。");
    }

    private static bool FindBlocking(AddressRecords records, string pressKey, bool singleAnswer, long frame, out string blockingKey) {
        foreach (var (key, rec) in records.ByKey) {
            var waited = frame - rec.Frame;
            if (waited >= rec.EscapeFrames)
                continue;   // 冷了:交給同 key 的逃生口去判要不要補按

            var sameKey = string.Equals(key, pressKey, StringComparison.Ordinal);
            var blocks = sameKey
                         || singleAnswer
                         // 「我們要求它關閉」與別的按法互擋,但同一幀內不互擋
                         // (「按完順手關掉」是正常流程,關閉的效果還沒發生)。
                         || ((key == ClosePressKey || pressKey == ClosePressKey) && rec.Frame != frame);
            if (!blocks)
                continue;

            blockingKey = key;
            return true;
        }

        blockingKey = string.Empty;
        return false;
    }

    private static void LogHold(string addonName, nint address, string pressKey, string blockingKey, bool routine) {
        if (!LogThrottle($"Hold-{addonName}", 60))
            return;

        var msg = $"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                  $"按過之後(紀錄「{blockingKey}」)還沒觀察到它收掉,這一幀不再碰它 —— " +
                  "對關閉中的視窗送 callback 是攔不到的存取違規。";
        if (routine)
            Service.Log.Debug(msg);
        else
            Service.Log.Information(msg);
    }

    private static void LogFinalizingHold(string addonName, nint address, string pressKey) {
        if (!LogThrottle($"Hold-{addonName}", 60))
            return;
        Service.Log.Information($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                                "已經看到它開始銷毀、還沒看到它消失,這一幀不再碰它 —— 對關閉中的視窗送 callback 是攔不到的存取違規。");
    }

    /// <summary>同一個按法對同一扇窗按過、已經冷掉而位址還在:放行補按。多次互動窗是常態寫 Debug,終結窗寫 Information。</summary>
    private static void LogEscape(string addonName, nint address, string pressKey, long waited, bool routine) {
        if (routine) {
            if (LogThrottle($"RoutineRelease-{addonName}", 600))
                Service.Log.Debug($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                                  $"按下後 {waited} 幀窗還在(多次互動窗的常態),放行下一次。");
        }
        else if (LogThrottle($"Release-{addonName}", 600)) {
            Service.Log.Information($"[AddonPressGuard] 「{addonName}」(實例 0x{address:X},按法「{pressKey}」)" +
                                    $"按下後 {waited} 幀既沒有被銷毀也沒有重新建立,判定為「上一次按下沒生效」" +
                                    "而不是「正在關閉」,解除封鎖讓呼叫端重試。");
        }
    }

    /// <summary>用守衛自己的幀時鐘做的節流,免得每幀路徑上的診斷洗版。</summary>
    private static bool LogThrottle(string key, int frames) {
        if (LogNextFrame.TryGetValue(key, out var next) && frameCount < next)
            return false;
        LogNextFrame[key] = frameCount + frames;
        return true;
    }

    /// <summary>該位址進入銷毀(上鎖)或重新開始(解除)生命週期。</summary>
    /// <remarks>
    /// <para>🔴 <b>按位址比對,不能按名稱整包清</b>:#A 進入關閉幀時同名的 #B 被建立,整包清掉的話下一幀對關閉中的 #A 查無紀錄而放行。</para>
    /// <para>🔴🔴 <see cref="AddonEvent.PostSetup"/> 只清「不是這一幀才登記的」紀錄:<c>PurificationManager.ResultsSetup</c> 是在 PostSetup 處理常式裡直接按下去,監聽器之間的順序不可依賴。</para>
    /// </remarks>
    private static void OnAddonLifecycle(AddonEvent type, AddonArgs args) {
        var address = (nint)args.Addon.Address;
        if (address == 0)
            return;

        if (type == AddonEvent.PreFinalize) {
            LockFinalizing(address, args);
            return;
        }

        if (!Pressed.TryGetValue(address, out var records))
            return;

        records.Finalizing = false;
        StaleKeysBuf.Clear();
        foreach (var (key, rec) in records.ByKey) {
            if (rec.Frame == frameCount)
                continue;   // 這一幀才登記的 ⇒ 是「模組剛在這次 PostSetup 派送裡按下」,不是新的一扇
            StaleKeysBuf.Add(key);
        }

        foreach (var key in StaleKeysBuf)
            records.ByKey.Remove(key);
        StaleKeysBuf.Clear();
        if (records.ByKey.Count == 0)
            Pressed.Remove(address);
    }

    private static void LockFinalizing(nint address, AddonArgs args) {
        if (Pressed.TryGetValue(address, out var records)) {
            records.Finalizing = true;
            return;
        }

        if (GuardedNames.Count == 0)
            return;

        var name = args.AddonName;
        if (!GuardedNames.Contains(name))
            return;

        Pressed[address] = new AddressRecords(name) { Listed = true, Finalizing = true, LastFrame = frameCount };
    }
}
