using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace TempHire.Server.Addresses;

/// <summary>A U.S. address Photon suggests for what the user typed, in TempHire's address fields.</summary>
/// <param name="Address1">House number and street, or the street alone when Photon knows no number.</param>
/// <param name="City">The city, or the nearest place name Photon has for it.</param>
/// <param name="State">The state's full name, as Photon gives it ("Washington").</param>
/// <param name="Zipcode">The ZIP code, when Photon has a valid one.</param>
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
/// OpenStreetMap's U.S. house numbers have gaps, so some addresses only come back as a street.
/// </para>
/// </remarks>
public sealed partial class PhotonAddressSuggester(
    HttpClient http,
    IMemoryCache cache,
    IOptions<AddressSuggestionOptions> options,
    ILogger<PhotonAddressSuggester> logger)
{
    private readonly AddressSuggestionOptions _options = options.Value;

    /// <summary>Up to <see cref="AddressSuggestionOptions.Limit"/> distinct U.S. addresses for <paramref name="text"/>.</summary>
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

        var key = (nameof(PhotonAddressSuggester), query.ToLowerInvariant());
        if (cache.TryGetValue(key, out IReadOnlyList<PhotonAddress> cached))
        {
            return cached;
        }

        var suggestions = await AskPhotonAsync(query, cancellationToken);
        if (suggestions is not null)
        {
            cache.Set(key, suggestions, _options.CacheDuration);
        }

        return suggestions ?? [];
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
}
