using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using Lumina.Excel.Sheets;
using System;
using visland.Helpers;

namespace visland.Workshop;

// 季號對位診斷。
//
// 問題:WorkshopSeasonDB.SeasonForWeek 純粹用日期算術推季號(錨點 S203 @ 2026-07-07),
// 從來沒有跟遊戲實際的資料對照過。如果相位差了,現在載入的那 5 天就是**錯的季**——
// 照樣會有貝殼幣、只是比較少,所以壞掉的方式是靜默的。
//
// 為什麼不離線定錨:試過了,不行。封存排程對受歡迎度幾乎不敏感
// (2026-08-06 離線量測:每一季拿它自己的排程去試 100 個受歡迎度列,
//  最好的一列只比中位數高 14%,而且 best-row 減 season 的差值在 100 季裡散得跟隨機一樣)。
// 也就是說「用受歡迎度反推季號」這條路本身不成立,不是查詢寫壞了。
//
// 所以改成:把「算出來的季號」與「遊戲實際的受歡迎度列號」同時擺出來,
// 讓實機一次觀測就能定錨。使用者跑 LogLevel 2 → 一律寫 Information。
public static unsafe class WorkshopSeasonDiagnostics {
    public struct Snapshot {
        public int ThisSeason;
        public int NextSeason;
        public bool DemandKnown;        // false = 遊戲還沒抓需求資料,下面三個是未知不是 0
        public int CurrentPopularityRow;
        public int NextPopularityRow;
        public int PopularityRowCount;
        public int SeasonOffset;        // 設定裡的人工修正量

        // 下週期的受歡迎度列剛好是本週期 +1 → 「每週前進一列、100 週一輪」的模型成立。
        // 這一條光靠一次觀測就能判定,是整個對位問題裡最關鍵的一格。
        public readonly bool NextFollowsCurrent
            => DemandKnown && PopularityRowCount > 0
               && NextPopularityRow == (CurrentPopularityRow + 1) % PopularityRowCount;

        // (受歡迎度列 - 季號) mod 100。若模型成立,這個差值應該永遠是同一個常數;
        // 一旦知道它的正解,季號就永久定錨了。
        public readonly int ImpliedOffset
            => DemandKnown && PopularityRowCount > 0
               ? ((CurrentPopularityRow - ThisSeason) % PopularityRowCount + PopularityRowCount) % PopularityRowCount
               : -1;

        public readonly string CurrentPopularityText => DemandKnown ? CurrentPopularityRow.ToString() : "?";
        public readonly string NextPopularityText => DemandKnown ? NextPopularityRow.ToString() : "?";
    }

    private static string _lastLogged = "";
    private static int _popularityRowCount = -1; // Capture 每幀都會跑,失敗時不能每幀記一次 log

    public static Snapshot Capture(WorkshopSeasonDB db, int seasonOffset) {
        var snap = new Snapshot {
            ThisSeason = db.Shift(db.CurrentSeason(false), seasonOffset),
            NextSeason = db.Shift(db.CurrentSeason(true), seasonOffset),
            SeasonOffset = seasonOffset,
            CurrentPopularityRow = -1,
            NextPopularityRow = -1,
        };

        if (_popularityRowCount < 0) {
            try {
                _popularityRowCount = MJICraftworksPopularity.Get().Count;
            }
            catch (Exception ex) {
                _popularityRowCount = 0;
                Service.Log.Warning($"Could not read MJICraftworksPopularity: {ex.Message}");
            }
        }
        snap.PopularityRowCount = _popularityRowCount;

        var mji = MJIManager.Instance();
        if (mji != null && !mji->DemandDirty) {
            snap.DemandKnown = true;
            snap.CurrentPopularityRow = mji->CurrentPopularity;
            snap.NextPopularityRow = mji->NextPopularity;
        }
        return snap;
    }

    // 🔴 使用者跑 LogLevel 2,Debug/Verbose 收不到 → 這行必須是 Information。
    // 同一組數字只印一次,避免每次開窗都洗版(數字一變就會再印)。
    public static void Log(Snapshot s, WorkshopSeasonDB db) {
        var line =
            $"[season-align] computed this=S{s.ThisSeason} next=S{s.NextSeason} (offset={s.SeasonOffset}, " +
            $"anchor S{db.AnchorSeason}@{db.AnchorStart:yyyy-MM-dd}, archive {db.RangeStart}-{db.RangeEnd}/{db.CycleLength}) | " +
            $"in-game popularity row cur={s.CurrentPopularityText} next={s.NextPopularityText} of {s.PopularityRowCount} | " +
            $"next==cur+1: {(s.DemandKnown ? s.NextFollowsCurrent.ToString() : "?")} | " +
            $"implied (popRow-season) mod {s.PopularityRowCount} = {(s.ImpliedOffset < 0 ? "?" : s.ImpliedOffset.ToString())}";
        if (line == _lastLogged)
            return;
        _lastLogged = line;
        Service.Log.Information(line);
    }
}
