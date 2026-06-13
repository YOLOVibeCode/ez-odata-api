using EzOdata.Data;
using Microsoft.EntityFrameworkCore;

namespace EzOdata.Admin;

/// <summary>
/// Caches whether first-run setup has completed (any user exists). Checked by middleware
/// on every request while incomplete, so the answer is cached once true (spec 03 §4).
/// </summary>
public sealed class SetupState
{
    private volatile bool _isComplete;

    public async Task<bool> IsCompleteAsync(SystemDbContext db, CancellationToken ct)
    {
        if (_isComplete) return true;

        var anyUsers = await db.Users.IgnoreQueryFilters().AnyAsync(ct);
        if (anyUsers) _isComplete = true;
        return anyUsers;
    }

    public void MarkComplete() => _isComplete = true;
}
