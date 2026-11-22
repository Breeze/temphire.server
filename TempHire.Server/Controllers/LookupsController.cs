using Breeze.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TempHire.Model;
using TempHire.Server.Persistence;

namespace TempHire.Server.Controllers;

/// <summary>
/// The lookup tables: states, address types, phone number types and rate types.
///
/// <para>
/// The lookups are a cross-cutting concern, not part of one workspace: the client loads them all
/// with one query at sign-in (<see cref="AllTypes"/>) into a "master" EntityManager, and every unit
/// of work, in any workspace, starts as a copy of it.  That is the replacement for
/// <c>TempHire/GlobalCache.cs</c> plus <c>GlobalCacheRepository&lt;T&gt;</c>: DevForce kept a
/// separate, long-lived EntityManager full of lookup entities and seeded every new manager from it
/// (<c>QueryStrategy.CacheOnly</c>).  See <c>entity-manager-provider.ts</c>.  A lookup only one
/// workspace uses would belong in that workspace's controller instead.
/// </para>
/// <para>
/// A signed-in user, no particular role: the client loads them whatever the user's role.
/// </para>
/// </summary>
[Route("api/lookups/[action]")]
[BreezeQueryFilter]
[Authorize]
public class LookupsController(TempHireDbContext dbContext) : ControllerBase
{
    /// <summary>
    /// One-shot load of every lookup table at sign-in: all the lookup types, in one bundle.
    /// </summary>
    [HttpGet]
    public LookupBundle AllTypes()
    {
        // This returns a bundle rather than an IQueryable, so there is nothing for the query filter
        // to apply; tell it to stand down.
        this.SkipBreezeQueryFilter();

        return new LookupBundle
        {
            AddressTypes = [.. dbContext.AddressTypes.OrderBy(t => t.DisplayName)],
            PhoneNumberTypes = [.. dbContext.PhoneNumberTypes.OrderBy(t => t.Name)],
            RateTypes = [.. dbContext.RateTypes.OrderBy(t => t.Sequence)],
            States = [.. dbContext.States.OrderBy(s => s.Name)]
        };
    }

    [HttpGet]
    public IQueryable<AddressType> AddressTypes() => dbContext.AddressTypes;

    [HttpGet]
    public IQueryable<PhoneNumberType> PhoneNumberTypes() => dbContext.PhoneNumberTypes;

    [HttpGet]
    public IQueryable<RateType> RateTypes() => dbContext.RateTypes;

    [HttpGet]
    public IQueryable<State> States() => dbContext.States;
}

/// <summary>
/// Shape of the <c>AllTypes</c> response.  Each collection is materialised into the client's
/// master EntityManager, which then seeds every sandbox manager - the Breeze equivalent of
/// DevForce's global cache.
/// </summary>
public class LookupBundle
{
    public IEnumerable<AddressType> AddressTypes { get; set; }
    public IEnumerable<PhoneNumberType> PhoneNumberTypes { get; set; }
    public IEnumerable<RateType> RateTypes { get; set; }
    public IEnumerable<State> States { get; set; }
}
