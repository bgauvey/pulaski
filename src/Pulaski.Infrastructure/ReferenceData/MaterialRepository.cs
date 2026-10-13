using Pulaski.Application.ReferenceData;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.ReferenceData;

internal sealed class MaterialRepository(DbSession session) : IMaterialRepository
{
    private const string IsActive = "SELECT is_active FROM core.material WHERE id = @Id FOR SHARE";

    public async Task<bool> IsActiveAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<bool?>(IsActive, new { Id = id }, cancellationToken) == true;
}
