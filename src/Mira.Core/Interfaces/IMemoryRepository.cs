using Mira.Core.Entities;

namespace Mira.Core.Interfaces;

public interface IMemoryRepository
{
    public Task SaveMemoryEntryAsync(MemoryEntry entry);

    public Task<IReadOnlyList<MemoryEntry>> GetMemoryAsync(string query);

    public Task<IReadOnlyList<MemoryEntry>> GetRecentMemoryAsync(int count);
}
