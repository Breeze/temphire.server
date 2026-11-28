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
    /// places with no street.  A postcode the ZIP code rule wouldn't accept is left out.  (The street
    /// gets the typed house number: see the tests below.)
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
                new PhotonAddress("908 Capitol Way South", "Olympia", "Washington", "98501-1234"),
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
    /// OpenStreetMap often knows a U.S. street but not the house: the street gets the house number
    /// the user typed, for every caller.  An address with its own number keeps it, a numbered street
    /// isn't taken for a house number, and an address that filling makes a duplicate is dropped.
    /// </summary>
    [Theory]
    [InlineData("908 W Capital Way", "908 West Capital Way")]
    [InlineData("12B W Capital", "12B West Capital Way")]
    [InlineData("1200-A W Capital", "1200-A West Capital Way")]
    [InlineData("West Capital Way", "West Capital Way")]
    public async Task A_street_without_its_house_number_gets_the_one_typed(string typed, string address1)
    {
        _photon.Answer = Photon(("", "West Capital Way", "Tacoma", "Washington", "98401"));

        var found = await NewSuggester().SuggestAsync(typed, CancellationToken.None);

        Assert.Equal(address1, Assert.Single(found).Address1);
    }

    [Fact]
    public async Task Only_addresses_without_a_number_get_the_typed_one()
    {
        _photon.Answer = Photon(
            ("910", "West Capital Way", "Tacoma", "Washington", "98401"),
            ("", "11th Avenue West", "Seattle", "Washington", "98119"),
            ("449", "11th Avenue West", "Seattle", "Washington", "98119"));

        var found = await NewSuggester().SuggestAsync("449 11th Ave", CancellationToken.None);

        Assert.Equal(["910 West Capital Way", "449 11th Avenue West"], found.Select(a => a.Address1));
    }

    /// <summary>
    /// An address without a ZIP code gets the one the user typed, if any; one Photon knows stays.  A
    /// five-digit house number is not a ZIP code.
    /// </summary>
    [Fact]
    public async Task A_missing_ZIP_code_is_the_one_typed()
    {
        _photon.Answer = Photon(
            ("12", "Pine Street", "Seattle", "Washington", null),
            ("12", "Pine Street", "Tacoma", "Washington", "98402"));

        var found = await NewSuggester().SuggestAsync("12 Pine St, Seattle, WA 98101", CancellationToken.None);
        Assert.Equal(["98101", "98402"], found.Select(a => a.Zipcode));

        found = await NewSuggester().SuggestAsync("12 Pine St", CancellationToken.None);
        Assert.Equal([null, "98402"], found.Select(a => a.Zipcode));

        found = await NewSuggester().SuggestAsync("12345 Pine St", CancellationToken.None);
        Assert.Equal([null, "98402"], found.Select(a => a.Zipcode));
    }

    /// <summary>
    /// The typed ZIP code fills only addresses in its own state: a street in another state with no
    /// ZIP code of its own stays without one.  A ZIP code with no state (military, a territory)
    /// fills none.
    /// </summary>
    [Fact]
    public async Task The_typed_ZIP_code_fills_only_addresses_in_its_state()
    {
        _photon.Answer = Photon(
            ("", "C Bar C Ranch Road", "Butte", "Montana", null),
            ("", "East County Road 50", "Shenandoah", "Texas", null));

        var found = await NewSuggester().SuggestAsync("50 C Bar C Ranch Road Butte 59701", CancellationToken.None);
        Assert.Equal(["59701", null], found.Select(a => a.Zipcode));

        found = await NewSuggester().SuggestAsync("50 C Bar C Ranch Road Butte 09001", CancellationToken.None);
        Assert.Equal([null, null], found.Select(a => a.Zipcode));
    }

    [Theory]
    [InlineData("59701", "Montana")]
    [InlineData("98401-1234", "Washington")]
    [InlineData("00501", "New York")]
    [InlineData("02108", "Massachusetts")]
    [InlineData("20500", "District of Columbia")]
    [InlineData("22101", "Virginia")]
    [InlineData("73301", "Texas")]
    [InlineData("73101", "Oklahoma")]
    [InlineData("88510", "Texas")]
    [InlineData("99501", "Alaska")]
    [InlineData("09001", null)]
    [InlineData("96201", null)]
    [InlineData("00901", null)]
    [InlineData("abcde", null)]
    [InlineData("123", null)]
    [InlineData(null, null)]
    public void A_ZIP_code_tells_its_state(string zipcode, string state) =>
        Assert.Equal(state, ZipCodeStates.StateOf(zipcode));

    /// <summary>The cache keeps Photon's answer: each request fills it from its own text.</summary>
    [Fact]
    public async Task A_cached_answer_is_filled_from_each_request()
    {
        _photon.Answer = Photon(("", "Pine Street", "Seattle", "Washington", null));
        var suggester = NewSuggester();

        var first = await suggester.SuggestAsync("12 Pine St 98101", CancellationToken.None);
        var second = await suggester.SuggestAsync("Pine St", CancellationToken.None);
        var third = await suggester.SuggestAsync("12 PINE ST 98101", CancellationToken.None);

        Assert.Equal(new PhotonAddress("12 Pine Street", "Seattle", "Washington", "98101"), Assert.Single(first));
        Assert.Equal(new PhotonAddress("Pine Street", "Seattle", "Washington", null), Assert.Single(second));
        Assert.Equal(first, third);
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
        Assert.Equal("1600 Capitol Avenue, Cheyenne, Wyoming", cheyenne.Label);
    }

    /// <summary>
    /// The address form gets the typed house number and ZIP code too, in the fields and the label:
    /// the same completion as Quinn's.
    /// </summary>
    [Fact]
    public async Task The_endpoint_fills_the_gaps_from_the_typed_text()
    {
        _photon.Answer = Photon(("", "Capitol Way", "Olympia", "Washington", null));
        using var db = _db.NewContext();
        var controller = new AddressController(NewSuggester(), db);

        var result = (await controller.Suggest("1600 Capitol Way 98501", CancellationToken.None)).Value!;

        var olympia = Assert.Single(result.Suggestions);
        Assert.Equal(("1600 Capitol Way", "98501"), (olympia.Address1, olympia.Zipcode));
        Assert.Equal("1600 Capitol Way, Olympia, WA 98501", olympia.Label);
    }

    private PhotonAddressSuggester NewSuggester() => new(
        new HttpClient(_photon) { BaseAddress = new Uri("https://photon.test/") },
        _cache,
        Options.Create(new AddressSuggestionOptions()),
        NullLogger<PhotonAddressSuggester>.Instance);

    /// <summary>A Photon answer of U.S. houses (with a number) and streets (without one).</summary>
    private static string Photon(params (string Number, string Street, string City, string State, string Zip)[] places) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features = places.Select(p => new
            {
                properties = new Dictionary<string, string>
                {
                    ["type"] = p.Number == "" ? "street" : "house",
                    ["housenumber"] = p.Number == "" ? null : p.Number,
                    ["street"] = p.Number == "" ? null : p.Street,
                    ["name"] = p.Number == "" ? p.Street : null,
                    ["city"] = p.City,
                    ["state"] = p.State,
                    ["postcode"] = p.Zip,
                    ["countrycode"] = "US",
                }.Where(kv => kv.Value is not null).ToDictionary()
            })
        });

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
