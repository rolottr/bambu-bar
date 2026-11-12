using BambuBar.Core.Json;

namespace BambuBar.Core.Protocol;

/// <summary>
/// The printer sends one full <c>print</c> report after <c>pushall</c>, then only the fields that changed.
/// <c>ReportDocument</c> keeps the merged picture. Values are <see cref="PlainJson"/> trees and are never changed in
/// place: a merge builds new dictionaries, so earlier documents and messages stay as they were.
/// </summary>
public sealed class ReportDocument
{
    public Dictionary<string, object?> Print { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>From <c>get_version</c>: module name → (product name, software version).</summary>
    public Dictionary<string, (string Product, string Version)> Modules { get; private set; } = new(StringComparer.Ordinal);

    public bool HasFullReport { get; private set; }

    /// <summary>Merges one message from <c>device/&lt;serial&gt;/report</c>. Returns true if the print state changed.</summary>
    public bool Apply(Dictionary<string, object?> message)
    {
        var changed = false;
        if (Cast.Dict(message.GetValueOrDefault("print")) is { } p)
        {
            var command = Cast.AsString(p.GetValueOrDefault("command"));
            // Command acknowledgements echo the request back; only status pushes describe the printer.
            if (command is null or "push_status")
            {
                if (Cast.AsInt(p.GetValueOrDefault("msg")) == 0 && p.ContainsKey("gcode_state"))
                {
                    // A full report replaces everything, so unplugged AMS units and cleared errors go away.
                    HasFullReport = true;
                    Print = p;
                }
                else
                {
                    Print = Merge(Print, p);
                }
                changed = true;
            }
        }
        if (Cast.Dict(message.GetValueOrDefault("info")) is { } info
            && Cast.AsString(info.GetValueOrDefault("command")) == "get_version"
            && Cast.DictArray(info.GetValueOrDefault("module")) is { } list)
        {
            var modules = new Dictionary<string, (string, string)>(Modules, StringComparer.Ordinal);
            foreach (var m in list)
            {
                if (Cast.AsString(m.GetValueOrDefault("name")) is not { } name) continue;
                modules[name] = (Cast.AsString(m.GetValueOrDefault("product_name")) ?? "",
                                 Cast.AsString(m.GetValueOrDefault("sw_ver")) ?? "");
            }
            Modules = modules;
            changed = true;
        }
        return changed;
    }

    public void Reset()
    {
        Print = new(StringComparer.Ordinal);
        Modules = new(StringComparer.Ordinal);
        HasFullReport = false;
    }

    /// <summary>Recursive merge. Arrays of objects carrying an <c>id</c> (or, for <c>lights_report</c>, a
    /// <c>node</c>) merge entry by entry; other arrays are replaced.</summary>
    public static Dictionary<string, object?> Merge(Dictionary<string, object?> base_, Dictionary<string, object?> delta)
    {
        var out_ = new Dictionary<string, object?>(base_, StringComparer.Ordinal);
        foreach (var (key, value) in delta)
        {
            var idKey = key == "lights_report" ? "node" : "id";
            var old = out_.GetValueOrDefault(key);
            if (value is Dictionary<string, object?> newDict && old is Dictionary<string, object?> oldDict)
            {
                out_[key] = Merge(oldDict, newDict);
            }
            else if (Cast.DictArray(value) is { Count: > 0 } newArray && Cast.DictArray(old) is { } oldArray
                     && newArray.TrueForAll(item => item.ContainsKey(idKey)))
            {
                var merged = new List<object?>(oldArray);
                foreach (var item in newArray)
                {
                    var id = IdText(item[idKey]);
                    var i = merged.FindIndex(e => IdText(((Dictionary<string, object?>)e!).GetValueOrDefault(idKey, "")) == id);
                    if (i >= 0) merged[i] = Merge((Dictionary<string, object?>)merged[i]!, item);
                    else merged.Add(item);
                }
                out_[key] = merged;
            }
            else
            {
                out_[key] = value;
            }
        }
        return out_;
    }

    /// <summary>An id as Swift's string interpolation prints it: numbers plainly, a JSON null as "&lt;null&gt;".</summary>
    private static string IdText(object? value) => value is null ? "<null>" : Cast.Describe(value) ?? value.ToString() ?? "";
}
