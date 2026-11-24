# Workshop definition probes

The observer reads game definitions once the dedicated server marks its session
ready. Each case runs an empty standalone control, standalone with the selected
mod, and the same mod on the World Authority and two regular nodes. Standalone
uses the cluster's local-folder patch to stage the pinned Workshop files. The
observer reads the engine's live values after the mod has initialized.

| Case | Workshop item | Engine values |
|---|---|---|
| `speed` | Midspace's Ship Speed Unlimited, 380165093 | Large- and small-grid speed limits |
| `speed-500` | Midspace's Ship Speed 500, 379815146 | Both grid speed limits at 500 m/s |
| `speed-configured` | Midspace's Configurable Max Ship Speed, 599536562 | Large and small grid limits of 850 and 420 m/s, read from the saved world |
| `sound` | Sneaky Sounds, 1662953858 | Idle welder, drill-on-metal and heavy-rain ambient volume |

The configurable speed case gives the unmodded and modded worlds the same saved settings. The unmodded server must keep its default limits, and the modded standalone server must apply both configured values.

The sound case samples both code paths in Sneaky Sounds. The two tool volumes
come from audio definitions and the rain volume comes from the weather
definition. Results describe the dedicated server's definitions. Playback on
a game client requires a separate test.
