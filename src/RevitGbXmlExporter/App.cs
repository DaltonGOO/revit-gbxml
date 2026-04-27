using Autodesk.Revit.UI;

namespace RevitGbXmlExporter;

public class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        var panel = application.CreateRibbonPanel("gbXML for HAP");

        var commandData = new PushButtonData(
            "ExportGbXml",
            "Export\ngbXML",
            typeof(App).Assembly.Location,
            typeof(ExportCommand).FullName)
        {
            ToolTip = "Export the current model to gbXML format optimized for HAP Carrier import.",
            LongDescription = "Generates a gbXML file from the Revit model with options to control " +
                              "which elements are exported. The output is validated for compatibility " +
                              "with Carrier HAP energy analysis software."
        };

        panel.AddItem(commandData);

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}
