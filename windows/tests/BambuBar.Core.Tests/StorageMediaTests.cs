using BambuBar.Core.Model;

namespace BambuBar.Core.Tests;

/// <summary>The report's <c>sdcard</c> flag means a microSD card on the older models and a USB drive on the newer
/// ones, so the Storage row names what the model takes.</summary>
public class StorageMediaTests
{
    [Theory]
    [InlineData("BL-P001"), InlineData("C13"), InlineData("C12"), InlineData("C11"), InlineData("N2S"), InlineData("N1"), InlineData("N9")]
    public void OlderModelsTakeAMicroSdCard(string code) => Assert.Equal(StorageMedia.SdCard, PrinterModel.Find(code)!.Storage);

    [Theory]
    [InlineData("N7"), InlineData("N6"), InlineData("O1D"), InlineData("O1E"), InlineData("O1C"), InlineData("O1S"), InlineData("N10"), InlineData("O1D-V2")]
    public void NewerModelsTakeAUsbDrive(string code) => Assert.Equal(StorageMedia.UsbDrive, PrinterModel.Find(code)!.Storage);

    private static string Row(string code, bool? inserted) => (PrinterModel.Find(code)?.Storage).Describe(inserted);

    [Fact]
    public void TheRowNamesTheModelsMedia()
    {
        Assert.Equal(Loc.T("USB drive inserted"), Row("N7", true));
        Assert.Equal(Loc.T("No USB drive"), Row("N7", false));
        Assert.Equal(Loc.T("SD card inserted"), Row("N1", true));
        Assert.Equal(Loc.T("No SD card"), Row("N1", false));
        Assert.Equal(Loc.T("Inserted", "Storage media"), Row("Z99", true));
        Assert.Equal("—", Row("N7", null));
    }
}
