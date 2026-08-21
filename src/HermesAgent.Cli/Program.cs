using HermesAgent.Agent;
using HermesAgent.Cli;
using HermesAgent.Core.Abstractions;
using HermesAgent.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

// ─── Bootstrap ──────────────────────────────────────────────────────────────

var userHermesDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes");

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile(Path.Combine(userHermesDir, "config.json"), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("HERMES_")
    .AddCommandLine(args)
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

services.Configure<HermesOptions>(config.GetSection(HermesOptions.SectionName));
services.AddHermes();

var provider = services.BuildServiceProvider();

// ─── CLI dispatch ─────────────────────────────────────────────────────────

var command = args.Length > 0 ? args[0] : "chat";

switch (command)
{
    case "chat" or "":
        await RunChatAsync(provider);
        break;
    case "skills":
        await RunSkillsAsync(provider);
        break;
    case "memory":
        await RunMemoryAsync(provider);
        break;
    case "history":
        await RunHistoryAsync(provider);
        break;
    case "version":
        AnsiConsole.MarkupLine("[bold cyan]Hermes Agent[/] for .NET — v1.0.0");
        break;
    default:
        AnsiConsole.MarkupLine($"[red]Unknown command:[/] {command}");
        break;
}

// ─── Chat REPL ────────────────────────────────────────────────────────────

static async Task RunChatAsync(IServiceProvider sp)
{
    var agent = sp.GetRequiredService<IAgent>();
    using var cts = new CancellationTokenSource();
    using var spinner = new ResponseSpinner();
    Guid? currentSession = null;

    AnsiConsole.Write(new FigletText("Hermes").Color(Color.Cyan1));
    AnsiConsole.MarkupLine("[dim]The self-improving AI agent — .NET edition[/]\n");

    while (!cts.Token.IsCancellationRequested)
    {
        var input = AnsiConsole.Prompt(new TextPrompt<string>("[bold green]You>[/]").AllowEmpty());
        if (string.IsNullOrWhiteSpace(input)) continue;

        if (input == "/new") { currentSession = null; Console.WriteLine("New session started."); continue; }
        if (input == "/exit") break;

        AnsiConsole.Markup("[bold blue]Hermes>[/] ");
        spinner.Start();
        try
        {
            await foreach (var evt in agent.RunStreamingAsync(input, currentSession, cts.Token))
            {
                switch (evt)
                {
                    case AgentEvent.TextDelta d:
                        if (spinner.IsRunning) spinner.Stop();
                        Console.Write(d.Delta);
                        break;
                    case AgentEvent.ToolStarted t:
                        if (spinner.IsRunning) spinner.Stop();
                        AnsiConsole.MarkupLine($"\n  [dim]⚙ {t.ToolName}...[/]");
                        spinner.Start($"running {t.ToolName}");
                        break;
                    case AgentEvent.ToolCompleted:
                        // Keep spinning with whatever label is current; next LLM round resumes
                        // "thinking" when ToolStarted's label is replaced on the next TextDelta cycle.
                        break;
                    case AgentEvent.TurnCompleted:
                        // Next turn begins with a fresh LLM call.
                        spinner.SetLabel("thinking");
                        break;
                    case AgentEvent.AgentFinished f:
                        if (spinner.IsRunning) spinner.Stop();
                        currentSession = currentSession ?? Guid.NewGuid(); // Note: session ID should be captured from loop
                        AnsiConsole.MarkupLine($"\n[dim]({f.Result.TurnsUsed} turns, {f.Result.Duration.TotalSeconds:0.0}s)[/]");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            if (spinner.IsRunning) spinner.Stop();
            AnsiConsole.MarkupLine($"\n[red]Error:[/] {ex.Message}");
        }
        spinner.Stop();
        Console.WriteLine();
    }
}

static async Task RunSkillsAsync(IServiceProvider sp)
{
    var mgr = sp.GetRequiredService<ISkillManager>();
    var skills = await mgr.GetSkillsAsync();
    var table = new Table().AddColumn("Name").AddColumn("Description");
    foreach (var s in skills) table.AddRow(s.Name, s.Description);
    AnsiConsole.Write(table);
}

static async Task RunMemoryAsync(IServiceProvider sp)
{
    var memory = sp.GetRequiredService<IMemoryStore>();
    var content = await memory.LoadMemoryAsync("MEMORY");
    AnsiConsole.MarkupLine(content ?? "No memory.");
}

static async Task RunHistoryAsync(IServiceProvider sp)
{
    var sessions = sp.GetRequiredService<ISessionManager>();
    var list = await sessions.ListSessionsAsync();
    var table = new Table().AddColumn("ID").AddColumn("Title").AddColumn("Date");
    foreach (var s in list) table.AddRow(s.Id.ToString()[..8], s.Title ?? "untitled", s.UpdatedAt.ToString("g"));
    AnsiConsole.Write(table);
}
