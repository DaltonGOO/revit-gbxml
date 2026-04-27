using Autodesk.Revit.DB;

namespace RevitGbXmlExporter.Utilities;

public static class GeometryUtils
{
    private const double Tolerance = 1e-6;

    // Max out-of-plane distance (ft) before a polygon is considered non-planar.
    // ~1/16 inch — matches Revit's modeling tolerance and HAP's import tolerance.
    public const double PlanarityTolerance = 0.005;

    // Target segment length when tessellating curves into planar strips (ft).
    // 2 ft keeps arc walls readable in HAP without producing hundreds of surfaces.
    public const double ArcSegmentLength = 2.0;

    // A tessellated edge is treated as horizontal if its Z spread is below this.
    private const double HorizontalEdgeZTolerance = 0.01;

    /// <summary>
    /// Extracts the outer boundary vertices from a face as a single polygon.
    /// Used directly only for faces already known to be planar — for unknown faces
    /// call GetFacePlanarSegments instead, which splits curved faces into planar quads.
    /// </summary>
    public static List<XYZ> GetFaceOuterVertices(Face face)
    {
        var vertices = new List<XYZ>();
        EdgeArrayArray edgeLoops = face.EdgeLoops;
        if (edgeLoops.Size == 0) return vertices;

        EdgeArray outerLoop = edgeLoops.get_Item(0);
        foreach (Edge edge in outerLoop)
        {
            IList<XYZ> points = edge.Tessellate();
            for (int i = 0; i < points.Count - 1; i++)
                vertices.Add(points[i]);
        }

        return RemoveConsecutiveDuplicates(vertices);
    }

    /// <summary>
    /// Returns one or more planar polygons that cover the face.
    /// - Planar faces return one polygon (the outer loop).
    /// - Curved vertical faces (e.g. cylindrical walls) return N planar quads by
    ///   pairing tessellation points on the bottom and top horizontal edges.
    /// - Anything else falls back to per-triangle polygons from Face.Triangulate().
    /// </summary>
    public static List<List<XYZ>> GetFacePlanarSegments(Face face)
    {
        if (face is PlanarFace)
        {
            var polygon = GetFaceOuterVertices(face);
            return polygon.Count >= 3 ? [polygon] : [];
        }

        var single = GetFaceOuterVertices(face);
        if (single.Count >= 3 && IsPolygonPlanar(single, PlanarityTolerance))
            return [single];

        // Try splitting as a vertical curved strip (cylindrical wall, sine wall, etc.)
        var strips = TrySplitVerticalCurvedFace(face);
        if (strips.Count > 0) return strips;

        // Final fallback: triangulate the face.
        return TriangulateFaceAsPolygons(face);
    }

    /// <summary>
    /// True if every vertex lies within tolerance of the plane defined by the polygon's
    /// Newell normal through its centroid.
    /// </summary>
    public static bool IsPolygonPlanar(List<XYZ> vertices, double tolerance)
    {
        if (vertices.Count < 4) return true;

        XYZ normal = ComputeNormal(vertices);
        if (normal.GetLength() < Tolerance) return true;

        XYZ centroid = ComputeCentroid(vertices);
        foreach (var v in vertices)
        {
            double dist = Math.Abs(normal.DotProduct(v - centroid));
            if (dist > tolerance) return false;
        }
        return true;
    }

    /// <summary>
    /// For vertical curved faces (walls): find the bottom and top horizontal edges,
    /// tessellate them with matching counts, and emit one planar quad per segment.
    /// Returns an empty list if the face doesn't fit this shape.
    /// </summary>
    private static List<List<XYZ>> TrySplitVerticalCurvedFace(Face face)
    {
        EdgeArrayArray loops = face.EdgeLoops;
        if (loops.Size == 0) return [];
        EdgeArray outerLoop = loops.get_Item(0);

        Edge? bottomEdge = null;
        Edge? topEdge = null;
        double bottomZ = double.MaxValue;
        double topZ = double.MinValue;

        foreach (Edge edge in outerLoop)
        {
            Curve? curve = edge.AsCurve();
            if (curve == null) continue;

            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);
            double dz = Math.Abs(p0.Z - p1.Z);
            if (dz > HorizontalEdgeZTolerance) continue; // vertical side

            double avgZ = (p0.Z + p1.Z) * 0.5;
            if (avgZ < bottomZ) { bottomZ = avgZ; bottomEdge = edge; }
            if (avgZ > topZ) { topZ = avgZ; topEdge = edge; }
        }

