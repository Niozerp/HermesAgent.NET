using System.Collections.Concurrent;
using System.ClientModel;
using System.Text.RegularExpressions;
using HermesAgent.Core.Abstractions;
using HermesAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using HtmlAgilityPack;
using OpenAI;
using OpenAI.Images;
using OpenAI.Audio;
using OpenAI.Chat;

namespace HermesAgent.Tools.Toolsets;

// ═══════════════════════════════════════════════════════════════════════════
// SHARED PLAYWRIGHT SESSION MANAGER
// ═══════════════════════════════════════════════════════════════════════════

internal static class PlaywrightSession
{
    public static IPlaywright? Playwright;
    public static IBrowser? Browser;
    public static IPage? Page;
    public static ConcurrentQueue<string> ConsoleLogs = new();
    public static IDialog? PendingDialog;

    public static async Task<IPage> GetOrCreatePageAsync()
    {
        if (Page != null) return Page;

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        Page = await Browser.NewPageAsync();

        Page.Console += (_, msg) => ConsoleLogs.Enqueue($"[{msg.Type}] {msg.Text}");
        Page.PageError += (_, err) => ConsoleLogs.Enqueue($"[Error] {err}");
        Page.Dialog += (_, dialog) => PendingDialog = dialog;

        return Page;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// BROWSER TOOLSET  (12 tools)
// ═══════════════════════════════════════════════════════════════════════════

public sealed class BrowserNavigateTool(ILogger<BrowserNavigateTool> log) : ToolBase
{
    public override string Name => "browser_navigate";
    public override string Description => "Navigate to a URL in the browser. Must be called before other browser tools.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition> { ["url"] = new() { Type = "string", Description = "URL to navigate to" } },
        Required = ["url"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var url = GetArg(call, "url");
        log.LogInformation("Browser navigate: {Url}", url);
        try
        {
            var page = await PlaywrightSession.GetOrCreatePageAsync();
            var resp = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            return resp != null ? $"Navigated to {url} — HTTP {resp.Status}" : $"Navigated to {url}";
        }
        catch (Exception ex) { return $"Navigation failed: {ex.Message}"; }
    }
}

public sealed class BrowserSnapshotTool : ToolBase
{
    public override string Name => "browser_snapshot";
    public override string Description => "Get a text-based snapshot of the current page's interactive elements with ref IDs (e.g. @e1) for clicking/typing.";
    public override ToolDefinition Definition => new() { Name = Name, Description = Description, Parameters = new Dictionary<string, ParameterDefinition>() };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (PlaywrightSession.Page == null) return "Error: Call browser_navigate first.";
        var js = @"() => {
            let id = 0;
            const elements = document.querySelectorAll('a, button, input, select, textarea');
            const results = [];
            for (const el of elements) {
                const style = window.getComputedStyle(el);
                if (style.display !== 'none' && style.visibility !== 'hidden' && el.offsetWidth > 0) {
                    const refId = '@e' + id;
                    el.setAttribute('data-hermes-id', refId);
                    const text = (el.innerText || el.value || el.placeholder || el.name || '').trim().replace(/\n/g, ' ').substring(0, 50);
                    results.push(`[${refId}] ${el.tagName.toLowerCase()} - ${text}`);
                    id++;
                }
            }
            return results.join('\n');
        }";
        var snapshot = await PlaywrightSession.Page.EvaluateAsync<string>(js);
        return string.IsNullOrEmpty(snapshot) ? "No interactive elements found." : snapshot;
    }
}

public sealed class BrowserClickTool : ToolBase
{
    public override string Name => "browser_click";
    public override string Description => "Click on an element identified by its ref ID from the snapshot (e.g., '@e5').";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition> { ["ref"] = new() { Type = "string", Description = "Ref ID from snapshot (e.g. @e5)" } },
        Required = ["ref"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (PlaywrightSession.Page == null) return "Error: Call browser_navigate first.";
        var refId = GetArg(call, "ref");
        await PlaywrightSession.Page.Locator($"[data-hermes-id='{refId}']").ClickAsync();
        return $"Clicked element {refId}";
    }
}

