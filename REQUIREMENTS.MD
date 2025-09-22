# Architecting a Reusable Blazor Playground for Semantic Kernel

## Introduction: The Strategic Value of Internal Development Tooling

The integration of Large Language Models (LLMs) into enterprise applications has transitioned from a novel concept to a strategic imperative. Frameworks like Microsoft's Semantic Kernel provide an orchestration layer that lets developers combine LLMs with traditional code, plugins, and memory to build sophisticated AI agents. However, harnessing this power effectively requires more than SDK access — it demands robust internal tooling for faster experimentation, standardized practices, and visibility into the AI's reasoning process.

A generic chat interface is insufficient for the nuanced work of prompt engineering, plugin validation, and plan debugging. This document describes an architectural design for a reusable Blazor UI component: a development playground for Semantic Kernel applications. The component is intended to be modular, self-contained, and distributable — consumable as a Razor Class Library (RCL) and packaged for NuGet.

The design rests on four core pillars:

- Reusability: Deliver the component as a Blazor RCL and package it for NuGet distribution.
- Encapsulation: Manage styling and client-side logic inside the component to avoid host conflicts.
- Configurability: Use .NET dependency injection to decouple the UI from backend AI service configuration.
- Extensibility: Provide a clear service abstraction so the component can support new models, plugins, and Semantic Kernel features without changing core UI logic.


---

## 1. Foundation: A Self-Contained and Distributable Razor Class Library

The RCL project type is the ideal foundation because it lets you bundle Razor components, static assets, and C# logic into a single distributable unit.

### 1.1 Structuring the RCL for Maintainability

Adopt a standardized folder layout inspired by clean architecture to keep concerns separated and the codebase maintainable. Example structure:

- /Components
  - Root component: `SemanticKernelPlayground.razor`
  - Child components: `ConfigurationPanel.razor`, `ChatInteractionPanel.razor`, `MetadataPanel.razor`
- /Services
  - Internal service abstractions and helper classes (e.g., `IPlaygroundKernelService`)
- /Models
  - UI-specific view models (e.g., `ChatMessageViewModel`)
- /wwwroot
  - Static web assets (global CSS, JS interop files)

This separation (presentation, logic, service contracts, assets) improves modularity and testing.


### 1.2 Managing Static Web Assets for Zero-Friction Consumption

Key goals:

- Zero-configuration: Consumers should not need to manually add `<link>` or `<script>` tags.
- Use Blazor mechanisms to isolate assets.

Recommended approaches:

- CSS isolation: Use `.razor.css` files alongside components (e.g., `ChatInteractionPanel.razor.css`) so the build tooling scopes styles automatically.
- JavaScript isolation / collocated JS: Place component-specific JS in `.razor.js` files next to the corresponding component and dynamically import via `IJSRuntime` when the component renders.
- Document render mode requirements: Since the playground is interactive, instruct consumers to use an interactive render mode such as `@rendermode="InteractiveServer"` or `@rendermode="InteractiveAuto"`.


### 1.3 Packaging Strategy and NuGet Configuration

Enable package generation in the `.csproj` and include metadata for a professional package. Example `.csproj` properties to include:

| Property | Example value | Purpose |
|---|---:|---|
| `PackageId` | `YourCompany.SemanticKernel.Playground` | NuGet package identifier |
| `Version` | `1.0.0-beta` | Semantic version |
| `Authors` | `YourCompany` | Author metadata |
| `Description` | `A reusable Blazor UI component providing a development playground for Semantic Kernel applications.` | Package description |
| `PackageReadmeFile` | `README.md` | Included package readme |
| `PackageLicenseExpression` | `MIT` | License expression |
| `PackageProjectUrl` | `https://github.com/your-repo` | Project URL |

When you run `dotnet pack --configuration Release` the built `.nupkg` will include the compiled assembly and static web assets under `_content/{PACKAGE_ID}/...`.


---

## 2. Designing the Playground UI and Experience

A playground's value depends on its UI/UX — it must be powerful yet intuitive. Leveraging a mature Blazor component library (e.g., MudBlazor) accelerates development and ensures a polished aesthetic.

