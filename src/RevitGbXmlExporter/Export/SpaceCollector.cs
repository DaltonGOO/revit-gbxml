using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using RevitGbXmlExporter.Models;
using RevitGbXmlExporter.Utilities;

namespace RevitGbXmlExporter.Export;

public class SpaceCollector
{
    private readonly Document _spatialDoc;
    private readonly ExportSettings _settings;
    private readonly ExportLog _log;
    private int _spaceCounter;
    private int _zoneCounter;

    // Synthetic levels: one per (Revit Level, half-foot height bucket).
    // Heights are rounded to 6 inches so small modeling variance doesn't fragment levels.
    private readonly Dictionary<(ElementId LevelId, int HalfFeetBucket), LevelData> _syntheticLevels = new();
    private readonly Dictionary<string, ZoneData> _zones = new();

    // Plan-AABB accumulator per level bucket — used to build each BuildingStorey's
    // <PlanarGeometry> outline from the XY extents of its rooms.
    private readonly Dictionary<string, PlanAabb> _levelPlanAabbs = new(StringComparer.Ordinal);

    private class PlanAabb
    {
        // All coordinates in Revit internal feet; converted at emit time.
        public double MinX = double.MaxValue;
        public double MaxX = double.MinValue;
        public double MinY = double.MaxValue;
        public double MaxY = double.MinValue;
        public double ZFeet;

        public bool IsValid => MaxX > MinX && MaxY > MinY;
        public void Expand(BoundingBoxXYZ bb)
        {
            if (bb.Min.X < MinX) MinX = bb.Min.X;
            if (bb.Max.X > MaxX) MaxX = bb.Max.X;
            if (bb.Min.Y < MinY) MinY = bb.Min.Y;
            if (bb.Max.Y > MaxY) MaxY = bb.Max.Y;
        }
    }

    public SpaceCollector(Document spatialDoc, ExportSettings settings, ExportLog log)
    {
        _spatialDoc = spatialDoc;
        _settings = settings;
        _log = log;
    }

    public (List<SpaceData> Spaces, List<LevelData> Levels, List<ZoneData> Zones) Collect()
    {
        var spaces = _settings.SpatialSource == SpatialElementSource.Rooms
            ? CollectRooms()
            : CollectMepSpaces();

        var levels = FinalizeLevels(spaces);

        _log.Info("Space",
            $"Collected {spaces.Count} {_settings.SpatialSource.ToString().ToLower()}, " +
            $"{levels.Count} level/height bucket(s), {_zones.Count} zones.");

        return (spaces, levels, _zones.Values.ToList());
    }

    /// <summary>
    /// Sorts synthetic levels by elevation then name, rewrites their IDs to a zero-padded
    /// sequence (level-001, level-002, ...) so HAP sees them in bottom-up order, patches
    /// each space's LevelId reference, and attaches a plan-AABB PolyLoop outline to each
    /// level so the BuildingStorey has &lt;PlanarGeometry&gt; for HAP to consume.
    /// </summary>
    private List<LevelData> FinalizeLevels(List<SpaceData> spaces)
    {
        var ordered = _syntheticLevels.Values
            .OrderBy(l => l.Elevation)
            .ThenBy(l => l.Name)
            .ToList();

        var idRewrite = new Dictionary<string, string>(ordered.Count, StringComparer.Ordinal);
        for (int i = 0; i < ordered.Count; i++)
        {
            string newId = $"level-{(i + 1):D3}";
            idRewrite[ordered[i].Id] = newId;
            ordered[i].Id = newId;
        }

        foreach (var s in spaces)
        {
            if (idRewrite.TryGetValue(s.LevelId, out var newId))
                s.LevelId = newId;
        }

        // Attach PlanarOutline (AABB polygon) to each level from the accumulated extents.
        // Dictionary keys are the pre-rewrite level IDs, so map via idRewrite.
        foreach (var kv in _levelPlanAabbs)
        {
            if (!idRewrite.TryGetValue(kv.Key, out var newId)) continue;
            var level = ordered.FirstOrDefault(l => l.Id == newId);
            if (level == null) continue;
            var aabb = kv.Value;
            if (!aabb.IsValid) continue;

            // PolyLoop vertices stay in Revit internal feet; GbXmlWriter converts at emit time,
            // consistent with how Surface.Vertices are handled.
            level.PlanarOutline =
            [
                new XYZ(aabb.MinX, aabb.MinY, aabb.ZFeet),
                new XYZ(aabb.MaxX, aabb.MinY, aabb.ZFeet),
                new XYZ(aabb.MaxX, aabb.MaxY, aabb.ZFeet),
                new XYZ(aabb.MinX, aabb.MaxY, aabb.ZFeet),
            ];
        }

        return ordered;
    }

    /// <summary>Accumulates a room's plan bounding box into its level's outline AABB.</summary>
    private void AccumulateLevelOutline(LevelData level, SpatialElement element)
    {
        BoundingBoxXYZ? bb = element.get_BoundingBox(null);
        if (bb == null) return;

        if (!_levelPlanAabbs.TryGetValue(level.Id, out var aabb))
        {
            aabb = new PlanAabb
            {
                ZFeet = element.Document.GetElement(element.LevelId) is Level lvl ? lvl.Elevation : 0
            };
            _levelPlanAabbs[level.Id] = aabb;
        }
        aabb.Expand(bb);
    }

