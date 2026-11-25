using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TempHire.Server.Addresses;
using TempHire.Server.Controllers;
using Xunit;

namespace TempHire.Server.Tests;

/// <summary>
/// Address completion: what is taken from Photon's answer, when Photon is asked at all, and how the
/// suggestions reach the client.  Photon itself is a stand-in here, answering with a recorded
/// shape of its <c>FeatureCollection</c>.
/// </summary>
public sealed class AddressSuggestionTests : IDisposable
{
    private readonly SaveFixture _db = new();
    private readonly FakePhoton _photon = new();

    // One cache per test, shared by the suggesters a test makes, as the app shares one.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }

    /// <summary>
    /// U.S. houses and streets, in Photon's order, without duplicates; not other countries, and not
    /// places with no street.  A postcode the ZIP code rule wouldn't accept is left out.
    /// </summary>
    [Fact]
    public async Task Suggestions_are_US_streets_and_houses_with_what_the_form_needs()
    {
        _photon.Answer = """
            {"type":"FeatureCollection","features":[
              {"properties":{"type":"house","housenumber":"908","street":"Capital Boulevard","city":"Raleigh","state":"North Carolina","postcode":"27603","countrycode":"US"}},
              {"properties":{"type":"house","housenumber":"908","street":"Damelart Way","city":"Central Saanich","state":"British Columbia","postcode":"V8M 1B4","countrycode":"CA"}},
              {"properties":{"type":"street","name":"Capitol Way South","locality":"Olympia","state":"Washington","postcode":"98501-1234","countrycode":"US"}},
              {"properties":{"type":"house","housenumber":"908","street":"Capital Boulevard","city":"Raleigh","state":"North Carolina","postcode":"27603","countrycode":"US"}},
              {"properties":{"type":"house","name":"Capital Center","city":"Austin","state":"Texas","countrycode":"US"}},
              {"properties":{"type":"house","housenumber":"12","street":"Capital Street","city":"Boston","state":"Massachusetts","postcode":"2108","countrycode":"US"}}
            ]}
            """;

        var found = await NewSuggester().SuggestAsync("908 Capital", CancellationToken.None);

        Assert.Equal(
            [
                new PhotonAddress("908 Capital Boulevard", "Raleigh", "North Carolina", "27603"),
                new PhotonAddress("Capitol Way South", "Olympia", "Washington", "98501-1234"),
                new PhotonAddress("12 Capital Street", "Boston", "Massachusetts", null),
            ],
            found);
        Assert.Contains("countrycode=US", _photon.Requests.Single());
        Assert.Contains("q=908%20Capital", _photon.Requests.Single());
    }

    [Fact]
    public async Task Short_text_does_not_ask_Photon()
    {
        Assert.Empty(await NewSuggester().SuggestAsync("90", CancellationToken.None));
        Assert.Empty(await NewSuggester().SuggestAsync("   ", CancellationToken.None));
        Assert.Empty(_photon.Requests);
    }

    [Fact]
    public async Task The_same_text_asks_Photon_once()
    {
        var suggester = NewSuggester();

        await suggester.SuggestAsync("908 Capital", CancellationToken.None);
        await suggester.SuggestAsync(" 908 CAPITAL ", CancellationToken.None);

        Assert.Single(_photon.Requests);
    }

    /// <summary>A failure gives no suggestions, and isn't kept: the next keystroke asks again.</summary>
    [Fact]
    public async Task A_failure_gives_no_suggestions_and_is_not_cached()
    {
        var suggester = NewSuggester();
        _photon.Status = HttpStatusCode.ServiceUnavailable;

        Assert.Empty(await suggester.SuggestAsync("908 Capital", CancellationToken.None));
        _photon.Answer = "not json";
        _photon.Status = HttpStatusCode.OK;
        Assert.Empty(await suggester.SuggestAsync("908 Capital", CancellationToken.None));

        Assert.Equal(2, _photon.Requests.Count);
    }

    /// <summary>
    /// The client gets the state's id among TempHire's states, to set the address's state with, and
    /// a label for the list; a state TempHire doesn't have leaves the id out.
    /// </summary>
    [Fact]
    public async Task The_endpoint_matches_the_state_to_TempHires_states()
    {
        _photon.Answer = """
            {"type":"FeatureCollection","features":[
              {"properties":{"type":"house","housenumber":"1600","street":"Capitol Way","city":"Olympia","state":"washington","postcode":"98501","countrycode":"US"}},
              {"properties":{"type":"street","name":"Capitol Avenue","city":"Cheyenne","state":"Wyoming","countrycode":"US"}}
            ]}
            """;
        using var db = _db.NewContext();
        var controller = new AddressController(NewSuggester(), db);

        var result = (await controller.Suggest("1600 Capitol", CancellationToken.None)).Value!;

        Assert.Equal(AddressController.Attribution, result.Attribution);
        var olympia = result.Suggestions[0];
        Assert.Equal(("1600 Capitol Way", "Olympia", SaveFixture.StateId, "WA", "98501"),
            (olympia.Address1, olympia.City, olympia.StateId!.Value, olympia.StateShortName, olympia.Zipcode));
        Assert.Equal("1600 Capitol Way, Olympia, WA 98501", olympia.Label);

        var cheyenne = result.Suggestions[1];
        Assert.Null(cheyenne.StateId);
        Assert.Equal("Capitol Avenue, Cheyenne, Wyoming", cheyenne.Label);
    }

    private PhotonAddressSuggester NewSuggester() => new(
        new HttpClient(_photon) { BaseAddress = new Uri("https://photon.test/") },
        _cache,
        Options.Create(new AddressSuggestionOptions()),
        NullLogger<PhotonAddressSuggester>.Instance);

    /// <summary>Photon, as far as these tests need it: records each request and gives one answer.</summary>
    private sealed class FakePhoton : HttpMessageHandler
    {
        public string Answer { get; set; } = """{"type":"FeatureCollection","features":[]}""";

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Answer) });
        }
    }
}
