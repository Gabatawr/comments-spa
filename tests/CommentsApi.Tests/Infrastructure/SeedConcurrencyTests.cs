using Comments.Application.Abstractions.Persistence;
using Comments.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommentsApi.Tests.Infrastructure;

/// <summary>
/// Regression for QA-301: <c>POST /api/dev/seed</c> must never collide with a concurrent normal
/// INSERT on <c>PK_comments</c>. The seeder reserves its id range under a table lock before COPY.
/// </summary>
public class SeedConcurrencyTests : IntegrationTestBase
{
    [Fact]
    public async Task Seed_and_concurrent_inserts_never_collide_on_the_primary_key()
    {
        const int seedCount = 20000;
        const int concurrentInserts = 50;

        using var seedScope = Factory.Services.CreateScope();
        using var writeScope = Factory.Services.CreateScope();

        var seeder = seedScope.ServiceProvider.GetRequiredService<ICommentSeeder>();
        var repository = writeScope.ServiceProvider.GetRequiredService<ICommentRepository>();
        var unitOfWork = writeScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var seedTask = Task.Run(() => seeder.SeedAsync(new SeedOptions(seedCount, 2000, 3, 1000, Clear: false)));

        // Overlap the normal create path with the bulk COPY window.
        await Task.Delay(25);
        var inserted = 0;
        for (var i = 0; i < concurrentInserts; i++)
        {
            await repository.AddAsync(new Comment
            {
                UserName = $"RaceUser{i:D3}",
                Email = $"race{i:D3}@example.com",
                TextHtml = "race",
                TextPlain = "race",
                CreatedAt = DateTime.UtcNow,
            });
            await unitOfWork.SaveChangesAsync();
            inserted++;
        }

        var outcome = await seedTask;

        Assert.Equal(seedCount, outcome.Created);
        Assert.Equal(concurrentInserts, inserted);

        var totals = await repository.GetTotalsAsync();
        Assert.Equal(seedCount + concurrentInserts, totals.TotalComments);
    }
}
