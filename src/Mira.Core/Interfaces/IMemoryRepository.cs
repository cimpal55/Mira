using Mira.Core.Entities;

namespace Mira.Core.Interfaces;

public interface IMemoryRepository
{
    Task SaveMemoryEntryAsync(MemoryEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<MemoryEntry>> SearchMemoryAsync(string query, CancellationToken ct = default);

    Task<IReadOnlyList<MemoryEntry>> GetRecentMemoryAsync(int count, CancellationToken ct = default);
}