    private List<SpaceData> CollectRooms()
    {
        var allRooms = new FilteredElementCollector(_spatialDoc)
            .OfClass(typeof(SpatialElement))
            .OfType<Room>()
            .ToList();

        foreach (var r in allRooms.Where(r => r.Area <= 0))
        {
            _log.Warn("Space",
                $"Room '{r.Name}' (id {r.Id.Value}) skipped: area is 0. " +
                "Room is unplaced or not enclosed by bounding elements.");
        }

        var rooms = allRooms.Where(r => r.Area > 0).ToList();
        return rooms.Select(CreateSpaceDataFromRoom).Where(s => s != null).ToList()!;
    }

    private List<SpaceData> CollectMepSpaces()
    {
        var allSpaces = new FilteredElementCollector(_spatialDoc)
            .OfClass(typeof(SpatialElement))
            .OfType<Space>()
            .ToList();

        foreach (var s in allSpaces.Where(s => s.Area <= 0))
        {
            _log.Warn("Space",
                $"MEP Space '{s.Name}' (id {s.Id.Value}) skipped: area is 0. " +
                "Space is unplaced or not enclosed.");
        }

        var spaces = allSpaces.Where(s => s.Area > 0).ToList();
        return spaces.Select(CreateSpaceDataFromMepSpace).Where(s => s != null).ToList()!;
    }

    private SpaceData? CreateSpaceDataFromRoom(Room room)
    {
        if (room.Area <= 0) return null;

        bool isPlenum = IsPlenumRoom(room);
        if (isPlenum && !_settings.ExportPlenumSpaces) return null;

        string id = $"space-{++_spaceCounter}";
        var levelData = GetOrCreateLevel(room.Level, ComputeRoomHeight(room));
        AccumulateLevelOutline(levelData, room);
        string zoneName = GetZoneName(room);
        string zoneId = GetOrCreateZone(zoneName);

        var locationPoint = room.Location as LocationPoint;

        return new SpaceData
        {
            Id = id,
            Name = BuildSpaceName(room),
            RevitElementId = room.Id,
            Area = UnitConverter.ConvertArea(room.Area, _settings.AreaUnit),
            Volume = UnitConverter.ConvertVolume(room.Volume, _settings.VolumeUnit),
            LevelId = levelData.Id,
            LevelName = levelData.Name,
            LevelElevation = levelData.Elevation,
            IsPlenum = isPlenum,
            ZoneId = zoneId,
            ZoneName = zoneName,
            Centroid = locationPoint?.Point,
            PeopleCount = _settings.ExportSpaceLoads ? GetParamDouble(room, BuiltInParameter.ROOM_NUMBER_OF_PEOPLE_PARAM) : null,
            LightingPowerPerArea = _settings.ExportSpaceLoads ? GetNamedParamDouble(room, "Lighting Load") : null,
            EquipmentPowerPerArea = _settings.ExportSpaceLoads ? GetNamedParamDouble(room, "Power Load", "Equipment Load") : null
        };
    }

    private SpaceData? CreateSpaceDataFromMepSpace(Space space)
    {
        if (space.Area <= 0) return null;

        bool isPlenum = space.SpaceType == SpaceType.kPlenum;
        if (isPlenum && !_settings.ExportPlenumSpaces) return null;

        string id = $"space-{++_spaceCounter}";
        var levelData = GetOrCreateLevel(space.Level, ComputeSpaceHeight(space));
        AccumulateLevelOutline(levelData, space);
        string zoneName = GetZoneNameFromSpace(space);
        string zoneId = GetOrCreateZone(zoneName);

        var locationPoint = space.Location as LocationPoint;

        return new SpaceData
        {
            Id = id,
            Name = BuildSpaceName(space),
            RevitElementId = space.Id,
            Area = UnitConverter.ConvertArea(space.Area, _settings.AreaUnit),
            Volume = UnitConverter.ConvertVolume(space.Volume, _settings.VolumeUnit),
            LevelId = levelData.Id,
            LevelName = levelData.Name,
            LevelElevation = levelData.Elevation,
            IsPlenum = isPlenum,
            ZoneId = zoneId,
            ZoneName = zoneName,
            Centroid = locationPoint?.Point,
            PeopleCount = _settings.ExportSpaceLoads ? GetParamDouble(space, BuiltInParameter.ROOM_NUMBER_OF_PEOPLE_PARAM) : null,
            LightingPowerPerArea = _settings.ExportSpaceLoads
                ? GetNamedParamDouble(space, "Lighting Load", "Lighting Load per area")
                : null,
            EquipmentPowerPerArea = _settings.ExportSpaceLoads
                ? GetNamedParamDouble(space, "Power Load", "Power Load per area", "Equipment Load")
                : null,
            InfiltrationFlowPerArea = _settings.ExportSpaceLoads
                ? GetNamedParamDouble(space, "Infiltration Airflow", "Infiltration Airflow per area")
                : null
        };
    }

