using BambuBar.Linux;

var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
const string dark = "background='#1a1b26'\nforeground='#a9b1d6'\naccent='#7aa2f7'\nselection='#292e42'\nmode='dark'";
var palette = OmarchyPalette.Parse(dark, "", 2)!;
Check(palette.Background == "#1a1b26" && palette.Selection == "#292e42" && palette.Dark, "System preserves the actual palette");
Check(!OmarchyPalette.Parse(dark, "", 1)!.Dark, "Explicit light overrides a dark desktop");
Check(OmarchyPalette.Parse(dark, "", 1)!.Accent == palette.Accent, "Mode adaptation retains the accent");
Check(OmarchyPalette.Parse(dark.Replace("#7aa2f7", "red; background: url(file:///etc/passwd)"), "", 2) is null, "Reject CSS injection");
Check(OmarchyPalette.Parse("background='#ffffff'\nforeground='#ffffff'\naccent='#123456'", "", 2) is null, "Reject unreadable source palette");
Check(OmarchyPalette.Parse("background='bad'", "", 2) is null, "Malformed/partial swaps fall back safely");
Check(OmarchyPalette.Parse(dark, "[controls]\nnormal-border='#123456'", 2)!.Border != "#ffffff", "Sections accepted");
var files = args.Length == 0 ? Array.Empty<string>() : Directory.GetFiles(args[0], "colors.toml", SearchOption.AllDirectories);
foreach (var file in files)
{
    foreach (var mode in new[] { 0, 1, 2 })
    {
        var p = OmarchyPalette.Parse(File.ReadAllText(file), "", mode);
        Check(p is not null, "Valid installed theme: " + file);
        Check(OmarchyPalette.Contrast(p!.Background, p.Foreground) >= 4.5, "Readable foreground: " + file);
        Check(OmarchyPalette.Contrast(p.Background, p.Secondary) >= 4.5, "Readable secondary: " + file);
        Check(OmarchyPalette.Contrast(p.Background, p.Red) >= 4.5 && OmarchyPalette.Contrast(p.Background, p.Green) >= 4.5, "Readable status: " + file);
        if (mode < 2) Check(p.Dark == (mode == 0), "Explicit mode survives switching: " + file);
    }
}
Console.WriteLine($"{checks} Omarchy palette checks passed ({files.Length} installed themes).");
