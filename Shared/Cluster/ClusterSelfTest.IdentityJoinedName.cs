namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1100: the name the World Authority gives an existing identity when its player logs in. A bot fleet that
	/// starts prefixing its names, or a player renamed on Steam, gets the new name; a Steam player's stored
	/// platform icon stays, and a login with no name of its own (the Gateway's fallback is the id) changes nothing.
	/// </summary>
	private static void IdentityJoinedName()
	{
		const ulong Id = 76561202000000017;
		void Check(string current, string joined, string expected) => Assert(
			MyPlayerCollectionPatch.JoinedName(current, joined, Id) == expected,
			$"identity-joined-name: '{current}' joining as '{joined}' should become '{expected ?? "(unchanged)"}', "
			+ $"got '{MyPlayerCollectionPatch.JoinedName(current, joined, Id) ?? "(unchanged)"}'");

		Check("Scott Becker", "[B] Scott Becker", "[B] Scott Becker");
		Check("[B] Scott Becker", "[B] Scott Becker", null);
		Check("\ue030Viktor", "Viktor", null);
		Check("\ue030Viktor", "Viktor F", "\ue030Viktor F");
		Check("\ue030Viktor", "\ue031Viktor F", "\ue031Viktor F");
		Check("Scott Becker", "76561202000000017", null);
		Check("Scott Becker", "", null);
		Check("Scott Becker", null, null);
		Check(null, "Scott Becker", "Scott Becker");
	}
}
