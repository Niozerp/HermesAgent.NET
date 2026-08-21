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

    AnsiConsole.Write(
        new FigletText("Hermes")
            .Color(Color.Cyan1));

    AnsiConsole.MarkupLine(
        "[dim]The self-improving AI agent — .NET edition[/]\n");

    while (!cts.Token.IsCancellationRequested)
    {
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold green]You>[/] ")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(input))
            continue;

        if (input == "/new")
        {
            currentSession = null;
            AnsiConsole.MarkupLine("[dim]New session started.[/]\n");
            continue;
        }

        if (input == "/exit")
            break;

        // Tracks whether the next TextDelta needs a new Hermes> prefix.
        bool waitingForHermesText = true;

        // Start in the LLM thinking state.
        spinner.Start("thinking");

        try
        {
            await foreach (
                var evt in agent.RunStreamingAsync(
                    input,
                    currentSession,
                    cts.Token))
            {
                switch (evt)
                {
                    case AgentEvent.TextDelta d:
                        {
                            // The LLM has produced visible output.
                            // Remove the spinner before writing streamed text.
                            if (spinner.IsRunning)
                                spinner.Stop();

                            // Print Hermes> only at the beginning of a new
                            // visible response block.
                            if (waitingForHermesText)
                            {
                                AnsiConsole.Markup("[bold blue]Hermes>[/] ");
                                waitingForHermesText = false;
                            }

                            Console.Write(d.Delta);
                            break;
                        }

                    case AgentEvent.ToolStarted t:
                        {
                            if (spinner.IsRunning)
                                spinner.Stop();

                            // If Hermes was already streaming text,
                            // terminate that line before showing the tool.
                            if (!waitingForHermesText)
                                Console.WriteLine();

                            AnsiConsole.MarkupLine(
                                $"  [dim]⚙ {Markup.Escape(t.ToolName)}[/]");

                            spinner.Start($"running {t.ToolName}");

                            // Any text after the tool is a new Hermes response block.
                            waitingForHermesText = true;

                            break;
                        }

                    case AgentEvent.ToolCompleted:
                        {
                            // Tool execution is over.
                            // The agent is now waiting for / processing the
                            // next LLM response.
                            if (spinner.IsRunning)
                                spinner.Stop();

                            spinner.Start("thinking");

                            waitingForHermesText = true;

                            break;
                        }

                    case AgentEvent.TurnCompleted:
                        {
                            // A turn may complete before another LLM round begins.
                            // Make sure the UI represents that as thinking.
                            if (spinner.IsRunning)
                            {
                                spinner.SetLabel("thinking");
                            }
                            else
                            {
                                spinner.Start("thinking");
                            }

                            waitingForHermesText = true;

                            break;
                        }

                    case AgentEvent.AgentFinished f:
                        {
                            if (spinner.IsRunning)
                                spinner.Stop();

                            // Finish whatever streamed Hermes output is on screen.
                            if (!waitingForHermesText)
                                Console.WriteLine();

                            currentSession ??= Guid.NewGuid();

                            AnsiConsole.MarkupLine(
                                $"[dim]({f.Result.TurnsUsed} turns, " +
                                $"{f.Result.Duration.TotalSeconds:0.0}s)[/]");

                            break;
                        }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (spinner.IsRunning)
                spinner.Stop();

            AnsiConsole.MarkupLine("\n[dim]Cancelled.[/]");
        }
        catch (Exception ex)
        {
            if (spinner.IsRunning)
                spinner.Stop();

            AnsiConsole.MarkupLine(
                $"\n[red]Error:[/] {Markup.Escape(ex.Message)}");
        }
        finally
        {
            if (spinner.IsRunning)
                spinner.Stop();
        }

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
