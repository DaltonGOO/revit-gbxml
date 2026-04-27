using System.IO;
using System.Windows;
using Autodesk.Revit.DB;
using Microsoft.Win32;
using RevitGbXmlExporter.Models;

namespace RevitGbXmlExporter.UI;

public partial class ExportWindow : Window
{
    private readonly Document _doc;

    public ExportSettings Settings { get; private set; } = new();

    public ExportWindow(Document doc)
    {
        _doc = doc;
        InitializeComponent();
        PopulateDefaults();
    }

    private record LinkInstanceItem(long InstanceId, string DisplayName);

    private void PopulateDefaults()
    {
        CbBuildingType.ItemsSource = BuildingTypes.All;
        CbBuildingType.SelectedItem = "Office";

        CbTemperature.ItemsSource = new[] { "F", "C", "K", "R" };
        CbTemperature.SelectedItem = "F";

        CbLength.ItemsSource = new[] { "Feet", "Meters", "Inches", "Centimeters", "Millimeters" };
        CbLength.SelectedItem = "Feet";

        PopulateLinkInstances();

        // Default file path based on the Revit document
        string docPath = _doc.PathName;
        if (!string.IsNullOrEmpty(docPath))
        {
            string dir = Path.GetDirectoryName(docPath) ?? "";
            string name = Path.GetFileNameWithoutExtension(docPath);
            TbFilePath.Text = Path.Combine(dir, $"{name}.xml");
        }
        else
        {
            TbFilePath.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "export.xml");
        }
    }

    private void PopulateLinkInstances()
    {
        var items = new List<LinkInstanceItem>();
        var collector = new FilteredElementCollector(_doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>();

        foreach (var li in collector)
        {
            var linkedDoc = li.GetLinkDocument();
            if (linkedDoc == null) continue; // unloaded link
            string title = linkedDoc.Title;
            string instanceName = li.Name;
            string label = string.IsNullOrEmpty(instanceName) || instanceName == title
                ? title
                : $"{title} — {instanceName}";
            items.Add(new LinkInstanceItem(li.Id.Value, label));
        }

        CbLinkInstance.ItemsSource = items;
        if (items.Count > 0) CbLinkInstance.SelectedIndex = 0;

        if (items.Count == 0)
        {
            CbUseLink.IsEnabled = false;
            CbUseLink.ToolTip = "No loaded Revit links in this project.";
        }
    }

    private void OnUseLinkChanged(object sender, RoutedEventArgs e)
    {
        bool on = CbUseLink.IsChecked == true;
        CbLinkInstance.IsEnabled = on;
        CbSpacesFromCurrent.IsEnabled = on;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "gbXML Files (*.xml)|*.xml|All Files (*.*)|*.*",
            DefaultExt = ".xml",
            FileName = Path.GetFileName(TbFilePath.Text),
            InitialDirectory = Path.GetDirectoryName(TbFilePath.Text) ?? ""
        };

        if (dialog.ShowDialog() == true)
            TbFilePath.Text = dialog.FileName;
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TbFilePath.Text))
        {
            MessageBox.Show("Please specify an output file path.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Map area/volume units from the length unit selection
        string lengthUnit = CbLength.SelectedItem?.ToString() ?? "Feet";
        string areaUnit = lengthUnit switch
        {
            "Meters" => "SquareMeters",
            "Centimeters" => "SquareCentimeters",
            "Millimeters" => "SquareMillimeters",
            "Inches" => "SquareInches",
            _ => "SquareFeet"
        };
        string volumeUnit = lengthUnit switch
        {
            "Meters" => "CubicMeters",
            "Centimeters" => "CubicCentimeters",
            "Millimeters" => "CubicMillimeters",
            "Inches" => "CubicInches",
            _ => "CubicFeet"
        };

        bool useLink = CbUseLink.IsChecked == true;
        long? linkedInstanceId = useLink && CbLinkInstance.SelectedItem is LinkInstanceItem li
            ? li.InstanceId
            : null;

        if (useLink && linkedInstanceId == null)
        {
            MessageBox.Show("Select a linked model from the dropdown, or uncheck \"Pull geometry from a linked model\".",
                "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings = new ExportSettings
        {
            UseLinkedModel = useLink,
            LinkedInstanceId = linkedInstanceId,
            SpacesFromCurrentModel = CbSpacesFromCurrent.IsChecked == true,

            SpatialSource = RbRooms.IsChecked == true
                ? SpatialElementSource.Rooms
                : SpatialElementSource.MEPSpaces,
            ExportPlenumSpaces = CbPlenumSpaces.IsChecked == true,
            ExportGeometry = true,

            ExportExteriorWalls = CbExteriorWalls.IsChecked == true,
            ExportInteriorWalls = CbInteriorWalls.IsChecked == true,
            ExportRoofs = CbRoofs.IsChecked == true,
            ExportFloors = CbFloors.IsChecked == true,
            ExportCeilings = CbCeilings.IsChecked == true,
            ExportUndergroundSurfaces = CbUnderground.IsChecked == true,
            ExportShadeSurfaces = CbShade.IsChecked == true,

            ExportWindows = CbWindows.IsChecked == true,
            ExportDoors = CbDoors.IsChecked == true,
            ExportSkylights = CbSkylights.IsChecked == true,

            ExportConstructions = CbConstructions.IsChecked == true,
            ExportWindowTypes = CbWindowTypes.IsChecked == true,
            ExportSpaceLoads = CbSpaceLoads.IsChecked == true,

            BuildingType = CbBuildingType.SelectedItem?.ToString() ?? "Office",
            TemperatureUnit = CbTemperature.SelectedItem?.ToString() ?? "F",
            LengthUnit = lengthUnit,
            AreaUnit = areaUnit,
            VolumeUnit = volumeUnit,

            OutputFilePath = TbFilePath.Text
        };

        DialogResult = true;
        Close();
    }
}
