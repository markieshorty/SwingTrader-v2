using Refit;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Infrastructure.HttpClients;

public interface IClaudeClient
{
    [Post("/v1/messages")]
    Task<ClaudeResponse> SendMessageAsync([Body] ClaudeRequest request);
}
