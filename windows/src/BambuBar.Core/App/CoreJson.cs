using System.Text.Json.Serialization;
using BambuBar.Core.Model;

namespace BambuBar.Core.App;

/// <summary>
/// Serializer metadata made at compile time for what the app reads at launch (the printers in the settings), so the
/// first read doesn't build it by reflection (150 ms of the launch on a slow machine). Default options, as
/// <see cref="System.Text.Json.JsonSerializer"/>'s, so the files read exactly as before.
/// </summary>
[JsonSerializable(typeof(List<PrinterConfig>))]
internal sealed partial class CoreJson : JsonSerializerContext;
