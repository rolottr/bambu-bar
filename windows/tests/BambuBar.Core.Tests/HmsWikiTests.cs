using System.Net;
using System.Web;
using BambuBar.Core.Model;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.Tests;

/// <summary>The "Wiki" link on an issue: Bambu Lab's redirect to the code's page for the printer model, carrying only
/// the serial's model prefix; print errors, which have no pages, go to the HMS index. Ported from HMSWikiTests.swift,
/// plus the resolution order against a fake redirect.</summary>
public class HmsWikiTests
{
    private static Dictionary<string, string?> Query(Uri url)
    {
        var q = HttpUtility.ParseQueryString(url.Query);
        return q.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => q[k]);
    }

    [Fact]
    public void HmsCodeLinksToTheModelsPage()
    {
        var url = HmsCatalog.WikiUrl("0700210000020019", "01P00A123456789", "en");
        Assert.Equal("e.bambulab.com", url.Host);
        var q = Query(url);
        Assert.Equal("0700210000020019", q["e"]);
        Assert.Equal("01P", q["d"]);
        Assert.Equal("device_hms", q["s"]);
        Assert.Equal("en", q["lang"]);
    }

    [Fact]
    public void WithoutASerialTheModelIsLeftOut()
    {
        var q = Query(HmsCatalog.WikiUrl("0501040000030002", null, "en"));
        Assert.Equal("0501040000030002", q["e"]);
        Assert.False(q.ContainsKey("d"));
    }

    [Fact]
    public void PrintErrorsGoToTheIndex()
    {
        var url = HmsCatalog.WikiUrl("07008006", "01P00A123456789", "en");
        Assert.Equal("wiki.bambulab.com", url.Host);
        Assert.EndsWith("/hms/home", url.AbsolutePath);
        Assert.Equal("https://wiki.bambulab.com/zh/hms/home", HmsCatalog.WikiUrl("07008006", null, "zh-Hans").ToString());
    }

    [Fact]
    public void AmsCodesAreTriedForEveryUnitAndSlot()
    {
        Assert.Equal("0700200000020019", HmsCatalog.AmsGeneric("0700210000020019"));
        Assert.Equal("0700200000020001", HmsCatalog.AmsGeneric("0701230000020001"));
        Assert.Equal("0501040000030002", HmsCatalog.AmsGeneric("0501040000030002"));
    }

    [Fact]
    public void CandidatesGoFromThisModelToAnyModel()
    {
        var url = HmsCatalog.WikiUrl("0700210000020019", "22E00A000000000", "en");
        var tried = HmsCatalog.WikiCandidates(url).Select(u => (Query(u)["e"], Query(u).GetValueOrDefault("d"))).ToList();
        Assert.Equal(["0700210000020019", "0700200000020019", "0700210000020019", "0700200000020019"], tried.Select(t => t.Item1));
        Assert.Equal(["22E", "22E", null, null], tried.Select(t => t.Item2));
        // A code that's the same either way isn't asked twice.
        Assert.Equal(2, HmsCatalog.WikiCandidates(HmsCatalog.WikiUrl("0501040000030002", "01P00A000000000", "en")).Count);
    }

    [Fact]
    public void ParserLinksIssuesForThePrinter()
    {
        var doc = new ReportDocument();
        doc.Apply(new()
        {
            ["print"] = new Dictionary<string, object?>
            {
                ["gcode_state"] = "PAUSE",
                ["hms"] = new List<object?> { new Dictionary<string, object?> { ["attr"] = 0x0700_2100L, ["code"] = 0x0002_0019L } },
                ["print_error"] = 0x0700_8006L,
            },
        });
        var issues = ReportParser.Status(doc, new HmsCatalog("en"), "22E00A000000000").Issues;
        var hms = Assert.Single(issues, i => i.Id == "HMS_0700_2100_0002_0019");
        Assert.Equal("22E", Query(hms.WikiUrl!)["d"]);
        var error = Assert.Single(issues, i => i.Id == "0700_8006");
        Assert.Equal("wiki.bambulab.com", error.WikiUrl!.Host);
    }

    /// <summary>A redirect that sends the slot-specific code to the index but the shared AMS page to the real one.</summary>
    private sealed class FakeRedirect : HttpMessageHandler
    {
        public readonly List<Uri> Asked = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            Asked.Add(request.RequestUri!);
            var code = HttpUtility.ParseQueryString(request.RequestUri!.Query)["e"];
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = code == "0700200000020019" && request.RequestUri.Query.Contains("d=")
                ? new Uri("/en/h2/troubleshooting/hmscode/0700_2000_0002_0019", UriKind.Relative)
                : new Uri("https://wiki.bambulab.com/en/hms/home");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ResolveSkipsTheIndexForTheFirstRealPage()
    {
        var fake = new FakeRedirect();
        var page = await new HmsWiki(fake).ResolveAsync(HmsCatalog.WikiUrl("0700210000020019", "22E00A000000000", "en"),
            TestContext.Current.CancellationToken);
        Assert.Equal("https://e.bambulab.com/en/h2/troubleshooting/hmscode/0700_2000_0002_0019", page?.ToString());
        Assert.Equal(2, fake.Asked.Count);
        // Not a redirect link: opened as it is.
        Assert.Null(await new HmsWiki(fake).ResolveAsync(new Uri("https://wiki.bambulab.com/en/hms/home"), TestContext.Current.CancellationToken));
    }
}
