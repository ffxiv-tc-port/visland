namespace visland.Helpers;

/// <summary>
/// 自己送出的尋路路徑在短時間內一再被中途停掉（取消→重算→又被取消）的迴圈偵測，只給診斷用。
/// 純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。
/// </summary>
public sealed class PathInterruptLoop
{
    public const int  Threshold = 4;
    public const long WindowMs  = 5_000;
    public const long LogGapMs  = 30_000;

    private readonly long[] _stamps = new long[Threshold];
    private int _count;
    private long _lastLogAt = long.MinValue;

    public void Reset() => _count = 0;

    /// <summary>記一次中途被停；回 true＝這一次該印一行診斷（窗內累計達門檻，且距上次印過至少 <see cref="LogGapMs"/>）。</summary>
    public bool Note(long nowMs)
    {
        if (_count == Threshold)
        {
            for (var i = 1; i < Threshold; ++i)
                _stamps[i - 1] = _stamps[i];
            --_count;
        }
        _stamps[_count++] = nowMs;

        if (_count < Threshold || nowMs - _stamps[0] > WindowMs)
            return false;
        if (_lastLogAt != long.MinValue && nowMs - _lastLogAt < LogGapMs)
            return false;
        _lastLogAt = nowMs;
        return true;
    }
}
