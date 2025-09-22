using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using DevCanopy;
using DevCanopy.Playground.Services;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Collections.Generic;
using System;
using System.Net.Http;
using System.Threading.Tasks;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Minimal in-memory implementation for local testing
builder.Services.AddScoped<IPlaygroundKernelService, DevCanopyAppPlaygroundService>();

await builder.Build().RunAsync();

// Minimal service implementation
public class DevCanopyAppPlaygroundService : IPlaygroundKernelService
{
    public Task<IEnumerable<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<string>>(new[] { "local-test-model" });
    }

    public Task<IEnumerable<PluginInfo>> GetAvailablePluginsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<PluginInfo>>(Array.Empty<PluginInfo>());
    }

    public async IAsyncEnumerable<string> ExecuteChatStreamAsync(object chatHistory, AiSettings settings, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = chatHistory?.GetType().GetProperty("Text")?.GetValue(chatHistory)?.ToString() ?? "";
        var response = $"Echo: {prompt}";

        foreach (var word in response.Split(' '))
        {
            await Task.Delay(120, cancellationToken);
            yield return word + " ";
        }
    }

    public Task<PlanResult> ExecutePlanAsync(string goal, AiSettings settings, CancellationToken cancellationToken = default)
    {
        var metadata = $"{{ \"tokens\": 10, \"durationMs\": 123 }}";
        return Task.FromResult(new PlanResult($"Planned answer for: {goal}", metadata));
    }
}
