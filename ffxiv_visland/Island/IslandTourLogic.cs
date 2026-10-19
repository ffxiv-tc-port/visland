using System;
using System.Collections.Generic;

namespace visland.Island;

/// <summary>一鍵巡島的純邏輯：不碰遊戲、不碰 Dalamud，離線探針直接連結這一個檔來測。</summary>
/// <remarks>小員的身分只靠遊戲表：ENpcBase 的事件資料指向 CustomTalk，CustomTalk.Name 是 ASCII 腳本名（不隨語言變）。
/// 只比前綴、不比流水號；座標也只從 Level 表或物件表拿，不寫死。</remarks>
public static class IslandTourLogic {
    public enum StopKind {
        Workshop,
        Granary,
        Export,
        Pasture,
        Farm,
    }

    private static readonly (string Prefix, StopKind Kind)[] ScriptPrefixes = [
        ("CtsMjiWorkShopManagement_", StopKind.Workshop),
        ("CtsMjiStockyardManagement_", StopKind.Granary),
        ("CtsMjiFarmShop_", StopKind.Export),
        ("CtsMjiAnimalLand_", StopKind.Pasture),
        ("CtsMjiCropLand_", StopKind.Farm),
    ];

    public static StopKind? ClassifyScript(string? scriptName) {
        if (string.IsNullOrEmpty(scriptName))
            return null;
        foreach (var (prefix, kind) in ScriptPrefixes)
            if (scriptName.StartsWith(prefix, StringComparison.Ordinal))
                return kind;
        return null;
    }

    /// <param name="enpcData">ENpcBase.ENpcData 的列號（0 會被略過）。</param>
    /// <param name="customTalkName">CustomTalk 列號 → 腳本名；不是 CustomTalk 的列號回 null。</param>
    public static StopKind? ClassifyNpc(IEnumerable<uint> enpcData, Func<uint, string?> customTalkName) {
        foreach (var id in enpcData) {
            if (id == 0)
                continue;
            if (ClassifyScript(customTalkName(id)) is { } kind)
                return kind;
        }
        return null;
    }

    /// <summary>巡島順序固定：工坊 → 倉庫 → 出口 → 牧場 → 耕地（與使用者平常手動走的順序相同）。</summary>
    public static List<StopKind> BuildPlan(bool workshop, bool granary, bool export, bool pasture, bool farm) {
        List<StopKind> plan = [];
        if (workshop) plan.Add(StopKind.Workshop);
        if (granary) plan.Add(StopKind.Granary);
        if (export) plan.Add(StopKind.Export);
        if (pasture) plan.Add(StopKind.Pasture);
        if (farm) plan.Add(StopKind.Farm);
        return plan;
    }

    public readonly record struct LevelNpc(uint NpcId, float X, float Y, float Z);

    /// <summary>從「本區的 Level NPC 列」挑出每一站的表上位置。</summary>
    /// <remarks>同一站在表上出現兩個以上不同的 NPC 時不猜，該站列進 <paramref name="ambiguous"/>、不給位置。</remarks>
    public static Dictionary<StopKind, LevelNpc> PositionsFromLevel(IEnumerable<LevelNpc> levelNpcs, Func<uint, StopKind?> classify, out List<StopKind> ambiguous) {
        Dictionary<StopKind, LevelNpc> found = [];
        HashSet<StopKind> clash = [];
        foreach (var npc in levelNpcs) {
            if (classify(npc.NpcId) is not { } kind)
                continue;
            if (found.TryGetValue(kind, out var prev) && prev.NpcId != npc.NpcId)
                clash.Add(kind);
            else
                found[kind] = npc;
        }
        foreach (var kind in clash)
            found.Remove(kind);
        ambiguous = [.. clash];
        return found;
    }

    public enum MoveVerdict {
        Arrived,
        Moving,
        Failed,
    }

    /// <param name="navBusy">vnavmesh 正在算路或正在走。</param>
    /// <param name="graceMs">剛送出移動請求、vnavmesh 還沒開始算路的寬限。</param>
    public static MoveVerdict JudgeMove(bool inRange, bool navBusy, long msSinceIssue, long graceMs, long timeoutMs) {
        if (inRange)
            return MoveVerdict.Arrived;
        if (msSinceIssue > timeoutMs)
            return MoveVerdict.Failed;
        if (!navBusy && msSinceIssue > graceMs)
            return MoveVerdict.Failed;
        return MoveVerdict.Moving;
    }

    public enum WindowAction {
        AutoClose,
        HandOver,
    }

    /// <summary>視窗開好、visland 的自動化跑過之後，要自己關掉還是交給使用者。</summary>
    /// <remarks>工坊一律交給使用者：套用排程在 visland 也是要按按鈕的。
    /// 其餘只有「visland 的自動化開著而且已經沒東西可收」才自己關，免得把「會滿出來沒收」的警告一起關掉。</remarks>
    public static WindowAction DecideWindow(StopKind kind, bool automationOn, bool nothingLeft) => kind switch {
        StopKind.Workshop => WindowAction.HandOver,
        StopKind.Export => automationOn ? WindowAction.AutoClose : WindowAction.HandOver,
        _ => automationOn && nothingLeft ? WindowAction.AutoClose : WindowAction.HandOver,
    };

    public static string AddonName(StopKind kind) => kind switch {
        StopKind.Workshop => "MJICraftSchedule",
        StopKind.Granary => "MJIGatheringHouse",
        StopKind.Export => "MJIDisposeShop",
        StopKind.Pasture => "MJIAnimalManagement",
        StopKind.Farm => "MJIFarmManagement",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
