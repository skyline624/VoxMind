using Microsoft.EntityFrameworkCore;
using VoxMind.Core.Database;

namespace VoxMind.Tests.Unit;

/// <summary>
/// IDbContextFactory en mémoire utilisé par tous les tests qui ont besoin
/// d'une base partagée entre plusieurs DbContext (les services prod
/// créent un contexte par opération via le factory).
/// Le nom de DB est unique par instance pour isoler les tests.
/// </summary>
internal sealed class TestDbContextFactory : IDbContextFactory<VoxMindDbContext>
{
    private readonly DbContextOptions<VoxMindDbContext> _options;

    public TestDbContextFactory()
    {
        _options = new DbContextOptionsBuilder<VoxMindDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
    }

    public VoxMindDbContext CreateDbContext() => new(_options);
}
