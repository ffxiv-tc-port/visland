using System.ComponentModel;
using visland.Helpers;

namespace visland.Granary;

public class GranaryConfig : Configuration.Node {
    public enum UpdateStrategy {
        [Description("Manual")]
        Manual,

        [Description("Max out, keep same destination")]
        MaxCurrent,

        [Description("Max out and select expedition bringing rare resources with two lowest counts")]
        BestDifferent,

        [Description("Max out and select expedition bringing rare resource with lowest count in both granaries")]
        BestSame,

        // ⚠️ 新值一律接在最後,而且既有三個值與預設值一個字都不要動。
        //    (實測使用者的 visland.json 是把列舉存成字串 "BestDifferent",所以順序其實不影響
        //     相容性 —— 但那是 Newtonsoft 的行為不是保證,接在最後兩種情況都安全。)
        // 🔴 列舉名保持 CoverShortages 不變 —— 設定檔存的是字串,改名會讓使用者已經選好的設定
        //    在下次載入時靜默退回 Manual。顯示文字與評分基準改成「絕對庫存最低優先」
        //    (實機回報:最缺鐵礦卻被派去沙灘,因為鐵礦沒被工坊排程吃到就不算「缺」)。
        [Description("Max out and select expeditions that top up your lowest island pouch stocks")]
        CoverShortages,
    }

    public CollectStrategy Collect = CollectStrategy.Manual;
    public UpdateStrategy Reassign = UpdateStrategy.Manual;
}
