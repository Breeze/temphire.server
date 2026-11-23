using Breeze.AspNetCore;
using Microsoft.AspNetCore.Mvc;

namespace TempHire.Server.Controllers;

/// <summary>
/// What a client-composed Breeze query may cost: at most <see cref="MaxTake"/> rows, and
/// <c>expand</c> / <c>select</c> paths at most <see cref="MaxDepth"/> navigations deep.  See
/// <see href="https://breeze.github.io/breeze-server-v3/guide/security">Breeze's security guide</see>.
///
/// <para>
/// <b>Registered once, for every action</b> (<see cref="AddTo"/>, from Program.cs), so a controller
/// added later is bounded whether or not its author thought about it.  An action that doesn't return
/// an <c>IQueryable</c> falls out of the filter at once, so the API's other endpoints are unaffected.
/// </para>
/// <para>
/// <b>A tighter limit goes on the controller or the action</b>, as a <c>[BreezeQueryFilter]</c> of its
/// own: the filter nearest the action runs first and answers the query, and the global one then finds
/// nothing left to do.  So such an attribute must state <i>both</i> limits - one that leaves
/// <c>MaxTake</c> out lifts the row cap, since Breeze's default is unlimited (-1).  Use the constants
/// here:
/// </para>
/// <code>
/// [BreezeQueryFilter(MaxTake = BreezeQueryLimits.MaxTake, MaxDepth = 1)]
/// </code>
/// <para>
/// These bound what a query may cost; they don't decide who sees what.  That is the queryable each
/// action returns, the tenant query filters in <c>TempHireDbContext</c>, and <c>[Authorize]</c>.
/// </para>
/// </summary>
public static class BreezeQueryLimits
{
    /// <summary>
    /// The most rows a query returns: Breeze adds <c>Take(1000)</c> when the client asks for more or
    /// doesn't say.  It doesn't cap the collections an <c>expand</c> brings, nor results that aren't
    /// EF queries.
    /// </summary>
    /// <remarks>
    /// So that it takes the same rows every time, every query action returns its set ordered by key
    /// (the search: by last name, then key); without an order EF Core also warns about a
    /// <c>Take</c> on every query.  A client's <c>orderBy</c> replaces that order: Breeze applies its
    /// first term with <c>OrderBy</c>.  A new query action should order its set the same way.
    /// </remarks>
    public const int MaxTake = 1000;

    /// <summary>
    /// How many navigations deep <c>expand</c> and <c>select</c> may go: from a staffing resource,
    /// <c>Addresses.State</c> but not <c>Addresses.StaffingResource.PhoneNumbers</c>.  Deeper is
    /// 400 Bad Request.
    /// </summary>
    public const int MaxDepth = 2;

    /// <summary>Registers the limits for every action.</summary>
    public static MvcOptions AddTo(MvcOptions options)
    {
        options.Filters.Add(new BreezeQueryFilterAttribute { MaxTake = MaxTake, MaxDepth = MaxDepth });
        return options;
    }
}
