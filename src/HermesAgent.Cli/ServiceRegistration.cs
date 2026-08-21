using HermesAgent.Agent;
using HermesAgent.Agent.Providers;
using HermesAgent.Core.Abstractions;
using HermesAgent.Core.Configuration;
using HermesAgent.Memory;
using HermesAgent.Memory.Sqlite;
using HermesAgent.Skills;
using HermesAgent.Tools;
using HermesAgent.Tools.Toolsets;
using Microsoft.Extensions.DependencyInjection;

namespace HermesAgent.Cli;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHermes(this IServiceCollection services)
    {
        HermesDebug.Log("DI: registering LLM + agent services");
        services.AddHttpClient<OpenAiCompatibleProvider>();
        services.AddSingleton<ILlmProvider, OpenAiCompatibleProvider>();
        services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();
        services.AddSingleton<IContextCompressor, SlidingWindowContextCompressor>();

        services.AddTransient<IAgent, HermesAgentLoop>();
        services.AddTransient<HermesAgentLoop>();
        // Lazy<IAgent> lets DelegateTaskTool break the IEnumerable<ITool> DI cycle
        services.AddTransient<Lazy<IAgent>>(sp => new Lazy<IAgent>(() => sp.GetRequiredService<IAgent>()));

        HermesDebug.Log("DI: registering persistence (SQLite, skills)");
        // Use SQLite for production feel
        services.AddSingleton<SqliteSessionStore>();
        services.AddSingleton<ISessionManager>(sp => sp.GetRequiredService<SqliteSessionStore>());
        services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<SqliteSessionStore>());

        services.AddSingleton<ISkillManager, FileSkillManager>();

        services.AddSingleton<MemoryTools>();
        services.AddSingleton<SkillTools>();

        HermesDebug.Log("DI: registering core tools");
        // Core Tools (HermesAgent.Tools)
        services.AddSingleton<ShellTool>();
        services.AddSingleton<ReadFileTool>();
        services.AddSingleton<WriteFileTool>();
        services.AddSingleton<ListDirectoryTool>();
        services.AddSingleton<SearchFilesTool>();
        services.AddHttpClient<WebFetchTool>();
        services.AddSingleton<WebFetchTool>();

        HermesDebug.Log("DI: registering advanced toolsets");
        // Advanced Toolsets (HermesAgent.Tools.Toolsets)
        services.AddSingleton<PatchTool>();
        services.AddHttpClient<WebSearchTool>();
        services.AddSingleton<WebSearchTool>();
        services.AddHttpClient<WebExtractTool>();
        services.AddSingleton<WebExtractTool>();
        services.AddSingleton<VisionAnalyzeTool>();
        services.AddSingleton<ClarifyTool>();
        services.AddSingleton<TodoTool>();
        services.AddHttpClient<ImageGenerateTool>();
        services.AddSingleton<ImageGenerateTool>();
        services.AddSingleton<MixtureOfAgentsTool>();
        services.AddSingleton<SendMessageTool>();
        services.AddSingleton<TextToSpeechTool>();
        services.AddSingleton<CronJobTool>();
        services.AddSingleton<DelegateTaskTool>();
        services.AddSingleton<ExecuteCodeTool>();
        services.AddSingleton<SessionSearchTool>();
        services.AddSingleton<ProcessTool>();

        HermesDebug.Log("DI: registering browser toolsets");
        // Browser Toolsets
        services.AddSingleton<BrowserNavigateTool>();
        services.AddSingleton<BrowserSnapshotTool>();
        services.AddSingleton<BrowserClickTool>();
        services.AddSingleton<BrowserTypeTool>();
        services.AddSingleton<BrowserPressTool>();
        services.AddSingleton<BrowserScrollTool>();
        services.AddSingleton<BrowserBackTool>();
        services.AddSingleton<BrowserConsoleTool>();
        services.AddSingleton<BrowserGetImagesTool>();
        services.AddSingleton<BrowserVisionTool>();
        services.AddSingleton<BrowserDialogTool>();
        services.AddSingleton<BrowserCdpTool>();

        services.AddSingleton<IEnumerable<ITool>>(sp =>
        {
            HermesDebug.Log("TOOLS: resolving tool list");

            var tools = new List<ITool>();
            void Add<T>(Func<T> resolve) where T : ITool
            {
                var name = typeof(T).Name;
                HermesDebug.Log($"TOOLS: START  resolve {name}");
                var instance = resolve();
                tools.Add(instance);
                HermesDebug.Log($"TOOLS: OK     resolve {name} -> '{instance.Name}'");
            }

            Add<ShellTool>(() => sp.GetRequiredService<ShellTool>());
            Add<ReadFileTool>(() => sp.GetRequiredService<ReadFileTool>());
            Add<WriteFileTool>(() => sp.GetRequiredService<WriteFileTool>());
            Add<ListDirectoryTool>(() => sp.GetRequiredService<ListDirectoryTool>());
            Add<SearchFilesTool>(() => sp.GetRequiredService<SearchFilesTool>());
            Add<WebFetchTool>(() => sp.GetRequiredService<WebFetchTool>());

            Add<PatchTool>(() => sp.GetRequiredService<PatchTool>());
            Add<WebSearchTool>(() => sp.GetRequiredService<WebSearchTool>());
            Add<WebExtractTool>(() => sp.GetRequiredService<WebExtractTool>());
            Add<VisionAnalyzeTool>(() => sp.GetRequiredService<VisionAnalyzeTool>());
            Add<ClarifyTool>(() => sp.GetRequiredService<ClarifyTool>());
            Add<TodoTool>(() => sp.GetRequiredService<TodoTool>());
            Add<ImageGenerateTool>(() => sp.GetRequiredService<ImageGenerateTool>());
            Add<MixtureOfAgentsTool>(() => sp.GetRequiredService<MixtureOfAgentsTool>());
            Add<SendMessageTool>(() => sp.GetRequiredService<SendMessageTool>());
            Add<TextToSpeechTool>(() => sp.GetRequiredService<TextToSpeechTool>());
            Add<CronJobTool>(() => sp.GetRequiredService<CronJobTool>());
            Add<DelegateTaskTool>(() => sp.GetRequiredService<DelegateTaskTool>());
            Add<ExecuteCodeTool>(() => sp.GetRequiredService<ExecuteCodeTool>());
            Add<SessionSearchTool>(() => sp.GetRequiredService<SessionSearchTool>());
            Add<ProcessTool>(() => sp.GetRequiredService<ProcessTool>());

            Add<BrowserNavigateTool>(() => sp.GetRequiredService<BrowserNavigateTool>());
            Add<BrowserSnapshotTool>(() => sp.GetRequiredService<BrowserSnapshotTool>());
            Add<BrowserClickTool>(() => sp.GetRequiredService<BrowserClickTool>());
            Add<BrowserTypeTool>(() => sp.GetRequiredService<BrowserTypeTool>());
            Add<BrowserPressTool>(() => sp.GetRequiredService<BrowserPressTool>());
            Add<BrowserScrollTool>(() => sp.GetRequiredService<BrowserScrollTool>());
            Add<BrowserBackTool>(() => sp.GetRequiredService<BrowserBackTool>());
            Add<BrowserConsoleTool>(() => sp.GetRequiredService<BrowserConsoleTool>());
            Add<BrowserGetImagesTool>(() => sp.GetRequiredService<BrowserGetImagesTool>());
            Add<BrowserVisionTool>(() => sp.GetRequiredService<BrowserVisionTool>());
            Add<BrowserDialogTool>(() => sp.GetRequiredService<BrowserDialogTool>());
            Add<BrowserCdpTool>(() => sp.GetRequiredService<BrowserCdpTool>());

            HermesDebug.Log("TOOLS: START  resolve MemoryTools.GetTools()");
            var mem = sp.GetRequiredService<MemoryTools>().GetTools();
            tools.AddRange(mem);
            HermesDebug.Log($"TOOLS: OK     resolve MemoryTools.GetTools() -> {mem.Count()} tool(s)");

            HermesDebug.Log("TOOLS: START  resolve SkillTools.GetTools()");
            var skl = sp.GetRequiredService<SkillTools>().GetTools();
            tools.AddRange(skl);
            HermesDebug.Log($"TOOLS: OK     resolve SkillTools.GetTools() -> {skl.Count()} tool(s)");

            HermesDebug.Log($"TOOLS: total {tools.Count} tool(s) registered");
            return tools;
        });

        HermesDebug.Log("DI: registration complete");
        return services;
    }
}
