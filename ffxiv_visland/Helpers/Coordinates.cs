using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Numerics;

namespace visland.Helpers;

internal class Coordinates {
    // MapMarker X/Y are "map pixel" coordinates on the 2048x2048 map image:
    //   pixel = (world + offset) * (SizeFactor / 100) + 1024
    // (same relation Dalamud's MapUtil.ConvertWorldCoordXZToMapCoord is derived from), so the
    // inverse gives us the marker's world-space position.
    public static float ConvertMapMarkerToWorldCoordinate(float pixel, ushort sizeFactor, short offset) {
        var scale = sizeFactor / 100f;
        return (pixel - 1024f) / scale - offset;
    }

    public static uint GetNearestAetheryte(uint zoneID, Vector3 pos) {
        var aetheryte = 0u;
        double distance = 0;
        foreach (var data in Aetheryte.Get()) {
            if (!data.IsAetheryte) continue;
            if (data.Territory.Value.RowId == zoneID) {
                var map = data.Territory.Value.Map.Value;
                var mapMarker = MapMarker.FirstOrNull(m => m.RowId == map.MapMarkerRange && m.DataType == 3 && m.DataKey.RowId == data.RowId);
                if (mapMarker == null) {
                    Service.Log.Error($"Cannot find aetherytes position for {zoneID}#{data.PlaceName.Value.Name}");
                    continue;
                }
                // Compare in world space: pos is a waypoint world position (hundreds of units),
                // while the old code converted markers to 1..42 map display coordinates - mixing
                // the two picked a wrong "nearest" aetheryte on multi-aetheryte maps.
                var aetherX = ConvertMapMarkerToWorldCoordinate(mapMarker.Value.X, map.SizeFactor, map.OffsetX);
                var aetherZ = ConvertMapMarkerToWorldCoordinate(mapMarker.Value.Y, map.SizeFactor, map.OffsetY);
                var temp_distance = Math.Pow(aetherX - pos.X, 2) + Math.Pow(aetherZ - pos.Z, 2);
                if (aetheryte == default || temp_distance < distance) {
                    distance = temp_distance;
                    aetheryte = data.RowId;
                }
            }
        }

        return aetheryte;
    }

    public static bool HasAetheryteInZone(uint TerritoryType) => Service.AetheryteList.Any(a => a.TerritoryId == TerritoryType);
}
