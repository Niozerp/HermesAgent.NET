using System.Text.Json;
using HermesAgent.Core.Abstractions;
using HermesAgent.Core.Configuration;
using HermesAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

namespace HermesAgent.Memory.Sqlite;

/// <summary>
/// SQLite-backed session store with FTS5 full-text search across all past sessions.
/// Replaces the pure-file FileSessionManager for production use.
/// </summary>
public sealed class SqliteSessionStore : ISessionManager, IMemoryStore, IDisposable
{
    private readonly SqliteConnection _db;
    private readonly ILlmProvider _llm;
    private readonly ILogger<SqliteSessionStore> _log;
    private readonly string _dbPath;

    public SqliteSessionStore(IOptions<HermesOptions> options, ILlmProvider llm, ILogger<SqliteSessionStore> log)
    {
        _llm = llm;
        _log = log;
        _dbPath = Path.Combine(options.Value.DataDirectory, "hermes.db");
        Directory.CreateDirectory(options.Value.DataDirectory);

        _db = new SqliteConnection($"Data Source={_dbPath}");
        _db.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS sessions (
                id          TEXT PRIMARY KEY,
                title       TEXT,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL,
                summary     TEXT
            );

            CREATE TABLE IF NOT EXISTS messages (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id  TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                role        TEXT NOT NULL,
                content     TEXT NOT NULL,
                timestamp   TEXT NOT NULL
            );

            -- FTS5 virtual table for full-text search across all session content
            CREATE VIRTUAL TABLE IF NOT EXISTS sessions_fts USING fts5(
                session_id UNINDEXED,
                content,
                tokenize='porter unicode61'
            );

            CREATE TABLE IF NOT EXISTS memory_entries (
                key         TEXT PRIMARY KEY,
                content     TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // These columns were added after the initial schema shipped. SQLite's
        // CREATE TABLE IF NOT EXISTS does not update an existing table, so
        // migrate in place before any saved tool exchanges are read or written.
        EnsureMessageColumn("tool_calls_json", "TEXT");
        EnsureMessageColumn("tool_call_id", "TEXT");
        EnsureMessageColumn("tool_name", "TEXT");
    }

    private void EnsureMessageColumn(string name, string type)
    {
        using var inspect = _db.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(messages)";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();

        using var alter = _db.CreateCommand();
        alter.CommandText = $"ALTER TABLE messages ADD COLUMN {name} {type}";
        alter.ExecuteNonQuery();
    }

    // ─── ISessionManager ─────────────────────────────────────────────────

    public Task<Conversation> StartSessionAsync(Guid? sessionId = null, CancellationToken ct = default)
    {
        var conv = new Conversation(sessionId ?? Guid.NewGuid());
        _log.LogDebug("Started session {Id}", conv.Id);
        return Task.FromResult(conv);
    }

    public async Task SaveSessionAsync(Conversation conversation, CancellationToken ct = default)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);

