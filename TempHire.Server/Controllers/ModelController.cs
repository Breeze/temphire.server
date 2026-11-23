using IdeaBlade.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TempHire.Model;
using TempHire.Server.Persistence;

namespace TempHire.Server.Controllers;

/// <summary>
/// The entity model, as Breeze metadata.
/// </summary>
/// <remarks>
/// The Breeze metadata the client's <c>MetadataStore</c> is built from, generated from the EF Core
/// model.  This is the replacement for DevForce's <c>.ibmmx</c> model file plus the code generation
/// that produced <c>TempHireEntities</c>.
///
/// <para>
/// The model is shared by every workspace, so it has a controller of its own, like the lookups
/// (<see cref="LookupsController"/>) and the validation metadata (<see cref="ValidationController"/>):
/// the client loads all three at sign-in.  Each workspace queries and saves through its own
/// controller, such as <see cref="ResourceMgtController"/>.
/// </para>
/// <para>
/// <c>[Authorize]</c> is the replacement for <c>[RequiresAuthentication]</c> on the DevForce
/// <c>EntityBase</c>: DevForce would not hand out the model to an anonymous caller either.  It needs
/// a signed-in user but no particular role, because the client loads it whatever the user's role.
/// </para>
/// </remarks>
[Route("api/model/[action]")]
[Authorize]
public class ModelController(
    TempHireDbContext dbContext,
    ValidationService validation)
    : ControllerBase
{
    /// <summary>The Breeze metadata: every entity type, its properties, keys and navigation properties.</summary>
    /// <remarks>
    /// Breeze's metadata document, as a JSON string, which a Breeze client imports into its
    /// <c>MetadataStore</c>.  It includes the rules that come from the entities' data annotations;
    /// the other rules are at <c>/api/validation/Metadata</c>.
    /// </remarks>
    /// <response code="200">The metadata, as a JSON string.</response>
    [HttpGet]
    public string Metadata() => new TempHirePersistenceManager(dbContext, User, validation).Metadata();
}
