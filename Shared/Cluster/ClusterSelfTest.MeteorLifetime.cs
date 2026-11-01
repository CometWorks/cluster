using System;
using Sandbox.Common.ObjectBuilders;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// #1381 meteor lifetime, the pure parts: vanilla's lifetime by speed, the age a loaded meteor resumes at (its real
	/// age, at least the load grace left, never negative) and the birth entry on a meteor builder.
	/// </summary>
	private static void MeteorLifetimeRules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("meteor-lifetime: " + message); }
		double still = MeteorLifetime.LifetimeMilliseconds(0), fast = MeteorLifetime.LifetimeMilliseconds(500);
		Check(Math.Abs(still - 111111) < 1 && Math.Abs(MeteorLifetime.LifetimeMilliseconds(90) - 111111) < 1 && Math.Abs(fast - 20000) < 1,
			"vanilla lifetime: 10000 m at 90 m/s or faster, " + still + " / " + fast);
		Check(MeteorLifetime.CarriedAge(40000, still) == 40000, "a young meteor resumes at its age");
		Check(MeteorLifetime.CarriedAge(600000, still) == (int)(still - MeteorLifetime.LoadGraceMilliseconds), "an expired one keeps the grace");
		Check(MeteorLifetime.CarriedAge(-5000, still) == 0, "a birth in the future (clock skew) is age 0");
		Check(MeteorLifetime.CarriedAge(600000, fast) == 0, "a lifetime under the grace starts over");
		var builder = new MyObjectBuilder_Meteor();
		BlockBuilderEntry.Write(builder, MeteorLifetime.Key, "1761130000000");
		Check(BlockBuilderEntry.Read(builder, MeteorLifetime.Key) == "1761130000000" && BlockBuilderEntry.Read(new MyObjectBuilder_Meteor(), MeteorLifetime.Key) == null,
			"the birth entry rides the meteor's builder");
	}
}
