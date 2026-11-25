namespace visland.Helpers;

/// <summary>
/// 「副本排到就暫停採集路線、出本回到同一張地圖再接續」的狀態機。純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// 契約：只有 <see cref="Decision.Pause"/> 會開始一次待接續；路線停了、被別人解除暫停、逾時、留在別張地圖、
/// 或呼叫端 <see cref="Clear"/>（停止、改跑別的路線、急停）都會作廢它，作廢後不會再接續。
/// 判斷失準的後果只有「沒暫停」或「沒接續」。
/// </summary>
public sealed class RouteDutyPause
{
    public enum Decision { None, Pause, Resume, Cancel }

    /// <summary>從暫停那一刻起算，超過就不接續（路線維持暫停）。</summary>
    public const long MaxPauseMs = 60 * 60_000;

    /// <summary>回到原地圖、可操作的狀態要連續維持這麼久才接續：確認框被取消後常在幾秒內重新排到。</summary>
    public const long ResumeSettleMs = 5_000;

    /// <summary>出本後先落在別張地圖（例：無人島會先回到碼頭再自動送回島上）不立刻作廢，停留超過這麼久才算回到別張地圖。</summary>
    public const long OtherMapGraceMs = 30_000;

    public struct Frame
    {
        public bool RouteRunning;
        public bool Paused;
        public bool PopShown;
        public bool InDuty;
        public bool BetweenAreas;
        public bool PlayerReady;
        public uint Territory;
        public long Now;
    }

    public bool Pending { get; private set; }
    public uint Territory { get; private set; }
    public long Since { get; private set; }
    /// <summary>最近一次 <see cref="Decision.Pause"/> 的原因，或最近一次 <see cref="Decision.Cancel"/> 的原因。</summary>
    public string Reason { get; private set; } = string.Empty;

    private bool _seeded;
    private bool _prevInDuty;
    private uint _lastOutsideTerritory;
    private long _settleSince;
    private long _otherMapSince;

    /// <summary>呼叫端自己停掉或換掉路線時呼叫；回 true＝原本有待接續、這次作廢了。</summary>
    public bool Clear(string reason)
    {
        if (!Pending)
            return false;
        Pending = false;
        Reason = reason;
        return true;
    }

    public Decision Observe(in Frame f, bool pauseEnabled, bool resumeEnabled)
    {
        // 第一幀只取樣：外掛在副本裡載入時不能把「已在副本中」當成剛進本。
        var enteredDuty = _seeded && f.InDuty && !_prevInDuty;
        _seeded = true;
        _prevInDuty = f.InDuty;
        if (!f.InDuty && !f.BetweenAreas && !f.PopShown)
            _lastOutsideTerritory = f.Territory;

        return Pending ? ObservePending(f, resumeEnabled) : ObserveRunning(f, pauseEnabled, enteredDuty);
    }

    // 只認「排到」與「剛進本」：在副本裡開始的路線（例：雲冠群島）不會被暫停。
    private Decision ObserveRunning(in Frame f, bool pauseEnabled, bool enteredDuty)
    {
        if (!pauseEnabled || !f.RouteRunning || f.Paused || !(f.PopShown || enteredDuty))
            return Decision.None;
        Pending = true;
        Territory = f.PopShown ? f.Territory : _lastOutsideTerritory;
        Since = f.Now;
        Reason = f.PopShown ? "副本排到" : "已在副本中";
        _settleSince = 0;
        _otherMapSince = 0;
        return Decision.Pause;
    }

    private Decision ObservePending(in Frame f, bool resumeEnabled)
    {
        if (!f.RouteRunning)
            return Cancel("路線已停止");
        if (!f.Paused)
            return Cancel("暫停被解除（使用者或別的插件）");
        if (f.Now - Since > MaxPauseMs)
            return Cancel($"暫停超過 {MaxPauseMs / 60_000} 分鐘");

        if (f.InDuty || f.PopShown || f.BetweenAreas || !f.PlayerReady) {
            _settleSince = 0;
            _otherMapSince = 0;
            return Decision.None;
        }

        if (f.Territory != Territory) {
            _settleSince = 0;
            if (_otherMapSince == 0)
                _otherMapSince = f.Now;
            return f.Now - _otherMapSince >= OtherMapGraceMs ? Cancel("回到的是別張地圖") : Decision.None;
        }

        _otherMapSince = 0;
        if (_settleSince == 0)
            _settleSince = f.Now;
        if (f.Now - _settleSince < ResumeSettleMs)
            return Decision.None;
        if (!resumeEnabled)
            return Cancel("沒開「回到原地圖後自動接續」");
        Pending = false;
        Reason = string.Empty;
        return Decision.Resume;
    }

    private Decision Cancel(string reason)
    {
        Pending = false;
        Reason = reason;
        return Decision.Cancel;
    }
}
