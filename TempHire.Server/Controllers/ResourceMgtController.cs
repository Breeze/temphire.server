using Breeze.AspNetCore;
using Breeze.Persistence;
using IdeaBlade.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using TempHire.Model;
using TempHire.Model.Projections;
using TempHire.Server.Auth;
using TempHire.Server.Persistence;

namespace TempHire.Server.Controllers;

/// <summary>
/// The Resource Management workspace: staffing resources and their details, the search, and saves.
/// Breeze queries and a Breeze save.
/// </summary>
/// <remarks>
/// The Resource Management workspace's data: its queries, its search and its saves.  The client's
/// <c>ResourceMgtUnitOfWork</c> talks to this controller and no other.
///
/// <para>
/// One controller per client workspace, so that a bigger app doesn't end up with one controller
/// full of unrelated endpoints.  What every workspace needs is elsewhere: the model's metadata
/// (<see cref="ModelController"/>) and the lookups loaded at sign-in (<see cref="LookupsController"/>).
/// </para>
/// <para>
/// In the DevForce version there was no controller at all: the client's <c>EntityManager</c> talked
/// to the DevForce EntityServer over WCF, and query composition happened client-side against
/// <c>EntityQuery&lt;T&gt;</c>.  Breeze keeps the same feel - the client app still writes
/// <c>EntityQuery.from('StaffingResources').where(...)</c> - but the query travels to the server as
/// a JSON payload and <c>[BreezeQueryFilter]</c> applies it to the <see cref="IQueryable{T}"/> that
/// each method returns.
/// </para>
/// <para>
/// Roles (see <see cref="TempHirePolicies"/>): the company's staffing data needs the Editor or
/// Reader role (<see cref="TempHirePolicies.ReadStaffingData"/>), and saving needs the Editor role
/// (<see cref="TempHirePolicies.EditStaffingData"/>).  <see cref="TempHirePersistenceManager"/>
/// checks the Editor role again for every save.
/// </para>
/// <para>
/// Query limits (<see cref="BreezeQueryLimits"/>): at most 1000 rows everywhere.  The aggregate root,
/// <see cref="StaffingResources"/>, may expand two levels (the app's limit), so a resource can come
/// with its addresses and their states.  Every other query may expand one level: an address its
/// address type, state or staffing resource, but nothing beyond them.  Those actions say so with
/// their own <c>[BreezeQueryFilter]</c>; the row cap comes from the global one.  Each query returns its set
/// ordered by key (the search by last name, then key), so the row cap takes the same rows every
/// time; a client's <c>orderBy</c> replaces that order.
/// </para>
/// </remarks>
[Route("api/resourcemgt/[action]")]
[Authorize]
public class ResourceMgtController(TempHireDbContext dbContext, ValidationService validation)
    : ControllerBase
{
    private readonly TempHireDbContext _dbContext = dbContext;
    private readonly ValidationService _validation = validation;
    private TempHirePersistenceManager _persistenceManager;

    /// <summary>
    /// Created lazily: <see cref="ControllerBase.User"/> is not populated until after the controller
    /// has been constructed, and the persistence manager needs it to stamp the audit columns.
    /// </summary>
    private TempHirePersistenceManager PersistenceManager =>
        _persistenceManager ??= new TempHirePersistenceManager(_dbContext, User, _validation);

    private TempHireDbContext Context => _dbContext;

    /// <summary>Saves a change set: added, modified and deleted entities, in one transaction.</summary>
    /// <remarks>
    /// The workspace's write endpoint.  Replaces DevForce's <c>EntityManager.SaveChangesAsync</c>
    /// round trip; the change set arrives as a Breeze "save bundle" (<c>{"entities": [...],
    /// "saveOptions": {...}}</c>, each entity with its <c>entityAspect</c>), which a Breeze client
    /// builds.  Editors only: anyone else gets 403 before the bundle is read.  Every rule is checked
    /// again on the server; a save that breaks one saves nothing.
    /// </remarks>
    /// <param name="saveBundle">The Breeze save bundle.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">Saved: the saved entities, with their new row versions, and any key mappings.</response>
    /// <response code="403">
    /// Nothing saved, also when the change set breaks a validation rule or includes a read-only type
    /// (Breeze's entity-errors problem, with each entity's errors), or touches another company's data.
    /// </response>
    /// <response code="409">Someone else changed one of the entities since it was loaded.</response>
    [HttpPost]
    [Consumes("application/json")]
    [Authorize(Policy = TempHirePolicies.EditStaffingData)]
    public async Task<ActionResult<SaveResult>> SaveChanges([FromBody] JObject saveBundle, CancellationToken cancellationToken)
    {
        return await PersistenceManager.SaveChangesAsync(saveBundle, cancellationToken: cancellationToken);
    }

    /// <summary>The staffing resources, with any related entities the query expands.</summary>
    /// <remarks>
    /// Aggregate root query: the resources alone, unless the client asks for related entities with
    /// <c>.expand(...)</c>.  The client decides what a load brings: its
    /// <c>StaffingResourceRepository.withId</c> expands every child collection, as the original's
    /// <c>StaffingResourceRepository.GetKeyQuery</c> included the addresses and phone numbers.
    /// That was the repository's key query only; a list or a grid of resources is a projection
    /// (<see cref="StaffingResourceListItems"/>), not resources with their children.
    ///
    /// <para>
    /// A split query, for the expanded load: one SQL query for the resources and one per
    /// collection.  As a single query, the JOINs multiply the rows (addresses x phone numbers x
    /// rates x skills x work experience per resource), and EF Core warns about it.
    /// </para>
    /// </remarks>
    /// <response code="200">The staffing resources the query selects.</response>
    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    public IQueryable<StaffingResource> StaffingResources()
    {
        return Context.StaffingResources.OrderBy(r => r.Id).AsSplitQuery();
    }

    /// <summary>The staffing resources' addresses.</summary>
    /// <response code="200">The addresses the query selects.</response>
    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    [BreezeQueryFilter(MaxDepth = 1)]
    public IQueryable<Address> Addresses() => Context.Addresses.OrderBy(a => a.Id);

    /// <summary>The staffing resources' phone numbers.</summary>
    /// <response code="200">The phone numbers the query selects.</response>
    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    [BreezeQueryFilter(MaxDepth = 1)]
    public IQueryable<PhoneNumber> PhoneNumbers() => Context.PhoneNumbers.OrderBy(p => p.Id);

    /// <summary>The staffing resources' rates.</summary>
    /// <response code="200">The rates the query selects.</response>
    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    [BreezeQueryFilter(MaxDepth = 1)]
    public IQueryable<Rate> Rates() => Context.Rates.OrderBy(r => r.Id);

    /// <summary>The staffing resources' skills.</summary>
    /// <response code="200">The skills the query selects.</response>
    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    [BreezeQueryFilter(MaxDepth = 1)]
    public IQueryable<Skill> Skills() => Context.Skills.OrderBy(s => s.Id);

    /// <summary>The staffing resources' work experience.</summary>
    /// <response code="200">The work experience items the query selects.</response>
    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    [BreezeQueryFilter(MaxDepth = 1)]
    public IQueryable<WorkExperienceItem> WorkExperienceItems() => Context.WorkExperienceItems.OrderBy(w => w.Id);

    /// <summary>
    /// Searches the staffing resources: one row each, with the primary address and phone number.
    /// </summary>
    /// <remarks>
    /// Backs the search grid.  Port of <c>DomainServices/Services/StaffingResourceSearchService.cs</c>,
    /// whose <c>Simple(text)</c> built a LINQ predicate over first/middle/last name, the primary
    /// address and the primary phone number, then projected into
    /// <see cref="StaffingResourceListItem"/>.
    ///
    /// <para>
    /// The predicate now runs on the server; it used to be composed on the client and shipped to the
    /// DevForce EntityServer as an expression tree.  Everything else is unchanged, including the
    /// ordering by last name.  The result type is not an entity, so Breeze returns it to the client
    /// as a plain DTO - the direct analogue of DevForce's <c>IKnownType</c> projection.
    /// </para>
    /// </remarks>
    /// <param name="searchText">
    /// Text to look for in the first, middle and last name, and in the primary address and phone
    /// number.  Leave it out for everyone.
    /// </param>
    /// <response code="200">The matching resources, ordered by last name.</response>
    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    [BreezeQueryFilter(MaxDepth = 1)]
    public IQueryable<StaffingResourceListItem> StaffingResourceListItems(string searchText = null)
    {
        var query = Context.StaffingResources.AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var text = searchText.Trim();

            query = query.Where(x =>
                x.FirstName.Contains(text) ||
                x.MiddleName.Contains(text) ||
                x.LastName.Contains(text) ||
                x.Addresses.Any(a => a.Primary && (
                    a.Address1.Contains(text) ||
                    a.Address2.Contains(text) ||
                    a.City.Contains(text) ||
                    a.Zipcode.Contains(text) ||
                    a.State.Name.Contains(text) ||
                    a.State.ShortName.Contains(text))) ||
                x.PhoneNumbers.Any(p => p.Primary && (
                    p.AreaCode.Contains(text) ||
                    p.Number.Contains(text))));
        }

        return query
            .OrderBy(x => x.LastName)
            .ThenBy(x => x.Id)
            .Select(x => new
            {
                Resource = x,
                PrimaryAddress = x.Addresses.FirstOrDefault(a => a.Primary),
                PrimaryPhone = x.PhoneNumbers.FirstOrDefault(p => p.Primary)
            })
            .Select(x => new StaffingResourceListItem
            {
                Id = x.Resource.Id,
                FirstName = x.Resource.FirstName,
                MiddleName = x.Resource.MiddleName,
                LastName = x.Resource.LastName,
                FullName = string.IsNullOrEmpty(x.Resource.MiddleName)
                    ? x.Resource.FirstName + " " + x.Resource.LastName
                    : x.Resource.FirstName + " " + x.Resource.MiddleName + " " + x.Resource.LastName,
                Address1 = x.PrimaryAddress.Address1,
                Address2 = x.PrimaryAddress.Address2,
                City = x.PrimaryAddress.City,
                State = x.PrimaryAddress.State.ShortName,
                Zipcode = x.PrimaryAddress.Zipcode,
                AreaCode = x.PrimaryPhone.AreaCode,
                Number = x.PrimaryPhone.Number,
                PhoneNumber = "(" + x.PrimaryPhone.AreaCode + ") " + x.PrimaryPhone.Number
            });
    }
}
