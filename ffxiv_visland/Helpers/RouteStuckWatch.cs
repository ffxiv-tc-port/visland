using System;
using System.Numerics;

namespace visland.Helpers;

/// <summary>
/// 路線往一個航點移動時的卡住判斷（標準 §2.4，形狀同 BMR AutoClear 的「6 秒內不足 1.5 米」）：
/// 沒有進展時依序回 重找路 → 跳躍 → 放棄；有進展（離錨點 ≥1.5 米，或離航點又近了 0.5 米）就全部歸零。
/// 純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。呼叫中斷超過 <see cref="GapResetMs"/>（讀圖、上坐騎、跳躍、讓位）就從目前位置重新起算。
/// </summary>
public sealed class RouteStuckWatch
{
    public enum Action
    {
        None,
        Repath,
        Jump,
        GiveUp,
    }

    public const float ProgressDistance        = 1.5f;
    public const float ApproachDistance        = 0.5f;
    public const long  NoProgressMs            = 6000;
    public const int   MaxSendsWithoutProgress = 5;
    public const long  GapResetMs              = 500;
    private const long MaxFrameMs              = 100;

    private Vector3 _anchor;
    private float   _bestDistance;
    private long    _noProgressMs;
    private int     _sends;
    private int     _level;
    private long    _lastCall;
    private bool    _started;

    /// <summary>目前升級到第幾步（0＝沒有卡住）。</summary>
    public int Level
        => _level;

    public void Reset()
    {
        _started = false;
        _level   = 0;
        _sends   = 0;
    }

    /// <summary>送出一次移動請求（PathfindAndMoveTo）。沒有進展卻一直重送（找不到路、終點不在網格上）也算卡住。</summary>
    public void NoteSend()
        => _sends++;

    /// <param name="counting">這一幀角色應該正被自己的移動推著走（vnavmesh 在走，或直線移動中）；算路、上坐騎的幀只更新位置不計時。</param>
    public Action Observe(Vector3 player, float distanceToWaypoint, bool counting, long now)
    {
        if (!_started || now - _lastCall > GapResetMs)
        {
            Rebase(player, distanceToWaypoint);
            _started  = true;
            _lastCall = now;
            return Action.None;
        }

        var delta = Math.Min(now - _lastCall, MaxFrameMs);
        _lastCall = now;
        if (Vector3.Distance(player, _anchor) >= ProgressDistance || distanceToWaypoint <= _bestDistance - ApproachDistance)
        {
            Rebase(player, distanceToWaypoint);
            _level = 0;
            return Action.None;
        }

        if (counting)
            _noProgressMs += delta;
        if (_noProgressMs < NoProgressMs && _sends < MaxSendsWithoutProgress)
            return Action.None;

        Rebase(player, distanceToWaypoint);
        _level++;
        return _level switch
        {
            1 => Action.Repath,
            2 => Action.Jump,
            _ => Action.GiveUp,
        };
    }

    private void Rebase(Vector3 player, float distanceToWaypoint)
    {
        _anchor       = player;
        _bestDistance = distanceToWaypoint;
        _noProgressMs = 0;
        _sends        = 0;
    }
}
