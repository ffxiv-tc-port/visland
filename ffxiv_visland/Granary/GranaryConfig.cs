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
        [Description("Max out and select expeditions covering the most materials you are short of")]
        CoverShortages,
    }

    public CollectStrategy Collect = CollectStrategy.Manual;
    public UpdateStrategy Reassign = UpdateStrategy.Manual;
}
