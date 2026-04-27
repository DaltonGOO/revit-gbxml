using Autodesk.Revit.DB;
using RevitGbXmlExporter.Models;
using RevitGbXmlExporter.Utilities;

namespace RevitGbXmlExporter.Export;

/// <summary>
/// Extracts construction assemblies, material layers, and window types from Revit elements.
/// Maps Revit compound structures to gbXML Construction/Layer/Material hierarchy.
/// </summary>
public class ConstructionMapper
{
    private readonly Document _geomDoc;
    private readonly ExportSettings _settings;
    private readonly Dictionary<ElementId, string> _constructionIdMap = new();
    private readonly Dictionary<ElementId, string> _materialIdMap = new();
    private readonly Dictionary<ElementId, string> _windowTypeIdMap = new();

    private readonly List<ConstructionData> _constructions = [];
    private readonly List<MaterialData> _materials = [];
    private readonly List<WindowTypeData> _windowTypes = [];

    private int _layerCounter;

    public ConstructionMapper(Document geomDoc, ExportSettings settings)
    {
        _geomDoc = geomDoc;
        _settings = settings;
    }

    /// <summary>
    /// Processes all surfaces and their openings, extracting construction and material data.
    /// Also assigns construction IDs to each surface.
    /// </summary>
    public (List<ConstructionData> Constructions, List<MaterialData> Materials, List<WindowTypeData> WindowTypes)
        MapConstructions(List<SurfaceData> surfaces)
    {
        foreach (var surface in surfaces)
        {
            if (_settings.ExportConstructions && surface.HostElementId != ElementId.InvalidElementId)
            {
                surface.ConstructionId = GetOrCreateConstruction(surface.HostElementId);
            }

            if (_settings.ExportWindowTypes)
            {
                foreach (var opening in surface.Openings)
                {
                    if (opening.WindowTypeId != null)
                    {
                        // Parse the symbol ID from the window type reference
                        string symbolIdStr = opening.WindowTypeId.Replace("wtype-", "");
                        if (long.TryParse(symbolIdStr, out long symbolIdVal))
                        {
                            var symbolId = new ElementId(symbolIdVal);
                            opening.WindowTypeId = GetOrCreateWindowType(symbolId);
                        }
                    }
                }
            }
            else
            {
                // Window types disabled — drop the placeholder so the writer doesn't
                // emit a dangling windowTypeIdRef pointing to an undefined WindowType.
                foreach (var opening in surface.Openings)
                    opening.WindowTypeId = null;
            }
        }

        return (_constructions, _materials, _windowTypes);
    }

    private string? GetOrCreateConstruction(ElementId hostElementId)
    {
        Element? host = _geomDoc.GetElement(hostElementId);
        if (host == null) return null;

        // Get the type element
        ElementId typeId = host.GetTypeId();
        if (typeId == ElementId.InvalidElementId) return null;

        // Check cache
        if (_constructionIdMap.TryGetValue(typeId, out string? existingId))
            return existingId;

        Element? typeElement = _geomDoc.GetElement(typeId);
        if (typeElement == null) return null;

        CompoundStructure? cs = GetCompoundStructure(typeElement);
        if (cs == null)
        {
            // No compound structure - create a simple single-layer construction
            return CreateSimpleConstruction(typeId, typeElement);
        }

        string constructionId = $"construction-{typeId.Value}";
        var layers = new List<LayerData>();

        foreach (CompoundStructureLayer layer in cs.GetLayers())
        {
            string layerId = $"layer-{++_layerCounter}";
            string? materialId = GetOrCreateMaterial(layer.MaterialId, layer.Width);

            if (materialId != null)
            {
                layers.Add(new LayerData
                {
                    Id = layerId,
                    MaterialId = materialId
                });
            }
        }

        if (layers.Count == 0) return null;

        _constructions.Add(new ConstructionData
        {
            Id = constructionId,
            Name = typeElement.Name ?? $"Construction {typeId.Value}",
            Layers = layers
        });

        _constructionIdMap[typeId] = constructionId;
        return constructionId;
    }

    private string? CreateSimpleConstruction(ElementId typeId, Element typeElement)
    {
        string constructionId = $"construction-{typeId.Value}";

        // Try to get any material from the element
        ICollection<ElementId> materialIds = typeElement.GetMaterialIds(false);
        if (materialIds.Count == 0)
        {
            _constructionIdMap[typeId] = constructionId;
            _constructions.Add(new ConstructionData
            {
                Id = constructionId,
                Name = typeElement.Name ?? "Unknown"
            });
            return constructionId;
        }

        string layerId = $"layer-{++_layerCounter}";
        var materialId = materialIds.First();
        string? gbxmlMaterialId = GetOrCreateMaterial(materialId, 0);

        var layers = gbxmlMaterialId != null
            ? new List<LayerData> { new() { Id = layerId, MaterialId = gbxmlMaterialId } }
            : new List<LayerData>();

        _constructions.Add(new ConstructionData
        {
            Id = constructionId,
            Name = typeElement.Name ?? "Unknown",
            Layers = layers
        });

        _constructionIdMap[typeId] = constructionId;
        return constructionId;
    }

