// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DocVault.Mcp.Audit;

/// <summary>The events this server records. One record per event, never per log line.</summary>
public static class AuditEvents
{
    /// <summary>A tool ran, or was refused inside the tool. The outcome says which.</summary>
    public const string ToolCall = "tool.call";

    /// <summary>A valid token lacked the scope a tool needs: the 403 challenge.</summary>
    public const string ScopeChallenge = "scope.challenge";

    /// <summary>A token was presented and refused: the 401.</summary>
    public const string TokenRefused = "token.refused";

    /// <summary>A request with a valid token was refused before any tool: unreadable or too large.</summary>
    public const string RequestRefused = "request.refused";
}

public static class AuditOutcomes
{
    public const string Ok = "ok";

    /// <summary>Permission said no: the scope, the user's role, or the token itself.</summary>
    public const string Refused = "refused";

    /// <summary>The input was not acceptable; no permission was tested.</summary>
    public const string Rejected = "rejected";

    public const string NotFound = "not_found";
    public const string Error = "error";
}

/// <summary>
/// The answer to "what did the assistant do, and for whom?". Each record names the
/// user, the client the assistant signed in through, the tool, and how it ended.
/// </summary>
public interface IAuditLog
{
    void Record(string eventName, string outcome, HttpContext? context, params (string Key, object? Value)[] fields);
}

/// <summary>
/// Writes each record as one JSON object: always to the log (category
/// <c>DocVault.Mcp.Audit</c>), and to a file as JSON Lines when
/// <c>Audit:File</c> is set.
///
/// What a record may contain is decided here, not at the call sites:
/// <list type="bullet">
/// <item>Who the caller is comes only from a token this server has validated. A
/// refused token's claims are an attacker's words and are not recorded.</item>
/// <item>Never a token, and never the client secret. A token is named by its
/// <c>jti</c>, which identifies it and cannot be used as one.</item>
/// </list>
/// </summary>
public sealed class AuditLog : IAuditLog, IDisposable
{
    public const string Category = "DocVault.Mcp.Audit";

    private readonly ILogger _logger;
    private readonly ILogger _operations;
    private readonly FileStream? _file;
    private readonly Lock _gate = new();
    private long _sequence;

    public AuditLog(McpSettings settings, ILoggerFactory loggers)
    {
        _logger = loggers.CreateLogger(Category);
        _operations = loggers.CreateLogger<AuditLog>();

        if (settings.AuditFile is null)
        {
            return;
        }

        // Opened at startup, so a path that cannot be written stops the server
        // instead of running with the trail silently missing.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
        };
        if (!OperatingSystem.IsWindows())
        {
            // The trail names users and what they did. Owner only.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            _file = new FileStream(settings.AuditFile, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException($"Audit:File '{settings.AuditFile}' cannot be opened for writing: {ex.Message}", ex);
        }
    }

    public void Record(string eventName, string outcome, HttpContext? context, params (string Key, object? Value)[] fields)
    {
        var record = new Dictionary<string, object?>
        {
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["seq"] = Interlocked.Increment(ref _sequence),
            ["event"] = eventName,
            ["outcome"] = outcome,
        };

        // The same id the API logs for the request this call makes: .NET sends it on
        // as a W3C traceparent header.
        Add(record, "trace.id", Activity.Current?.TraceId.ToString());

        if (context is not null)
        {
            Add(record, "client.address", context.Connection.RemoteIpAddress?.ToString());

            var user = context.User;
            if (user.Identity?.IsAuthenticated == true)
            {
                Add(record, "user.id", user.FindFirst("sub")?.Value);
                Add(record, "user.name", user.FindFirst("preferred_username")?.Value);
                // The client the assistant signed in through, not the assistant's model.
                Add(record, "client.id", user.FindFirst("azp")?.Value);
                Add(record, "scope", user.FindFirst("scope")?.Value);
                Add(record, "token.id", user.FindFirst("jti")?.Value);
                Add(record, "session.id", user.FindFirst("sid")?.Value);
            }
        }

        foreach (var (key, value) in fields)
        {
            Add(record, key, value);
        }

        // The default encoder escapes control characters and everything outside
        // ASCII, so a value cannot start a new line or a new record.
        var json = JsonSerializer.Serialize(record);

        var level = outcome == AuditOutcomes.Ok ? LogLevel.Information : LogLevel.Warning;
        _logger.Log(level, "audit {Record}", json);

        if (_file is null)
        {
            return;
        }

        try
        {
            var line = Encoding.UTF8.GetBytes(json + "\n");
            lock (_gate)
            {
                _file.Write(line);
                _file.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The call this record describes has already happened; refusing now would
            // undo nothing. Say loudly that the file is incomplete. The record is
            // still in the log above.
            _operations.LogError(ex, "Audit record {Sequence} could not be written to the audit file.", record["seq"]);
        }
    }

    public void Dispose() => _file?.Dispose();

    private static void Add(Dictionary<string, object?> record, string key, object? value)
    {
        if (value is not null && value is not "")
        {
            record[key] = value;
        }
    }
}
