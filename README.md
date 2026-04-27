# Revit gbXML Exporter for HAP

A simple, open-source Revit add-in that exports gbXML optimized for **Carrier HAP** energy analysis.

> **Status: beta.** This is early-stage software. It works on the models I've tested, but corners are still being shaken out — expect rough edges and please [report issues](https://github.com/DaltonGOO/revit-gbxml/issues) when you find them. The output should always be reviewed before being used for design decisions (see [Disclaimer](#disclaimer) below).

Revit's built-in gbXML export has known compatibility issues with HAP. This tool writes a leaner gbXML focused on what HAP actually consumes — clean room/space geometry, paired interior surfaces with adjacency, openings positioned correctly in the surface frame, and synthesized plenums between floors.

## Compatibility

- Autodesk Revit **2025** or **2026** (.NET 8, Windows)
- Carrier HAP v6.2 or newer (gbXML v6.01 / v8.01 import)

## Install

1. Download `RevitGbXmlExporter.dll` and `RevitGbXmlExporter.addin` from the [latest release](https://github.com/DaltonGOO/revit-gbxml/releases) — or build from source (see below).
2. Copy **both** files into your Revit add-ins folder. Open the Run dialog (`Win+R`) and paste:
   - **Revit 2025**: `%APPDATA%\Autodesk\Revit\Addins\2025`
   - **Revit 2026**: `%APPDATA%\Autodesk\Revit\Addins\2026`

   The same DLL works in both — drop a copy in each folder if you use both versions.
3. Restart Revit. The exporter shows up under the **Add-Ins** tab → **External Tools** → **gbXML Exporter for HAP**.

## Usage

1. Open the project (or open it with a linked architectural model already loaded).
2. Run **gbXML Exporter for HAP**.
3. Pick what to export and click **Export**. A `.xml` and a `.xml.log.txt` are written next to your project file by default.

### Workflows

- **Current model only** — pulls everything from the active document. Default.
- **Linked architectural model** — check *Pull geometry from a linked model*, pick the link, and decide whether spaces also come from the link or from the current model. The common MEP workflow is rooms/spaces in the current model + walls/openings from a linked architectural model. For this to work, the link instance must have **Room Bounding** turned on.

### What gets exported

- Rooms or MEP Spaces (your choice)
- Walls (interior + exterior, with adjacency), floors, ceilings, roofs, slabs
- Doors and windows hosted on walls; skylights on roofs
- Plenum spaces synthesized between rooms with vertical gaps (off by default in HAP terms — toggle in the dialog)
- Constructions, materials, window thermal properties (each optional)

Every checkbox in the dialog gates exactly that category — uncheck what you don't want, and it won't be in the output.

## Build from source

```sh
git clone https://github.com/DaltonGOO/revit-gbxml.git
cd revit-gbxml
dotnet build src/RevitGbXmlExporter/RevitGbXmlExporter.csproj -c Release

# Build for Revit 2026 instead:
dotnet build src/RevitGbXmlExporter/RevitGbXmlExporter.csproj -c Release /p:RevitVersion=2026
```

The build expects Revit installed at `C:\Program Files\Autodesk\Revit <version>`. Override with `/p:RevitApiPath=...` if yours lives elsewhere.

The DLL lands in `src/RevitGbXmlExporter/bin/Release/net8.0-windows/`.

## Disclaimer

This tool is provided **as-is, without warranty of any kind**. The output is your responsibility — always verify the gbXML content and the resulting HAP loads/zones before relying on them for design decisions. The author is not liable for any errors, omissions, or consequences of using this tool.

## Contributing

Issues and pull requests are welcome.

- **Bug report?** Open an [issue](https://github.com/DaltonGOO/revit-gbxml/issues) describing the model and the unexpected behavior. A small example `.rvt` and the generated `.xml.log.txt` are the fastest way to a fix.
- **Feature idea?** Open an issue first so we can talk about scope before you write code.
- **Pull request?** Branch from `main`, keep the change focused, build cleanly against Revit 2025, and explain the *why* in the description.

The intent of this tool is to stay **simple and easy** — please favor focused, surgical changes over large refactors.

## Acknowledgments

Co-developed by [@DaltonGOO](https://github.com/DaltonGOO) and Claude (Anthropic), via [Claude Code](https://claude.ai/code) — design decisions, gbXML/HAP compatibility tuning, and the code itself came out of pair-programming sessions.

## License

[MIT](LICENSE)
