namespace Mira.Core.Interfaces;

using Mira.Core.Models;

public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
