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

    // 季號人工修正(以「週」為單位,在 100 季的循環裡位移)。
    // 0 = 完全沿用原本的日期算術。實機看 Schedule 分頁的對位診斷確認相位後才需要動它。
    public int SeasonOffset = 0;
}
