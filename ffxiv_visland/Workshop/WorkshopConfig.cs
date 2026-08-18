using visland.Helpers;

namespace visland.Workshop;

// ⚠️ Configuration.Node.Deserialize 是用 GetField(名字) 逐鍵比對的 —— 欄位改名等於使用者的設定靜默消失。
// 新增欄位可以,既有欄位名不要動(前例:UseFavorSolver -> UseFavourSolver)。
// 新欄位的預設值一律沿用「加這個功能之前」的行為。
public class WorkshopConfig : Configuration.Node {
    public bool AutoOpenNextDay = false;
    public bool AutoImport = false;
    public bool UseFavourSolver = false;
    public FavourMode FavourMode = FavourMode.ReplaceWorkshop4;

    // 封存排程只有 5 個生產日(C2~C7 扣掉一天休息,C1 從來不在封存裡)。
    // 開啟後會用遊戲自己的受歡迎度/市場需求資料把剩下的兩天在本機解出來。
    // 預設關 = 維持既有行為。
    public bool FillEmptyDays = false;

    // 讓「貓耳小員的請求」也看得到補出來的生產日(C1 與被釋放的休息日),並且從最早的生產日開始放。
    // 🔴 前提是 FillEmptyDays 也開著 —— 封存本身最早只到 C2,沒有補天就沒有更早的日子可以放,
    //    而且請求整合必須跑在補天**之後**才看得到那些日子(既有順序剛好相反)。
    // 預設關 = 維持既有行為(C1 是純貝殼幣最佳化,請求只落在封存的那幾天)。
    public bool FavoursEarliestCycles = false;

    // 補空生產日時對「過剩材料」的偏好強度,單位是 %(0~50)。
    // 過剩 = 收納袋現有 − 工坊排程兩週需求,取正值(與屯貨倉庫派遣、缺料總表同一把尺)。
    // 🔴 預設 0 = 完全不偏好 = 加這個功能之前的行為。
    // ⚠️ 這是**使用者參數**,不是遊戲資料 —— 調高會用掉囤積的材料,但期望貝殼幣會下降。
    public int SurplusPreferencePercent = 0;

    // 季號人工修正(以「週」為單位,在 100 季的循環裡位移)。
    // 0 = 完全沿用原本的日期算術。實機看 Schedule 分頁的對位診斷確認相位後才需要動它。
    public int SeasonOffset = 0;
}
