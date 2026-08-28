// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using DocVault.Desktop.Auth;

namespace DocVault.Desktop.Auth.Tests;

public class TokenStoreTests
{
    [Fact]
    public void Round_trips_tokens()
    {
        var store = new InMemoryTokenStore();
        var tokens = new StoredTokens("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(5));

        store.Save(tokens);

        Assert.Equal(tokens, store.Load());
    }

    [Fact]
    public void Clear_removes_everything()
    {
        var store = new InMemoryTokenStore();
        store.Save(new StoredTokens("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)));

        store.Clear();

        Assert.Null(store.Load());
    }

    [Fact]
    public void A_token_close_to_expiry_is_refreshed_early()
    {
        // Inside the 60s margin. Returning it would risk the API rejecting it mid-flight,
        // producing an intermittent 401 that is painful to reproduce.
        var nearlyExpired = new StoredTokens("a", "r", DateTimeOffset.UtcNow.AddSeconds(30));

        Assert.True(nearlyExpired.NeedsRefresh);
    }

    [Fact]
    public void A_fresh_token_is_used_as_is()
    {
        Assert.False(new StoredTokens("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)).NeedsRefresh);
    }

    [Fact]
    public void An_expired_or_absent_token_needs_refreshing()
    {
        Assert.True(new StoredTokens("a", "r", DateTimeOffset.UtcNow.AddMinutes(-1)).NeedsRefresh);
        Assert.True(new StoredTokens(null, "r", DateTimeOffset.UtcNow.AddMinutes(5)).NeedsRefresh);
    }
}
