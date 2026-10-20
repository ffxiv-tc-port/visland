using System.Collections.Generic;
using System.Numerics;

namespace visland.Helpers;

/// <summary>
/// 採集路線往一個航點的這一段：走 vnavmesh 還是直線、要不要上坐騎、算好的路徑停下之後怎麼接。
/// 純邏輯、不碰遊戲、不配置；只在 framework 執行緒用。長度一律是米，-1＝不知道。
/// </summary>
public static class RouteLegPlan
{
    public enum Mode
    {
        Straight,
        Navmesh,
        WaitForNavmesh,
    }

    public enum AfterStop
    {
        /// <summary>路徑被打斷（讓位、卡住重找路、別的插件停掉）：從目前位置重算。</summary>
        Resend,

        /// <summary>路徑走完了還沒到航點：再算一次多半一樣，剩下的直線走。</summary>
        Straight,
    }

    /// <summary>路徑終點離航點超過「半徑＋這個值」就算到不了。</summary>
    public const float EndSlack = 1f;

    /// <summary>路徑停下時角色離路徑終點不超過這個值＝路徑走完了。</summary>
    public const float PathEndNear = 2f;

    /// <summary>送出算路之後角色被移開超過這個值，算好的路徑就作廢重算。</summary>
    public const float StaleStart = 2f;

    /// <param name="waypointPathfind">航點自己勾了「尋路?」。</param>
    /// <param name="pathfindAll">全域「所有航點都用 vnavmesh 尋路」。</param>
    /// <param name="latchedStraight">這一段已經因為尋路失敗或路徑到不了而改直線。</param>
    public static Mode Choose(bool waypointPathfind, bool pathfindAll, bool navInstalled, bool navReady, bool latchedStraight)
    {
        if (!waypointPathfind && !pathfindAll || !navInstalled || latchedStraight)
            return Mode.Straight;
        if (navReady)
            return Mode.Navmesh;
        // 航點自己勾了尋路的照舊等網格（直線可能到不了）；只因全域開關才尋路的先直線走，網格好了再換。
        return waypointPathfind ? Mode.WaitForNavmesh : Mode.Straight;
    }

    /// <param name="threshold">≤ 0＝關閉，照航點的移動方式上坐騎。</param>
    public static bool SkipMount(float legLength, float threshold)
        => threshold > 0 && legLength >= 0 && legLength < threshold;

    /// <summary>從 <paramref name="start"/> 沿路徑走到終點、再從終點直線到航點的總長；路徑空回 -1。</summary>
    public static float LegLength(Vector3 start, IReadOnlyList<Vector3>? path, Vector3 waypoint)
    {
        if (path == null || path.Count == 0)
            return -1f;

        var length = Vector3.Distance(start, path[0]);
        for (var i = 1; i < path.Count; i++)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length + Vector3.Distance(path[^1], waypoint);
    }

    public static bool PathReaches(IReadOnlyList<Vector3>? path, Vector3 waypoint, float radius)
        => path is { Count: > 0 } && Vector3.Distance(path[^1], waypoint) <= radius + EndSlack;

    public static AfterStop AfterPathStopped(Vector3 player, Vector3 pathEnd)
        => Vector3.Distance(player, pathEnd) <= PathEndNear ? AfterStop.Straight : AfterStop.Resend;

    public static bool ResultStale(Vector3 requestedFrom, Vector3 player)
        => Vector3.Distance(requestedFrom, player) > StaleStart;
}
