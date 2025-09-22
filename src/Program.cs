// Program.cs - Initial Version
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.Extensions.DependencyInjection;

var builder = Kernel.CreateBuilder();

// Allow selecting model at runtime via environment variable, default to phi4-mini
var modelId = Environment.GetEnvironmentVariable("LLAMA_MODEL") ?? "llama3.1";

// Step 1: Register dependencies and model BEFORE building the kernel
// Configure HttpClient with automatic GZip/Deflate decompression to reduce transfer size.
builder.Services
    .AddHttpClient("default")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    });
builder.AddOllamaChatCompletion(
    modelId: modelId,
    endpoint: new Uri("http://localhost:11434")
);

// Step 2: Register plugin type so its [KernelFunction] methods are discovered
builder.Plugins.AddFromType<WeatherPlugin>();

// Step 3: Build kernel after all registrations
var kernel = builder.Build();
Console.WriteLine($"Kernel initialized with Ollama connector and Model {modelId}.");

// Diagnostic: list discovered functions to verify detection
try
{
    var discovered = kernel.Plugins.GetFunctionsMetadata();
    foreach (var f in discovered.Where(f => f.PluginName == nameof(WeatherPlugin)))
    {
        // Print discovered function with colored segments: plugin, function, description
        Console.Write("Discovered function: ");
        Console.ForegroundColor = ConsoleColor.Yellow; Console.Write(f.PluginName); Console.ResetColor();
        Console.Write(".");
        Console.ForegroundColor = ConsoleColor.Cyan; Console.Write(f.Name); Console.ResetColor();
        Console.Write(" -> ");
        Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine(f.Description); Console.ResetColor();
    }
}
catch (Exception ex)
{
    Console.WriteLine($"(Diagnostic) Could not enumerate plugin functions: {ex.Message}");
}

// Step 4: Enabling Auto-Invocation
// This is the most critical step for enabling agentic behavior.
// We create PromptExecutionSettings and set FunctionChoiceBehavior to Auto.
// This tells the LLM that it is allowed to choose and call a function if it
// deems it necessary to answer the user's prompt.
// Use Ollama-specific execution settings so we can set Temperature=0 (deterministic)
var executionSettings = new OllamaPromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
    Temperature = 0
};
Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine("Auto function invocation enabled."); Console.ResetColor();

var chat = kernel.GetRequiredService<IChatCompletionService>();

// Add a stricter system prompt to enforce tool use for real-time facts
var history = new ChatHistory(
    "You are a realtime weather and air-quality assistant. For ANY user question about current weather, pollen, air quality, forecasts, or other time-sensitive facts you MUST call the available plugin functions to fetch live data and you MUST base your response solely on the plugin output.\n" +
    "Do NOT answer from your internal knowledge, do NOT preface your answer with knowledge-cutoff text, and do NOT invent or hallucinate.\n" +
    "When a function/tool is required, return exactly one function call (the kernel will execute it) and do not include extra commentary. If the user asks anything unrelated to live/weather/air-quality data, answer normally."
);

Console.ForegroundColor = ConsoleColor.White; Console.WriteLine("Ask me about the weather and pollen levels in a city or place. Type 'exit' to quit."); Console.ResetColor();

while (true)
{
    Console.ResetColor();
    Console.ForegroundColor = ConsoleColor.Green; Console.Write("User > "); Console.ResetColor();
    var userInput = Console.ReadLine();
    if (userInput?.ToLower() == "exit") break;

    history.AddUserMessage(userInput!);

    // Spinner setup
    using var ctsSpinner = new CancellationTokenSource();
    var spinnerTask = Task.Run(async () =>
    {
        var frames = new[] { '|', '/', '-', '\\' };
        var i = 0;
        while (!ctsSpinner.Token.IsCancellationRequested)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"\rThinking {frames[i++ % frames.Length]} ");
            Console.ResetColor();
            await Task.Delay(100, ctsSpinner.Token).ConfigureAwait(false);
        }
        // Do not clear here; main thread will clear after spinner stops to avoid race conditions.
    }, ctsSpinner.Token);

    ChatMessageContent? result = null;
    Exception? callEx = null;
    try
    {
        result = await chat.GetChatMessageContentAsync(history, executionSettings, kernel).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        callEx = ex;
    }
    finally
    {
        try { ctsSpinner.Cancel(); } catch { }
        try { await spinnerTask.ConfigureAwait(false); } catch { }
    }

    // Clear spinner line (ensure any residual 'Thinking' text is removed before output)
    Console.Write("\r" + new string(' ', 50) + "\r");

    if (callEx != null)
    {
        Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"Agent > Error: {callEx.Message}"); Console.ResetColor();
        continue;
    }

    Console.ForegroundColor = ConsoleColor.Blue; Console.WriteLine($"Agent > {result?.Content}"); Console.ResetColor();
    if (result != null) history.Add(result);
}