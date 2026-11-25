using System.ComponentModel;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Breeze.Persistence;
using IdeaBlade.Validation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TempHire.Model;
using TempHire.Server.Addresses;
using TempHire.Server.Auth;
using TempHire.Server.Controllers;
using TempHire.Server.Persistence;
using EntityState = Breeze.Persistence.EntityState;

namespace TempHire.Server.Mcp;

/// <summary>
/// The MCP server's only tools that change data: they let an editor change one existing address of
/// a staffing resource, through Quinn or another MCP client.
///
/// <para>Three tools, called in turn:</para>
/// <list type="number">
///   <item><c>choose_address_to_update</c>: which of the person's addresses.  If the user named
///   none and the person has several, or named one the person doesn't have, the user picks one.</item>
///   <item><c>complete_address</c>: the new address, completed by the address service (Photon).
///   If several addresses match, the user picks one, or cancels and gives a corrected address.</item>
///   <item><c>update_staffing_resource_address</c>: checks the change, shows the user the
///   current and the new address, and saves it only once the user confirms.</item>
/// </list>
/// <para>
/// The user answers those questions, not the model: they are MCP elicitations (<see cref="IUserInput"/>),
/// which the client shows the user.  So the model cannot pick for the user or confirm for them,
/// and neither can text in the data that the model reads.  A client that can't ask the user gets
/// a refusal, and nothing changes.
/// </para>
/// <para>What the update checks, whatever the model passes:</para>
/// <list type="bullet">
///   <item>The user is an editor.  The MCP server's filters also hide these tools from anyone else
///   and refuse their calls (Program.cs); this is the same check where the work happens, like the
///   save interceptor's.</item>
///   <item>The staffing resource and the address belong to the user's tenant.  The context's query
///   filter already hides every other tenant's rows, and its save refuses writes to them; the
///   tools check the rows' tenant as well, so a mistake in one place doesn't open the data.</item>
///   <item>The address belongs to the staffing resource.</item>
///   <item>Only an existing address changes, and only its street, city, state and ZIP code: no
///   new address, no other property, no other entity.</item>
///   <item>The validation rules pass, checked explicitly before the user is asked to confirm, with
///   the same check the save interceptor makes (<see cref="TempHirePersistenceManager.ValidateAsync"/>).</item>
/// </list>
/// <para>
/// The change is then saved through <see cref="TempHirePersistenceManager"/>, as a Breeze save
/// bundle built here: the save interceptor runs as for any client save - the editor check, the
/// entity-type check, the audit columns, the row version and the validation again - and the
/// row version catches an address someone else changed since it was read.
/// </para>
/// </summary>
[McpServerToolType]
public partial class AddressUpdateTools(
    TempHireDbContext db,
    PhotonAddressSuggester suggester,
    ValidationService validation,
    IDataProtectionProvider protection)
{
    /// <summary>The tools in this class: for editors only (see the MCP filters in Program.cs).</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>
    {
        "choose_address_to_update", "complete_address", "update_staffing_resource_address"
    };

    private const string NotAnEditor =
        "Access denied: only editors can change addresses, and this user can only read the staffing data. " +
        "Tell the user they can't make changes. Nothing was changed.";

    private const string CannotAsk =
        "This MCP client can't ask the user to choose or confirm, so addresses can't be changed from it. " +
        "Nothing was changed.";

    private readonly TempHireDbContext _db = db;
    private readonly PhotonAddressSuggester _suggester = suggester;
    private readonly ValidationService _validation = validation;
    private readonly IDataProtectionProvider _protection = protection;

    // ------------------------------------------------------------------------------------------
    // The tools, as MCP sees them
    // ------------------------------------------------------------------------------------------

    [McpServerTool(Name = "choose_address_to_update", Title = "Choose the address to update", ReadOnly = true)]
    [Description("Step 1 of changing an address (editors only): which of a staffing resource's existing " +
                 "addresses to change. Pass the address type if the user named one (home, work, mailing...). " +
                 "If they named none and the person has several, or named one the person doesn't have, the app " +
                 "asks the user to pick. Returns the address id and the current address, or that the user cancelled.")]
    public Task<object> ChooseAddressToUpdate(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("The staffing resource's id, from search_staffing_resources or get_staffing_resource")] Guid staffingResourceId,
        [Description("The address type the user named, e.g. 'home' or 'work'; leave out if they named none")] string addressType = null,
        CancellationToken cancellationToken = default) =>
        Run(server, context, input => ChooseAddressAsync(context.User, input, staffingResourceId, addressType, cancellationToken));

    [McpServerTool(Name = "complete_address", Title = "Complete an address", ReadOnly = true, OpenWorld = true)]
    [Description("Step 2 of changing an address (editors only): completes the new address the user gave with " +
                 "the address service, as one line, e.g. '908 W Capital Way, Tacoma, WA 98401'. If several " +
                 "addresses match, the app asks the user to pick one. Returns the completed address (street, city, " +
                 "state, ZIP code), that none matched, or that the user cancelled because none was right.")]
    public Task<object> CompleteAddress(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("The new address as the user gave it, house number first")] string address,
        CancellationToken cancellationToken = default) =>
        Run(server, context, input => CompleteAddressAsync(context.User, input, address, cancellationToken));

    [McpServerTool(Name = "update_staffing_resource_address", Title = "Update a staffing resource's address",
        ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Step 3 of changing an address (editors only): changes one existing address of a staffing " +
                 "resource to the completed address. Checks it, then the app shows the user the current and the new " +
                 "address and saves only if the user confirms. Returns whether it was updated, the rules it broke, " +
                 "or that the user cancelled. Can't add, delete or change anything else.")]
    public Task<object> UpdateStaffingResourceAddress(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("The staffing resource's id")] Guid staffingResourceId,
        [Description("The id of the address to change, from choose_address_to_update")] Guid addressId,
        [Description("House number and street, from complete_address")] string address1,
        [Description("City, from complete_address")] string city,
        [Description("State: two-letter code, from complete_address")] string state,
        [Description("ZIP code, from complete_address")] string zipcode,
        [Description("Second line (apartment, suite), only if the user gave one")] string address2 = null,
        CancellationToken cancellationToken = default) =>
        Run(server, context, input => UpdateAddressAsync(
            context.User, input,
            new AddressChange(staffingResourceId, addressId, address1, address2, city, state, zipcode),
            cancellationToken));

    /// <summary>Refuses a client that can't ask the user; otherwise runs the tool.</summary>
    private Task<object> Run(McpServer server, RequestContext<CallToolRequestParams> context, Func<IUserInput, Task<object>> tool)
    {
        var input = new McpUserInput(server, context, _protection);
        return input.Supported ? tool(input) : Task.FromResult<object>(new { updated = false, message = CannotAsk });
    }

    // ------------------------------------------------------------------------------------------
    // Step 1: which address
    // ------------------------------------------------------------------------------------------

    /// <summary>The address to change: the one named, the only one, or the one the user picks.</summary>
    internal async Task<object> ChooseAddressAsync(
        ClaimsPrincipal user, IUserInput input, Guid staffingResourceId, string addressType, CancellationToken cancellationToken)
    {
        if (!TempHirePolicies.CanEdit(user))
        {
            return new { chosen = false, message = NotAnEditor };
        }

        if (await ResourceAsync(staffingResourceId, cancellationToken) is not { } resource)
        {
            return new { chosen = false, message = "No staffing resource with that id was found. Nothing was changed." };
        }

        var addresses = await AddressesOf(resource.Id).ToListAsync(cancellationToken);
        if (addresses.Count == 0)
        {
            return new { chosen = false, message = $"{resource.Name} has no address to change, and adding one isn't possible here." };
        }

        var type = addressType?.Trim();
        var named = string.IsNullOrEmpty(type)
            ? []
            : addresses.Where(a => a.TypeName.Equals(type, StringComparison.OrdinalIgnoreCase)
                                   || a.TypeDisplayName.Equals(type, StringComparison.OrdinalIgnoreCase)
                                   || a.TypeDisplayName.StartsWith(type + " ", StringComparison.OrdinalIgnoreCase))
                .ToList();

        AddressRow chosen = null;
        if (named.Count == 1)
        {
            chosen = named[0];
        }
        else if (string.IsNullOrEmpty(type) && addresses.Count == 1)
        {
            chosen = addresses[0];
        }
        else
        {
            var question = string.IsNullOrEmpty(type)
                ? $"**{resource.Name}** has {addresses.Count} addresses. Which one do you want to update?"
                : $"**{resource.Name}** has no {type} address. Which address do you want to update?";

            var answer = input.Ask("address", Choice(question, addresses.Select(a => (a.Id.ToString(), a.Label))),
                McpUserInput.Fingerprint(resource.Id, addresses.Select(a => (a.Id, a.RowVersion))));

            chosen = Chosen(answer) is { } id ? addresses.FirstOrDefault(a => a.Id.ToString() == id) : null;
            if (chosen is null)
            {
                return new { chosen = false, message = "The user cancelled. Nothing was changed." };
            }
        }

        return new
        {
            chosen = true,
            staffingResourceId = resource.Id,
            staffingResource = resource.Name,
            addressId = chosen.Id,
            addressType = chosen.TypeDisplayName,
            currentAddress = chosen.Text,
            next = "Ask for the new address if the user hasn't given it, then complete it with complete_address."
        };
    }

    // ------------------------------------------------------------------------------------------
    // Step 2: complete the new address
    // ------------------------------------------------------------------------------------------

    /// <summary>The address the user gave, completed by Photon; the user picks if several match.</summary>
    internal async Task<object> CompleteAddressAsync(
        ClaimsPrincipal user, IUserInput input, string address, CancellationToken cancellationToken)
    {
        if (!TempHirePolicies.CanEdit(user))
        {
            return new { found = false, message = NotAnEditor };
        }

        var text = (address ?? "").Trim();
        var states = await _db.States.AsNoTracking().ToListAsync(cancellationToken);
        var candidates = Complete(text, await _suggester.SuggestAsync(text, cancellationToken), states);

        if (candidates.Count == 0)
        {
            return new
            {
                found = false,
                message = "The address service found no U.S. address like that. Ask the user to check it: " +
                          "house number, street, city and state. Nothing was changed."
            };
        }

        var completed = candidates[0];
        if (candidates.Count > 1)
        {
            var answer = input.Ask("match",
                Choice($"Several addresses match **{text}**. Which one is right?\n\n*Addresses {AddressController.Attribution}*",
                    candidates.Select((c, i) => (i.ToString(), c.Text))),
                McpUserInput.Fingerprint(text, candidates));

            completed = Chosen(answer) is { } chosen && int.TryParse(chosen, out var i) && i >= 0 && i < candidates.Count
                ? candidates[i]
                : null;
            if (completed is null)
            {
                return new
                {
                    found = false,
                    cancelled = true,
                    message = "None of the matches was right, and the user cancelled. Nothing was changed. " +
                              "Ask the user for a corrected address, then complete it again."
                };
            }
        }

        return new
        {
            found = true,
            address1 = completed.Address1,
            completed.City,
            state = completed.State,
            zipcode = completed.Zipcode,
            attribution = AddressController.Attribution,
            next = completed.Zipcode is null
                ? "The address service has no ZIP code for it: ask the user for it before updating."
                : "Update the address with update_staffing_resource_address."
        };
    }

    /// <summary>A completed address: TempHire's fields, with the state as its two-letter code.</summary>
    internal sealed record CompletedAddress(string Address1, string City, string State, string Zipcode)
    {
        public string Text => AddressText(Address1, null, City, State, Zipcode);
    }

    /// <summary>
    /// Photon's matches as TempHire addresses, filling the gaps of the address service from what the
    /// user typed:
    /// <list type="bullet">
    ///   <item>OpenStreetMap's U.S. house numbers have gaps, so a match may be only the street.  It
    ///   gets the house number the user gave, so "908 W Capital Way" completes to that house, not
    ///   to the street.  (The address form does the same, in its address completion.)</item>
    ///   <item>A match without a ZIP code gets the one the user gave, if any.</item>
    ///   <item>Matches in a state TempHire doesn't have are dropped: the address couldn't be saved.</item>
    /// </list>
    /// </summary>
    internal static List<CompletedAddress> Complete(string text, IEnumerable<PhotonAddress> matches, IEnumerable<State> states)
    {
        var number = HouseNumber().Match(text) is { Success: true } n ? n.Groups[1].Value : null;
        var zip = ZipCode().Matches(text).LastOrDefault()?.Value;
        var byName = states.ToDictionary(s => s.Name, s => s.ShortName, StringComparer.OrdinalIgnoreCase);

        return
        [
            .. matches
                .Where(m => m.State is not null && byName.ContainsKey(m.State))
                .Select(m => new CompletedAddress(
                    Address1: number is not null && !StartsWithNumber().IsMatch(m.Address1) ? $"{number} {m.Address1}" : m.Address1,
                    City: m.City,
                    State: byName[m.State],
                    Zipcode: m.Zipcode ?? zip))
                .Distinct()
        ];
    }

    /// <summary>The house number at the start of what the user typed: <c>908</c>, <c>12B</c>, <c>1200-A</c>.</summary>
    [GeneratedRegex(@"^\s*(\d+[A-Za-z]?(?:-\w+)?)\s+\S")]
    private static partial Regex HouseNumber();

    /// <summary>
    /// A house number at the start of a match: <c>908 Main St</c>, but not the street of
    /// <c>11th Avenue</c>, which is a street without a number.
    /// </summary>
    [GeneratedRegex(@"^\d+[A-Za-z]?(?:-\w+)?\s")]
    private static partial Regex StartsWithNumber();

    [GeneratedRegex(@"\b\d{5}(?:-\d{4})?\b")]
    private static partial Regex ZipCode();

    // ------------------------------------------------------------------------------------------
    // Step 3: check, confirm and save
    // ------------------------------------------------------------------------------------------

    /// <summary>The change the model asks for: which address, and its new street, city, state and ZIP code.</summary>
    internal sealed record AddressChange(
        Guid StaffingResourceId, Guid AddressId, string Address1, string Address2, string City, string State, string Zipcode);

    /// <summary>Checks the change, asks the user to confirm it, and saves it.</summary>
    internal async Task<object> UpdateAddressAsync(
        ClaimsPrincipal user, IUserInput input, AddressChange change, CancellationToken cancellationToken)
    {
        if (!TempHirePolicies.CanEdit(user))
        {
            return new { updated = false, message = NotAnEditor };
        }

        if (await ResourceAsync(change.StaffingResourceId, cancellationToken) is not { } resource)
        {
            return new { updated = false, message = "No staffing resource with that id was found. Nothing was changed." };
        }

        // The address, by its id alone, then checked: one of another resource is refused rather than
        // simply not found, so the model learns it passed the wrong id.
        var tenantId = _db.CurrentTenantId;
        var stored = await _db.Addresses.AsNoTracking()
            .Include(a => a.AddressType)
            .Include(a => a.State)
            .FirstOrDefaultAsync(a => a.Id == change.AddressId, cancellationToken);
        if (stored is null || stored.TenantId != tenantId)
        {
            return new { updated = false, message = "No address with that id was found. Nothing was changed." };
        }

        if (stored.StaffingResourceId != resource.Id)
        {
            return new
            {
                updated = false,
                message = $"That address isn't one of {resource.Name}'s. Nothing was changed. " +
                          "Choose the address again with choose_address_to_update."
            };
        }

        var stateCode = change.State?.Trim();
        var state = string.IsNullOrEmpty(stateCode)
            ? null
            : await _db.States.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ShortName == stateCode || s.Name == stateCode, cancellationToken);
        if (state is null)
        {
            return new { updated = false, message = $"'{change.State}' is not a U.S. state TempHire knows. Nothing was changed." };
        }

        // The stored address with the new values: only these five properties can change.
        var updated = Copy(stored);
        updated.Address1 = Clean(change.Address1);
        updated.Address2 = Clean(change.Address2);
        updated.City = Clean(change.City);
        updated.StateId = state.Id;
        updated.Zipcode = Clean(change.Zipcode);

        var from = AddressText(stored.Address1, stored.Address2, stored.City, stored.State?.ShortName, stored.Zipcode);
        var to = AddressText(updated.Address1, updated.Address2, updated.City, state.ShortName, updated.Zipcode);
        if (from == to)
        {
            return new { updated = false, message = $"{resource.Name}'s {stored.AddressType.DisplayName} already is {to}. Nothing was changed." };
        }

        // The validation rules, explicitly and before the user is asked: the same check the save
        // interceptor makes on every Breeze save.
        var manager = new TempHirePersistenceManager(_db, user, _validation);
        var errors = await manager.ValidateAsync(updated, EntityState.Modified, cancellationToken);
        if (errors.Count > 0)
        {
            return new
            {
                updated = false,
                message = "The new address breaks the validation rules, so it can't be saved. Nothing was changed. " +
                          "Tell the user what to correct.",
                errors = errors.Select(e => e.ErrorMessage).Distinct()
            };
        }

        var confirmation = input.Ask("confirm",
            Confirmation(
                $"Update **{resource.Name}**'s {stored.AddressType.DisplayName}?\n\n" +
                $"- **Current:** {from}\n" +
                $"- **New:** {to}"),
            McpUserInput.Fingerprint(resource.Id, stored.Id, stored.RowVersion, updated.Address1, updated.Address2,
                updated.City, updated.StateId, updated.Zipcode));

        if (!confirmation.IsAccepted)
        {
            return new { updated = false, message = "The user cancelled the update. Nothing was changed." };
        }

        try
        {
            await manager.SaveChangesAsync(SaveBundle(stored, updated), cancellationToken: cancellationToken);
        }
        catch (EntityErrorsException e)
        {
            return new
            {
                updated = false,
                message = "The save was refused. Nothing was changed.",
                errors = e.EntityErrors.Select(x => x.ErrorMessage).Distinct()
            };
        }
        catch (DbUpdateConcurrencyException)
        {
            return new
            {
                updated = false,
                message = "Someone else changed or deleted this address meanwhile. Nothing was changed. " +
                          "Start again with choose_address_to_update."
            };
        }

        return new
        {
            updated = true,
            staffingResource = resource.Name,
            addressType = stored.AddressType.DisplayName,
            previousAddress = from,
            newAddress = to,
            // What was saved, for the client to refresh, and to remember in the conversation
            // (Agent/ToolResultChanges).
            changed = new[]
            {
                new
                {
                    entityType = nameof(Address),
                    id = stored.Id,
                    staffingResourceId = resource.Id,
                    summary = $"Updated {resource.Name}'s {stored.AddressType.DisplayName} from {from} to {to}."
                }
            }
        };
    }

    /// <summary>
    /// A Breeze save bundle that modifies one address: the entity with its new values, and the
    /// original values of what changed, as the Breeze client sends them.  The row version is the one
    /// read; the save interceptor increments it, and EF Core's UPDATE matches only that version.
    /// </summary>
    private static JObject SaveBundle(Address stored, Address updated)
    {
        var entity = JObject.FromObject(updated, JsonSerializer.Create(new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        }));

        entity["entityAspect"] = new JObject
        {
            ["entityTypeName"] = $"{nameof(Address)}:#{typeof(Address).Namespace}",
            ["defaultResourceName"] = "Addresses",
            ["entityState"] = "Modified",
            ["originalValuesMap"] = new JObject
            {
                [nameof(Address.Address1)] = stored.Address1,
                [nameof(Address.Address2)] = stored.Address2,
                [nameof(Address.City)] = stored.City,
                [nameof(Address.StateId)] = stored.StateId,
                [nameof(Address.Zipcode)] = stored.Zipcode
            }
        };

        return new JObject { ["entities"] = new JArray(entity), ["saveOptions"] = new JObject() };
    }

    /// <summary>The address's own values, without its navigation properties.</summary>
    private static Address Copy(Address a) => new()
    {
        Id = a.Id,
        Address1 = a.Address1,
        Address2 = a.Address2,
        City = a.City,
        StaffingResourceId = a.StaffingResourceId,
        AddressTypeId = a.AddressTypeId,
        Zipcode = a.Zipcode,
        Primary = a.Primary,
        StateId = a.StateId,
        Created = a.Created,
        CreatedUser = a.CreatedUser,
        Modified = a.Modified,
        ModifyUser = a.ModifyUser,
        RowVersion = a.RowVersion,
        TenantId = a.TenantId
    };

    private static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ------------------------------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------------------------------

    private sealed record ResourceRow(Guid Id, string Name);

    /// <summary>
    /// The staffing resource, if it is the user's tenant's.  The query filter already limits the
    /// query to that tenant; the explicit tenant check repeats it here, where the change is made.
    /// </summary>
    private async Task<ResourceRow> ResourceAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = _db.CurrentTenantId;
        var row = await _db.StaffingResources.AsNoTracking()
            .Where(r => r.Id == id && r.TenantId == tenantId)
            .Select(r => new { r.Id, r.FirstName, r.MiddleName, r.LastName })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null || tenantId is null
            ? null
            : new ResourceRow(row.Id, string.Join(" ", new[] { row.FirstName, row.MiddleName, row.LastName }
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())));
    }

    private sealed record AddressRow(
        Guid Id, string TypeName, string TypeDisplayName, bool Primary, int RowVersion,
        string Address1, string Address2, string City, string State, string Zipcode)
    {
        public string Text => AddressText(Address1, Address2, City, State, Zipcode);

        /// <summary>How a choice shows it: <c>Home Address: 908 W Capital Way, Tacoma, WA 98401 (primary)</c>.</summary>
        public string Label => $"{TypeDisplayName}: {Text}{(Primary ? " (primary)" : "")}";
    }

    /// <summary>The resource's addresses, primary first, each checked to be the user's tenant's.</summary>
    private IQueryable<AddressRow> AddressesOf(Guid resourceId)
    {
        var tenantId = _db.CurrentTenantId;
        return _db.Addresses.AsNoTracking()
            .Where(a => a.StaffingResourceId == resourceId && a.TenantId == tenantId)
            .OrderByDescending(a => a.Primary).ThenBy(a => a.AddressType.DisplayName)
            .Select(a => new AddressRow(a.Id, a.AddressType.Name, a.AddressType.DisplayName, a.Primary, a.RowVersion,
                a.Address1, a.Address2, a.City, a.State.ShortName, a.Zipcode));
    }

    /// <summary>One line: <c>908 W Capital Way, Apt 2, Tacoma, WA 98401</c>.</summary>
    private static string AddressText(string address1, string address2, string city, string state, string zipcode) =>
        string.Join(", ", new[] { address1, address2, city, string.Join(" ", new[] { state, zipcode }.Where(p => !string.IsNullOrEmpty(p))) }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

    // ------------------------------------------------------------------------------------------
    // Questions
    // ------------------------------------------------------------------------------------------

    /// <summary>The form field a choice is made in.</summary>
    internal const string ChoiceField = "choice";

    /// <summary>Pick one of <paramref name="options"/> (value, label): a single required choice.</summary>
    internal static ElicitRequestParams Choice(string message, IEnumerable<(string Value, string Label)> options) => new()
    {
        Message = message,
        RequestedSchema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
            {
                [ChoiceField] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                {
                    Title = "Choose one",
                    OneOf = [.. options.Select(o => new ElicitRequestParams.EnumSchemaOption { Const = o.Value, Title = o.Label })]
                }
            },
            Required = [ChoiceField]
        }
    };

    /// <summary>Confirm or cancel: a form without fields.</summary>
    internal static ElicitRequestParams Confirmation(string message) => new()
    {
        Message = message,
        RequestedSchema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>()
        }
    };

    /// <summary>The value the user chose, if they accepted.</summary>
    private static string Chosen(ElicitResult answer) =>
        answer.IsAccepted && answer.Content?.TryGetValue(ChoiceField, out var value) == true
            && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
}
