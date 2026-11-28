using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace TempHire.Server.Addresses;

/// <summary>A U.S. address Photon suggests for what the user typed, in TempHire's address fields.</summary>
/// <param name="Address1">
/// House number and street.  When Photon knows no number, the one the user typed; the street alone
/// when they typed none either.
/// </param>
/// <param name="City">The city, or the nearest place name Photon has for it.</param>
/// <param name="State">The state's full name, as Photon gives it ("Washington").</param>
/// <param name="Zipcode">
/// The ZIP code, when Photon has a valid one; else the one the user typed, if it is in this state.
/// </param>
public sealed record PhotonAddress(string Address1, string City, string State, string Zipcode);

/// <summary>
/// Asks Photon (<see href="https://github.com/komoot/photon"/>), a geocoder over OpenStreetMap
/// data, for U.S. house and street addresses that match the start of an address.
/// </summary>
/// <remarks>
/// <para>
/// Answers are cached per text for <see cref="AddressSuggestionOptions.CacheDuration"/>, so the
/// same keystrokes from several users ask Photon once.  A failure - a timeout, an error status,
/// an answer that isn't Photon's - is logged and gives no suggestions: the user goes on typing.
/// </para>
/// <para>
/// OpenStreetMap's U.S. house numbers have gaps, so some addresses only come back as a street, and
/// some without a ZIP code.  Those gaps are filled from what the user typed
/// (<see cref="FillFromTyped"/>), for every caller: the address form's completion
/// (<c>AddressController</c>) and Quinn's <c>complete_address</c> tool alike.
/// </para>
/// </remarks>
public sealed partial class PhotonAddressSuggester(
    HttpClient http,
    IMemoryCache cache,
    IOptions<AddressSuggestionOptions> options,
    ILogger<PhotonAddressSuggester> logger)
{
    private readonly AddressSuggestionOptions _options = options.Value;

    /// <summary>
    /// Up to <see cref="AddressSuggestionOptions.Limit"/> distinct U.S. addresses for <paramref name="text"/>,
    /// with the house number and ZIP code the user typed where Photon has none.
    /// </summary>
    public async Task<IReadOnlyList<PhotonAddress>> SuggestAsync(string text, CancellationToken cancellationToken)
    {
        var query = (text ?? "").Trim();
        if (query.Length < _options.MinLength)
        {
            return [];
        }

        if (query.Length > 100)
        {
            query = query[..100];
        }

        // Photon's own answer is what's cached; the gaps are filled per request, from the text as typed.
        var key = (nameof(PhotonAddressSuggester), query.ToLowerInvariant());
        if (!cache.TryGetValue(key, out IReadOnlyList<PhotonAddress> suggestions))
        {
            suggestions = await AskPhotonAsync(query, cancellationToken);
            if (suggestions is null)
            {
                return [];
            }

            cache.Set(key, suggestions, _options.CacheDuration);
        }

        return FillFromTyped(query, suggestions);
    }

    /// <summary>
    /// Photon's addresses with their gaps filled from what the user typed:
    /// <list type="bullet">
    ///   <item>OpenStreetMap's U.S. house numbers have gaps, so an address may be only the street.  It
    ///   gets the house number the user typed first, so "908 W Capital Way" completes to that house,
    ///   not to the street.  An address that has a number keeps it.</item>
    ///   <item>An address without a ZIP code gets the one the user typed, if any, when that ZIP code
    ///   is in the address's state (<see cref="ZipCodeStates"/>): a ZIP code typed for Montana
    ///   doesn't end up on a street in Texas.  It is the last one after the house number, so a
    ///   five-digit house number isn't taken for a ZIP code.</item>
    /// </list>
    /// Filling can make two addresses the same; the duplicate is dropped.
    /// </summary>
    internal static IReadOnlyList<PhotonAddress> FillFromTyped(string typed, IReadOnlyList<PhotonAddress> addresses)
    {
        var numberMatch = HouseNumber().Match(typed ?? "");
        var number = numberMatch.Success ? numberMatch.Groups[1].Value : null;
        var afterNumber = numberMatch.Success ? numberMatch.Groups[1].Index + numberMatch.Groups[1].Length : 0;
        var zip = TypedZipCode().Matches(typed ?? "").LastOrDefault(m => m.Index >= afterNumber)?.Value;
        var zipState = zip is null ? null : ZipCodeStates.StateOf(zip);
        if (zipState is null)
        {
            zip = null;
        }

        if (number is null && zip is null)
        {
            return addresses;
        }

        return
        [
            .. addresses
                .Select(a => a with
                {
                    Address1 = number is not null && !StartsWithNumber().IsMatch(a.Address1) ? $"{number} {a.Address1}" : a.Address1,
                    Zipcode = a.Zipcode ?? (zip is not null && string.Equals(a.State, zipState, StringComparison.OrdinalIgnoreCase) ? zip : null)
                })
                .Distinct()
        ];
    }

    /// <summary>Photon's answer, or null when there is none to cache.</summary>
    private async Task<IReadOnlyList<PhotonAddress>> AskPhotonAsync(string query, CancellationToken cancellationToken)
    {
        // Twice the limit: duplicates and entries without a street are dropped below.
        var url = string.Create(CultureInfo.InvariantCulture,
            $"api/?q={Uri.EscapeDataString(query)}&limit={_options.Limit * 2}&lang=en&countrycode=US&layer=house&layer=street");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);

            using var response = await http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Photon answered {Status} for an address suggestion.", (int)response.StatusCode);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
            return Read(json.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Photon took longer than {Timeout} for an address suggestion.", _options.Timeout);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Address suggestions from Photon failed.");
            return null;
        }
    }

    /// <summary>The U.S. addresses in a Photon <c>FeatureCollection</c>, distinct, in Photon's order.</summary>
    internal IReadOnlyList<PhotonAddress> Read(JsonElement featureCollection)
    {
        var addresses = new List<PhotonAddress>();
        foreach (var feature in featureCollection.GetProperty("features").EnumerateArray())
        {
            var p = feature.GetProperty("properties");
            if (!string.Equals(Text(p, "countrycode"), "US", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A house has a street and maybe a number; a street is its own name.
            var street = Text(p, "street") ?? (Text(p, "type") == "street" ? Text(p, "name") : null);
            if (street is null)
            {
                continue;
            }

            var number = Text(p, "housenumber");
            var postcode = Text(p, "postcode");
            var address = new PhotonAddress(
                Address1: number is null ? street : $"{number} {street}",
                City: Text(p, "city") ?? Text(p, "locality") ?? Text(p, "district"),
                State: Text(p, "state"),
                Zipcode: postcode is not null && ZipCode().IsMatch(postcode) ? postcode : null);

            if (!addresses.Contains(address))
            {
                addresses.Add(address);
            }

            if (addresses.Count == _options.Limit)
            {
                break;
            }
        }

        return addresses;
    }

    private static string Text(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>The format of the ZIP code rule (the <c>zipCode</c> validator in the validation metadata).</summary>
    [GeneratedRegex(@"^\d{5}(-\d{4})?$")]
    private static partial Regex ZipCode();

    /// <summary>A ZIP code anywhere in what the user typed.</summary>
    [GeneratedRegex(@"\b\d{5}(?:-\d{4})?\b")]
    private static partial Regex TypedZipCode();

    /// <summary>The house number at the start of what the user typed: <c>908</c>, <c>12B</c>, <c>1200-A</c>.</summary>
    [GeneratedRegex(@"^\s*(\d+[A-Za-z]?(?:-\w+)?)\s+\S")]
    private static partial Regex HouseNumber();

    /// <summary>
    /// A house number at the start of an address: <c>908 Main St</c>, but not the street of
    /// <c>11th Avenue</c>, which is a street without a number.
    /// </summary>
    [GeneratedRegex(@"^\d+[A-Za-z]?(?:-\w+)?\s")]
    private static partial Regex StartsWithNumber();
}
