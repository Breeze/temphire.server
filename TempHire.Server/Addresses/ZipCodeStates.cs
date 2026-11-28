namespace TempHire.Server.Addresses;

/// <summary>
/// The state a ZIP code belongs to, from its first three digits (the sectional center), for the 50
/// states and the District of Columbia.
/// </summary>
/// <remarks>
/// <para>
/// Used to decide whether a ZIP code the user typed fits an address Photon suggests
/// (<see cref="PhotonAddressSuggester.FillFromTyped"/>).  The ranges follow USPS's assignment.  A
/// few ZIP codes lie outside their range (a handful of IRS, military and border offices); for
/// those the typed ZIP code is not used, which only means the user types it into the ZIP code field.
/// </para>
/// <para>
/// Military (AA, AE, AP) and territories' ZIP codes have no state here: TempHire has none of them.
/// </para>
/// </remarks>
public static class ZipCodeStates
{
    // (first prefix, last prefix, state's full name as Photon gives it), in order.
    private static readonly (int From, int To, string State)[] Ranges =
    [
        (5, 5, "New York"),
        (10, 27, "Massachusetts"),
        (28, 29, "Rhode Island"),
        (30, 38, "New Hampshire"),
        (39, 49, "Maine"),
        (50, 59, "Vermont"),
        (60, 69, "Connecticut"),
        (70, 89, "New Jersey"),
        (100, 149, "New York"),
        (150, 196, "Pennsylvania"),
        (197, 199, "Delaware"),
        (200, 200, "District of Columbia"),
        (201, 201, "Virginia"),
        (202, 205, "District of Columbia"),
        (206, 219, "Maryland"),
        (220, 246, "Virginia"),
        (247, 268, "West Virginia"),
        (270, 289, "North Carolina"),
        (290, 299, "South Carolina"),
        (300, 319, "Georgia"),
        (320, 339, "Florida"),
        (341, 349, "Florida"),
        (350, 369, "Alabama"),
        (370, 385, "Tennessee"),
        (386, 397, "Mississippi"),
        (398, 399, "Georgia"),
        (400, 427, "Kentucky"),
        (430, 459, "Ohio"),
        (460, 479, "Indiana"),
        (480, 499, "Michigan"),
        (500, 528, "Iowa"),
        (530, 549, "Wisconsin"),
        (550, 567, "Minnesota"),
        (569, 569, "District of Columbia"),
        (570, 577, "South Dakota"),
        (580, 588, "North Dakota"),
        (590, 599, "Montana"),
        (600, 629, "Illinois"),
        (630, 658, "Missouri"),
        (660, 679, "Kansas"),
        (680, 693, "Nebraska"),
        (700, 714, "Louisiana"),
        (716, 729, "Arkansas"),
        (730, 732, "Oklahoma"),
        (733, 733, "Texas"),
        (734, 749, "Oklahoma"),
        (750, 799, "Texas"),
        (800, 816, "Colorado"),
        (820, 831, "Wyoming"),
        (832, 838, "Idaho"),
        (840, 847, "Utah"),
        (850, 865, "Arizona"),
        (870, 884, "New Mexico"),
        (885, 885, "Texas"),
        (889, 898, "Nevada"),
        (900, 961, "California"),
        (967, 968, "Hawaii"),
        (970, 979, "Oregon"),
        (980, 994, "Washington"),
        (995, 999, "Alaska"),
    ];

    /// <summary>The state's full name for a ZIP code (<c>98401</c> or <c>98401-1234</c>), or null if none is known.</summary>
    public static string StateOf(string zipcode)
    {
        if (zipcode is not { Length: >= 5 } || !int.TryParse(zipcode.AsSpan(0, 3), out var prefix) || !char.IsAsciiDigit(zipcode[0]))
        {
            return null;
        }

        foreach (var (from, to, state) in Ranges)
        {
            if (prefix < from)
            {
                return null;
            }

            if (prefix <= to)
            {
                return state;
            }
        }

        return null;
    }
}
