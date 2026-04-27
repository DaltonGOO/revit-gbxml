using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using RevitGbXmlExporter.Models;
using RevitGbXmlExporter.Utilities;

namespace RevitGbXmlExporter.Export;

/// <summary>
/// Builds gbXML surfaces from room boundary segments rather than from
/// SpatialElementGeometryCalculator's raw 3D face output. For each Room/Space:
///   - N walls are extruded from the 2D boundary segments up to the room height
///   - 1 floor is the polygon at base elevation
///   - 1 ceiling is the polygon at top elevation
/// Arcs are tessellated into straight wall segments. Slanted walls and sloped
/// ceilings are flattened (with a warning). Every room produces a closed volume
/// by construction, so HAP can compute its level assignment reliably.
/// </summary>
public class SurfaceAnalyzer
{
    // _spatialDoc owns the rooms/spaces (and their boundary segments); _geomDoc owns
    // walls, floors, ceilings, and opening family instances. They differ in the hybrid
    // workflow (spaces from host, geometry from a linked arch model). _geomTransform
    // brings a point in _geomDoc's internal coords into _spatialDoc's frame, so the
    // openings we read from a link line up with surfaces built from host boundaries.
    private readonly Document _spatialDoc;
    private readonly Document _geomDoc;
    private readonly Transform _geomTransform;
    private readonly ExportSettings _settings;
    private readonly Dictionary<ElementId, string> _spaceIdMap;
    private readonly ExportLog _log;
    private int _surfaceCounter;
    private int _openingCounter;

    private readonly Dictionary<ElementId, List<FamilyInstance>> _hostedOpenings = new();

    // Tolerance for matching wall-segment endpoints (6 inches — walls have thickness).
    private const double WallEndpointTolerance = 0.5;

    // Max vertical gap between a floor and a ceiling that should pair directly (slab).
    // Wider gaps are interpreted as a plenum — the orphans get bridged by a synthetic
    // plenum space rather than collapsed into one InteriorFloor surface.
    private const double MaxSlabGap = 3.0;

    // Plenum synthesis bounds. Below MinPlenumGap the gap is treated as modeling noise
    // (no plenum). Above MaxPlenumGap it's likely an unrelated open space, not a plenum.
    private const double MinPlenumGap = 0.5;
    private const double MaxPlenumGap = 20.0;

    private int _plenumCounter;

    public SurfaceAnalyzer(Document spatialDoc, Document geomDoc, Transform geomTransform,
        ExportSettings settings, Dictionary<ElementId, string> spaceIdMap, ExportLog log)
    {
        _spatialDoc = spatialDoc;
        _geomDoc = geomDoc;
        _geomTransform = geomTransform;
        _settings = settings;
        _spaceIdMap = spaceIdMap;
        _log = log;
        CacheHostedOpenings();
    }

    public (List<SurfaceData> Surfaces, List<SpaceData> Plenums) Analyze(List<SpaceData> spaces)
    {
        // Phase 1: build a simple extrusion per room from boundary segments.
        var extrusions = new List<RoomExtrusion>();
        foreach (var space in spaces)
        {
            var e = BuildRoomExtrusion(space);
            if (e == null) continue;
            extrusions.Add(e);
            // Hand the room footprint to the writer so each <Space> gets a <PlanarGeometry>.
            space.FloorOutline = e.FloorOutline;
        }
        _log.Info("Surface", $"Built extrusions for {extrusions.Count}/{spaces.Count} spaces.");

        // Phase 2: emit raw candidate surfaces (walls, floor, ceiling) per room.
        var wallCandidates = new List<SurfaceData>();
        var floorCandidates = new List<SurfaceData>();
        var ceilingCandidates = new List<SurfaceData>();

        foreach (var e in extrusions)
        {
            wallCandidates.AddRange(BuildWalls(e));
            floorCandidates.Add(BuildFloor(e));
            ceilingCandidates.Add(BuildCeiling(e));
        }

        // Phase 3: pair walls (same host wall + overlapping curve = interior wall).
        var (pairedWalls, unpairedWalls) = PairWalls(wallCandidates);
        _log.Info("Surface",
            $"Walls: {pairedWalls.Count} interior pair(s), {unpairedWalls.Count} exterior.");

        // Phase 4: pair floor + ceiling across the slab/plenum.
        var (pairedFloors, orphanFloors, orphanCeilings) =
            PairFloorsAndCeilings(floorCandidates, ceilingCandidates);
        _log.Info("Surface",
            $"Floor/ceiling: {pairedFloors.Count} interior pair(s), " +
            $"{orphanFloors.Count} orphan floor(s), {orphanCeilings.Count} orphan ceiling(s).");

        // Phase 4b: synthesize plenum spaces between orphan ceiling/floor pairs.
        var plenums = _settings.ExportPlenumSpaces
            ? GeneratePlenumSpaces(spaces, orphanFloors, orphanCeilings)
            : [];
        if (plenums.Count > 0)
            _log.Info("Plenum", $"Synthesized {plenums.Count} plenum space(s) bridging orphan floor/ceiling gaps.");

        var allSurfaces = new List<SurfaceData>();
        allSurfaces.AddRange(pairedWalls);
        allSurfaces.AddRange(unpairedWalls);
        allSurfaces.AddRange(pairedFloors);
        allSurfaces.AddRange(orphanFloors);
        allSurfaces.AddRange(orphanCeilings);

        // Phase 5: classify + compute rectangular geometry.
        // Orphans wired to a plenum in Phase 4b now classify as Ceiling/InteriorFloor.
        foreach (var s in allSurfaces)
        {
            ClassifySurfaceType(s);
            ComputeRectangularGeometry(s);
        }

        // Phase 6: attach openings to their host walls.
        AttachOpenings(allSurfaces);

        var filtered = allSurfaces
            .Where(s => _settings.ShouldExportSurfaceType(s.SurfaceType))
            .ToList();

        WarnAboutBareSpaces(spaces, filtered);

        _log.Info("Surface",
            $"Exporting {filtered.Count} surfaces ({allSurfaces.Count - filtered.Count} filtered by settings), " +
            $"{filtered.Sum(s => s.Openings.Count)} openings.");
        return (filtered, plenums);
    }

