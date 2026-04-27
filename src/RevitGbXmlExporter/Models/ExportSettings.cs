namespace RevitGbXmlExporter.Models;

public class ExportSettings
{
    // Data source. When UseLinkedModel is true, geometry (walls, floors, ceilings,
    // openings) comes from the chosen RevitLinkInstance. SpacesFromCurrentModel
    // controls whether rooms/spaces also come from the link or from the host doc
    // (the common workflow: spaces in user's model, geometry from a linked arch model).
    public bool UseLinkedModel { get; set; }
    public long? LinkedInstanceId { get; set; }
    public bool SpacesFromCurrentModel { get; set; } = true;

    // Spatial elements — always on.
    public SpatialElementSource SpatialSource { get; set; } = SpatialElementSource.Rooms;
    public bool ExportPlenumSpaces { get; set; } = true;

    // Master switch for all geometry (walls, floors, ceilings, openings, constructions).
    public bool ExportGeometry { get; set; } = true;

    // Sub-filters applied only when ExportGeometry is true.
    public bool ExportExteriorWalls { get; set; } = true;
    public bool ExportInteriorWalls { get; set; } = true;
    public bool ExportRoofs { get; set; } = true;
    public bool ExportFloors { get; set; } = true;
    public bool ExportCeilings { get; set; } = true;
    public bool ExportUndergroundSurfaces { get; set; } = true;
    public bool ExportShadeSurfaces { get; set; }

    public bool ExportWindows { get; set; } = true;
    public bool ExportDoors { get; set; } = true;
    public bool ExportSkylights { get; set; } = true;

    public bool ExportConstructions { get; set; } = true;
    public bool ExportWindowTypes { get; set; } = true;
    public bool ExportSpaceLoads { get; set; }

    // Building settings
    public string BuildingType { get; set; } = "Office";
    public string TemperatureUnit { get; set; } = "F";
    public string LengthUnit { get; set; } = "Feet";
    public string AreaUnit { get; set; } = "SquareFeet";
    public string VolumeUnit { get; set; } = "CubicFeet";

    // Output
    public string OutputFilePath { get; set; } = string.Empty;

    public bool ShouldExportSurfaceType(string surfaceType) => surfaceType switch
    {
        "ExteriorWall" => ExportExteriorWalls,
        "InteriorWall" => ExportInteriorWalls,
        "Roof" => ExportRoofs,
        "InteriorFloor" or "ExposedFloor" or "SlabOnGrade" or "RaisedFloor" => ExportFloors,
        "Ceiling" => ExportCeilings,
        "UndergroundWall" or "UndergroundSlab" or "UndergroundCeiling" => ExportUndergroundSurfaces,
        "Shade" => ExportShadeSurfaces,
        "Air" => true,
        _ => true
    };

    public bool ShouldExportOpening(string openingType) => openingType switch
    {
        "FixedWindow" or "OperableWindow" => ExportWindows,
        "FixedSkylight" or "OperableSkylight" => ExportSkylights,
        "NonSlidingDoor" or "SlidingDoor" => ExportDoors,
        _ => true
    };
}

public enum SpatialElementSource
{
    Rooms,
    MEPSpaces
}

public static class BuildingTypes
{
    public static readonly string[] All =
    [
        "Office",
        "Retail",
        "Hotel",
        "Motel",
        "Dormitory",
        "HospitalOrHealthcare",
        "SchoolOrUniversity",
        "Library",
        "Museum",
        "Warehouse",
        "Manufacturing",
        "Workshop",
        "Assembly",
        "ConventionCenter",
        "DiningBarLoungeOrLeisure",
        "DiningCafeFastFood",
        "DiningFamily",
        "ExerciseCenter",
        "FireStation",
        "Gymnasium",
        "MultiFamily",
        "SingleFamily",
        "MotionPictureTheatre",
        "ParkingGarage",
        "Penitentiary",
        "PerformingArtsTheatre",
        "PoliceStation",
        "PostOffice",
        "ReligiousBuilding",
        "SportsArena",
        "TownHall",
        "Transportation"
    ];
}
