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

        // ⚠️ 新值一律接在最後,既有的值與預設值不要動。
        // 🔴 列舉名保持 CoverShortages 不變 —— 設定檔存的是字串,名字對不上＝這個節點讀檔中斷
        //    (Reassign 與檔中排在它後面的欄位回預設),下次存檔寫回。顯示文字與評分基準是「絕對庫存最低優先」
        //    (只看工坊缺口的話,沒被排程吃到的材料再少也不算「缺」)。
        [Description("Max out and select expeditions that top up your lowest stocks after workshop demand")]
        CoverShortages,
    }

    public CollectStrategy Collect = CollectStrategy.Manual;
    public UpdateStrategy Reassign = UpdateStrategy.Manual;
}