public sealed class BrowserTypeTool : ToolBase
{
    public override string Name => "browser_type";
    public override string Description => "Type text into an input field identified by its ref ID. Clears the field first.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["ref"] = new() { Type = "string", Description = "Ref ID from snapshot" },
            ["text"] = new() { Type = "string", Description = "Text to type" }
        },
        Required = ["ref", "text"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (PlaywrightSession.Page == null) return "Error: Call browser_navigate first.";
        var refId = GetArg(call, "ref");
        var text = GetArg(call, "text");
        await PlaywrightSession.Page.Locator($"[data-hermes-id='{refId}']").FillAsync(text);
        return $"Typed into {refId}";
    }
}

public sealed class BrowserPressTool : ToolBase
{
    public override string Name => "browser_press";
    public override string Description => "Press a keyboard key (e.g. Enter, Tab, Escape).";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition> { ["key"] = new() { Type = "string", Description = "Key name" } },
        Required = ["key"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (PlaywrightSession.Page == null) return "Error: Call browser_navigate first.";
        var key = GetArg(call, "key");
        await PlaywrightSession.Page.Keyboard.PressAsync(key);
        return $"Pressed {key}";
    }
}

public sealed class BrowserScrollTool : ToolBase
{
    public override string Name => "browser_scroll";
    public override string Description => "Scroll the page in a direction.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["direction"] = new() { Type = "string", Description = "up, down, left, right", Enum = ["up", "down", "left", "right"] },
            ["amount"] = new() { Type = "integer", Description = "Pixels to scroll (default: 500)" }
        },
        Required = ["direction"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (PlaywrightSession.Page == null) return "Error: Call browser_navigate first.";
        var dir = GetArg(call, "direction");
        var amt = int.TryParse(GetArgOrDefault(call, "amount", "500"), out var a) ? a : 500;
        var js = dir switch { "up" => $"window.scrollBy(0, -{amt})", "down" => $"window.scrollBy(0, {amt})", "left" => $"window.scrollBy(-{amt}, 0)", "right" => $"window.scrollBy({amt}, 0)", _ => "" };
        await PlaywrightSession.Page.EvaluateAsync(js);
        return $"Scrolled {dir} {amt}px";
    }
}

public sealed class BrowserBackTool : ToolBase
{
    public override string Name => "browser_back";
    public override string Description => "Navigate back to the previous page.";
    public override ToolDefinition Definition => new() { Name = Name, Description = Description, Parameters = new Dictionary<string, ParameterDefinition>() };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (PlaywrightSession.Page == null) return "Error: Call browser_navigate first.";
        await PlaywrightSession.Page.GoBackAsync();
        return "Navigated back.";
    }
}

public sealed class BrowserConsoleTool : ToolBase
{
    public override string Name => "browser_console";
    public override string Description => "Get browser console output and JavaScript errors.";
    public override ToolDefinition Definition => new() { Name = Name, Description = Description, Parameters = new Dictionary<string, ParameterDefinition>() };
    protected override Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var logs = PlaywrightSession.ConsoleLogs.ToArray();
        PlaywrightSession.ConsoleLogs.Clear();
        return Task.FromResult(logs.Length == 0 ? "Console is empty." : string.Join('\n', logs));
    }
}

public sealed class BrowserGetImagesTool : ToolBase
{
    public override string Name => "browser_get_images";
    public override string Description => "Get all images on the current page with URLs and alt text.";
    public override ToolDefinition Definition => new() { Name = Name, Description = Description, Parameters = new Dictionary<string, ParameterDefinition>() };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (PlaywrightSession.Page == null) return "Error: Call browser_navigate first.";
        var js = "() => Array.from(document.images).map(img => `[Img] ${img.src} | Alt: ${img.alt || 'none'}`).join('\\n')";
        var images = await PlaywrightSession.Page.EvaluateAsync<string>(js);
        return string.IsNullOrEmpty(images) ? "No images found." : images;
    }
}

