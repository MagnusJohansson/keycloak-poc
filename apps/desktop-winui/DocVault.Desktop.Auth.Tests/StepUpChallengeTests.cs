// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using DocVault.Desktop.Auth;

namespace DocVault.Desktop.Auth.Tests;

/// <summary>
/// The RFC 9470 contract between the API and every client: a 401 that carries
/// <c>insufficient_user_authentication</c> is recoverable by re-authenticating; a plain 403 is not.
/// Getting this wrong sends an authenticated-but-unauthorized user round a login loop.
/// </summary>
public class StepUpChallengeTests
{
    [Fact]
    public void Recognises_a_step_up_challenge_and_extracts_the_required_level()
    {
        const string header =
            """Bearer error="insufficient_user_authentication", error_description="A higher authentication level is required", acr_values="silver" """;

        Assert.Equal("silver", DocVaultApiClient.ParseStepUpChallenge(header));
    }

    [Fact]
    public void Ignores_an_ordinary_bearer_challenge()
    {
        // A missing or invalid token is also a 401, but a different problem; re-authenticating at
        // a higher level would not help. The error code is what tells the two apart.
        Assert.Null(DocVaultApiClient.ParseStepUpChallenge("""Bearer error="invalid_token" """));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic realm=\"x\"")]
    public void Ignores_absent_or_unrelated_headers(string? header)
    {
        Assert.Null(DocVaultApiClient.ParseStepUpChallenge(header));
    }

    [Fact]
    public void Handles_a_challenge_whose_parameters_are_reordered()
    {
        // Header parameter order is not guaranteed, so the parser must not depend on it.
        const string header = """Bearer acr_values="gold", error="insufficient_user_authentication" """;

        Assert.Equal("gold", DocVaultApiClient.ParseStepUpChallenge(header));
    }
}
