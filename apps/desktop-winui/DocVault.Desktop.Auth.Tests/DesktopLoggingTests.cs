// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using DocVault.Desktop.Auth;
using Microsoft.Extensions.Logging;

namespace DocVault.Desktop.Auth.Tests;

public class DesktopLoggingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docvault-logs").FullName;

    [Fact]
    public void Writes_to_a_file()
    {
        using var factory = DesktopLogging.Create(LogLevel.Debug, _dir);
        factory.CreateLogger("Test").LogInformation("hello {Thing}", "world");

        var file = Directory.GetFiles(_dir, "docvault-*.log").Single();
        Assert.Contains("hello world", File.ReadAllText(file));
    }

    [Fact]
    public void Includes_level_and_category()
    {
        using var factory = DesktopLogging.Create(LogLevel.Debug, _dir);
        factory.CreateLogger("DocVault.Desktop.Auth.KeycloakDesktopClient").LogWarning("careful");

        var text = File.ReadAllText(Directory.GetFiles(_dir, "*.log").Single());
        Assert.Contains("WRN", text);
        Assert.Contains("KeycloakDesktopClient", text);
    }

    [Fact]
    public void Records_exceptions()
    {
        using var factory = DesktopLogging.Create(LogLevel.Debug, _dir);
        factory.CreateLogger("Test").LogError(new InvalidOperationException("boom"), "failed");

        var text = File.ReadAllText(Directory.GetFiles(_dir, "*.log").Single());
        Assert.Contains("boom", text);
        Assert.Contains("InvalidOperationException", text);
    }

    [Fact]
    public void Respects_the_minimum_level()
    {
        using var factory = DesktopLogging.Create(LogLevel.Warning, _dir);
        var log = factory.CreateLogger("Test");
        log.LogDebug("noise");
        log.LogWarning("signal");

        var text = File.ReadAllText(Directory.GetFiles(_dir, "*.log").Single());
        Assert.DoesNotContain("noise", text);
        Assert.Contains("signal", text);
    }

    [Fact]
    public void Exposes_the_current_file_so_the_ui_can_show_it()
    {
        using var factory = DesktopLogging.Create(LogLevel.Debug, _dir);

        Assert.NotNull(DesktopLogging.CurrentLogFile);
        Assert.True(File.Exists(DesktopLogging.CurrentLogFile));
    }

    [Fact]
    public void Keeps_only_the_most_recent_logs()
    {
        // Diagnostics, not an audit trail - an unbounded directory is its own bug.
        for (var i = 0; i < 25; i++)
        {
            File.WriteAllText(Path.Combine(_dir, $"docvault-old-{i:00}.log"), "x");
        }

        using var factory = DesktopLogging.Create(LogLevel.Debug, _dir);

        Assert.True(Directory.GetFiles(_dir, "docvault-*.log").Length <= 21);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