public sealed class BrowserDialogTool : ToolBase
{
    public override string Name => "browser_dialog";
    public override string Description => "Respond to a native JavaScript dialog (alert/confirm/prompt).";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["action"] = new() { Type = "string", Description = "accept or dismiss", Enum = ["accept", "dismiss"] },
            ["text"] = new() { Type = "string", Description = "Text for prompt dialogs" }
        },
        Required = ["action"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (PlaywrightSession.PendingDialog == null) return "No pending dialog found.";
        var action = GetArg(call, "action");
        if (action == "accept") await PlaywrightSession.PendingDialog.AcceptAsync(GetArg(call, "text"));
        else await PlaywrightSession.PendingDialog.DismissAsync();

        PlaywrightSession.PendingDialog = null;
        return $"Dialog {action}ed.";
    }
}

public sealed class BrowserCdpTool : ToolBase
{
    public override string Name => "browser_cdp";
    public override string Description => "Send a raw Chrome DevTools Protocol (CDP) command.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["method"] = new() { Type = "string", Description = "CDP method" }
        },
        Required = ["method"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (PlaywrightSession.Page == null) return "Error: Call browser_navigate first.";
        var method = GetArg(call, "method");
        var cdp = await PlaywrightSession.Page.Context.NewCDPSessionAsync(PlaywrightSession.Page);
        await cdp.SendAsync(method);
        return $"Executed CDP method: {method}";
    }
}

public sealed class BrowserVisionTool : ToolBase
{
    public override string Name => "browser_vision";
    public override string Description => "Take a screenshot and analyze it with vision AI.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition> { ["question"] = new() { Type = "string", Description = "What to analyze" } },
        Required = ["question"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (PlaywrightSession.Page == null) return "Error: Call browser_navigate first.";
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return "Error: OPENAI_API_KEY is not set.";

        var screenshot = await PlaywrightSession.Page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Jpeg });
        var base64 = Convert.ToBase64String(screenshot);

        var client = new OpenAIClient(apiKey).GetChatClient("gpt-4o");
        var messages = new List<ChatMessage> {
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(GetArg(call, "question")),
                ChatMessageContentPart.CreateImagePart(new Uri($"data:image/jpeg;base64,{base64}"))
            )
        };
        var resp = await client.CompleteChatAsync(messages, cancellationToken: ct);
        return resp.Value.Content[0].Text;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// WEB TOOLSET  (HtmlAgilityPack)
// ═══════════════════════════════════════════════════════════════════════════

public sealed class WebExtractTool : ToolBase
{
    private readonly HttpClient _http;
    public WebExtractTool(HttpClient http) => _http = http;

    public override string Name => "web_extract";
    public override string Description => "Extract content from web page URLs as clean text.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["url"] = new () { Type = "string", Description="Web Url" },
            ["max_chars"] = new() { Type = "integer", Description = "Default: 8000" }
        },
        Required = ["url"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var url = GetArg(call, "url");
        var max = int.TryParse(GetArgOrDefault(call, "max_chars", "8000"), out var m) ? m : 8000;

        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 HermesAgent/1.0");
        var html = await _http.GetStringAsync(url, ct);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remove unwanted tags
        var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//nav|//footer");
        if (nodesToRemove != null) foreach (var node in nodesToRemove) node.Remove();

        var text = System.Net.WebUtility.HtmlDecode(doc.DocumentNode.InnerText);
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return text.Length <= max ? text : text[..max] + "\n[truncated]";
    }
}

public sealed class WebSearchTool : ToolBase
{
    private readonly HttpClient _http;
    public WebSearchTool(HttpClient http) => _http = http;

