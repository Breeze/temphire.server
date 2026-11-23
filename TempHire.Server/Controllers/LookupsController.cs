using Breeze.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TempHire.Model;
using TempHire.Server.Persistence;

namespace TempHire.Server.Controllers;

/// <summary>
/// The lookup tables: states, address types, phone number types and rate types.  Breeze queries.
/// </summary>
/// <remarks>
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
/// <para>
/// No <c>expand</c> or navigation <c>select</c> at all (<c>MaxDepth = 0</c>): a lookup is a flat
/// table the client caches whole, with nothing to reach through it.  The row cap is the app's
/// (<see cref="BreezeQueryLimits"/>), restated because this attribute replaces the global one.
/// </para>
/// </remarks>
[Route("api/lookups/[action]")]
[BreezeQueryFilter(MaxTake = BreezeQueryLimits.MaxTake, MaxDepth = 0)]
[Authorize]
public class LookupsController(TempHireDbContext dbContext) : ControllerBase
{
    /// <summary>Every lookup table, in one bundle.</summary>
    /// <remarks>
    /// The client loads this once at sign-in.  Not a Breeze query: it takes no query string.
    /// </remarks>
    /// <response code="200">The address types, phone number types, rate types and states.</response>
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

    /// <summary>The address types (home, mailing, work).</summary>
    /// <response code="200">The address types the query selects.</response>
    [HttpGet]
    public IQueryable<AddressType> AddressTypes() => dbContext.AddressTypes;

    /// <summary>The phone number types (home, mobile, work).</summary>
    /// <response code="200">The phone number types the query selects.</response>
    [HttpGet]
    public IQueryable<PhoneNumberType> PhoneNumberTypes() => dbContext.PhoneNumberTypes;

    /// <summary>The rate types (hourly, daily, weekly, monthly).</summary>
    /// <response code="200">The rate types the query selects.</response>
    [HttpGet]
    public IQueryable<RateType> RateTypes() => dbContext.RateTypes;

    /// <summary>The US states and the District of Columbia.</summary>
    /// <response code="200">The states the query selects.</response>
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
