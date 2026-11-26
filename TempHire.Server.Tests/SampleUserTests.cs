using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TempHire.Model;
using TempHire.Model.Identity;
using TempHire.Model.SampleData;
using Xunit;

namespace TempHire.Server.Tests;

/// <summary>
/// The sample users seeding creates (<see cref="SampleDataProvider.Users"/>): in each company an
/// editor, a reader and a user with no role, created on every start if they are missing.
/// </summary>
public sealed class SampleUserTests : IDisposable
{
    private readonly SqliteConnection _business = new("DataSource=:memory:");
    private readonly SqliteConnection _identity = new("DataSource=:memory:");

    public SampleUserTests()
    {
        _business.Open();
        _identity.Open();
        using var db = Business();
        db.Database.EnsureCreated();
        using var identity = Identity();
        identity.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _business.Dispose();
        _identity.Dispose();
    }

    [Fact]
    public void Each_company_has_an_editor_a_reader_and_a_user_without_a_role()
    {
        Seed();

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["anna@harbor.test"] = TempHireRoles.Editor,
                ["ben@harbor.test"] = TempHireRoles.Reader,
                ["emma@harbor.test"] = null,
                ["carla@summit.test"] = TempHireRoles.Editor,
                ["david@summit.test"] = TempHireRoles.Reader,
                ["felix@summit.test"] = null
            },
            RolesByUser());
    }

    /// <summary>
    /// A database seeded before a sample user was added gets it on the next start, and the users
    /// already there keep what they have.
    /// </summary>
    [Fact]
    public void A_missing_sample_user_is_added_on_the_next_start()
    {
        Seed();
        using (var identity = Identity())
        {
            identity.Users.RemoveRange(identity.Users.Where(u => u.UserName == "emma@harbor.test" || u.UserName == "felix@summit.test"));
            identity.SaveChanges();
        }
        var anna = PasswordHashOf("anna@harbor.test");

        Seed();

        Assert.Equal(6, RolesByUser().Count);
        Assert.Null(RolesByUser()["emma@harbor.test"]);
        Assert.Equal(TempHireRoles.Editor, RolesByUser()["anna@harbor.test"]);
        Assert.Equal(anna, PasswordHashOf("anna@harbor.test"));
    }

    private void Seed()
    {
        using var db = Business();
        using var identity = Identity();
        SampleDataProvider.Initialize(db, identity);
    }

    /// <summary>Each user's role, or null for none.</summary>
    private Dictionary<string, string> RolesByUser()
    {
        using var identity = Identity();
        var roles = identity.Roles.ToDictionary(r => r.Id, r => r.Name);
        var userRoles = identity.UserRoles.ToList();
        return identity.Users.ToList().ToDictionary(
            u => u.UserName,
            u => userRoles.Where(ur => ur.UserId == u.Id).Select(ur => roles[ur.RoleId]).SingleOrDefault());
    }

    private string PasswordHashOf(string userName)
    {
        using var identity = Identity();
        return identity.Users.Single(u => u.UserName == userName).PasswordHash;
    }

    private TempHireDbContext Business() =>
        new(new DbContextOptionsBuilder<TempHireDbContext>().UseSqlite(_business).Options);

    private TempHireIdentityDbContext Identity() =>
        new(new DbContextOptionsBuilder<TempHireIdentityDbContext>().UseSqlite(_identity).Options);
}
