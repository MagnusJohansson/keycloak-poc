// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Net;
using DocVault.Desktop.Auth;

namespace DocVault.Desktop.Auth.Tests;

public class LoopbackBrowserTests
{
    [Fact]
    public void Redirect_uri_is_loopback_on_an_os_assigned_port()
    {
        var browser = new LoopbackBrowser();
        var uri = new Uri(browser.RedirectUri);

        // 127.0.0.1, not "localhost": localhost can resolve to IPv6 ::1, and the listener would
        // then never receive the callback.
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal("/callback", uri.AbsolutePath);
        Assert.InRange(uri.Port, 1024, 65535);
    }

    [Fact]
    public void Each_instance_gets_its_own_port()
    {
        // A hardcoded port collides with whatever else is running and prevents two instances.
        Assert.NotEqual(new LoopbackBrowser().RedirectUri, new LoopbackBrowser().RedirectUri);
    }

    [Fact]
    public void Redirect_uri_matches_the_wildcard_registered_in_keycloak()
    {
        // infra/terraform/20-realm/clients.tf registers http://127.0.0.1:*/callback.
        var uri = new Uri(new LoopbackBrowser().RedirectUri);

        Assert.Equal("http", uri.Scheme);
        Assert.True(uri.IsLoopback);
        Assert.Equal("/callback", uri.AbsolutePath);
    }

    [Fact]
    public void The_port_it_advertises_is_actually_bindable()
    {
        // Guards against handing Keycloak a redirect URI the app then cannot listen on.
        var browser = new LoopbackBrowser();

        using var listener = new HttpListener();
        listener.Prefixes.Add(browser.RedirectUri + "/");

        var exception = Record.Exception(listener.Start);

        Assert.Null(exception);
        listener.Stop();
    }
}
