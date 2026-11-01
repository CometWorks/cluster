# Standalone offline resource reference

`Test/offline_reference.py` starts stock DS instances through the normal isolated standalone
launcher. It registers only this observer and the host's compatibility plugins. ClusterNode.dll
is a dormant dependency for exact product capture/model code: its plugin never initializes,
there is no gateway, claim, offline sweep, cluster clock or replication patch. The live engine
advances reference stocks; the model touches clones only. Actual Survival/non-simplified mode
is a required precondition. The dedicated config copies the fixture's own Settings.

The runner first compares3600 ordinary60Hz engine frames with3600 engine frames at bounded
native max-speed pacing. Both gameplay time and elapsed-sun counters must advance by exactly
the native amount per frame, and battery/fuel outcomes must agree within the recorded native
phase bounds. Only then can three independent1296000-frame baselines start. Native max-speed
removes the engine's pacing wait; a1ms observer sleep bounds CPU/noise. No update is skipped,
no engine counter is assigned, and no admin sun jump is used. The long references consume six
hours of60Hz resource updates, including native reactor900/1800/3600 timer transitions.

Vanilla elapsed sun time advances16ms/frame, whereas the clustered authority is stamped in
milliseconds/16. The model boundary explicitly converts the same number of honest resource
updates into authority stamps (1296000 engine updates =1350000 authority units =6h).
Native elapsed sun time over those updates is5.76h. Sun rotation is disabled for the energy
cases; this lane makes no planetary/full-rotation claim. No production formula is used as
the reference. Modes Auto/Recharge/Discharge and a sun-facing priority control are separate.

The observed lane calls the actual `Prepare`/`InPlace` path every100 native frames for a full
natural learning rotation, then advances an independent full reference rotation. Only the product's
guarded native reactor-fuel observer patch is installed; the cluster plugin remains dormant.
Known native debits and unexpected inventory edits are separate positive/negative controls.

The planet lane initializes native voxel physics without changing terrain, then prefetches shapes
as native targeting does. Ordinary live rolling-pivot output and fresh unregistered native
component-point computations are recorded separately. Passing instantaneous geometry does not
establish the original2% live-output bound: `check_offline_solar.py` requires both and reports
each result explicitly. No sun counter is assigned in either lane.

Evidence includes initial profiles, real stocks/timer phase, native frame/time counters,
package/source provenance, progress and final modeled-versus-native quantities. A failed
pacing or Survival precondition prohibits long accelerated runs. A failed differential stays
failed; running/authored baselines are not acceptance. Run from Test with a task-owned host
contract, never shared production roots. The observer stops on completion and the runner
stops only its own server processes. Resource floors are10GiB RAM and100GiB disk.
