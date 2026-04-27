using Autodesk.Revit.DB;

namespace RevitGbXmlExporter.Models;

public class SpaceData
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public ElementId RevitElementId { get; set; } = ElementId.InvalidElementId;
    public double Area { get; set; }
    public double Volume { get; set; }
    public required string LevelId { get; set; }
    public required string LevelName { get; set; }
    public double LevelElevation { get; set; }
    public bool IsPlenum { get; set; }
    public required string ZoneId { get; set; }
    public required string ZoneName { get; set; }
    public XYZ? Centroid { get; set; }

    // Optional space loads
    public double? PeopleCount { get; set; }
    public double? LightingPowerPerArea { get; set; }
    public double? EquipmentPowerPerArea { get; set; }
    public double? InfiltrationFlowPerArea { get; set; }

    // Floor outline polygon (Revit internal feet). Emitted as <Space>/<PlanarGeometry>
    // so HAP has a polygon to label in plan view. Populated by SurfaceAnalyzer; empty
    // when geometry export is off.
    public List<XYZ> FloorOutline { get; set; } = [];
}

public class LevelData
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public double Elevation { get; set; }

    // PolyLoop outline for the storey (4-point AABB of all rooms in this bucket).
    // Emitted as <PlanarGeometry><PolyLoop> inside <BuildingStorey>. Empty → skip.
    public List<XYZ> PlanarOutline { get; set; } = [];
}

public class SurfaceData
{
    public required string Id { get; set; }
    public ElementId HostElementId { get; set; } = ElementId.InvalidElementId;
    public required string SurfaceType { get; set; }
    public List<XYZ> Vertices { get; set; } = [];
    public XYZ Normal { get; set; } = XYZ.Zero;
    public required string FirstAdjacentSpaceId { get; set; }
    public string? SecondAdjacentSpaceId { get; set; }
    public string? ConstructionId { get; set; }
    public string? CadObjectId { get; set; }
    public List<OpeningData> Openings { get; set; } = [];

    // Rectangular geometry (computed from vertices)
    public double Azimuth { get; set; }
    public double Tilt { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public XYZ Origin { get; set; } = XYZ.Zero;

    public bool IsExterior => SecondAdjacentSpaceId == null;
}

public class OpeningData
{
    public required string Id { get; set; }
    public required string OpeningType { get; set; }
    public string? WindowTypeId { get; set; }
    public List<XYZ> Vertices { get; set; } = [];
    public double Width { get; set; }
    public double Height { get; set; }
    public XYZ Origin { get; set; } = XYZ.Zero;
    public string? CadObjectId { get; set; }
}

public class ConstructionData
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public List<LayerData> Layers { get; set; } = [];
}

public class LayerData
{
    public required string Id { get; set; }
    public required string MaterialId { get; set; }
}

public class MaterialData
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public double Thickness { get; set; }
    public double Conductivity { get; set; }
    public double Density { get; set; }
    public double SpecificHeat { get; set; }
    public double? Absorptance { get; set; }
    public double? Roughness { get; set; }
}

public class WindowTypeData
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public double UValue { get; set; }
    public double SolarHeatGainCoeff { get; set; }
    public double? VisibleTransmittance { get; set; }
}

public class ZoneData
{
    public required string Id { get; set; }
    public required string Name { get; set; }
}

/// <summary>
/// Aggregates all extracted data for the gbXML writer.
/// </summary>
public class BuildingData
{
    public string BuildingName { get; set; } = "Building";
    public string BuildingType { get; set; } = "Office";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Elevation { get; set; }

    public List<LevelData> Levels { get; set; } = [];
    public List<SpaceData> Spaces { get; set; } = [];
    public List<ZoneData> Zones { get; set; } = [];
    public List<SurfaceData> Surfaces { get; set; } = [];
    public List<ConstructionData> Constructions { get; set; } = [];
    public List<MaterialData> Materials { get; set; } = [];
    public List<WindowTypeData> WindowTypes { get; set; } = [];
}
