using visland.Helpers;

namespace visland.Export;

public class ExportConfig : Configuration.Node {
    public bool AutoSell = false;

    // 🔴 預設 false = 完全維持既有行為。既有使用者的 json 本來就沒有這個鍵,
    //    反序列化後會是欄位初始值 false,所以出貨後不會有人的賣出行為被動改變。
    //    要用得自己在 UI 勾。
    public bool RespectWorkshopNeeds = false;

    public int NormalLimit = 900;
    public int GranaryLimit = 900;
    public int FarmLimit = 900;
    public int PastureLimit = 900;
}