    /// <summary>
    /// Returns a synthetic level for the (Revit Level, room height) pair, creating it lazily.
    /// Heights are rounded to the nearest 6 inches so trivial modeling variance (e.g.
    /// rooms computed at 10.04 vs 10.15 ft) doesn't fragment levels.
    /// Name format: "{Revit level name} - {ft}'" or "{Revit level name} - {ft}'-6\"".
    /// </summary>
    private LevelData GetOrCreateLevel(Level? revitLevel, double unboundedHeightFeet)
    {
        ElementId levelId = revitLevel?.Id ?? ElementId.InvalidElementId;

        // Round to nearest 6 inches (0.5 ft).
        int halfFeetBucket = (int)Math.Round(unboundedHeightFeet * 2.0);
        if (halfFeetBucket < 1) halfFeetBucket = 1;

        var key = (levelId, halfFeetBucket);
        if (_syntheticLevels.TryGetValue(key, out var existing))
            return existing;

        string baseName = revitLevel?.Name ?? "Unknown";
        string heightLabel = FormatHalfFeet(halfFeetBucket);
        double elevationFt = revitLevel?.Elevation ?? 0.0;

        // Temporary ID; FinalizeLevels rewrites to a sorted, padded sequence later.
        var data = new LevelData
        {
            Id = $"tmp-level-{_syntheticLevels.Count + 1}",
            Name = $"{baseName} - {heightLabel}",
            Elevation = UnitConverter.ConvertLength(elevationFt, _settings.LengthUnit)
        };
        _syntheticLevels[key] = data;
        return data;
    }

    private static string FormatHalfFeet(int halfFeetBucket)
    {
        int feet = halfFeetBucket / 2;
        bool hasHalf = halfFeetBucket % 2 != 0;
        return hasHalf ? $"{feet}'-6\"" : $"{feet}'";
    }

    /// <summary>
    /// Returns the effective floor-to-ceiling height in feet.
    /// Prefers Volume / Area (actual mean ceiling height from Revit's computed room solid)
    /// because Room.UnboundedHeight reflects the user-set "Upper Limit" parameter —
    /// modelers often crank it to the top of the building, producing bogus tall heights.
    /// </summary>
    private static double ComputeRoomHeight(Room room)
    {
        if (room.Volume > 0 && room.Area > 0)
            return room.Volume / room.Area;
        return room.UnboundedHeight;
    }

    private static double ComputeSpaceHeight(Space space)
    {
        if (space.Volume > 0 && space.Area > 0)
            return space.Volume / space.Area;
        return space.UnboundedHeight;
    }

    /// <summary>
    /// Returns "{Name} {Number}" using Revit's Name/Number parameters (which are blank
    /// when unset, unlike Element.Name which auto-fills with strings like "Unnamed Space (1)").
    /// Falls back to whichever piece is present, then to Element.Name.
    /// </summary>
    private static string BuildSpaceName(SpatialElement el)
    {
        string name = el.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()?.Trim() ?? "";
        string number = el.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString()?.Trim() ?? "";

        if (name.Length > 0 && number.Length > 0) return $"{name} {number}";
        if (name.Length > 0) return name;
        if (number.Length > 0) return number;
        return el.Name ?? "Unnamed";
    }

    private bool IsPlenumRoom(Room room)
    {
        string name = room.Name?.ToLower() ?? "";
        return name.Contains("plenum") || name.Contains("plnm");
    }

    private string GetZoneName(Room room)
    {
        var zoneParam = room.LookupParameter("Zone");
        if (zoneParam != null && !string.IsNullOrEmpty(zoneParam.AsString()))
            return zoneParam.AsString();

        return room.Level?.Name ?? "Default Zone";
    }

    private string GetZoneNameFromSpace(Space space)
    {
        var zone = space.Zone;
        if (zone != null && !string.IsNullOrEmpty(zone.Name))
            return zone.Name;

        return space.Level?.Name ?? "Default Zone";
    }

    private string GetOrCreateZone(string zoneName)
    {
        if (_zones.TryGetValue(zoneName, out var existing))
            return existing.Id;

        string id = $"zone-{++_zoneCounter}";
        _zones[zoneName] = new ZoneData { Id = id, Name = zoneName };
        return id;
    }

    private static double? GetParamDouble(Element element, BuiltInParameter param)
    {
        var p = element.get_Parameter(param);
        if (p == null || !p.HasValue) return null;
        return p.AsDouble();
    }

    private static double? GetNamedParamDouble(Element element, params string[] names)
    {
        foreach (string name in names)
        {
            var p = element.LookupParameter(name);
            if (p is { HasValue: true, StorageType: StorageType.Double })
            {
                double val = p.AsDouble();
                if (val > 0) return val;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the Revit ElementId-to-gbXML-space-Id mapping for use by other extractors.
    /// </summary>
    public Dictionary<ElementId, string> GetSpaceIdMap(List<SpaceData> spaces)
    {
        return spaces.ToDictionary(s => s.RevitElementId, s => s.Id);
    }
}
