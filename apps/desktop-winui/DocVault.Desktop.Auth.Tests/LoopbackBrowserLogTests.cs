// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using DocVault.Desktop.Auth;

namespace DocVault.Desktop.Auth.Tests;

/// <summary>
/// The redirect's query string is written to the log when it carries an error. Any
/// local process can send a request to the loopback port, so that text is untrusted.
/// </summary>
public sealed class LoopbackBrowserLogTests
{
    [Fact]
    public void A_logged_redirect_cannot_start_a_new_log_line()
    {
        var logged = LoopbackBrowser.ForLog("?error=access_denied\r\n2026-01-01 info: Signed in as admin");

        Assert.DoesNotContain('\r', logged);
        Assert.DoesNotContain('\n', logged);
        Assert.StartsWith("?error=access_denied", logged);
    }

    [Fact]
    public void A_logged_redirect_is_bounded()
    {
        Assert.True(LoopbackBrowser.ForLog("?error=" + new string('a', 10_000)).Length <= 501);
    }
}