        try {
            // Upsert session row
            var upsertSession = _db.CreateCommand();
            upsertSession.Transaction = (SqliteTransaction)tx;
            upsertSession.CommandText = """
                INSERT INTO sessions (id, title, created_at, updated_at)
                VALUES (@id, @title, @created, @updated)
                ON CONFLICT(id) DO UPDATE SET title=excluded.title, updated_at=excluded.updated_at
                """;
            upsertSession.Parameters.AddWithValue("@id", conversation.Id.ToString());
            upsertSession.Parameters.AddWithValue("@title", (object?)conversation.Title ?? DBNull.Value);
            upsertSession.Parameters.AddWithValue("@created", conversation.CreatedAt.ToString("O"));
            upsertSession.Parameters.AddWithValue("@updated", conversation.UpdatedAt.ToString("O"));
            await upsertSession.ExecuteNonQueryAsync(ct);

            // Delete and re-insert messages (simpler than diffing)
            var delMsgs = _db.CreateCommand();
            delMsgs.Transaction = (SqliteTransaction)tx;
            delMsgs.CommandText = "DELETE FROM messages WHERE session_id=@sid";
            delMsgs.Parameters.AddWithValue("@sid", conversation.Id.ToString());
            await delMsgs.ExecuteNonQueryAsync(ct);

            foreach (var msg in conversation.Messages)
            {
                var ins = _db.CreateCommand();
                ins.Transaction = (SqliteTransaction)tx;
                ins.CommandText = """
                    INSERT INTO messages
                        (session_id, role, content, timestamp, tool_calls_json, tool_call_id, tool_name)
                    VALUES
                        (@sid, @role, @content, @ts, @toolCalls, @toolCallId, @toolName)
                    """;
                ins.Parameters.AddWithValue("@sid", conversation.Id.ToString());
                ins.Parameters.AddWithValue("@role", msg.Role);
                ins.Parameters.AddWithValue("@content", msg.Content);
                ins.Parameters.AddWithValue("@ts", msg.Timestamp.ToString("O"));
                ins.Parameters.AddWithValue("@toolCalls", msg.ToolCalls is { Count: > 0 }
                    ? (object)JsonSerializer.Serialize(msg.ToolCalls)
                    : DBNull.Value);
                ins.Parameters.AddWithValue("@toolCallId", (object?)msg.ToolCallId ?? DBNull.Value);
                ins.Parameters.AddWithValue("@toolName", (object?)msg.ToolName ?? DBNull.Value);
                await ins.ExecuteNonQueryAsync(ct);
            }

            // Rebuild FTS index for this session
            var delFts = _db.CreateCommand();
            delFts.Transaction = (SqliteTransaction)tx;
            delFts.CommandText = "DELETE FROM sessions_fts WHERE session_id=@sid";
            delFts.Parameters.AddWithValue("@sid", conversation.Id.ToString());
            await delFts.ExecuteNonQueryAsync(ct);

            var allContent = string.Join(" ", conversation.Messages
                .Where(m => m.Role is "user" or "assistant")
                .Select(m => m.Content));
            if (!string.IsNullOrWhiteSpace(allContent))
            {
                var insFts = _db.CreateCommand();
                insFts.Transaction = (SqliteTransaction)tx;
                insFts.CommandText = "INSERT INTO sessions_fts (session_id, content) VALUES (@sid, @content)";
                insFts.Parameters.AddWithValue("@sid", conversation.Id.ToString());
                insFts.Parameters.AddWithValue("@content", allContent);
                await insFts.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _log.LogDebug("Saved session {Id} ({Count} messages)", conversation.Id, conversation.Messages.Count);
        } catch {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<Conversation?> LoadSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT role, content, timestamp, tool_calls_json, tool_call_id, tool_name
            FROM messages
            WHERE session_id=@sid
            ORDER BY id
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId.ToString());

        var conv = new Conversation(sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        bool found = false;
        var pendingToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct))
        {
            found = true;
            var role = reader.GetString(0);
            var content = reader.GetString(1);
            var toolCalls = reader.IsDBNull(3)
                ? null
                : JsonSerializer.Deserialize<List<ToolCall>>(reader.GetString(3));
            var toolCallId = reader.IsDBNull(4) ? null : reader.GetString(4);
            var toolName = reader.IsDBNull(5) ? null : reader.GetString(5);

            if (toolCalls is { Count: > 0 })
            {
                foreach (var call in toolCalls)
                    pendingToolCallIds.Add(call.Id);
            }

            // Sessions saved by older versions lost the assistant tool-call
            // payload and correlation ID. Sending those orphaned `tool`
            // messages back to an OpenAI-compatible API poisons every later
            // request in the session. Drop only the unrecoverable protocol
            // fragments; ordinary user/assistant history remains intact.
            if (role == "tool" &&
                (string.IsNullOrWhiteSpace(toolCallId) || !pendingToolCallIds.Remove(toolCallId)))
            {
                _log.LogWarning("Skipped orphaned tool result while repairing session {Id}", sessionId);
                continue;
            }

            if (role == "assistant" && string.IsNullOrWhiteSpace(content) && toolCalls is not { Count: > 0 })
                continue;

            conv.AddMessage(new Message
            {
                Role = role,
                Content = content,
                Timestamp = DateTimeOffset.Parse(reader.GetString(2)),
                ToolCalls = toolCalls,
                ToolCallId = toolCallId,
                ToolName = toolName
            });
        }
        return found ? conv : null;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int maxResults = 50, CancellationToken ct = default)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.title, s.created_at, s.updated_at, COUNT(m.id) as msg_count
            FROM sessions s
            LEFT JOIN messages m ON m.session_id = s.id
            GROUP BY s.id
            ORDER BY s.updated_at DESC
            LIMIT @max
            """;
        cmd.Parameters.AddWithValue("@max", maxResults);

        var results = new List<SessionSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SessionSummary
            {
                Id = Guid.Parse(reader.GetString(0)),
                Title = reader.IsDBNull(1) ? null : reader.GetString(1),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(2)),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(3)),
                MessageCount = reader.GetInt32(4)
            });
        }
        return results;
    }

    public async Task<string> SummarizeSessionAsync(Conversation conversation, CancellationToken ct = default)
    {
        var content = string.Join("\n\n", conversation.Messages
            .Where(m => m.Role is "user" or "assistant")
            .Select(m => $"[{m.Role}]: {m.Content}"));

        var resp = await _llm.CompleteAsync([
            Message.System("Summarize this conversation in 2-3 sentences."),
            Message.User(content)
        ], null, ct);

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET summary=@s WHERE id=@id";
        cmd.Parameters.AddWithValue("@s", resp.Content);
        cmd.Parameters.AddWithValue("@id", conversation.Id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);

        return resp.Content;
    }

    public async Task DeleteSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", sessionId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─── IMemoryStore ────────────────────────────────────────────────────

    public async Task SaveMemoryAsync(string key, string content, CancellationToken ct = default)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_entries (key, content, created_at, updated_at)
            VALUES (@key, @content, @now, @now)
            ON CONFLICT(key) DO UPDATE SET content=excluded.content, updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("@key", key.ToUpperInvariant());
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> LoadMemoryAsync(string key, CancellationToken ct = default)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT content FROM memory_entries WHERE key=@key";
        cmd.Parameters.AddWithValue("@key", key.ToUpperInvariant());
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxResults = 10, CancellationToken ct = default)
    {
        var results = new List<MemoryEntry>();
        using var ftsCmd = _db.CreateCommand();
        ftsCmd.CommandText = """
            SELECT f.session_id, f.content, s.created_at,
                   bm25(sessions_fts) as rank
            FROM sessions_fts f
            JOIN sessions s ON s.id = f.session_id
            WHERE sessions_fts MATCH @query
            ORDER BY rank
            LIMIT @max
            """;
        ftsCmd.Parameters.AddWithValue("@query", EscapeFtsQuery(query));
        ftsCmd.Parameters.AddWithValue("@max", maxResults);

        try
        {
            await using var reader = await ftsCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new MemoryEntry
                {
                    Key = reader.GetString(0),
                    Content = reader.GetString(1)[..Math.Min(2000, reader.GetString(1).Length)],
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(2)),
                    Relevance = -reader.GetDouble(3)
                });
            }
        }
        catch { }

        using var memCmd = _db.CreateCommand();
        memCmd.CommandText = "SELECT key, content, created_at FROM memory_entries ORDER BY updated_at DESC LIMIT @max";
        memCmd.Parameters.AddWithValue("@max", maxResults);

        await using var memReader = await memCmd.ExecuteReaderAsync(ct);
        var terms = query.ToLowerInvariant().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        while (await memReader.ReadAsync(ct))
        {
            var content = memReader.GetString(1);
            var relevance = terms.Sum(t => content.ToLowerInvariant().Split(t, System.StringSplitOptions.None).Length - 1) / (double)terms.Length;
            if (relevance > 0)
            {
                results.Add(new MemoryEntry
                {
                    Key = memReader.GetString(0),
                    Content = content,
                    CreatedAt = DateTimeOffset.Parse(memReader.GetString(2)),
                    Relevance = relevance
                });
            }
        }

        return results.OrderByDescending(r => r.Relevance).Take(maxResults).ToList();
    }

    public async Task AppendMemoryAsync(string content, CancellationToken ct = default)
    {
        var existing = await LoadMemoryAsync("MEMORY", ct) ?? "";
        var appended = existing + $"\n- [{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}] {content}";
        await SaveMemoryAsync("MEMORY", appended, ct);
    }

    private static string EscapeFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Select(t => $"\"{t.Replace("\"", "")}\"");
        return string.Join(" OR ", terms);
    }

    public void Dispose() => _db.Dispose();
}
