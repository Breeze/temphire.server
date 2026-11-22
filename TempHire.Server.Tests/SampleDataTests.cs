using IdeaBlade.Validation;
using Microsoft.Extensions.DependencyInjection;
using TempHire.Model;
using TempHire.Model.SampleData;
using TempHire.Server.Validation;
using Xunit;

namespace TempHire.Server.Tests;

/// <summary>
/// The sample data a new database is seeded with passes every rule the server checks on a save:
/// the validation metadata and the data annotations.  A new rule that the sample data breaks fails
/// here, not as a sample record nobody can save.
/// </summary>
public sealed class SampleDataTests
{
    [Fact]
    public async Task Every_seeded_row_passes_the_servers_rules()
    {
        var metadata = new ValidationMetadataFile(Path.Combine(AppContext.BaseDirectory, "validation.metadata.json"));
        var services = new ServiceCollection();
        services.AddIdeaBladeValidation(v => v.AddMetadataJson(metadata.Json).AddDataAnnotations("breeze"));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var validation = scope.ServiceProvider.GetRequiredService<ValidationService>();

        // Both tenants' data in one store: rules like "at least one address" read it with store(type).
        var entities = SampleDataProvider.SampleEntities();
        var errors = await validation.ValidateEntitiesAsync(new ValidationEntityStore([.. entities]));

        Assert.NotEmpty(entities);
        Assert.Empty(errors.Select(e => $"{e.ModelType} {e.Id}: {e.ErrorMessage}"));

        // A positive control: the same check finds a broken row, so an empty list means something.
        // (A date of birth of tomorrow breaks "18 when hired" too.)
        var broken = entities.OfType<StaffingResource>().First();
        broken.DateOfBirth = DateTime.Today.AddDays(1);
        errors = await validation.ValidateEntitiesAsync(new ValidationEntityStore([.. entities]));
        Assert.Contains("PLAUSIBLE_DATE_OF_BIRTH", errors.Select(e => e.Id));
    }
}