    /// <summary>
    /// For each orphan ceiling, find the closest orphan floor above it that overlaps in
    /// plan with a meaningful gap. Synthesize a plenum SpaceData between them and wire
    /// both surfaces' SecondAdjacentSpaceId to the plenum (which lets ClassifySurfaceType
    /// promote them from Roof/ExposedFloor to Ceiling/InteriorFloor in Phase 5).
    /// </summary>
    private List<SpaceData> GeneratePlenumSpaces(
        List<SpaceData> existingSpaces,
        List<SurfaceData> orphanFloors,
        List<SurfaceData> orphanCeilings)
    {
        var spacesById = existingSpaces.ToDictionary(s => s.Id, s => s);
        var plenums = new List<SpaceData>();
        var usedFloors = new HashSet<string>();

        foreach (var ceiling in orphanCeilings.OrderBy(c => c.Vertices[0].Z))
        {
            double ceilingZ = ceiling.Vertices[0].Z;

            SurfaceData? bestFloor = null;
            double bestGap = double.MaxValue;
            foreach (var floor in orphanFloors)
            {
                if (usedFloors.Contains(floor.Id)) continue;
                double floorZ = floor.Vertices[0].Z;
                double gap = floorZ - ceilingZ;
                if (gap < MinPlenumGap || gap > MaxPlenumGap) continue;
                if (!AabbOverlap(floor.Vertices, ceiling.Vertices)) continue;
                if (gap < bestGap) { bestGap = gap; bestFloor = floor; }
            }
            if (bestFloor == null) continue;

            if (!spacesById.TryGetValue(ceiling.FirstAdjacentSpaceId, out var belowSpace))
                continue;

            _plenumCounter++;
            // Use the ceiling polygon (CCW from above, +Z normal) as the plenum's footprint.
            var floorOutline = ceiling.Vertices
                .Select(v => new XYZ(v.X, v.Y, ceilingZ))
                .ToList();
            double areaFt2 = GeometryUtils.ComputePolygonArea(ceiling.Vertices);
            double volumeFt3 = areaFt2 * bestGap;

            var plenum = new SpaceData
            {
                Id = $"space-plenum-{_plenumCounter}",
                Name = $"Plenum {_plenumCounter} ({belowSpace.Name})",
                RevitElementId = ElementId.InvalidElementId,
                Area = UnitConverter.ConvertArea(areaFt2, _settings.AreaUnit),
                Volume = UnitConverter.ConvertVolume(volumeFt3, _settings.VolumeUnit),
                LevelId = belowSpace.LevelId,
                LevelName = belowSpace.LevelName,
                LevelElevation = belowSpace.LevelElevation,
                IsPlenum = true,
                ZoneId = belowSpace.ZoneId,
                ZoneName = belowSpace.ZoneName,
                FloorOutline = floorOutline
            };
            plenums.Add(plenum);

            // Both orphans now bound the plenum on the side their normal points toward.
            ceiling.SecondAdjacentSpaceId = plenum.Id;
            bestFloor.SecondAdjacentSpaceId = plenum.Id;
            usedFloors.Add(bestFloor.Id);
        }

        return plenums;
    }

    #region Room Extrusion

    private class RoomExtrusion
    {
        public string SpaceId = "";
        public ElementId RoomId = ElementId.InvalidElementId;
        public double BaseZ;
        public double TopZ;
        public List<WallSegment> Walls = new();
        public List<XYZ> FloorOutline = new(); // 2D outline at baseZ, CCW when viewed from above
    }

