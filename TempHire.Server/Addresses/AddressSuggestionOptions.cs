namespace TempHire.Server.Addresses;

/// <summary>
/// The <c>AddressSuggestions</c> section of appsettings.json: where the address suggestions come
/// from (<see cref="PhotonAddressSuggester"/>).
///
/// <para>
/// The default is komoot's public Photon server, which needs no API key.  Its maintainers welcome
/// use "within a reasonable limit" and call it a demo and test service with no availability
/// guarantee, which suits TempHire.  For more than that, run Photon yourself (GraphHopper publishes
/// weekly data dumps) and point <see cref="BaseUrl"/> at it.
/// </para>
/// </summary>
public class AddressSuggestionOptions
{
    public const string SectionName = "AddressSuggestions";

    /// <summary>The Photon server, ending in a slash.</summary>
    public string BaseUrl { get; set; } = "https://photon.komoot.io/";

    /// <summary>The most suggestions one request returns.</summary>
    public int Limit { get; set; } = 5;

    /// <summary>The fewest characters worth asking about; shorter text gets no suggestions.</summary>
    public int MinLength { get; set; } = 3;

    /// <summary>How long Photon may take before the user gets no suggestions instead.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long an answer is reused for the same text, which keeps the public server's load down.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(10);
}
