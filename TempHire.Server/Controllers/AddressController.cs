using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using TempHire.Model;
using TempHire.Server.Addresses;
using TempHire.Server.Auth;

namespace TempHire.Server.Controllers;

/// <summary>
/// Address completion.
/// </summary>
/// <remarks>
/// <para>
/// As the user types the first address line, the client asks for addresses that start that way, and
/// fills the address form with the one the user picks.  The suggestions come from Photon, a
/// geocoder over OpenStreetMap data (<see cref="PhotonAddressSuggester"/>); the server asks it so
/// the client needs no key or knowledge of the provider, and so the answers can be cached.
/// </para>
/// <para>
/// For editors only, like a save: a reader has no address form to fill.
/// </para>
/// </remarks>
[ApiController]
[Route("api/address/[action]")]
[Authorize(Policy = TempHirePolicies.EditStaffingData)]
public class AddressController(PhotonAddressSuggester suggester, TempHireDbContext db) : ControllerBase
{
    /// <summary>OpenStreetMap's licence asks for this credit wherever its data is shown.</summary>
    public const string Attribution = "© OpenStreetMap contributors";

    /// <summary>U.S. addresses that start with the given text.</summary>
    /// <remarks>
    /// Up to five house or street addresses, each with its first line, city, state and ZIP code,
    /// and the state's id among TempHire's states.  Text shorter than three characters gets none,
    /// and so does a failure of the address service: the user goes on typing.  OpenStreetMap's U.S.
    /// house numbers have gaps, so some addresses only come back as a street: those get the house
    /// number at the start of the text, and an address without a ZIP code gets one typed after it,
    /// as in Quinn's <c>complete_address</c> tool.  camelCase names, unlike the Breeze endpoints.
    /// </remarks>
    /// <param name="q">What the user has typed so far, such as <c>1600 Pennsylvania</c>.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The suggestions, possibly none, with the credit to show next to them.</response>
    [HttpGet]
    public async Task<ActionResult<AddressSuggestions>> Suggest([FromQuery] string q, CancellationToken cancellationToken)
    {
        var addresses = await suggester.SuggestAsync(q, cancellationToken);
        var states = addresses.Count == 0
            ? []
            : await db.States.AsNoTracking().ToDictionaryAsync(s => s.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return new AddressSuggestions
        {
            Attribution = Attribution,
            Suggestions =
            [
                .. addresses.Select(a =>
                {
                    var state = a.State is not null && states.TryGetValue(a.State, out var s) ? s : null;
                    return new AddressSuggestion
                    {
                        Address1 = a.Address1,
                        City = a.City,
                        StateId = state?.Id,
                        StateShortName = state?.ShortName,
                        Zipcode = a.Zipcode,
                        Label = Label(a.Address1, a.City, state?.ShortName ?? a.State, a.Zipcode),
                    };
                }),
            ],
        };
    }

    /// <summary>One line to show in the list: <c>908 W Capital Way, Tacoma, WA 98401</c>.</summary>
    private static string Label(string address1, string city, string state, string zipcode) =>
        string.Join(", ", new[] { address1, city, string.Join(" ", new[] { state, zipcode }.Where(p => p is not null)) }
            .Where(p => !string.IsNullOrEmpty(p)));
}

// See the note on the DTOs in AccountController: camelCase on the wire, by type.

/// <summary>Address suggestions, and the credit to show with them.</summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class AddressSuggestions
{
    /// <summary>The credit the data's licence asks for, to show next to the suggestions.</summary>
    public string Attribution { get; set; }

    /// <summary>The suggestions, in the order the address service ranks them.</summary>
    public List<AddressSuggestion> Suggestions { get; set; } = [];
}

/// <summary>An address to fill the address form with.</summary>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public class AddressSuggestion
{
    /// <summary>House number and street (the typed number when the address service has none), or the street alone.</summary>
    public string Address1 { get; set; }

    /// <summary>The city, when known.</summary>
    public string City { get; set; }

    /// <summary>The state's id among TempHire's states, when it is one of them.</summary>
    public Guid? StateId { get; set; }

    /// <summary>The state's two-letter code, when it is one of TempHire's states.</summary>
    public string StateShortName { get; set; }

    /// <summary>The ZIP code, when known.</summary>
    public string Zipcode { get; set; }

    /// <summary>The whole address on one line, for the list.</summary>
    public string Label { get; set; }
}