### 2.1 Three-Panel Layout Paradigm

A three-panel layout helps separate responsibilities:

- Left (Configuration): Model selection, LLM parameter controls, plugin browser.
- Center (Interaction): Chat history and prompt input — the primary workspace.
- Right (Metadata & Plan): Observability and debugging (planner visualization, token counts, execution time).


### 2.2 Configuration Panel

Key controls:

- Model selection: A dropdown (e.g., `MudSelect<string>`) populated by the service.
- LLM parameter controls: Encapsulate sliders and numeric inputs in a `ParameterControls.razor` child component (temperature, top-p, max tokens, frequency/presence penalties). Provide tooltips for each control.
- Plugin browser: A tree view (e.g., `MudTreeView`) showing loaded plugins and functions.


### 2.3 Interaction Panel

Core features:

- Multi-modal chat: `ChatInteractionPanel.razor` built atop a chat component like `MudChat`.
- Prompt input: Multi-line `MudTextField` and optional file/image upload using `MudFileUpload` with an immediate client-side preview (read `IBrowserFile` and generate a base64 URL).
- Planner toggle: Explicit `MudSwitch` labeled "Use Planner" to indicate whether to invoke the Semantic Kernel planner or a direct chat completion.
- Plan visualization: Show planner results in an indented `pre` block, `MudExpansionPanel`, or a `MudTreeView`.


### 2.4 Metadata Panel

Provide diagnostics after each interaction:

- Prompt tokens, completion tokens, total tokens, execution duration.
- Collapsible raw JSON response for deeper analysis.


---

## 3. Service Architecture and Semantic Kernel Integration

Decouple UI from the Semantic Kernel SDK via DI and a well-defined interface. The RCL defines the `IPlaygroundKernelService` interface; the consuming application implements it and registers the implementation in DI.

### 3.1 Service Abstractions for Decoupling

- The RCL declares an IoC contract (`IPlaygroundKernelService`) but does not instantiate `Kernel` directly.
- Consumers provide the concrete implementation and manage secrets/configuration.


### 3.2 Implementation Guidance for `IPlaygroundKernelService`

Typical responsibilities of the consumer-provided implementation:

- Inject `IConfiguration` or `IOptions<T>` for API keys, endpoints, and model names.
- Build and configure a `Kernel` instance and register chat/completion services and plugins.
- Implement streaming and planner methods, converting multi-modal prompts into the appropriate `ChatHistory`.
- Register the service as scoped in Blazor Server apps: `builder.Services.AddScoped<IPlaygroundKernelService, MyPlaygroundKernelService>();` — do not register as singleton.


### 3.3 Core Service Interface (IPlaygroundKernelService)

Example contract (descriptive):

- `Task<IEnumerable<string>> GetAvailableModelsAsync()`
  - Returns model IDs for selection.
- `Task<IEnumerable<PluginInfo>> GetAvailablePluginsAsync()`
  - Returns plugin metadata for the plugin browser.
- `IAsyncEnumerable<string> ExecuteChatStreamAsync(ChatHistory history, AiSettings settings)`
  - Executes a streaming chat request and yields text chunks.
- `Task<PlanResult> ExecutePlanAsync(string goal, AiSettings settings)`
  - Invokes the planner and returns a `PlanResult` containing the final answer and plan representation.

This interface guarantees the UI remains agnostic to the concrete backend.


---

## 4. Real-Time Interactivity with Asynchronous Streaming

A modern chat UX shows responses in real time by consuming asynchronous streams (`IAsyncEnumerable<T>`). Implement this carefully with Blazor's rendering model.

### 4.1 Consuming `IAsyncEnumerable<T>`

- `IPlaygroundKernelService.ExecuteChatStreamAsync` returns `IAsyncEnumerable<string>` representing incremental text chunks.
- In Blazor Server the service can call SDK streaming methods and yield chunks directly.
- In Blazor WASM, typically the client calls a backend endpoint that streams the SDK response and the client consumes it via `HttpClient` as an async enumerable.


