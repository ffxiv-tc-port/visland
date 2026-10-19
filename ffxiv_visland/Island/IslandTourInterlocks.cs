using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Generic;
using System.Linq;

namespace visland.Island;

/// <summary>巡島期間請 YesAlready 與 TextAdvance 讓開，讓對話框與選單只有 visland 一個人按。</summary>
/// <remarks>🔴 兩個外掛同一幀各按一次同一扇原生視窗，第二次打在已釋放的視窗上＝攔不到的存取違規。
/// 所以「有裝而且載入中卻壓不住」就不開始巡島，不退而求其次。
/// 只在 framework 執行緒上呼叫（TextAdvance 的停止清單是跨外掛共用的裸 HashSet，其他持有者也只在 framework 執行緒上動它）。</remarks>
internal sealed class IslandTourInterlocks {
    private const string Owner = "visland";
    private const string TextAdvanceStopRequests = "TextAdvance.StopRequests";
    private const int LeaseMs = 60_000;
    private const long RenewEveryMs = 20_000;

    private readonly ICallGateSubscriber<string, int, Guid> _yaAcquire = Service.Interface.GetIpcSubscriber<string, int, Guid>("YesAlready.AcquireSuppressionFor");
    private readonly ICallGateSubscriber<Guid, int, bool> _yaRenew = Service.Interface.GetIpcSubscriber<Guid, int, bool>("YesAlready.RenewSuppressionFor");
    private readonly ICallGateSubscriber<Guid, bool> _yaRelease = Service.Interface.GetIpcSubscriber<Guid, bool>("YesAlready.ReleaseSuppression");

    private Guid _lease;
    private long _nextRenewMs;
    private bool _textAdvanceLocked;
    // 持有期間快取並保留一次登記：DataShare 在還有人登記時不移除條目，TextAdvance 重新載入拿到的仍是同一個集合。
    private HashSet<string>? _textAdvanceSet;
    private DateTime _textAdvanceAddedInFrame = DateTime.MaxValue;
    private bool _textAdvanceReAddLogged;

    public bool YesAlreadyHeld => _lease != Guid.Empty;
    public bool TextAdvanceHeld => _textAdvanceLocked;

    private static bool IsLoaded(string internalName)
        => Service.Interface.InstalledPlugins.Any(p => p.InternalName == internalName && p.IsLoaded);

    /// <returns>壓不住時回 false，<paramref name="failure"/> 是給使用者看的原因；已經拿到的會先放回去。</returns>
    public bool TryAcquire(out string failure) {
        failure = string.Empty;
        if (IsLoaded("YesAlready")) {
            try {
                _lease = _yaAcquire.InvokeFunc(Owner, LeaseMs);
            }
            catch (Exception ex) {
                Service.Log.Information($"[巡島] 向 YesAlready 要暫停租約失敗：{ex.GetType().Name}: {ex.Message}");
                _lease = Guid.Empty;
            }
            if (_lease == Guid.Empty) {
                failure = "YesAlready 載入中但暫停不了（可能是舊版），為了不和它同時按選單，巡島不開始。";
                return false;
            }
            _nextRenewMs = Environment.TickCount64 + RenewEveryMs;
        }

        if (IsLoaded("TextAdvance")) {
            if (!TryGetTextAdvanceStopRequests()) {
                Release();
                failure = "TextAdvance 載入中但暫停不了（找不到它的停止清單），為了不和它同時按對話框，巡島不開始。";
                return false;
            }
            TryAddToTextAdvance();
            _textAdvanceAddedInFrame = Service.Framework.LastUpdateUTC;
            _textAdvanceLocked = true;
            _textAdvanceReAddLogged = false;
        }
        return true;
    }

    /// <summary>visland 這一幀可以點 Talk：沒暫停 TextAdvance，或名字已經在它的名單上滿一整幀（它同一幀稍早可能已經按過）。</summary>
    public bool MayClickTalk => !_textAdvanceLocked || Service.Framework.LastUpdateUTC > _textAdvanceAddedInFrame;

    /// <summary>每幀叫；TextAdvance 的名單每幀補回（它重新載入會清空），YesAlready 租約每 20 秒續一次，續不到就重拿一把。</summary>
    public void Tick() {
        if (_textAdvanceLocked && TryAddToTextAdvance() && !_textAdvanceReAddLogged) {
            _textAdvanceReAddLogged = true;
            Service.Log.Information("[巡島] TextAdvance 的停止清單裡沒有 visland 了（它可能重新載入過），已補回。");
        }

        if (_lease == Guid.Empty || Environment.TickCount64 < _nextRenewMs)
            return;
        _nextRenewMs = Environment.TickCount64 + RenewEveryMs;
        try {
            if (_yaRenew.InvokeFunc(_lease, LeaseMs))
                return;
            _lease = _yaAcquire.InvokeFunc(Owner, LeaseMs);
            Service.Log.Information($"[巡島] YesAlready 暫停租約已失效，重新取得＝{_lease != Guid.Empty}");
        }
        catch (Exception ex) {
            Service.Log.Information($"[巡島] 續 YesAlready 暫停租約失敗：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>冪等；卸載與每一種停止路徑都要走到這裡，否則 TextAdvance 會一直停著。</summary>
    public void Release() {
        if (_lease != Guid.Empty) {
            var lease = _lease;
            _lease = Guid.Empty;
            try {
                _yaRelease.InvokeFunc(lease);
            }
            catch (Exception ex) {
                Service.Log.Information($"[巡島] 交回 YesAlready 暫停租約失敗（租約會自己逾時）：{ex.GetType().Name}: {ex.Message}");
            }
        }
        _textAdvanceLocked = false;
        _textAdvanceAddedInFrame = DateTime.MaxValue;
        if (_textAdvanceSet != null) {
            var set = _textAdvanceSet;
            _textAdvanceSet = null;
            try {
                set.Remove(Owner);
            }
            catch (Exception ex) {
                Service.Log.Information($"[巡島] 把 visland 從 TextAdvance 停止清單拿掉失敗：{ex.GetType().Name}: {ex.Message}");
            }
            try {
                Service.Interface.RelinquishData(TextAdvanceStopRequests);
            }
            catch (Exception ex) {
                Service.Log.Information($"[巡島] 歸還 TextAdvance 停止清單失敗：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>每次巡島只取一次：<c>TryGetData</c> 每次成功都多登記一個使用者、不去重。</summary>
    private bool TryGetTextAdvanceStopRequests() {
        if (_textAdvanceSet != null)
            return true;
        try {
            if (Service.Interface.TryGetData<HashSet<string>>(TextAdvanceStopRequests, out var set) && set != null)
                _textAdvanceSet = set;
        }
        catch (Exception ex) {
            Service.Log.Information($"[巡島] 讀取 TextAdvance 停止清單失敗：{ex.GetType().Name}: {ex.Message}");
        }
        return _textAdvanceSet != null;
    }

    /// <returns>這一次真的把名字加回去了（原本不在名單上）。</returns>
    private bool TryAddToTextAdvance() {
        if (_textAdvanceSet == null)
            return false;
        try {
            if (!_textAdvanceSet.Add(Owner))
                return false;
        }
        catch (InvalidOperationException) {
            return false;
        }
        _textAdvanceAddedInFrame = Service.Framework.LastUpdateUTC;
        return true;
    }
}
