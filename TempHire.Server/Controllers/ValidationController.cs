using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TempHire.Server.Validation;

namespace TempHire.Server.Controllers;

/// <summary>
/// The validation rules.
/// </summary>
/// <remarks>
/// Serves the validation metadata, the counterpart of <see cref="ModelController.Metadata"/>: the
/// client loads both at sign-in.  The Breeze metadata brings the rules from the entities' data
/// annotations; this brings the rest (<c>Validation/validation.metadata.json</c>), which the client
/// installs into Breeze, so both are checked as the user edits and before a save, and again by the
/// server when it saves.
///
/// <para>
/// Like the Breeze metadata, it needs a signed-in user but no particular role.
/// </para>
/// </remarks>
[Route("api/validation/[action]")]
[Authorize]
public class ValidationController(ValidationMetadataFile metadata) : ControllerBase
{
    /// <summary>The validation metadata: every rule beyond the entities' data annotations.</summary>
    /// <remarks>
    /// IdeaBlade Validation metadata (<c>Validation/validation.metadata.json</c>): rules per entity
    /// type and property, with their messages.  The client checks them as the user edits; the
    /// server checks them again on every save.
    /// </remarks>
    /// <response code="200">The validation metadata.</response>
    [HttpGet]
    [Produces("application/json")]
    public ContentResult Metadata() => Content(metadata.Json, "application/json");
}