    private class WallSegment
    {
        public string SpaceId = "";
        public ElementId HostWallId = ElementId.InvalidElementId;
        public XYZ BaseStart = XYZ.Zero;
        public XYZ BaseEnd = XYZ.Zero;
        public double BaseZ;
        public double TopZ;
    }

    private RoomExtrusion? BuildRoomExtrusion(SpaceData space)
    {
        if (_spatialDoc.GetElement(space.RevitElementId) is not SpatialElement el)
        {
            _log.Warn("Space", $"Space {space.Id} ({space.Name}): Revit element missing.");
            return null;
        }

        double baseZ = GetRoomBaseZ(el);
        double height = ComputeRoomHeight(el);
        if (height < 1.0)
        {
            _log.Warn("Space",
                $"Space {space.Id} ({space.Name}): height {height:F2} ft too small; skipped.");
            return null;
        }
        double topZ = baseZ + height;

        // Sloped-ceiling detection: if UnboundedHeight (max) and Volume/Area (avg)
        // disagree by more than 10%, the ceiling is probably non-flat.
        WarnIfSlopedCeiling(el, height, space.Name);

        IList<IList<BoundarySegment>> loops;
        try
        {
            // IMPORTANT: use Center (wall centerline) not Finish (interior finish face).
            // With Finish, the two rooms bounding a wall get segments on OPPOSITE sides of
            // the wall (separated by wall thickness), so the segments never match during
            // pairing. Center puts both rooms' segments on the same line — trivial matching.
            var opts = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Center
            };
            loops = el.GetBoundarySegments(opts);
        }
        catch (Exception ex)
        {
            _log.Error("Space",
                $"Space {space.Id} ({space.Name}): GetBoundarySegments failed — {ex.Message}");
            return null;
        }

        if (loops.Count == 0)
        {
            _log.Warn("Space", $"Space {space.Id} ({space.Name}): no boundary segments.");
            return null;
        }

        // Outer loop only (skip inner loops = courtyards; rare).
        var outer = loops[0];

        var extr = new RoomExtrusion
        {
            SpaceId = space.Id,
            RoomId = el.Id,
            BaseZ = baseZ,
            TopZ = topZ
        };

        bool slantWarned = false;
        var slantedHosts = new HashSet<ElementId>();

        foreach (var seg in outer)
        {
            Curve curve;
            try { curve = seg.GetCurve(); }
            catch
            {
                _log.Warn("Surface",
                    $"Space {space.Name}: boundary segment returned no curve, skipped.");
                continue;
            }

            // Boundary segments referencing a linked element expose the linked
            // element's id via LinkElementId; seg.ElementId in that case is the link
            // instance (useless for grouping/lookup). Prefer the linked id and look up
            // in _geomDoc — which is the linked doc in hybrid mode.
            ElementId hostWallId = seg.LinkElementId != ElementId.InvalidElementId
                ? seg.LinkElementId
                : seg.ElementId;
            Element? hostElement = hostWallId != ElementId.InvalidElementId
                ? _geomDoc.GetElement(hostWallId)
                : null;

            // Warn once per slanted wall that we see on this room.
            if (hostElement is Wall wall && IsSlantedWall(wall) && slantedHosts.Add(hostWallId))
            {
                _log.Warn("Surface",
                    $"Space {space.Name}: wall {hostWallId.Value} is slanted — flattened to vertical.");
                slantWarned = true;
            }

            // Floor outline vertex: start of this segment (end connects to next segment's start).
            XYZ segStart = curve.GetEndPoint(0);
            extr.FloorOutline.Add(new XYZ(segStart.X, segStart.Y, baseZ));

            // Tessellate arcs into straight wall segments; keep straight lines as-is.
            if (curve is Arc)
            {
                double length = curve.Length;
                int n = Math.Max(4, (int)Math.Ceiling(length / GeometryUtils.ArcSegmentLength));
                for (int i = 0; i < n; i++)
                {
                    XYZ p0 = curve.Evaluate((double)i / n, true);
                    XYZ p1 = curve.Evaluate((double)(i + 1) / n, true);
                    AddWallSegment(extr, hostWallId, p0, p1, baseZ, topZ);

                    // Intermediate arc vertex also goes in the floor outline so the floor
                    // polygon follows the arc, not a chord.
                    if (i > 0) extr.FloorOutline.Add(new XYZ(p0.X, p0.Y, baseZ));
                }
            }
            else
            {
                XYZ p0 = curve.GetEndPoint(0);
                XYZ p1 = curve.GetEndPoint(1);
                AddWallSegment(extr, hostWallId, p0, p1, baseZ, topZ);
            }
        }

