using System;
using System.Numerics;

namespace visland.Helpers;

/// <summary>
/// 「導航停了、角色卻還在動」＝使用者在手動移動：讓位不重送，靜止後從目前位置接續（形狀同 BOCCHI 的 ManualMoveDetector，只留持續移動這一種判斷）。
/// 純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。速度＝水平位移除以約 <see cref="WindowMs"/> 的時間窗。
/// </summary>
public sealed class ManualMoveYield
{
    public enum Step
    {
        /// <summary>照常（可以送路徑、可以算卡住）。</summary>
        None,

        /// <summary>這一幀不要重送、也不要算卡住：正在確認是不是手動，或正在讓位。</summary>
        Hold,

        /// <summary>導航停著、角色持續移動滿 <see cref="ConfirmMs"/>：開始讓位（同時也是 Hold）。</summary>
        Started,

        /// <summary>讓位中角色靜止滿 <see cref="ResumeQuietMs"/>：從目前位置接續。</summary>
        Resumed,

        /// <summary>讓位滿 <see cref="MaxYieldMs"/>：這一趟之後不再讓位，回到原本的重送規則。</summary>
        Expired,
    }

    public const long  SampleIntervalMs = 100;
    public const long  WindowMs         = 300;
    public const float MoveSpeedMin     = 1.5f;
    public const float StillSpeedMax    = 1f;
    public const float TeleportSpeed    = 50f;
    public const long  ConfirmMs        = 1500;
    public const long  ResumeQuietMs    = 1500;
    public const long  MaxYieldMs       = 30_000;

    // 比這更舊的取樣（中間有一段沒呼叫）不拿來算速度。
    private const long StaleMs  = 1000;
    private const int  Capacity = 8;

    private readonly long[]  _times = new long[Capacity];
    private readonly float[] _xs    = new float[Capacity];
    private readonly float[] _zs    = new float[Capacity];
    private          int     _count;
    private          int     _head;
    private          long    _nextSampleAt;
    private          long    _movingSince;
    private          long    _stillSince;
    private          long    _yieldStartedAt;
    private          bool    _capped;
    private          bool    _lastNavigating;

    public bool Yielding
        => _yieldStartedAt != 0;

    /// <summary>最近一次取樣的水平速度（米／秒）；-1＝歷史不夠長、不知道。</summary>
    public float LastSpeed { get; private set; } = -1f;

    public void Reset()
    {
        _count          = 0;
        _head           = 0;
        _nextSampleAt   = 0;
        _movingSince    = 0;
        _stillSince     = 0;
        _yieldStartedAt = 0;
        _capped         = false;
        _lastNavigating = false;
        LastSpeed       = -1f;
    }

    /// <param name="navigating">vnavmesh 在走或還在算路。</param>
    /// <param name="busy">上下坐騎、跳躍、詠唱、讀圖這類不是手動移動造成的位移。</param>
    public Step Observe(Vector3 player, bool navigating, bool busy, long now)
    {
        // 導航剛停的那一幀一定要取樣：否則取樣間隔內回 None，呼叫端會先重送一次。
        if (now < _nextSampleAt && navigating == _lastNavigating)
            return Yielding || _movingSince != 0 ? Step.Hold : Step.None;

        _lastNavigating = navigating;
        _nextSampleAt   = now + SampleIntervalMs;
        var speed = Push(player, now);
        LastSpeed = speed;

        if (navigating)
        {
            _movingSince    = 0;
            _stillSince     = 0;
            _yieldStartedAt = 0;
            return Step.None;
        }

        if (busy)
        {
            _movingSince = 0;
            _stillSince  = 0;
            return Yielding ? Step.Hold : Step.None;
        }

        if (_capped)
            return Step.None;

        if (!Yielding)
        {
            if (speed < MoveSpeedMin)
            {
                _movingSince = 0;
                return Step.None;
            }

            _movingSince = _movingSince == 0 ? now : _movingSince;
            if (now - _movingSince < ConfirmMs)
                return Step.Hold;

            _yieldStartedAt = now;
            _movingSince    = 0;
            _stillSince     = 0;
            return Step.Started;
        }

        if (now - _yieldStartedAt >= MaxYieldMs)
        {
            _capped         = true;
            _yieldStartedAt = 0;
            _stillSince     = 0;
            return Step.Expired;
        }

        if (speed < 0 || speed >= StillSpeedMax)
        {
            _stillSince = 0;
            return Step.Hold;
        }

        _stillSince = _stillSince == 0 ? now : _stillSince;
        if (now - _stillSince < ResumeQuietMs)
            return Step.Hold;

        _yieldStartedAt = 0;
        _stillSince     = 0;
        return Step.Resumed;
    }

    // 記下這一筆，回傳時間窗以前最近那一筆到現在的水平速度；歷史不夠長回 -1。瞬移時清掉歷史、回 -1。
    private float Push(Vector3 player, long now)
    {
        var speed = -1f;
        for (var i = 0; i < _count; i++)
        {
            var index = (_head - 1 - i + Capacity) % Capacity;
            if (now - _times[index] < WindowMs)
                continue;
            if (now - _times[index] > StaleMs)
                break;

            var seconds = (now - _times[index]) / 1000f;
            var dx      = player.X - _xs[index];
            var dz      = player.Z - _zs[index];
            speed = MathF.Sqrt(dx * dx + dz * dz) / seconds;
            break;
        }

        if (speed > TeleportSpeed)
        {
            _count = 0;
            speed  = -1f;
        }

        _times[_head] = now;
        _xs[_head]    = player.X;
        _zs[_head]    = player.Z;
        _head         = (_head + 1) % Capacity;
        _count        = Math.Min(_count + 1, Capacity);
        return speed;
    }
}
