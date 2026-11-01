# A hard-killed Direct Transport client holds its session for two minutes

Date: 2026-07-27. Found while building P1-CHR-01 (login/reconnect never duplicates a character).

## Measurement

A headless client was `SIGKILL`ed and the Gateway's session list polled every five seconds for two
minutes. The session never dropped:

```
1 [1234567890] … 24 [1234567890]
```

## Why

Direct Transport deliberately has **no activity-based silence watchdog**
(`DirectGateway.cs`, session-maintenance loop): during the post-respawn re-stream flood the poll
thread lags 30 s+ behind, so `LastClientActivity` reads as silence while the client is provably
alive, and a watchdog there zombied live sessions. A dead DT peer is therefore left to LiteNetLib's
`DisconnectTimeout`, which the Gateway sets to **120 000 ms**.

Steam sessions have no equivalent event, so they *are* reaped, at 180 s of silence.

## Why it matters now

Gateway.md §7.1 single-flight refuses a second join for an identity that already has a session. The
two combine into: **a player whose client crashes cannot rejoin for two minutes.** Before
single-flight the second join was simply refused as a duplicate, so the behaviour is not new, but it
is now load-bearing.

§7.1 anticipates this and allows the other branch — "or replaces the stalled session, which is
detached first — never both proceeding". That branch is **not implemented**. Implementing it needs a
stalled-session signal that the re-stream flood cannot forge; the peer's connection state is the
obvious candidate and is exactly what stays `Connected` for those 120 s after a kill.

## What was done

Nothing, deliberately. The scenario that needed a relog now performs a *graceful* leave
(`exit_game`), which is what "leave" means in TestPlan §1.2's verb list and what a player does. The
crash-then-rejoin case belongs to P1-CHR-04's matrix and is recorded here as a known window rather
than papered over with a shorter timeout — lowering `DisconnectTimeout` below the observed 30 s+
poll-thread lag is how the zombie sessions were created in the first place.

Recommended fix, when it is worth the risk: on a second join for an identity, probe the existing
peer once and replace the session if the probe fails, rather than shortening the timeout for
everyone.
