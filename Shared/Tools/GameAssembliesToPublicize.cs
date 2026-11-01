using System.Runtime.CompilerServices;

// Game assemblies the plugin accesses beyond their public surface. The Magnetar compiler
// publicizes every assembly named here when it builds the plugin from a dev folder; the
// Krafs publicizer does the same for IDE / msbuild builds from the <Publicize> items of the
// project files. Keep the two lists identical.
[assembly: IgnoresAccessChecksTo("Sandbox.Game")]
[assembly: IgnoresAccessChecksTo("Sandbox.Common")]
[assembly: IgnoresAccessChecksTo("SpaceEngineers.Game")]
[assembly: IgnoresAccessChecksTo("SpaceEngineers.ObjectBuilders")]
[assembly: IgnoresAccessChecksTo("VRage")]
[assembly: IgnoresAccessChecksTo("VRage.Game")]
[assembly: IgnoresAccessChecksTo("VRage.Library")]
[assembly: IgnoresAccessChecksTo("VRage.Math")]
[assembly: IgnoresAccessChecksTo("VRage.Scripting")]
[assembly: IgnoresAccessChecksTo("VRage.Dedicated")]
