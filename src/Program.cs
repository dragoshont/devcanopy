// Program.cs - Initial Version
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.Extensions.DependencyInjection;

var builder = Kernel.CreateBuilder();

// Step 1: Register dependencies and model BEFORE building the kernel
builder.Services.AddHttpClient(); // Provides HttpClient via DI
builder.AddOllamaChatCompletion(
   // modelId: "phi4-mini",
    modelId: "llama3.1",
    endpoint: new Uri("http://localhost:11434")
);

// Step 2: Register plugin type so its [KernelFunction] methods are discovered
builder.Plugins.AddFromType<WeatherPlugin>();

// Step 3: Build kernel after all registrations
var kernel = builder.Build();
Console.WriteLine("Kernel initialized with Ollama connector and WeatherPlugin loaded.");

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
var executionSettings = new PromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};
Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine("Auto function invocation enabled."); Console.ResetColor();
Console.WriteLine("Auto function invocation enabled.");

var chat = kernel.GetRequiredService<IChatCompletionService>();

// Add a system prompt to guide the model
var history = new ChatHistory("You are a weather assistant. When users ask about weather or pollen, use your tools to get the information and respond directly with the data. Do not explain your process, ask for permission, or mention that you're checking anything. Just provide the weather information immediately.");

Console.ForegroundColor = ConsoleColor.White; Console.BackgroundColor = ConsoleColor.DarkBlue; Console.WriteLine("Ask me about the weather and pollen levels in a city or place. Type 'exit' to quit."); Console.ResetColor();

while (true)
{
    Console.ForegroundColor = ConsoleColor.Green; Console.Write("User > "); Console.ResetColor();
    var userInput = Console.ReadLine();
    if (userInput?.ToLower() == "exit") break;

    history.AddUserMessage(userInput!);

    var result = await chat.GetChatMessageContentAsync(history, executionSettings, kernel);

    Console.ForegroundColor = ConsoleColor.Blue; Console.WriteLine($"Agent > {result.Content}"); Console.ResetColor();
    history.Add(result);
}