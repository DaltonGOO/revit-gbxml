using System.Globalization;
using System.Xml.Linq;
using Autodesk.Revit.DB;
using RevitGbXmlExporter.Models;
using RevitGbXmlExporter.Utilities;

namespace RevitGbXmlExporter.Export;

/// <summary>
/// Builds a gbXML document from extracted building data.
/// Targets gbXML version 0.37 for maximum HAP Carrier compatibility.
/// </summary>
public class GbXmlWriter
{
    private static readonly XNamespace Ns = "http://www.gbxml.org/schema";
    private readonly ExportSettings _settings;

    public GbXmlWriter(ExportSettings settings)
    {
        _settings = settings;
    }

    public XDocument Build(BuildingData data)
    {
        var root = new XElement(Ns + "gbXML",
            new XAttribute("xmlns", Ns.NamespaceName),
            new XAttribute("version", "6.01"),
            new XAttribute("temperatureUnit", _settings.TemperatureUnit),
            new XAttribute("lengthUnit", _settings.LengthUnit),
            new XAttribute("areaUnit", _settings.AreaUnit),
            new XAttribute("volumeUnit", _settings.VolumeUnit),
            new XAttribute("useSIUnitsForResults",
                _settings.LengthUnit == "Meters" ? "true" : "false"),
            new XAttribute("SurfaceReferenceLocation", "Centerline")
        );

        // Campus (required, exactly 1)
        var campus = BuildCampus(data);
        root.Add(campus);

        // Constructions, Layers, Materials
        foreach (var construction in data.Constructions)
            root.Add(BuildConstruction(construction));

        var writtenLayers = new HashSet<string>();
        foreach (var construction in data.Constructions)
        {
            foreach (var layer in construction.Layers)
            {
                if (writtenLayers.Add(layer.Id))
                    root.Add(BuildLayer(layer));
            }
        }

        foreach (var material in data.Materials)
            root.Add(BuildMaterial(material));

        // Window types
        foreach (var wt in data.WindowTypes)
            root.Add(BuildWindowType(wt));

        // Zones
        foreach (var zone in data.Zones)
            root.Add(BuildZone(zone));

        // Document history
        root.Add(BuildDocumentHistory());

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            root
        );
    }

    private XElement BuildCampus(BuildingData data)
    {
        var campus = new XElement(Ns + "Campus",
            new XAttribute("id", "campus-1")
        );

        // Location — Elevation carries a unit attribute so HAP doesn't mis-interpret the value.
        if (data.Latitude.HasValue && data.Longitude.HasValue)
        {
            campus.Add(new XElement(Ns + "Location",
                new XElement(Ns + "Latitude", Fmt(data.Latitude.Value)),
                new XElement(Ns + "Longitude", Fmt(data.Longitude.Value)),
                data.Elevation.HasValue
                    ? new XElement(Ns + "Elevation",
                        new XAttribute("unit", _settings.LengthUnit),
                        Fmt(data.Elevation.Value))
                    : null
            ));
        }

        // Building (1 per campus for typical exports)
        campus.Add(BuildBuilding(data));

        // Surfaces live at Campus level per gbXML schema
        foreach (var surface in data.Surfaces)
            campus.Add(BuildSurface(surface));

        return campus;
    }

    private XElement BuildBuilding(BuildingData data)
    {
        var building = new XElement(Ns + "Building",
            new XAttribute("id", "building-1"),
            new XAttribute("buildingType", data.BuildingType)
        );

        building.Add(new XElement(Ns + "Name", data.BuildingName));

        // Building storeys — emitted in ascending-elevation order so HAP renders floors correctly.
        foreach (var level in data.Levels.OrderBy(l => l.Elevation).ThenBy(l => l.Name))
        {
            var storey = new XElement(Ns + "BuildingStorey",
                new XAttribute("id", level.Id),
                new XElement(Ns + "Name", level.Name),
                new XElement(Ns + "Level",
                    new XAttribute("unit", _settings.LengthUnit),
                    Fmt(level.Elevation))
            );

            // PlanarGeometry outline (plan AABB of all rooms in this bucket) — HAP uses
            // this to spatially anchor each storey.
            if (level.PlanarOutline.Count >= 3)
            {
                storey.Add(new XElement(Ns + "PlanarGeometry",
                    BuildPolyLoop(level.PlanarOutline)
                ));
            }

            building.Add(storey);
        }

        // Spaces
        foreach (var space in data.Spaces)
            building.Add(BuildSpace(space));

        // Total area and volume — Area is always emitted (gbXML schema requires it).
        double totalArea = data.Spaces.Sum(s => s.Area);
        double totalVolume = data.Spaces.Sum(s => s.Volume);
        building.Add(new XElement(Ns + "Area", Fmt(totalArea)));
        if (totalVolume > 0)
            building.Add(new XElement(Ns + "Volume", Fmt(totalVolume)));

        return building;
    }

    private XElement BuildSpace(SpaceData space)
    {
        var spaceElement = new XElement(Ns + "Space",
            new XAttribute("id", space.Id),
            new XAttribute("zoneIdRef", space.ZoneId),
            new XAttribute("buildingStoreyIdRef", space.LevelId)
        );

        if (space.IsPlenum)
            spaceElement.Add(new XAttribute("spaceType", "PlenumSpace"));

        // Order matches Revit's native gbXML: loads → Area → Volume → PlanarGeometry →
        // Name. HAP reads PlanarGeometry to draw the room outline in plan view and uses
        // <Name> as the label; without the polygon, the name is shown as "unnamed".
        if (space.PeopleCount.HasValue && space.PeopleCount > 0)
        {
            spaceElement.Add(new XElement(Ns + "PeopleNumber",
                new XAttribute("unit", "NumberOfPeople"),
                Fmt(space.PeopleCount.Value)
            ));
        }

        if (space.LightingPowerPerArea.HasValue && space.LightingPowerPerArea > 0)
        {
            spaceElement.Add(new XElement(Ns + "LightPowerPerArea",
                new XAttribute("unit", "WattPerSquareFoot"),
                Fmt(space.LightingPowerPerArea.Value)
            ));
        }

        if (space.EquipmentPowerPerArea.HasValue && space.EquipmentPowerPerArea > 0)
        {
            spaceElement.Add(new XElement(Ns + "EquipPowerPerArea",
                new XAttribute("unit", "WattPerSquareFoot"),
                Fmt(space.EquipmentPowerPerArea.Value)
            ));
        }

        if (space.InfiltrationFlowPerArea.HasValue && space.InfiltrationFlowPerArea > 0)
        {
            spaceElement.Add(new XElement(Ns + "InfiltrationFlow",
                new XAttribute("unit", "CFMPerSquareFoot"),
                Fmt(space.InfiltrationFlowPerArea.Value)
            ));
        }

        spaceElement.Add(new XElement(Ns + "Area", Fmt(space.Area)));
        spaceElement.Add(new XElement(Ns + "Volume", Fmt(space.Volume)));

        if (space.FloorOutline.Count >= 3)
        {
            spaceElement.Add(new XElement(Ns + "PlanarGeometry",
                BuildPolyLoop(space.FloorOutline)
            ));
        }

        spaceElement.Add(new XElement(Ns + "Name", space.Name));

        return spaceElement;
    }

    private XElement BuildSurface(SurfaceData surface)
    {
        var surfaceElement = new XElement(Ns + "Surface",
            new XAttribute("id", surface.Id),
            new XAttribute("surfaceType", surface.SurfaceType)
        );

        if (surface.ConstructionId != null)
            surfaceElement.Add(new XAttribute("constructionIdRef", surface.ConstructionId));

        // Adjacent space references
        surfaceElement.Add(new XElement(Ns + "AdjacentSpaceId",
            new XAttribute("spaceIdRef", surface.FirstAdjacentSpaceId)));

        if (surface.SecondAdjacentSpaceId != null)
        {
            surfaceElement.Add(new XElement(Ns + "AdjacentSpaceId",
                new XAttribute("spaceIdRef", surface.SecondAdjacentSpaceId)));
        }

        // Rectangular geometry
        surfaceElement.Add(new XElement(Ns + "RectangularGeometry",
            new XElement(Ns + "Azimuth", Fmt(surface.Azimuth)),
            new XElement(Ns + "Tilt", Fmt(surface.Tilt)),
            new XElement(Ns + "Height", Fmt(surface.Height)),
            new XElement(Ns + "Width", Fmt(surface.Width)),
            BuildCartesianPoint(surface.Origin)
        ));

        // Planar geometry with full vertex loop
        surfaceElement.Add(new XElement(Ns + "PlanarGeometry",
            BuildPolyLoop(surface.Vertices)
        ));

        // CAD object reference
        if (surface.CadObjectId != null)
            surfaceElement.Add(new XElement(Ns + "CADObjectId", surface.CadObjectId));

        // Openings — coordinates are projected into the parent surface's 2D frame per gbXML spec.
        foreach (var opening in surface.Openings)
            surfaceElement.Add(BuildOpening(opening, surface));

        return surfaceElement;
    }

    private XElement BuildOpening(OpeningData opening, SurfaceData parentSurface)
    {
        var openingElement = new XElement(Ns + "Opening",
            new XAttribute("id", opening.Id),
            new XAttribute("openingType", opening.OpeningType)
        );

        if (opening.WindowTypeId != null)
            openingElement.Add(new XAttribute("windowTypeIdRef", opening.WindowTypeId));

        // RectangularGeometry: origin is the opening's bottom-left corner expressed in
        // the parent surface's 2D frame (distance along the surface from its bottom-left
        // corner viewed from outside). Order matches Revit's native exporter:
        // CartesianPoint → Width → Height.
        var (localX, localY) = GeometryUtils.GetInPlaneAxes(parentSurface.Normal);
        XYZ surfaceOrigin = parentSurface.Origin;
        XYZ openingOriginWorld = opening.Vertices.Count > 0 ? opening.Vertices[0] : opening.Origin;
        double localOriginU = (openingOriginWorld - surfaceOrigin).DotProduct(localX);
        double localOriginV = (openingOriginWorld - surfaceOrigin).DotProduct(localY);

        openingElement.Add(new XElement(Ns + "RectangularGeometry",
            Build2DCartesianPoint(localOriginU, localOriginV),
            new XElement(Ns + "Width", Fmt(opening.Width)),
            new XElement(Ns + "Height", Fmt(opening.Height))
        ));

        // PlanarGeometry: world 3D coordinates of the opening polygon. HAP reads these
        // directly to place the opening, regardless of coordinatesAbsolute — matches
        // Revit's native gbXML output.
        if (opening.Vertices.Count >= 3)
        {
            openingElement.Add(new XElement(Ns + "PlanarGeometry",
                BuildPolyLoop(opening.Vertices)
            ));
        }

        if (opening.CadObjectId != null)
            openingElement.Add(new XElement(Ns + "CADObjectId", opening.CadObjectId));

        return openingElement;
    }

    /// <summary>
    /// CartesianPoint in a surface's 2D frame (u, v, 0). Emits three Coordinates per the
    /// gbXML schema — the third is always zero for in-plane points.
    /// </summary>
    private XElement Build2DCartesianPoint(double u, double v)
    {
        double uOut = UnitConverter.ConvertLength(u, _settings.LengthUnit);
        double vOut = UnitConverter.ConvertLength(v, _settings.LengthUnit);
        return new XElement(Ns + "CartesianPoint",
            new XElement(Ns + "Coordinate", Fmt(uOut)),
            new XElement(Ns + "Coordinate", Fmt(vOut)),
            new XElement(Ns + "Coordinate", Fmt(0.0))
        );
    }

    private XElement BuildPolyLoop(List<XYZ> vertices)
    {
        var polyLoop = new XElement(Ns + "PolyLoop");
        foreach (var vertex in vertices)
            polyLoop.Add(BuildCartesianPoint(vertex));
        return polyLoop;
    }

    private XElement BuildCartesianPoint(XYZ point)
    {
        double x = UnitConverter.ConvertLength(point.X, _settings.LengthUnit);
        double y = UnitConverter.ConvertLength(point.Y, _settings.LengthUnit);
        double z = UnitConverter.ConvertLength(point.Z, _settings.LengthUnit);

        return new XElement(Ns + "CartesianPoint",
            new XElement(Ns + "Coordinate", Fmt(x)),
            new XElement(Ns + "Coordinate", Fmt(y)),
            new XElement(Ns + "Coordinate", Fmt(z))
        );
    }

    private XElement BuildConstruction(ConstructionData construction)
    {
        var element = new XElement(Ns + "Construction",
            new XAttribute("id", construction.Id),
            new XElement(Ns + "Name", construction.Name)
        );

        foreach (var layer in construction.Layers)
        {
            element.Add(new XElement(Ns + "LayerId",
                new XAttribute("layerIdRef", layer.Id)));
        }

        return element;
    }

    private XElement BuildLayer(LayerData layer)
    {
        return new XElement(Ns + "Layer",
            new XAttribute("id", layer.Id),
            new XElement(Ns + "MaterialId",
                new XAttribute("materialIdRef", layer.MaterialId))
        );
    }

    private XElement BuildMaterial(MaterialData material)
    {
        string thicknessUnit = _settings.LengthUnit;
        string conductivityUnit = _settings.LengthUnit == "Meters"
            ? "WPerMeterK" : "BtuPerHourFtF";
        string densityUnit = _settings.LengthUnit == "Meters"
            ? "KgPerCubicM" : "LbsPerCubicFt";
        string specificHeatUnit = _settings.LengthUnit == "Meters"
            ? "JPerKgK" : "BtuPerLbF";

        var element = new XElement(Ns + "Material",
            new XAttribute("id", material.Id),
            new XElement(Ns + "Name", material.Name)
        );

        if (material.Thickness > 0)
        {
            element.Add(new XElement(Ns + "Thickness",
                new XAttribute("unit", thicknessUnit),
                Fmt(material.Thickness)));
        }

        if (material.Conductivity > 0)
        {
            element.Add(new XElement(Ns + "Conductivity",
                new XAttribute("unit", conductivityUnit),
                Fmt(material.Conductivity)));
        }

        if (material.Density > 0)
        {
            element.Add(new XElement(Ns + "Density",
                new XAttribute("unit", densityUnit),
                Fmt(material.Density)));
        }

        if (material.SpecificHeat > 0)
        {
            element.Add(new XElement(Ns + "SpecificHeat",
                new XAttribute("unit", specificHeatUnit),
                Fmt(material.SpecificHeat)));
        }

        if (material.Absorptance.HasValue)
        {
            element.Add(new XElement(Ns + "Absorptance",
                new XAttribute("unit", "Fraction"),
                new XAttribute("type", "ExtIR"),
                Fmt(material.Absorptance.Value)));
        }

        // If no usable thermal data was extracted, emit a neutral R-value so HAP
        // doesn't silently substitute its own default and flag the material.
        bool hasThermalData = material.Conductivity > 0
            || (material.Thickness > 0 && material.Density > 0);
        if (!hasThermalData)
        {
            string rValueUnit = _settings.LengthUnit == "Meters"
                ? "SquareMeterKPerW" : "SquareFootDegFHourPerBTU";
            double defaultR = _settings.LengthUnit == "Meters" ? 0.18 : 1.0;
            element.Add(new XElement(Ns + "R-value",
                new XAttribute("unit", rValueUnit),
                Fmt(defaultR)));
        }

        return element;
    }

    private XElement BuildWindowType(WindowTypeData wt)
    {
        string uValueUnit = _settings.LengthUnit == "Meters"
            ? "WPerSquareMeterK" : "BtuPerHourSquareFtF";

        var element = new XElement(Ns + "WindowType",
            new XAttribute("id", wt.Id),
            new XElement(Ns + "Name", wt.Name),
            new XElement(Ns + "U-value",
                new XAttribute("unit", uValueUnit),
                Fmt(wt.UValue)),
            new XElement(Ns + "SolarHeatGainCoeff",
                new XAttribute("unit", "Fraction"),
                Fmt(wt.SolarHeatGainCoeff))
        );

        if (wt.VisibleTransmittance.HasValue)
        {
            element.Add(new XElement(Ns + "Transmittance",
                new XAttribute("unit", "Fraction"),
                new XAttribute("type", "Visible"),
                Fmt(wt.VisibleTransmittance.Value)));
        }

        return element;
    }

    private XElement BuildZone(ZoneData zone)
    {
        return new XElement(Ns + "Zone",
            new XAttribute("id", zone.Id),
            new XElement(Ns + "Name", zone.Name)
        );
    }

    private XElement BuildDocumentHistory()
    {
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        return new XElement(Ns + "DocumentHistory",
            new XElement(Ns + "ProgramInfo",
                new XAttribute("id", "program-1"),
                new XElement(Ns + "ProductName", "Revit gbXML Exporter for HAP"),
                new XElement(Ns + "CompanyName", "The BIM Coordinator (github.com/DaltonGOO/revit-gbxml)")
            ),
            new XElement(Ns + "PersonInfo",
                new XAttribute("id", "person-1"),
                new XElement(Ns + "FirstName", Environment.UserName)
            ),
            new XElement(Ns + "CreatedBy",
                new XAttribute("programId", "program-1"),
                new XAttribute("personId", "person-1"),
                new XAttribute("date", now)
            )
        );
    }

    private static string Fmt(double value)
        => value.ToString("G10", CultureInfo.InvariantCulture);
}
