using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TempHire.Server.Controllers;
using Xunit;

namespace TempHire.Server.Tests
{
    /// <summary>
    /// <see cref="BreezeController.StaffingResources"/> returns the resources alone; the client says
    /// what else to load with <c>expand</c>.  The first port included the addresses and phone numbers
    /// in every query, lists included, and the client's <c>withId</c> expanded them again.
    ///
    /// <para>
    /// The client's <c>StaffingResourceRepository.withId</c> expands all five collections.  As one SQL
    /// query, the JOINs multiply the rows (addresses x phone numbers x rates x ...), and EF Core logged
    /// a warning on every load.  It is a split query: one query for the resource, one per collection.
    /// </para>
    /// </summary>
    public sealed class ResourceQueryTests : IDisposable
    {
        private readonly SaveFixture _db = new();

        public void Dispose() => _db.Dispose();

        [Fact]
        public void Resources_come_without_their_children_unless_expanded()
        {
            var (resource, _, _) = _db.StoreValidResource();
            var commands = new CommandCounter();
            using var context = _db.NewContext(commands);

            var loaded = new BreezeController(context, validation: null!).StaffingResources().ToList();

            var single = Assert.Single(loaded);
            Assert.Equal(resource.Id, single.Id);
            Assert.Empty(single.Addresses);
            Assert.Empty(single.PhoneNumbers);
            Assert.Equal(1, commands.Readers);
        }

        [Fact]
        public void An_expanded_resource_loads_with_one_query_per_collection()
        {
            var (resource, address, phone) = _db.StoreValidResource();
            var commands = new CommandCounter();
            using var context = _db.NewContext(commands);

            // What Breeze's [BreezeQueryFilter] does with the client's where and expand.
            var loaded = new BreezeController(context, validation: null!)
                .StaffingResources()
                .Include(r => r.Addresses)
                .Include(r => r.PhoneNumbers)
                .Include(r => r.Rates)
                .Include(r => r.Skills)
                .Include(r => r.WorkExperience)
                .Where(r => r.Id == resource.Id)
                .ToList();

            var single = Assert.Single(loaded);
            Assert.Equal(address.Id, Assert.Single(single.Addresses).Id);
            Assert.Equal(phone.Id, Assert.Single(single.PhoneNumbers).Id);
            Assert.Equal(6, commands.Readers);
        }

        /// <summary>Counts the queries a context sends.</summary>
        private sealed class CommandCounter : DbCommandInterceptor
        {
            public int Readers { get; private set; }

            public override InterceptionResult<DbDataReader> ReaderExecuting(
                DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
            {
                Readers++;
                return result;
            }
        }
    }
}
