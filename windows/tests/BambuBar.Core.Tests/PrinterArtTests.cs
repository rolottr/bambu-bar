using BambuBar.Core.Imaging;
using BambuBar.Core.Model;

namespace BambuBar.Core.Tests;

public sealed class PrinterArtTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bambubar-art-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (DirectoryNotFoundException) { }
    }

    private static readonly string[] AmsColors = ["E8DBB7FF", "000000FF", "3F8E43FF", "F4A925FF"];

    private static PrinterArt.Scene Printing(double progress, PrinterShape shape = PrinterShape.Enclosed) => new()
    {
        Shape = shape, State = JobState.Printing, Progress = progress, Filament = "F4A925FF", Spools = AmsColors,
    };

    [Fact]
    public void PngRoundTripsEveryRender()
    {
        foreach (var file in Directory.GetFiles(Repo.Shared("Printers"), "*.png"))
        {
            var image = Png.Read(file);
            var again = Png.Decode(Png.Encode(image));
            Assert.Equal((image.Width, image.Height), (again.Width, again.Height));
            Assert.True(image.Pixels.AsSpan().SequenceEqual(again.Pixels), Path.GetFileName(file));
        }
    }

    [Fact]
    public void EveryShapeComposes()
    {
        foreach (var shape in Enum.GetValues<PrinterShape>())
        {
            var image = PrinterArt.Compose(Printing(0.5, shape));
            Assert.NotNull(image);
            Assert.Equal((480, 576), (image.Width, image.Height));
        }
    }

    /// <summary>The part grows from the plate: a pixel near the top of the finished print is part-coloured only when
    /// the print is far enough along.</summary>
    [Fact]
    public void ThePartGrowsWithProgress()
    {
        // enclosed.json: the part spans y 309–401 around x 192–286.
        var early = PrinterArt.Compose(Printing(0.1))!;
        var done = PrinterArt.Compose(Printing(1.0))!;
        var finished = PrinterArt.Compose(Printing(0.2) with { State = JobState.Finished })!;
        var (x, y) = FindPartPixel(done, top: 312, bottom: 330);
        Assert.NotEqual(Pixel(early, x, y), Pixel(done, x, y));
        Assert.Equal(Pixel(done, x, y), Pixel(finished, x, y));
        // Gold filament: red and green well above blue at that pixel.
        var (r, g, b, _) = Pixel(done, x, y);
        Assert.True(r > b + 40 && g > b + 20, $"part colour {r},{g},{b}");
    }

    [Fact]
    public void OfflineIsWashedOut()
    {
        var lit = PrinterArt.Compose(Printing(0.6))!;
        var dimmed = PrinterArt.Compose(Printing(0.6) with { Dimmed = true })!;
        Assert.True(dimmed.Pixels.Where((_, i) => i % 4 == 3).Max() <= 141, "at most 55 % opaque");
        var (x, y) = FindPartPixel(lit, top: 312, bottom: 400);
        var (r, g, b, _) = Pixel(dimmed, x, y);
        Assert.True(Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 25, "almost grey");
    }

    [Fact]
    public void ArtFilesAreNamedByContentAndPruned()
    {
        var first = PrinterArt.WritePng(Printing(0.64), 196, 232, _folder);
        var same = PrinterArt.WritePng(Printing(0.64), 196, 232, _folder);
        var other = PrinterArt.WritePng(Printing(0.80), 196, 232, _folder);
        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
        Assert.Matches("^art-[0-9a-f]{16}\\.png$", first!);
        var image = Png.Read(Path.Combine(_folder, first!));
        Assert.Equal((196, 232), (image.Width, image.Height));
        PrinterArt.Prune(_folder, [other!]);
        Assert.Equal([other], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("F4A925FF", 0xF4 / 255.0, 1.0)]
    [InlineData("F4A925", 0xF4 / 255.0, 1.0)]
    [InlineData("FFFFFF10", 1.0, 0.35)]
    public void BambuColours(string hex, double red, double alpha)
    {
        var c = PrinterArt.Color(hex)!.Value;
        Assert.Equal(red, c.R, 3);
        Assert.Equal(alpha, c.A, 3);
    }

    [Fact]
    public void TransparentAndBadColoursAreNone()
    {
        Assert.Null(PrinterArt.Color("00000000"));
        Assert.Null(PrinterArt.Color("nope"));
        Assert.Null(PrinterArt.Color(null));
    }

    /// <summary>Renders for a person to look at (BAMBUBAR_ART_OUT=folder).</summary>
    [Fact]
    public void WritesSamplesWhenAsked()
    {
        var folder = Environment.GetEnvironmentVariable("BAMBUBAR_ART_OUT");
        Assert.SkipWhen(folder is null, "Set BAMBUBAR_ART_OUT to write sample renders");
        Directory.CreateDirectory(folder!);
        void Save(string name, PrinterArt.Scene scene) => File.WriteAllBytes(Path.Combine(folder!, name + ".png"),
            Png.Encode(PrinterArt.Render(scene, 196, 232)!));
        Save("p2s-64", Printing(0.64));
        Save("p2s-offline", Printing(0.64) with { Dimmed = true });
        Save("a1-30", Printing(0.30, PrinterShape.BedSlinger) with { Filament = "DE4343FF", Spools = ["DE4343FF", "8671CBFF", null, "00AE42FF"] });
        Save("h2d-finished", Printing(1, PrinterShape.Wide) with { State = JobState.Finished, LightOn = false });
        Save("h3c-idle-empty", new PrinterArt.Scene { Shape = PrinterShape.H3C, State = JobState.Idle });
    }

    private static (int X, int Y) FindPartPixel(Rgba image, int top, int bottom)
    {
        // The most saturated pixel in the part's area of a gold print.
        (int, int) best = (0, 0);
        var bestScore = -1;
        for (var y = top; y < bottom; y++)
        {
            for (var x = 192; x < 286; x++)
            {
                var (r, g, b, a) = Pixel(image, x, y);
                var score = a == 255 ? r + g - 2 * b : -1;
                if (score > bestScore) { bestScore = score; best = (x, y); }
            }
        }
        return best;
    }

    private static (int R, int G, int B, int A) Pixel(Rgba image, int x, int y)
    {
        var o = image.Offset(x, y);
        return (image.Pixels[o], image.Pixels[o + 1], image.Pixels[o + 2], image.Pixels[o + 3]);
    }
}
