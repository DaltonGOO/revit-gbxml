using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitGbXmlExporter.Export;
using RevitGbXmlExporter.Models;
using RevitGbXmlExporter.UI;

namespace RevitGbXmlExporter;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public class ExportCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument.Document;

        if (doc.IsFamilyDocument)
        {
            TaskDialog.Show("gbXML Export", "Cannot export a family document. Please open a project.");
            return Result.Failed;
        }

        // Show the export settings window
        var window = new ExportWindow(doc);
        var result = window.ShowDialog();

        if (result != true)
            return Result.Cancelled;

        var settings = window.Settings;

        try
        {
            var engine = new GbXmlExportEngine(doc, settings);
            var exportResult = engine.Export();

            string summary =
                $"Export completed.\n\n" +
                $"File: {exportResult.OutputPath}\n" +
                $"Spaces: {exportResult.SpaceCount}\n" +
                $"Surfaces: {exportResult.SurfaceCount}\n" +
                $"Warnings: {exportResult.WarningCount}\n" +
                $"Errors: {exportResult.ErrorCount}\n\n" +
                $"Log: {exportResult.LogPath}";

            TaskDialog.Show("gbXML Export", summary);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("gbXML Export Error",
                $"Export failed:\n\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}");
            return Result.Failed;
        }
    }
}
