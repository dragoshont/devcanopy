using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.IO;

// Resolve DevCanopy project path robustly — prefer src/DevCanopy/DevCanopy.csproj from repository root
string repoRoot = Directory.GetCurrentDirectory();
string candidate1 = Path.GetFullPath(Path.Combine(repoRoot, "src", "DevCanopy", "DevCanopy.csproj"));
string candidate2 = Path.GetFullPath(Path.Combine(repoRoot, "..", "DevCanopy", "DevCanopy.csproj"));
string candidate3 = Path.GetFullPath(Path.Combine(repoRoot, "..", "src", "DevCanopy", "DevCanopy.csproj"));

string devCanopyPath = null;
if (File.Exists(candidate1)) devCanopyPath = candidate1;
else if (File.Exists(candidate3)) devCanopyPath = candidate3;
else if (File.Exists(candidate2)) devCanopyPath = candidate2;
else
{
    Console.Error.WriteLine($"Cannot find DevCanopy.csproj. Checked:\n  {candidate1}\n  {candidate3}\n  {candidate2}");
    return;
}

Console.WriteLine($"Launching DevCanopy project: {devCanopyPath}");

// Start the DevCanopy app via 'dotnet run' without forcing a URL (let system pick a port)
var psi = new ProcessStartInfo("dotnet", $"run --project \"{devCanopyPath}\"")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true,
};

var proc = Process.Start(psi);
if (proc is null)
{
    Console.Error.WriteLine("Failed to start DevCanopy process.");
    return;
}

string? discoveredUrl = null;

_ = Task.Run(async () =>
{
    string? line;
    var stdout = proc.StandardOutput;
    var stderr = proc.StandardError;

    // Read both stdout and stderr in case the hosting logs go to either stream
    while ((stdout is not null && (line = await stdout.ReadLineAsync()) != null) || (stderr is not null && (line = await stderr.ReadLineAsync()) != null))
    {
        if (line is null) continue;
        Console.WriteLine(line);

        // Typical runtime lines contain 'Now listening on: <urls>' or 'Now listening on <urls>'
        if (line.Contains("Now listening on", StringComparison.OrdinalIgnoreCase) || line.Contains("Now listening on:", StringComparison.OrdinalIgnoreCase))
        {
            // extract the URL using regex
            var m = Regex.Match(line, @"https?://[^\s,;]+", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                discoveredUrl = m.Value.TrimEnd('/');
                var playground = discoveredUrl + "/playground";
                Console.WriteLine($"Playground available at: {playground}");
                OpenBrowser(playground);
                break;
            }
        }

        // Some SDK messages may include 'Application started' followed by listening urls on a following line
        if (line.Contains("Application started", StringComparison.OrdinalIgnoreCase) && discoveredUrl is null)
        {
            // continue reading; next iteration will catch the listening line when printed
        }
    }
});

Console.WriteLine("Started DevCanopy; attempting to detect listening URL and open playground. Press Ctrl+C to exit.");
await (proc?.WaitForExitAsync() ?? Task.CompletedTask);

static void OpenBrowser(string url)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Process.Start("xdg-open", url);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
    }
    catch
    {
        // best-effort
    }
}
