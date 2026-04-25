using Mira.Core.Entities;

namespace Mira.Core.Interfaces;

public interface IPersonRepository
{
    public Task<Person> FindPersonByNameAsync(string name);
    public Task<IReadOnlyList<Person>> GetAllPeopleAsync();
    public Task SavePersonAsync(Person person);
}
