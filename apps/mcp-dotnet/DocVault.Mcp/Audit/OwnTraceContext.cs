// DocVault - a Keycloak proof-of-concept lab.
// Copyright (C) 2026 Magnus Johansson
// SPDX-License-Identifier: GPL-3.0-or-later
// See the LICENSE file for the full licence text.

using System.Diagnostics;

namespace DocVault.Mcp.Audit;

/// <summary>
/// Trace context that goes out and does not come in.
///
/// ASP.NET Core adopts a caller's <c>traceparent</c> header by default. The audit
/// log uses the trace id to tie a tool call to the API request it caused, so a
/// caller who chose the id could give every call the same one and blur that link.
/// With this, the id is always one this server made. Outgoing requests (to
/// Keycloak and the API) still carry it, through the default propagator.
/// </summary>
public sealed class OwnTraceContext : DistributedContextPropagator
{
    private static readonly DistributedContextPropagator Default = CreateDefaultPropagator();

    public override IReadOnlyCollection<string> Fields => Default.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        Default.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(
        object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
        (traceId, traceState) = (null, null);

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier, PropagatorGetterCallback? getter) => null;
}
