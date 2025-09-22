using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DevCanopy.Playground.Services
{
    public record PluginInfo(string Name, string Description);
    public record AiSettings(double Temperature = 0.7, double TopP = 1.0, int MaxTokens = 512);
    public record PlanResult(string Answer, string PlanJson);

    public interface IPlaygroundKernelService
    {
        Task<IEnumerable<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<PluginInfo>> GetAvailablePluginsAsync(CancellationToken cancellationToken = default);
        IAsyncEnumerable<string> ExecuteChatStreamAsync(object chatHistory, AiSettings settings, CancellationToken cancellationToken = default);
        Task<PlanResult> ExecutePlanAsync(string goal, AiSettings settings, CancellationToken cancellationToken = default);
    }
}