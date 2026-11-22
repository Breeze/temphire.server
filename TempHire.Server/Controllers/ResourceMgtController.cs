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
/// </summary>
[Route("api/resourcemgt/[action]")]
[BreezeQueryFilter]
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

    /// <summary>
    /// The workspace's write endpoint.  Replaces DevForce's <c>EntityManager.SaveChangesAsync</c>
    /// round trip; the change set arrives as a Breeze "save bundle".  Editors only: anyone else gets
    /// 403 before the bundle is read.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = TempHirePolicies.EditStaffingData)]
    public async Task<ActionResult<SaveResult>> SaveChanges([FromBody] JObject saveBundle, CancellationToken cancellationToken)
    {
        return await PersistenceManager.SaveChangesAsync(saveBundle, cancellationToken: cancellationToken);
    }

    /// <summary>
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
    /// </summary>
    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    public IQueryable<StaffingResource> StaffingResources()
    {
        return Context.StaffingResources.AsSplitQuery();
    }

    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    public IQueryable<Address> Addresses() => Context.Addresses;

    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    public IQueryable<PhoneNumber> PhoneNumbers() => Context.PhoneNumbers;

    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    public IQueryable<Rate> Rates() => Context.Rates;

    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    public IQueryable<Skill> Skills() => Context.Skills;

    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
    public IQueryable<WorkExperienceItem> WorkExperienceItems() => Context.WorkExperienceItems;

    /// <summary>
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
    /// </summary>
    [HttpGet]
    [Authorize(Policy = TempHirePolicies.ReadStaffingData)]
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