    public override string Name => "web_search";
    public override string Description => "Search the web via DuckDuckGo Lite.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition> { ["query"] = new() { Type = "string", Description="Search Query" } },
        Required = ["query"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var query = Uri.EscapeDataString(GetArg(call, "query"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        var html = await _http.GetStringAsync($"https://lite.duckduckgo.com/lite/?q={query}", ct);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var results = new List<string>();
        var rows = doc.DocumentNode.SelectNodes("//tr")?.ToList() ?? new();

        for (int i = 0; i < rows.Count; i++)
        {
            var aTag = rows[i].SelectSingleNode(".//a[@class='result-url']");
            var snippet = rows.Count > i + 1 ? rows[i + 1].SelectSingleNode(".//td[@class='result-snippet']")?.InnerText : "";

            if (aTag != null)
            {
                var title = System.Net.WebUtility.HtmlDecode(aTag.InnerText);
                var url = aTag.GetAttributeValue("href", "");
                snippet = System.Net.WebUtility.HtmlDecode(snippet?.Trim() ?? "");
                results.Add($"- {title}\n  {url}\n  {snippet}");
            }
        }

        return results.Count > 0 ? string.Join("\n\n", results.Take(5)) : "No results found.";
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// OPENAI TOOLS (Vision, Image Gen, TTS)
// ═══════════════════════════════════════════════════════════════════════════

public sealed class VisionAnalyzeTool : ToolBase
{
    public override string Name => "vision_analyze";
    public override string Description => "Analyze images from a URL using OpenAI Vision.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["image_url"] = new() { Type = "string", Description="Image URL" },
            ["question"] = new() { Type = "string", Description="Question about the image" }
        },
        Required = ["image_url", "question"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return "Error: OPENAI_API_KEY is not set.";

        var client = new OpenAIClient(apiKey).GetChatClient("gpt-4o");
        var messages = new List<ChatMessage> {
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(GetArg(call, "question")),
                ChatMessageContentPart.CreateImagePart(new Uri(GetArg(call, "image_url")))
            )
        };
        var resp = await client.CompleteChatAsync(messages, cancellationToken: ct);
        return resp.Value.Content[0].Text;
    }
}

public sealed class ImageGenerateTool : ToolBase
{
    public override string Name => "image_generate";
    public override string Description => "Generate images from text prompts using DALL-E 3.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition> { ["prompt"] = new() { Type = "string", Description="Text prompt for image generation" } },
        Required = ["prompt"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return "Error: OPENAI_API_KEY is not set.";

        var client = new OpenAIClient(apiKey).GetImageClient("dall-e-3");
        var resp = await client.GenerateImageAsync(GetArg(call, "prompt"), new ImageGenerationOptions { Size = GeneratedImageSize.W1024xH1024 });
        return $"Image generated successfully: {resp.Value.ImageUri}";
    }
}

public sealed class TextToSpeechTool : ToolBase
{
    public override string Name => "text_to_speech";
    public override string Description => "Convert text to speech audio using OpenAI TTS.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition> { ["text"] = new() { Type = "string", Description="Text to convert to speech" } },
        Required = ["text"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return "Error: OPENAI_API_KEY is not set.";

        var client = new OpenAIClient(apiKey).GetAudioClient("tts-1");
        var text = GetArg(call, "text");

        var resp = await client.GenerateSpeechAsync(text, GeneratedSpeechVoice.Alloy);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "voice-memos");
        Directory.CreateDirectory(dir);

        var outputPath = Path.Combine(dir, $"{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(outputPath, resp.Value.ToArray(), ct);

        return $"TTS Audio generated and saved to: MEDIA:{outputPath}";
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// UTILITY TOOLSETS (Clarify, Todo, MoA, SendMessage, Patch)
// ═══════════════════════════════════════════════════════════════════════════

public sealed class ClarifyTool : ToolBase
{
    public override string Name => "clarify";
    public override string Description => "Ask the user a question before proceeding.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition> { ["question"] = new() { Type = "string", Description="Question to ask the user" } },
        Required = ["question"]
    };
    protected override Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n❓ {GetArg(call, "question")}");
        Console.ResetColor();
        Console.Write("Your answer: ");
        var answer = Console.ReadLine() ?? "";
        return Task.FromResult(string.IsNullOrWhiteSpace(answer) ? "[No answer provided]" : answer);
    }
}

public sealed record TodoItem(string Id, string Text, string Status);
public sealed class TodoTool : ToolBase
{
    private static readonly List<TodoItem> _todos = [];
    private static int _nextId = 1;

    public override string Name => "todo";
    public override string Description => "Manage session task list. Actions: read, add, complete, delete.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["action"] = new() { Type = "string", Enum = ["read", "add", "complete", "delete"], Description="Action to perform on the task list" },
            ["text"] = new() { Type = "string", Description="Text of the task" },
            ["id"] = new() { Type = "string", Description="ID of the task" }
        }
    };
    protected override Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var action = GetArgOrDefault(call, "action", "read");
        var id = GetArg(call, "id");
        if (action == "add") { _todos.Add(new TodoItem((_nextId++).ToString(), GetArg(call, "text"), "pending")); return Task.FromResult("Task added."); }
        if (action == "complete") { var i = _todos.FindIndex(t => t.Id == id); if (i >= 0) _todos[i] = _todos[i] with { Status = "done" }; return Task.FromResult("Task done."); }
        if (action == "delete") { _todos.RemoveAll(t => t.Id == id); return Task.FromResult("Task deleted."); }
        return Task.FromResult(_todos.Count == 0 ? "No tasks." : string.Join('\n', _todos.Select(t => $"[{t.Status}] {t.Id}. {t.Text}")));
    }
}