        if (extr.Walls.Count < 3)
        {
            _log.Warn("Space",
                $"Space {space.Id} ({space.Name}): only {extr.Walls.Count} wall segment(s), skipped.");
            return null;
        }

        return extr;
    }

    private static void AddWallSegment(RoomExtrusion extr, ElementId hostWallId,
        XYZ start, XYZ end, double baseZ, double topZ)
    {
        extr.Walls.Add(new WallSegment
        {
            SpaceId = extr.SpaceId,
            HostWallId = hostWallId,
            BaseStart = new XYZ(start.X, start.Y, baseZ),
            BaseEnd = new XYZ(end.X, end.Y, baseZ),
            BaseZ = baseZ,
            TopZ = topZ
        });
    }

    private static double GetRoomBaseZ(SpatialElement el)
    {
        var level = el.Document.GetElement(el.LevelId) as Level;
        double levelZ = level?.Elevation ?? 0;
        double offset = el.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET)?.AsDouble() ?? 0;
        return levelZ + offset;
    }

    private static double ComputeRoomHeight(SpatialElement el)
    {
        // Prefer computed mean height; fall back to user's upper-limit setting.
        if (el is Room r)
        {
            if (r.Volume > 0 && r.Area > 0) return r.Volume / r.Area;
            return r.UnboundedHeight;
        }
        if (el is Space s)
        {
            if (s.Volume > 0 && s.Area > 0) return s.Volume / s.Area;
            return s.UnboundedHeight;
        }
        return 0;
    }

    private void WarnIfSlopedCeiling(SpatialElement el, double avgHeight, string name)
    {
        double maxHeight = el is Room r ? r.UnboundedHeight
            : el is Space s ? s.UnboundedHeight
            : 0;
        if (maxHeight <= 0 || avgHeight <= 0) return;
        double ratio = Math.Abs(maxHeight - avgHeight) / avgHeight;
        // Allow a small modeling tolerance; flag anything beyond 15 % divergence.
        if (ratio > 0.15 && maxHeight > avgHeight + 0.5)
        {
            _log.Warn("Surface",
                $"Space '{name}': ceiling appears sloped " +
                $"(avg {avgHeight:F2} ft vs max {maxHeight:F2} ft). Flattened to average height.");
        }
    }

    private static bool IsSlantedWall(Wall wall)
    {
        // Revit 2020+ exposes this parameter for slanted walls.
        var p = wall.get_Parameter(BuiltInParameter.WALL_SINGLE_SLANT_ANGLE_FROM_VERTICAL);
        if (p != null && p.HasValue)
        {
            return Math.Abs(p.AsDouble()) > 0.001; // > ~0.06°
        }
        // Fallback: check if the wall's location curve has any Z variation.
        if (wall.Location is LocationCurve lc && lc.Curve != null)
        {
            double z0 = lc.Curve.GetEndPoint(0).Z;
            double z1 = lc.Curve.GetEndPoint(1).Z;
            return Math.Abs(z1 - z0) > 0.01;
        }
        return false;
    }

    #endregion

    #region Surface Generation

    private IEnumerable<SurfaceData> BuildWalls(RoomExtrusion extr)
    {
        foreach (var seg in extr.Walls)
        {
            // Revit's room boundary walks CCW when viewed from above, so the room
            // interior lies to the LEFT of the segment's direction. The wall's
            // outward (exterior) normal therefore points to the RIGHT.
            //
            // Winding for an outward-normal rectangle (Newell's right-hand rule
            // pointing away from the room):
            //   baseStart → baseEnd → topEnd → topStart
            XYZ topStart = new(seg.BaseStart.X, seg.BaseStart.Y, seg.TopZ);
            XYZ topEnd = new(seg.BaseEnd.X, seg.BaseEnd.Y, seg.TopZ);
            var verts = new List<XYZ> { seg.BaseStart, seg.BaseEnd, topEnd, topStart };

            // Skip zero-length walls (adjacent coincident boundary points).
            if (seg.BaseStart.DistanceTo(seg.BaseEnd) < 0.01) continue;

            yield return new SurfaceData
            {
                Id = $"surface-{++_surfaceCounter}",
                HostElementId = seg.HostWallId,
                SurfaceType = "ExteriorWall", // classified later
                Vertices = verts,
                Normal = GeometryUtils.ComputeNormal(verts),
                FirstAdjacentSpaceId = seg.SpaceId,
                CadObjectId = seg.HostWallId != ElementId.InvalidElementId
                    ? BuildCadObjectId(seg.HostWallId)
                    : null
            };
        }
    }

    private SurfaceData BuildFloor(RoomExtrusion extr)
    {
        // Floor outline is CCW when viewed from above, which gives a +Z Newell normal.
        // For the floor's outward normal (DOWN, away from the room interior), we reverse.
        var verts = extr.FloorOutline
            .Select(p => new XYZ(p.X, p.Y, extr.BaseZ))
            .Reverse()
            .ToList();

        return new SurfaceData
        {
            Id = $"surface-{++_surfaceCounter}",
            HostElementId = ElementId.InvalidElementId,
            SurfaceType = "ExposedFloor", // classified later
            Vertices = verts,
            Normal = GeometryUtils.ComputeNormal(verts),
            FirstAdjacentSpaceId = extr.SpaceId
        };
    }

    private SurfaceData BuildCeiling(RoomExtrusion extr)
    {
        // Ceiling outline is CCW when viewed from above → +Z Newell normal (outward).
        var verts = extr.FloorOutline
            .Select(p => new XYZ(p.X, p.Y, extr.TopZ))
            .ToList();

        return new SurfaceData
        {
            Id = $"surface-{++_surfaceCounter}",
            HostElementId = ElementId.InvalidElementId,
            SurfaceType = "Roof", // classified later; may become Ceiling when paired
            Vertices = verts,
            Normal = GeometryUtils.ComputeNormal(verts),
            FirstAdjacentSpaceId = extr.SpaceId
        };
    }

    private string BuildCadObjectId(ElementId id)
    {
        Element? e = _geomDoc.GetElement(id);
        return e != null ? $"{e.Category?.Name}:{id.Value}" : $":{id.Value}";
    }

    #endregion

    #region Wall Pairing

    /// <summary>
    /// Two walls pair as interior if they share the same host wall AND their base
    /// segments are reverse-equal within a tolerance (one endpoint of A matches the
    /// opposite endpoint of B, and vice versa — CCW-from-each-side means endpoints
    /// swap between paired walls).
    /// </summary>
    private (List<SurfaceData> paired, List<SurfaceData> unpaired) PairWalls(List<SurfaceData> walls)
    {
        var paired = new List<SurfaceData>();
        var consumed = new HashSet<string>();

        var grouped = walls
            .Where(w => w.HostElementId != ElementId.InvalidElementId)
            .GroupBy(w => w.HostElementId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var kv in grouped)
        {
            var group = kv.Value;
            for (int i = 0; i < group.Count; i++)
            {
                var a = group[i];
                if (consumed.Contains(a.Id)) continue;

                for (int j = i + 1; j < group.Count; j++)
                {
                    var b = group[j];
                    if (consumed.Contains(b.Id)) continue;
                    if (a.FirstAdjacentSpaceId == b.FirstAdjacentSpaceId) continue;
                    if (!WallsMatch(a, b)) continue;

                    // A's normal points outward from A's space — i.e., INTO B's space.
                    // That's exactly the direction we want for First=A, Second=B.
                    paired.Add(new SurfaceData
                    {
                        Id = $"surface-{++_surfaceCounter}",
                        HostElementId = a.HostElementId,
                        SurfaceType = "InteriorWall",
                        Vertices = a.Vertices,
                        Normal = a.Normal,
                        FirstAdjacentSpaceId = a.FirstAdjacentSpaceId,
                        SecondAdjacentSpaceId = b.FirstAdjacentSpaceId,
                        CadObjectId = a.CadObjectId
                    });
                    consumed.Add(a.Id);
                    consumed.Add(b.Id);
                    break;
                }
            }
        }

        var unpaired = walls.Where(w => !consumed.Contains(w.Id)).ToList();
        return (paired, unpaired);
    }

    private static bool WallsMatch(SurfaceData a, SurfaceData b)
    {
        // Base edges are vertices[0] and vertices[1] by construction.
        XYZ a0 = a.Vertices[0], a1 = a.Vertices[1];
        XYZ b0 = b.Vertices[0], b1 = b.Vertices[1];

        // Normals must be approximately opposite.
        if (a.Normal.DotProduct(b.Normal) > -0.7) return false;

        // The two base segments must be the "same" line — endpoints swap between sides.
        bool reversed = a0.DistanceTo(b1) < WallEndpointTolerance
                     && a1.DistanceTo(b0) < WallEndpointTolerance;
        bool aligned = a0.DistanceTo(b0) < WallEndpointTolerance
                     && a1.DistanceTo(b1) < WallEndpointTolerance;

        return reversed || aligned;
    }

    #endregion

    #region Floor / Ceiling Pairing

    /// <summary>
    /// Pairs each floor with a ceiling below it (within a slab-thickness tolerance)
    /// whose 2D footprint overlaps. A room above pairs its FLOOR with the ceiling of
    /// the room below; that produces one interior surface with First=above room,
    /// Second=below room, normal pointing downward (into Second).
    /// </summary>
    private (List<SurfaceData> paired, List<SurfaceData> orphanFloors, List<SurfaceData> orphanCeilings)
        PairFloorsAndCeilings(List<SurfaceData> floors, List<SurfaceData> ceilings)
    {
        var paired = new List<SurfaceData>();
        var usedFloors = new HashSet<string>();
        var usedCeilings = new HashSet<string>();

        // Process floors in ascending Z so we match the lowest stack first.
        foreach (var floor in floors.OrderBy(f => f.Vertices[0].Z))
        {
            if (usedFloors.Contains(floor.Id)) continue;
            double floorZ = floor.Vertices[0].Z;

            SurfaceData? best = null;
            double bestGap = double.MaxValue;

            foreach (var ceiling in ceilings)
            {
                if (usedCeilings.Contains(ceiling.Id)) continue;
                if (floor.FirstAdjacentSpaceId == ceiling.FirstAdjacentSpaceId) continue;

                double ceilingZ = ceiling.Vertices[0].Z;
                double gap = floorZ - ceilingZ;
                if (gap < -0.1 || gap > MaxSlabGap) continue;

                if (!AabbOverlap(floor.Vertices, ceiling.Vertices)) continue;

                if (gap < bestGap) { bestGap = gap; best = ceiling; }
            }

            if (best != null)
            {
                paired.Add(new SurfaceData
                {
                    Id = $"surface-{++_surfaceCounter}",
                    HostElementId = floor.HostElementId,
                    SurfaceType = "InteriorFloor",
                    Vertices = floor.Vertices,
                    Normal = floor.Normal,
                    FirstAdjacentSpaceId = floor.FirstAdjacentSpaceId,
                    SecondAdjacentSpaceId = best.FirstAdjacentSpaceId,
                    CadObjectId = floor.CadObjectId
                });
                usedFloors.Add(floor.Id);
                usedCeilings.Add(best.Id);
            }
        }

        var orphanFloors = floors.Where(f => !usedFloors.Contains(f.Id)).ToList();
        var orphanCeilings = ceilings.Where(c => !usedCeilings.Contains(c.Id)).ToList();
        return (paired, orphanFloors, orphanCeilings);
    }

    private static bool AabbOverlap(List<XYZ> a, List<XYZ> b)
    {
        double aMinX = double.MaxValue, aMaxX = double.MinValue;
        double aMinY = double.MaxValue, aMaxY = double.MinValue;
        foreach (var p in a)
        {
            if (p.X < aMinX) aMinX = p.X;
            if (p.X > aMaxX) aMaxX = p.X;
            if (p.Y < aMinY) aMinY = p.Y;
            if (p.Y > aMaxY) aMaxY = p.Y;
        }

        double bMinX = double.MaxValue, bMaxX = double.MinValue;
        double bMinY = double.MaxValue, bMaxY = double.MinValue;
        foreach (var p in b)
        {
            if (p.X < bMinX) bMinX = p.X;
            if (p.X > bMaxX) bMaxX = p.X;
            if (p.Y < bMinY) bMinY = p.Y;
            if (p.Y > bMaxY) bMaxY = p.Y;
        }

        // Overlap requires both X and Y ranges to intersect with positive area.
        return aMinX < bMaxX && bMinX < aMaxX && aMinY < bMaxY && bMinY < aMaxY;
    }

    #endregion

    #region Classification & Geometry

    private void ClassifySurfaceType(SurfaceData surface)
    {
        Element? host = surface.HostElementId != ElementId.InvalidElementId
            ? _geomDoc.GetElement(surface.HostElementId)
            : null;
        double tilt = GeometryUtils.ComputeTilt(surface.Normal);
        bool isInterior = surface.SecondAdjacentSpaceId != null;

        surface.SurfaceType = (host, tilt, isInterior) switch
        {
            (_, < 30, true) when surface.Normal.Z > 0 => "Ceiling",
            (_, < 30, true) when surface.Normal.Z < 0 => "InteriorFloor",
            (_, < 30, false) when surface.Normal.Z > 0 => "Roof",
            (_, < 30, false) when surface.Normal.Z < 0 && IsAtGrade(surface) => "SlabOnGrade",
            (_, < 30, false) when surface.Normal.Z < 0 && IsBelowGrade(surface) => "UndergroundSlab",
            (_, < 30, false) when surface.Normal.Z < 0 => "ExposedFloor",

            (Wall, >= 60, true) => "InteriorWall",
            (Wall, >= 60, false) when IsBelowGrade(surface) => "UndergroundWall",
            (Wall, >= 60, false) => "ExteriorWall",

            (_, >= 60, true) => "InteriorWall",
            (_, >= 60, false) => "ExteriorWall",

            (_, _, true) => "InteriorWall",
            (_, _, false) => "ExteriorWall"
        };
    }

    private static bool IsAtGrade(SurfaceData s)
        => Math.Abs(s.Vertices.Min(v => v.Z)) < 1.0;

    private static bool IsBelowGrade(SurfaceData s)
        => s.Vertices.Average(v => v.Z) < -0.5;

    private void ComputeRectangularGeometry(SurfaceData surface)
    {
        surface.Azimuth = GeometryUtils.ComputeAzimuth(surface.Normal);
        surface.Tilt = GeometryUtils.ComputeTilt(surface.Normal);

        var (width, height, origin) = GeometryUtils.ComputeRectangularExtents(surface.Vertices, surface.Normal);
        surface.Width = UnitConverter.ConvertLength(width, _settings.LengthUnit);
        surface.Height = UnitConverter.ConvertLength(height, _settings.LengthUnit);
        surface.Origin = origin;
    }

    private void WarnAboutBareSpaces(List<SpaceData> spaces, List<SurfaceData> exported)
    {
        var counts = new Dictionary<string, int>();
        foreach (var s in exported)
        {
            counts[s.FirstAdjacentSpaceId] = counts.GetValueOrDefault(s.FirstAdjacentSpaceId) + 1;
            if (s.SecondAdjacentSpaceId != null)
                counts[s.SecondAdjacentSpaceId] = counts.GetValueOrDefault(s.SecondAdjacentSpaceId) + 1;
        }

        int bare = 0, low = 0;
        foreach (var sp in spaces)
        {
            int n = counts.GetValueOrDefault(sp.Id);
            if (n == 0)
            {
                _log.Error("Space", $"Space {sp.Id} ({sp.Name}) has 0 bounding surfaces.");
                bare++;
            }
            else if (n < 4)
            {
                _log.Warn("Space",
                    $"Space {sp.Id} ({sp.Name}) has only {n} bounding surface(s); expected ≥4.");
                low++;
            }
        }
        if (bare + low > 0)
            _log.Warn("Space", $"Closure issues: {bare} bare, {low} under-bounded.");
    }

    #endregion

    #region Openings

    private void CacheHostedOpenings()
    {
        var instances = new FilteredElementCollector(_geomDoc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => fi.Host != null && IsOpeningCategory(fi));

        foreach (var fi in instances)
        {
            ElementId hostId = fi.Host!.Id;
            if (!_hostedOpenings.TryGetValue(hostId, out var list))
            {
                list = [];
                _hostedOpenings[hostId] = list;
            }
            list.Add(fi);
        }
    }

    private void AttachOpenings(List<SurfaceData> surfaces)
    {
        var byHost = surfaces
            .Where(s => s.HostElementId != ElementId.InvalidElementId)
            .GroupBy(s => s.HostElementId)
            .ToDictionary(g => g.Key, g => g.ToList());

        int overlap = _hostedOpenings.Keys.Count(id => byHost.ContainsKey(id));
        _log.Info("Opening",
            $"Attaching openings: {_hostedOpenings.Count} host element(s), " +
            $"{byHost.Count} surface host(s), {overlap} overlapping.");

        int attached = 0;
        int noHostCount = 0;

        foreach (var (hostId, fis) in _hostedOpenings)
        {
            if (!byHost.TryGetValue(hostId, out var candidates) || candidates.Count == 0)
            {
                foreach (var fi in fis)
                {
                    if (GetOpeningType(fi) != null) noHostCount++;
                }
                continue;
            }

            var centroids = candidates
                .Select(s => GeometryUtils.ComputeCentroid(s.Vertices))
                .ToList();

            foreach (var fi in fis)
            {
                string? openingType = GetOpeningType(fi);
                if (openingType == null) continue;
                if (!_settings.ShouldExportOpening(openingType)) continue;

                XYZ? anchor = GetOpeningAnchor(fi);
                if (anchor == null)
                {
                    _log.Warn("Opening",
                        $"Opening {fi.Id.Value} ({fi.Symbol?.FamilyName}): no location or bounding box.");
                    continue;
                }

                int bestIdx = 0;
                double bestDist = double.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    double d = anchor.DistanceTo(centroids[i]);
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }

                var target = candidates[bestIdx];
                try
                {
                    var data = BuildOpeningData(fi, anchor, target, openingType);
                    if (data != null)
                    {
                        target.Openings.Add(data);
                        attached++;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Opening",
                        $"Opening {fi.Id.Value} ({fi.Symbol?.FamilyName}) failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        if (noHostCount > 0)
            _log.Warn("Opening",
                $"{noHostCount} opening(s) skipped: host element has no exported surface.");
        _log.Info("Opening", $"{attached} opening(s) attached.");
    }

    private XYZ? GetOpeningAnchor(FamilyInstance fi)
    {
        XYZ? local = null;
        if (fi.Location is LocationPoint lp) local = lp.Point;
        else
        {
            var bb = fi.get_BoundingBox(null);
            if (bb != null) local = (bb.Min + bb.Max) * 0.5;
        }
        // Family instance lives in _geomDoc; bring its anchor into the spatial frame
        // where surfaces are built. Identity transform when both docs are the same.
        return local == null ? null : _geomTransform.OfPoint(local);
    }

    private OpeningData? BuildOpeningData(FamilyInstance fi, XYZ anchor, SurfaceData target, string openingType)
    {
        XYZ surfaceNormal = target.Normal;

        double width = GetDimension(fi, BuiltInParameter.WINDOW_WIDTH,
            BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM);
        double height = GetDimension(fi, BuiltInParameter.WINDOW_HEIGHT,
            BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM);

        if (width <= 0 || height <= 0)
        {
            var (bw, bh) = DimensionsFromBoundingBox(fi, surfaceNormal);
            if (width <= 0) width = bw;
            if (height <= 0) height = bh;
        }
        if (width <= 0 || height <= 0)
        {
            _log.Warn("Opening",
                $"Opening {fi.Id.Value} ({fi.Symbol?.FamilyName}) skipped: no width/height.");
            return null;
        }

        var (localX, localY) = GeometryUtils.GetInPlaneAxes(surfaceNormal);
        bool horizontal = Math.Abs(surfaceNormal.Z) > 0.7;

        XYZ openingCenter;
        if (fi.Location is LocationPoint)
        {
            double sill = horizontal
                ? 0
                : fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0;
            openingCenter = anchor + localY * (sill + height / 2);
        }
        else
        {
            openingCenter = anchor;
        }

        var vertices = new List<XYZ>
        {
            openingCenter - localX * (width / 2) - localY * (height / 2),
            openingCenter + localX * (width / 2) - localY * (height / 2),
            openingCenter + localX * (width / 2) + localY * (height / 2),
            openingCenter - localX * (width / 2) + localY * (height / 2)
        };

        bool glazed = openingType.Contains("Window") || openingType.Contains("Skylight");

        return new OpeningData
        {
            Id = $"opening-{++_openingCounter}",
            OpeningType = openingType,
            WindowTypeId = glazed ? $"wtype-{fi.Symbol.Id.Value}" : null,
            Vertices = vertices,
            Width = UnitConverter.ConvertLength(width, _settings.LengthUnit),
            Height = UnitConverter.ConvertLength(height, _settings.LengthUnit),
            Origin = vertices[0],
            CadObjectId = $"{fi.Category?.Name}:{fi.Id.Value}"
        };
    }

    private (double width, double height) DimensionsFromBoundingBox(
        FamilyInstance fi, XYZ surfaceNormal)
    {
        var bb = fi.get_BoundingBox(null);
        if (bb == null) return (0, 0);

        var (localX, localY) = GeometryUtils.GetInPlaneAxes(surfaceNormal);

        // Bring corners into the spatial frame so projections onto localX/localY (which
        // live in spatial coords) are meaningful when the link has a rotation.
        var corners = new[]
        {
            _geomTransform.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Min.Z)),
            _geomTransform.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z)),
            _geomTransform.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z)),
            _geomTransform.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z)),
            _geomTransform.OfPoint(new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z)),
            _geomTransform.OfPoint(new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z)),
            _geomTransform.OfPoint(new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z)),
            _geomTransform.OfPoint(new XYZ(bb.Max.X, bb.Max.Y, bb.Max.Z)),
        };

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var c in corners)
        {
            double px = c.DotProduct(localX);
            double py = c.DotProduct(localY);
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;
        }

        return (maxX - minX, maxY - minY);
    }

    private static double GetDimension(FamilyInstance fi, params BuiltInParameter[] candidates)
    {
        foreach (var bip in candidates)
        {
            double? val = fi.get_Parameter(bip)?.AsDouble();
            if (val is > 0) return val.Value;

            val = fi.Symbol.get_Parameter(bip)?.AsDouble();
            if (val is > 0) return val.Value;
        }
        return 0;
    }

    private static bool IsOpeningCategory(FamilyInstance fi)
    {
        var cat = fi.Category?.BuiltInCategory;
        return cat is BuiltInCategory.OST_Windows
            or BuiltInCategory.OST_Doors
            or BuiltInCategory.OST_CurtainWallPanels;
    }

    private static string? GetOpeningType(FamilyInstance fi)
    {
        var cat = fi.Category?.BuiltInCategory;
        return cat switch
        {
            BuiltInCategory.OST_Windows => fi.Host is RoofBase ? "FixedSkylight" : "FixedWindow",
            BuiltInCategory.OST_Doors => IsSlidingDoor(fi) ? "SlidingDoor" : "NonSlidingDoor",
            BuiltInCategory.OST_CurtainWallPanels => "FixedWindow",
            _ => null
        };
    }

    private static bool IsSlidingDoor(FamilyInstance fi)
    {
        string name = fi.Symbol.FamilyName?.ToLower() ?? "";
        return name.Contains("sliding") || name.Contains("slider");
    }

    #endregion
}
