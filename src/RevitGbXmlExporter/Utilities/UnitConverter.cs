namespace RevitGbXmlExporter.Utilities;

/// <summary>
/// Converts from Revit internal units (feet, Fahrenheit) to gbXML target units.
/// Revit 2025+ uses ForgeTypeId for units; internal storage is always feet for length.
/// </summary>
public static class UnitConverter
{
    // Length: Revit internal = feet
    public static double ConvertLength(double feetValue, string targetUnit) => targetUnit switch
    {
        "Meters" => feetValue * 0.3048,
        "Centimeters" => feetValue * 30.48,
        "Millimeters" => feetValue * 304.8,
        "Inches" => feetValue * 12.0,
        "Kilometers" => feetValue * 0.0003048,
        "Miles" => feetValue / 5280.0,
        _ => feetValue // Feet
    };

    // Area: Revit internal = square feet
    public static double ConvertArea(double sqFeetValue, string targetUnit) => targetUnit switch
    {
        "SquareMeters" => sqFeetValue * 0.092903,
        "SquareCentimeters" => sqFeetValue * 929.03,
        "SquareMillimeters" => sqFeetValue * 92903.04,
        "SquareInches" => sqFeetValue * 144.0,
        "SquareKilometers" => sqFeetValue * 9.2903e-8,
        _ => sqFeetValue // SquareFeet
    };

    // Volume: Revit internal = cubic feet
    public static double ConvertVolume(double cuFeetValue, string targetUnit) => targetUnit switch
    {
        "CubicMeters" => cuFeetValue * 0.0283168,
        "CubicCentimeters" => cuFeetValue * 28316.8,
        "CubicMillimeters" => cuFeetValue * 28316846.6,
        "CubicInches" => cuFeetValue * 1728.0,
        _ => cuFeetValue // CubicFeet
    };

    // Conductivity: Revit stores as W/(m*K), gbXML wants BTU/(hr*ft*F) for imperial
    public static double ConductivityToImperial(double wPerMK) => wPerMK * 0.5778;

    // Density: Revit stores as kg/m^3, gbXML wants lb/ft^3 for imperial
    public static double DensityToImperial(double kgPerM3) => kgPerM3 * 0.062428;

    // Specific heat: Revit stores as J/(kg*K), gbXML wants BTU/(lb*F) for imperial
    public static double SpecificHeatToImperial(double jPerKgK) => jPerKgK * 0.000238846;

    // Thickness: Revit internal is feet, convert if needed
    public static double ThicknessToTarget(double feet, string lengthUnit) => ConvertLength(feet, lengthUnit);

    // U-value: Revit stores as W/(m^2*K), gbXML wants BTU/(hr*ft^2*F) for imperial
    public static double UValueToImperial(double wPerM2K) => wPerM2K * 0.17611;

    // Temperature is not converted (HAP works with whatever unit is declared in the header)
}