        if (bottomEdge == null || topEdge == null || ReferenceEquals(bottomEdge, topEdge))
            return [];

        Curve bottomCurve = bottomEdge.AsCurve();
        Curve topCurve = topEdge.AsCurve();
        if (bottomCurve == null || topCurve == null) return [];

        // Use the longer of the two lengths to pick a segment count so both sides pair cleanly.
        double length = Math.Max(bottomCurve.Length, topCurve.Length);
        int segCount = Math.Max(4, (int)Math.Ceiling(length / ArcSegmentLength));

        var bottomPts = EvaluateCurve(bottomCurve, segCount);
        var topPts = EvaluateCurve(topCurve, segCount);

        // Align directions: outer loop walks bottom forward then top backward (or vice versa).
        double d0 = DistanceXY(bottomPts[0], topPts[0]);
        double dLast = DistanceXY(bottomPts[0], topPts[^1]);
        if (dLast < d0) topPts.Reverse();

        XYZ faceNormalHint = ComputeFaceNormalAtCenter(face);

        var segments = new List<List<XYZ>>();
        for (int i = 0; i < segCount; i++)
        {
            var quad = new List<XYZ>
            {
                bottomPts[i],
                bottomPts[i + 1],
                topPts[i + 1],
                topPts[i]
            };

            quad = RemoveConsecutiveDuplicates(quad);
            if (quad.Count < 3) continue;
            if (ComputePolygonArea(quad) < 0.01) continue;

            // Ensure winding matches the parent face's outward normal.
            XYZ quadNormal = ComputeNormal(quad);
            if (faceNormalHint.GetLength() > Tolerance &&
                quadNormal.DotProduct(faceNormalHint) < 0)
            {
                quad.Reverse();
            }

            segments.Add(quad);
        }

