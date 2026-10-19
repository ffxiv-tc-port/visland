using visland.Helpers;

namespace visland.Island;

/// <summary>一鍵巡島要走哪幾站。沒有任何「自動開始」的欄位：只有按下開始才會動。</summary>
public class IslandTourConfig : Configuration.Node {
    public bool VisitWorkshop = true;
    public bool VisitGranary = true;
    public bool VisitExport = false;
    public bool VisitPasture = true;
    public bool VisitFarm = true;
}