    private string? GetOrCreateMaterial(ElementId materialId, double layerWidth)
    {
        if (materialId == ElementId.InvalidElementId) return null;

        if (_materialIdMap.TryGetValue(materialId, out string? existingId))
            return existingId;

        var material = _geomDoc.GetElement(materialId) as Material;
        if (material == null) return null;

        string gbxmlId = $"material-{materialId.Value}";

        double conductivity = 0;
        double density = 0;
        double specificHeat = 0;
        double? absorptance = null;

        // Extract thermal properties from the material's thermal asset
        ElementId thermalAssetId = material.ThermalAssetId;
        if (thermalAssetId != ElementId.InvalidElementId)
        {
            var pse = _geomDoc.GetElement(thermalAssetId) as PropertySetElement;
            if (pse != null)
            {
                var thermalAsset = pse.GetThermalAsset();
                if (thermalAsset != null)
                {
                    conductivity = thermalAsset.ThermalConductivity; // W/(m*K)
                    density = thermalAsset.Density; // kg/m^3
                    specificHeat = thermalAsset.SpecificHeat; // J/(kg*K)
                    absorptance = thermalAsset.Emissivity > 0
                        ? thermalAsset.Emissivity
                        : null;
                }
            }
        }

        // Convert to target units
        bool useImperial = _settings.LengthUnit == "Feet" || _settings.LengthUnit == "Inches";

        _materials.Add(new MaterialData
        {
            Id = gbxmlId,
            Name = material.Name ?? "Unknown Material",
            Thickness = UnitConverter.ConvertLength(layerWidth, _settings.LengthUnit),
            Conductivity = useImperial ? UnitConverter.ConductivityToImperial(conductivity) : conductivity,
            Density = useImperial ? UnitConverter.DensityToImperial(density) : density,
            SpecificHeat = useImperial ? UnitConverter.SpecificHeatToImperial(specificHeat) : specificHeat,
            Absorptance = absorptance
        });

        _materialIdMap[materialId] = gbxmlId;
        return gbxmlId;
    }

    private string GetOrCreateWindowType(ElementId symbolId)
    {
        if (_windowTypeIdMap.TryGetValue(symbolId, out string? existingId))
            return existingId;

        var symbol = _geomDoc.GetElement(symbolId) as FamilySymbol;
        string wtId = $"wtype-{symbolId.Value}";

        double uValue = 0;
        double shgc = 0;
        double? vt = null;

        if (symbol != null)
        {
            // Try analytical thermal properties
            uValue = GetParamDouble(symbol, BuiltInParameter.ANALYTICAL_HEAT_TRANSFER_COEFFICIENT) ?? 0;
            shgc = GetParamDouble(symbol, BuiltInParameter.ANALYTICAL_SOLAR_HEAT_GAIN_COEFFICIENT) ?? 0;
            vt = GetParamDouble(symbol, BuiltInParameter.ANALYTICAL_VISUAL_LIGHT_TRANSMITTANCE);

            // Fall back to common shared parameter names if analytical params are missing
            if (uValue <= 0)
                uValue = GetNamedParamDouble(symbol, "Heat Transfer Coefficient", "U-Factor", "U-Value") ?? 0.5;
            if (shgc <= 0)
                shgc = GetNamedParamDouble(symbol, "Solar Heat Gain Coefficient", "SHGC") ?? 0.4;
        }

        bool useImperial = _settings.LengthUnit == "Feet" || _settings.LengthUnit == "Inches";

        _windowTypes.Add(new WindowTypeData
        {
            Id = wtId,
            Name = symbol?.FamilyName != null ? $"{symbol.FamilyName}: {symbol.Name}" : "Unknown Window",
            UValue = useImperial ? UnitConverter.UValueToImperial(uValue) : uValue,
            SolarHeatGainCoeff = shgc,
            VisibleTransmittance = vt
        });

        _windowTypeIdMap[symbolId] = wtId;
        return wtId;
    }

    private static CompoundStructure? GetCompoundStructure(Element typeElement) => typeElement switch
    {
        WallType wt => wt.GetCompoundStructure(),
        FloorType ft => ft.GetCompoundStructure(),
        RoofType rt => rt.GetCompoundStructure(),
        CeilingType ct => ct.GetCompoundStructure(),
        _ => null
    };

    private static double? GetParamDouble(Element element, BuiltInParameter param)
    {
        var p = element.get_Parameter(param);
        if (p == null || !p.HasValue) return null;
        double val = p.AsDouble();
        return val > 0 ? val : null;
    }

    private static double? GetNamedParamDouble(Element element, params string[] names)
    {
        foreach (string name in names)
        {
            var p = element.LookupParameter(name);
            if (p != null && p.HasValue && p.StorageType == StorageType.Double)
            {
                double val = p.AsDouble();
                if (val > 0) return val;
            }
        }
        return null;
    }
}