        return segments;
    }

    private static List<XYZ> EvaluateCurve(Curve curve, int segments)
    {
        var pts = new List<XYZ>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            double t = (double)i / segments;
            pts.Add(curve.Evaluate(t, true));
        }
        return pts;
    }

    private static double DistanceXY(XYZ a, XYZ b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static XYZ ComputeFaceNormalAtCenter(Face face)
    {
        try
        {
            BoundingBoxUV bb = face.GetBoundingBox();
            UV center = (bb.Min + bb.Max) * 0.5;
            return face.ComputeNormal(center);
        }
        catch
        {
            return XYZ.Zero;
        }
    }

    /// <summary>
    /// Fallback: return every triangle from Face.Triangulate as its own planar polygon.
    /// Loses some fidelity (many small surfaces) but is robust for exotic face shapes.
    /// </summary>
    private static List<List<XYZ>> TriangulateFaceAsPolygons(Face face)
    {
        var result = new List<List<XYZ>>();
        try
        {
            Mesh mesh = face.Triangulate();
            if (mesh == null) return result;

            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle tri = mesh.get_Triangle(i);
                var poly = new List<XYZ>
                {
                    tri.get_Vertex(0),
                    tri.get_Vertex(1),
                    tri.get_Vertex(2)
                };
                if (ComputePolygonArea(poly) > 0.001)
                    result.Add(poly);
            }
        }
        catch
        {
            // Face may be unsupported for triangulation — give up.
        }
        return result;
    }

    public static XYZ ComputeCentroid(List<XYZ> vertices)
    {
        if (vertices.Count == 0) return XYZ.Zero;

        double x = 0, y = 0, z = 0;
        foreach (var v in vertices)
        {
            x += v.X;
            y += v.Y;
            z += v.Z;
        }
        return new XYZ(x / vertices.Count, y / vertices.Count, z / vertices.Count);
    }

    public static double ComputePolygonArea(List<XYZ> vertices)
    {
        if (vertices.Count < 3) return 0;

        XYZ cross = XYZ.Zero;
        for (int i = 0; i < vertices.Count; i++)
        {
            XYZ current = vertices[i];
            XYZ next = vertices[(i + 1) % vertices.Count];
            cross = cross.Add(current.CrossProduct(next));
        }
        return cross.GetLength() / 2.0;
    }

    public static XYZ ComputeNormal(List<XYZ> vertices)
    {
        if (vertices.Count < 3) return XYZ.BasisZ;

        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < vertices.Count; i++)
        {
            XYZ current = vertices[i];
            XYZ next = vertices[(i + 1) % vertices.Count];
            nx += (current.Y - next.Y) * (current.Z + next.Z);
            ny += (current.Z - next.Z) * (current.X + next.X);
            nz += (current.X - next.X) * (current.Y + next.Y);
        }

        XYZ normal = new(nx, ny, nz);
        double length = normal.GetLength();
        return length > Tolerance ? normal.Normalize() : XYZ.BasisZ;
    }

    public static double ComputeAzimuth(XYZ normal)
    {
        double x = normal.X;
        double y = normal.Y;

        if (Math.Abs(x) < Tolerance && Math.Abs(y) < Tolerance)
            return 0;

        double angle = Math.Atan2(x, y) * 180.0 / Math.PI;
        if (angle < 0) angle += 360.0;
        return angle;
    }

    public static double ComputeTilt(XYZ normal)
    {
        double dot = normal.DotProduct(XYZ.BasisZ);
        double angle = Math.Acos(Math.Clamp(dot, -1.0, 1.0)) * 180.0 / Math.PI;
        return angle;
    }

    /// <summary>
    /// Deterministic in-plane axes for a given surface normal. Vertical surfaces get
    /// world-Z as the "up" axis; horizontal surfaces fall back to world X/Y.
    /// localX points to the viewer's RIGHT when looking at the surface from outside
    /// (along -normal toward the surface) — this matches gbXML's "bottom-left when
    /// facing from outside" convention for RectangularGeometry/CartesianPoint.
    /// </summary>
    public static (XYZ LocalX, XYZ LocalY) GetInPlaneAxes(XYZ normal)
    {
        XYZ up = XYZ.BasisZ;
        double dotUp = Math.Abs(normal.DotProduct(up));
        if (dotUp > 1.0 - Tolerance)
            return (XYZ.BasisX, XYZ.BasisY);

        XYZ localX = up.CrossProduct(normal).Normalize();
        XYZ localY = normal.CrossProduct(localX).Normalize();
        return (localX, localY);
    }

    public static (double Width, double Height, XYZ Origin) ComputeRectangularExtents(List<XYZ> vertices, XYZ normal)
    {
        if (vertices.Count < 3) return (0, 0, XYZ.Zero);

        var (localX, localY) = GetInPlaneAxes(normal);

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        foreach (var v in vertices)
        {
            double px = v.DotProduct(localX);
            double py = v.DotProduct(localY);
            minX = Math.Min(minX, px);
            maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py);
            maxY = Math.Max(maxY, py);
        }

        double width = maxX - minX;
        double height = maxY - minY;

        // localX, localY, normal form an orthonormal basis. The bottom-left corner is
        // (minX, minY) in the (localX, localY) plane PLUS the surface's offset along
        // its own normal — without that last piece, walls not passing through the
        // world origin get a bogus surface origin even though the polygon is correct.
        double normalOffset = vertices[0].DotProduct(normal);
        XYZ origin = localX * minX + localY * minY + normal * normalOffset;

        return (width, height, origin);
    }

    public static bool AreNormalsOpposite(XYZ a, XYZ b) => a.DotProduct(b) < -0.7;

    public static bool ArePointsClose(XYZ a, XYZ b, double tolerance = 3.0)
        => a.DistanceTo(b) < tolerance;

    public static List<XYZ> RemoveConsecutiveDuplicates(List<XYZ> vertices, double tolerance = 0.001)
    {
        if (vertices.Count <= 1) return vertices;

        var result = new List<XYZ> { vertices[0] };
        for (int i = 1; i < vertices.Count; i++)
        {
            if (vertices[i].DistanceTo(result[^1]) > tolerance)
                result.Add(vertices[i]);
        }

        if (result.Count > 1 && result[^1].DistanceTo(result[0]) < tolerance)
            result.RemoveAt(result.Count - 1);

        return result;
    }

    public static List<XYZ> ReverseVertices(List<XYZ> vertices)
    {
        var reversed = new List<XYZ>(vertices);
        reversed.Reverse();
        return reversed;
    }
}
