using Mira.Core.Entities;

namespace Mira.Core.Interfaces;

public interface IPersonRepository
{
    Task<Person?> FindPersonByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Person>> GetAllPeopleAsync(CancellationToken ct = default);
    Task SavePersonAsync(Person person, CancellationToken ct = default);
}
