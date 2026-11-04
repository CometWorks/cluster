# Standalone offline resource reference

`Test/offline_reference.py` starts stock DS instances through the normal isolated standalone
launcher. It registers the observer, the pinned Concealment core and compatibility plugins.
The actual standalone adapter supplies its native clock; its automatic concealment is disabled
before fixtures spawn. ClusterNode.dll remains a dormant fixture dependency: its plugin never
initializes. Observed-reference policy overrides leave the installed clock/admission delegates
intact. The live engine advances reference stocks; the model touches clones only. Actual Survival/non-simplified mode
is a required precondition. The dedicated config copies the fixture's own Settings.

The runner first compares3600 ordinary60Hz engine frames with3600 engine frames at bounded
native max-speed pacing. Both gameplay time and elapsed-sun counters must advance by exactly
the native amount per frame, and battery/fuel outcomes must agree within the recorded native
phase bounds. Only then can three independent1296000-frame baselines start. Native max-speed
removes the engine's pacing wait; a1ms observer sleep bounds CPU/noise. No update is skipped,
no engine counter is assigned, and no admin sun jump is used. The long references consume six
hours of60Hz resource updates, including native reactor900/1800/3600 timer transitions.

Vanilla elapsed sun time advances16ms/frame; both hosts address milliseconds/16.
1296000 engine updates are1296000 world-frame units,5.76 elapsed world hours and
six resource hours. The former1350000-unit normalized oracle was invalid for the
actual host clock (round6 E2); retain its old results as historical evidence only.
Corrected references use actual saved elapsed ticks without inflating the horizon.
Sun rotation is disabled for the energy
cases; this lane makes no planetary/full-rotation claim. No production formula is used as
the reference. Modes Auto/Recharge/Discharge and a sun-facing priority control are separate.

The observed lane calls the actual `Prepare`/`InPlace` path every100 native frames for a full
natural learning rotation, then advances an independent full reference rotation. Only the product's
guarded native reactor-fuel observer patch is installed; the cluster plugin remains dormant.
Known native debits and unexpected inventory edits are separate positive/negative controls.

The planet lane initializes native voxel physics without changing terrain, then prefetches shapes
as native targeting does. Ordinary live rolling-pivot output and fresh unregistered native
component-point computations are recorded separately. Passing instantaneous geometry does not
change the historical2% live-output verdict: `check_offline_solar.py` retains both oracles and
reports each explicitly. The user now selects fresh all-pivot geometric catch-up, accepting rolling
history as an approximation. `OfflineCompensationCheck --solar-shadow-reference NATIVE_DIR OUTPUT`
replays the retained geometry through the separately bounded mixed-pivot shadow allowance; it is not
a fresh native run or a relabeling of that older strict audit. No sun counter is assigned in either lane.

Evidence includes initial profiles, real stocks/timer phase, native frame/time counters,
package/source provenance, progress and final modeled-versus-native quantities. A failed
pacing or Survival precondition prohibits long accelerated runs. A failed differential stays
failed; running/authored baselines are not acceptance. Run from Test with a task-owned host
contract, never shared production roots. The observer stops on completion and the runner
stops only its own server processes. Resource floors are10GiB RAM and100GiB disk.