public sealed class MixtureOfAgentsTool(ILlmProvider llm) : ToolBase
{
    public override string Name => "mixture_of_agents";
    public override string Description => "Route hard problems through multi-pass LLM reasoning.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition> { ["problem"] = new() { Type = "string", Description="Problem to solve" } },
        Required = ["problem"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var problem = GetArg(call, "problem");
        var pass1 = await llm.CompleteAsync([Message.User(problem)], null, ct);
        var prompt2 = $"Given problem:\n{problem}\n\nDraft analysis:\n{pass1.Content}\n\nProvide the final refined solution.";
        var final = await llm.CompleteAsync([Message.User(prompt2)], null, ct);
        return $"[MoA Result]\n{final.Content}";
    }
}

public sealed class SendMessageTool : ToolBase
{
    public override string Name => "send_message";
    public override string Description => "Send a message to a connected platform.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["action"] = new() { Type = "string", Enum = ["send", "list"], Description="Action to perform" },
            ["target"] = new() { Type = "string", Description="Target platform" },
            ["message"] = new() { Type = "string", Description="Message content" }
        },
        Required = ["action"]
    };
    protected override Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        if (GetArg(call, "action") == "list") return Task.FromResult("Targets: email, slack, discord. (Integration pending API keys)");
        return Task.FromResult($"Message queued to {GetArg(call, "target")}.");
    }
}

public sealed class PatchTool : ToolBase
{
    public override string Name => "patch";
    public override string Description => "Targeted find-and-replace edits in files.";
    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new Dictionary<string, ParameterDefinition>
        {
            ["path"] = new() { Type = "string", Description="Path to the file to be patched" },
            ["old_content"] = new() { Type = "string", Description="Content to be replaced" },
            ["new_content"] = new() { Type = "string", Description="Content to replace with" }
        },
        Required = ["path", "old_content", "new_content"]
    };
    protected override async Task<string> ExecuteCoreAsync(ToolCall call, CancellationToken ct)
    {
        var path = GetArg(call, "path");
        if (!File.Exists(path)) return $"Error: '{path}' not found.";

        var text = await File.ReadAllTextAsync(path, ct);
        var oldC = GetArg(call, "old_content");
        var newC = GetArg(call, "new_content");

        if (text.Contains(oldC))
        {
            await File.WriteAllTextAsync(path, text.Replace(oldC, newC), ct);
            return $"Patched {path}.";
        }
        return $"Error: Exact content not found in '{path}'.";
    }
}