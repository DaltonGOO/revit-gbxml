using Autodesk.Revit.DB;
using RevitGbXmlExporter.Models;
using RevitGbXmlExporter.Utilities;

namespace RevitGbXmlExporter.Export;

/// <summary>
/// Orchestrates the full gbXML export pipeline:
/// 1. Collect spaces and levels
/// 2. Analyze surfaces and detect adjacency
/// 3. Map constructions and materials
/// 4. Write the gbXML document
/// </summary>
public class GbXmlExportEngine
{
    private readonly Document _doc;
    private readonly ExportSettings _settings;

    public GbXmlExportEngine(Document doc, ExportSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    public ExportResult Export()
    {
        var log = new ExportLog();
        log.Info("Export", $"Starting export to {_settings.OutputFilePath}");

        try
        {
            return ExportInternal(log);
        }
        catch (Exception ex)
        {
            log.Error("Export", $"Export failed: {ex.GetType().Name}: {ex.Message}");
            WriteLog(log);
            throw;
        }
    }

    private ExportResult ExportInternal(ExportLog log)
    {
        // Resolve which doc supplies spatial elements vs. geometry. With UseLinkedModel
        // off, both = host. With it on, geometry comes from the link; spaces come from
        // the host (default, "spaces created in user's model") or from the link.
        var (spatialDoc, geomDoc, geomTransform) = ResolveDataSources(log);

        var spaceCollector = new SpaceCollector(spatialDoc, _settings, log);
        var (spaces, levels, zones) = spaceCollector.Collect();

        if (spaces.Count == 0)
        {
            string msg = _settings.SpatialSource == SpatialElementSource.Rooms
                ? "No bounded rooms found in the model. Ensure rooms are placed and properly bounded."
                : "No bounded MEP spaces found in the model. Ensure spaces are placed and properly bounded.";
            log.Error("Export", msg);
            throw new InvalidOperationException(msg);
        }

        var buildingData = new BuildingData
        {
            BuildingName = GetBuildingName(),
            BuildingType = _settings.BuildingType,
            Levels = levels,
            Spaces = spaces,
            Zones = zones
        };

        if (_settings.ExportGeometry)
        {
            var spaceIdMap = spaceCollector.GetSpaceIdMap(spaces);
            var surfaceAnalyzer = new SurfaceAnalyzer(
                spatialDoc, geomDoc, geomTransform, _settings, spaceIdMap, log);
            var (surfaces, plenums) = surfaceAnalyzer.Analyze(spaces);
            buildingData.Surfaces = surfaces;

            // Plenums are synthesized between orphan ceiling/floor pairs and need to
            // appear in the building's <Space> list so HAP can resolve adjacencies.
            if (plenums.Count > 0)
                buildingData.Spaces.AddRange(plenums);

            var constructionMapper = new ConstructionMapper(geomDoc, _settings);
            var (constructions, materials, windowTypes) =
                constructionMapper.MapConstructions(buildingData.Surfaces);
            buildingData.Constructions = constructions;
            buildingData.Materials = materials;
            buildingData.WindowTypes = windowTypes;
        }
        else
        {
            log.Info("Export", "ExportGeometry=false — skipping surfaces, openings, constructions.");
        }

        ExtractProjectLocation(buildingData, log);

        var writer = new GbXmlWriter(_settings);
        var document = writer.Build(buildingData);

        string directory = System.IO.Path.GetDirectoryName(_settings.OutputFilePath) ?? "";
        if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            System.IO.Directory.CreateDirectory(directory);

        document.Save(_settings.OutputFilePath);
        log.Info("Export",
            $"Wrote {spaces.Count} spaces, {levels.Count} levels, " +
            $"{buildingData.Surfaces.Count} surfaces.");

        string logPath = WriteLog(log);

        return new ExportResult
        {
            OutputPath = _settings.OutputFilePath,
            LogPath = logPath,
            SpaceCount = spaces.Count,
            SurfaceCount = buildingData.Surfaces.Count,
            WarningCount = log.WarnCount,
            ErrorCount = log.ErrorCount
        };
    }

    private string WriteLog(ExportLog log)
    {
        string logPath = _settings.OutputFilePath + ".log.txt";
        try
        {
            log.WriteTo(logPath);
        }
        catch
        {
            // Log write is best-effort; don't fail the export.
        }
        return logPath;
    }

    /// <summary>
    /// Returns (spatialDoc, geomDoc, geomTransform) — the docs to read spaces and
    /// geometry from, plus the Transform that brings a point in geomDoc's internal
    /// coords into spatialDoc's frame (Identity when both are the same doc).
    /// </summary>
    private (Document spatialDoc, Document geomDoc, Transform geomTransform)
        ResolveDataSources(ExportLog log)
    {
        if (!_settings.UseLinkedModel || _settings.LinkedInstanceId == null)
            return (_doc, _doc, Transform.Identity);

        var instanceId = new ElementId(_settings.LinkedInstanceId.Value);
        if (_doc.GetElement(instanceId) is not RevitLinkInstance linkInstance)
        {
            log.Warn("Source",
                $"Linked model instance {instanceId.Value} not found — falling back to current model.");
            return (_doc, _doc, Transform.Identity);
        }

        var linkedDoc = linkInstance.GetLinkDocument();
        if (linkedDoc == null)
        {
            log.Warn("Source",
                $"Linked model '{linkInstance.Name}' is not loaded — falling back to current model.");
            return (_doc, _doc, Transform.Identity);
        }

        Transform linkTransform = linkInstance.GetTotalTransform();
        Document spatialDoc = _settings.SpacesFromCurrentModel ? _doc : linkedDoc;

        // When spaces come from current and geometry from link, host rooms live in
        // _doc's coords and linked geometry needs the link transform to align with them.
        // When everything comes from the link, both live in linkedDoc's coords already
        // — geomTransform is Identity in that case (no double-transform).
        Transform geomTransform = spatialDoc == linkedDoc ? Transform.Identity : linkTransform;

        log.Info("Source",
            $"Using linked model '{linkedDoc.Title}'. Spaces from: " +
            $"{(spatialDoc == _doc ? "current model" : "link")}.");
        return (spatialDoc, linkedDoc, geomTransform);
    }

    private string GetBuildingName()
    {
        var projectInfo = _doc.ProjectInformation;
        if (projectInfo != null)
        {
            string? name = projectInfo.Name;
            if (!string.IsNullOrWhiteSpace(name) && name != "Project Name")
                return name;

            string? buildingName = projectInfo.BuildingName;
            if (!string.IsNullOrWhiteSpace(buildingName))
                return buildingName;
        }

        string title = _doc.Title;
        return !string.IsNullOrWhiteSpace(title) ? title : "Building";
    }

    private void ExtractProjectLocation(BuildingData data, ExportLog log)
    {
        try
        {
            var siteLocation = _doc.SiteLocation;
            if (siteLocation != null)
            {
                data.Latitude = siteLocation.Latitude * 180.0 / Math.PI;
                data.Longitude = siteLocation.Longitude * 180.0 / Math.PI;
                data.Elevation = siteLocation.Elevation;
            }
        }
        catch (Exception ex)
        {
            log.Warn("Location", $"Could not read project site location: {ex.Message}");
        }
    }
}

public class ExportResult
{
    public string OutputPath { get; set; } = string.Empty;
    public string LogPath { get; set; } = string.Empty;
    public int SpaceCount { get; set; }
    public int SurfaceCount { get; set; }
    public int WarningCount { get; set; }
    public int ErrorCount { get; set; }
}
