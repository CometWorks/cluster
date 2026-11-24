# Workshop definition probes

The observer reads game definitions once the dedicated server marks its session
ready. Each case runs an empty standalone control, standalone with the selected
mod, and the same mod on the World Authority and two regular nodes. Standalone
uses the cluster's local-folder patch to stage the pinned Workshop files. The
observer reads the engine's live values after the mod has initialized.

| Case | Workshop item | Engine values |
|---|---|---|
| `speed` | Midspace's Ship Speed Unlimited, 380165093 | Large- and small-grid speed limits |
| `sound` | Sneaky Sounds, 1662953858 | Idle welder, drill-on-metal and heavy-rain ambient volume |

The sound case samples both code paths in Sneaky Sounds. The two tool volumes
come from audio definitions and the rain volume comes from the weather
definition. Results describe the dedicated server's definitions. Playback on
a game client requires a separate test.