### 4.2 The Typewriter Effect: Rendering the Stream

In `ChatInteractionPanel.razor`:

1. Add an empty AI message bubble to the chat history.
2. Call `ExecuteChatStreamAsync` and iterate with `await foreach`.
3. For each chunk append to the AI bubble and call `StateHasChanged()` to force immediate DOM updates.

Wrap the `await foreach` in `try...finally` and use a boolean `IsStreaming` to lock UI controls while streaming.


### 4.3 State Management, Cancellation, and Errors

- Use a `CancellationTokenSource` to support user-initiated cancellation ("Stop Generating").
- In the `finally` block set `IsStreaming = false` so UI is re-enabled.
- Properly handle exceptions and propagate meaningful diagnostics to the metadata panel.


---

## 5. Packaging, Distribution, and Consumption Guide

### 5.1 The dotnet pack Workflow

Use the .NET CLI to create the package:

```bash
dotnet pack --configuration Release
```

The resulting `.nupkg` will be in `bin/Release` and include static web assets under `_content/{PACKAGE_ID}/...`.


### 5.2 Step-by-Step Consumption Guide

1. Install the package:

```bash
dotnet add package YourCompany.SemanticKernel.Playground
```

1. Implement and register the `IPlaygroundKernelService` in the consuming application.

Example minimal implementation (consumer side):

```csharp
using Microsoft.SemanticKernel;

public class MyPlaygroundKernelService : IPlaygroundKernelService
{
    private readonly Kernel _kernel;

    public MyPlaygroundKernelService(IConfiguration configuration)
    {
        var builder = Kernel.CreateBuilder();
        builder.AddAzureOpenAIChatCompletion(
            deploymentName: configuration["AzureOpenAI:DeploymentName"],
            endpoint: configuration["AzureOpenAI:Endpoint"],
            apiKey: configuration["AzureOpenAI:ApiKey"]);

        // Add plugins here
        // builder.Plugins.AddFromType<MyPlugin>();

        _kernel = builder.Build();
    }

    public async IAsyncEnumerable<string> ExecuteChatStreamAsync(ChatHistory history, AiSettings settings)
    {
        var chatCompletionService = _kernel.GetRequiredService<IChatCompletionService>();
        var promptSettings = new OpenAIPromptExecutionSettings
        {
            Temperature = settings.Temperature,
            TopP = settings.TopP,
            MaxTokens = settings.MaxTokens
        };

        await foreach (var update in chatCompletionService.GetStreamingChatMessageContentsAsync(
            history,
            executionSettings: promptSettings))
        {
            yield return update.Content;
        }
    }

    // ...other methods implementing the interface
}
```

Register in `Program.cs`:

```csharp
// In Program.cs
builder.Services.AddScoped<IPlaygroundKernelService, MyPlaygroundKernelService>();
```

3. Add the component to a page and use an interactive render mode:

```razor
@page "/sk-playground"

<YourCompany.SemanticKernel.Playground.Components.SemanticKernelPlayground @rendermode="InteractiveServer" />
```

1. (Optional) Add the component namespace to `_Imports.razor` for cleaner markup:

```razor
@using YourCompany.SemanticKernel.Playground.Components
```


---

## Conclusion: A Strategic Asset for AI Development

This design describes a reusable, enterprise-grade Blazor UI component for Semantic Kernel development. Key strengths include:

- A self-contained RCL packaged for NuGet distribution.
- Loose coupling to backend logic via `IPlaygroundKernelService` and DI.
- Real-time streaming support with `IAsyncEnumerable<T>` for responsive UX.

Potential future enhancements:

- Dynamic plugin argument forms generated from plugin signatures.
- Advanced agent and message-flow visualizations for multi-agent systems.
- Memory management UI for inspecting and controlling Kernel memory stores.
- Themeability via CSS custom properties to allow brand alignment.

By investing in a high-quality internal playground, organizations can lower the barrier to AI development, foster experimentation, and accelerate delivery of AI-powered solutions.