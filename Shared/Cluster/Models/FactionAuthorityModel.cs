using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sandbox.Game.Multiplayer;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;

namespace ClusterPlugin;

public sealed record FactionAuthorityMessage
{
	public string Operation { get; init; }
	public ulong Sender { get; init; }
	public bool IsLocallyInvoked { get; init; }
	public MyFactionCollection.AddFactionMsg Faction { get; init; }
	public string FactionIconType { get; init; }
	public string FactionIconSubtype { get; init; }
	public MyFactionStateChange StateChange { get; init; }
	public long First { get; init; }
	public long Second { get; init; }
	public long Third { get; init; }
	public long Fourth { get; init; }
	public bool FirstFlag { get; init; }
	public bool SecondFlag { get; init; }
	public int Number { get; init; }
	public ReputationChangeReason Reason { get; init; }
	public bool Propagate { get; init; }
	public bool AdminChange { get; init; }
	public float Amount { get; init; }
	public string Snapshot { get; init; }
	public MyFactionCollection.MyReputationChangeWrapper[] ReputationChanges { get; init; } = Array.Empty<MyFactionCollection.MyReputationChangeWrapper>();
	/// <summary>Chaos-test only: holds the mutation in flight on the authority's game thread. Zero in production traffic.</summary>
	public int DelayMilliseconds { get; init; }
}

public static class FactionAuthorityCodec
{
	private const int MaximumPayloadLength = 32 * 1024 * 1024;
	private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { IncludeFields = true };
	private static readonly HashSet<string> Operations = new HashSet<string>(StringComparer.Ordinal)
	{
		"Create", "Edit", "AutoAccept", "StateChange", "AddReputation", "DamageReputation",
		"SetFactionReputation", "SetPlayerReputation", "CreateSuccess", "EditSuccess",
		"AcceptHumans", "AutoAcceptSuccess", "AcceptHumansSuccess", "StateChangeSuccess", "ReputationSuccess", "Snapshot"
	};

	public static string Write(FactionAuthorityMessage message)
	{
		message = WithSerializedIcon(message);
		Validate(message);
		return JsonSerializer.Serialize(message, Options);
	}

	public static FactionAuthorityMessage Read(string payload)
	{
		if (string.IsNullOrEmpty(payload) || payload.Length > MaximumPayloadLength)
			throw new InvalidOperationException("Invalid faction authority payload length");
		FactionAuthorityMessage message = JsonSerializer.Deserialize<FactionAuthorityMessage>(payload, Options)
			?? throw new InvalidOperationException("Invalid faction authority payload");
		message = WithDeserializedIcon(message);
		Validate(message);
		return message;
	}

	public static string WriteSnapshot(MyObjectBuilder_FactionCollection snapshot)
	{
		ValidateSnapshot(snapshot);
		using var stream = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, snapshot))
			throw new InvalidDataException("Failed to serialize faction authority snapshot");
		return Convert.ToBase64String(stream.ToArray());
	}

	public static MyObjectBuilder_FactionCollection ReadSnapshot(string payload)
	{
		if (string.IsNullOrEmpty(payload) || payload.Length > MaximumPayloadLength)
			throw new InvalidOperationException("Invalid faction snapshot length");
		byte[] bytes;
		try { bytes = Convert.FromBase64String(payload); }
		catch (FormatException exception) { throw new InvalidOperationException("Invalid faction snapshot encoding", exception); }
		using var stream = new MemoryStream(bytes, writable: false);
		if (!MyObjectBuilderSerializerKeen.DeserializeXML(stream, out MyObjectBuilder_FactionCollection snapshot))
			throw new InvalidDataException("Failed to deserialize faction authority snapshot");
		ValidateSnapshot(snapshot);
		return snapshot;
	}

	private static void Validate(FactionAuthorityMessage message)
	{
		if (message == null || !Operations.Contains(message.Operation))
			throw new InvalidOperationException("Unknown faction authority operation");
		if (!Enum.IsDefined(message.StateChange) || !Enum.IsDefined(message.Reason) || !float.IsFinite(message.Amount))
			throw new InvalidOperationException("Invalid faction authority value");
		if (message.ReputationChanges == null || message.ReputationChanges.Length > 4096)
			throw new InvalidOperationException("Invalid faction reputation changes");
		if (message.Operation == "Snapshot" && string.IsNullOrEmpty(message.Snapshot))
			throw new InvalidOperationException("Invalid faction snapshot message");
		if (message.Operation == "DamageReputation" && !Enum.IsDefined(typeof(MyReputationDamageType), message.Number))
			throw new InvalidOperationException("Invalid faction reputation damage type");
		if (message.Operation is "Create" or "Edit")
		{
			if ((message.Faction.FactionTag?.Length ?? 0) > 16 || (message.Faction.FactionName?.Length ?? 0) > 256
				|| (message.Faction.FactionDescription?.Length ?? 0) > 2048 || (message.Faction.FactionPrivateInfo?.Length ?? 0) > 2048
				|| !float.IsFinite(message.Faction.FactionObjectivePercentageCompleted))
				throw new InvalidOperationException("Invalid faction request fields");
		}
		if ((message.FactionIconType?.Length ?? 0) > 256 || (message.FactionIconSubtype?.Length ?? 0) > 256)
			throw new InvalidOperationException("Invalid faction icon definition");
	}

	private static FactionAuthorityMessage WithSerializedIcon(FactionAuthorityMessage message)
	{
		if (message.Faction.FactionIconGroupId is not SerializableDefinitionId icon) return message;
		return message with { FactionIconType = icon.TypeIdString, FactionIconSubtype = icon.SubtypeId };
	}

	private static FactionAuthorityMessage WithDeserializedIcon(FactionAuthorityMessage message)
	{
		if (string.IsNullOrEmpty(message.FactionIconType)) return message;
		var icon = new SerializableDefinitionId { TypeIdString = message.FactionIconType, SubtypeId = message.FactionIconSubtype };
		MyFactionCollection.AddFactionMsg faction = message.Faction;
		faction.FactionIconGroupId = icon;
		return message with { Faction = faction };
	}

	private static void ValidateSnapshot(MyObjectBuilder_FactionCollection snapshot)
	{
		if (snapshot?.Factions == null || snapshot.Players?.Dictionary == null || snapshot.Relations == null
			|| snapshot.Requests == null || snapshot.RelationsWithPlayers == null || snapshot.PlayerToFactionsVis == null)
			throw new InvalidOperationException("Incomplete faction authority snapshot");
		if (snapshot.Factions.Count > 100000 || snapshot.Factions.GroupBy(x => x.FactionId).Any(x => x.Count() != 1))
			throw new InvalidOperationException("Invalid faction authority snapshot");
		foreach (MyObjectBuilder_Faction faction in snapshot.Factions)
			if (faction == null || faction.Members == null || faction.JoinRequests == null || faction.Stations == null
				|| faction.DamageInflicted?.Dictionary == null || faction.Members.GroupBy(x => x.PlayerId).Any(x => x.Count() != 1)
				|| faction.JoinRequests.GroupBy(x => x.PlayerId).Any(x => x.Count() != 1))
				throw new InvalidOperationException("Invalid faction authority entry");
		if (snapshot.Relations.GroupBy(x => Pair(x.FactionId1, x.FactionId2)).Any(x => x.Count() != 1)
			|| snapshot.RelationsWithPlayers.GroupBy(x => Pair(x.PlayerId, x.FactionId)).Any(x => x.Count() != 1)
			|| snapshot.Requests.Any(x => x.FactionRequests == null || x.FactionRequests.Count != x.FactionRequests.Distinct().Count())
			|| snapshot.Requests.GroupBy(x => x.FactionId).Any(x => x.Count() != 1)
			|| snapshot.PlayerToFactionsVis.Any(x => x.DiscoveredFactions == null || x.DiscoveredFactions.Count != x.DiscoveredFactions.Distinct().Count())
			|| snapshot.PlayerToFactionsVis.GroupBy(x => x.IdentityId).Any(x => x.Count() != 1))
			throw new InvalidOperationException("Duplicate faction authority snapshot state");
	}

	private static (long, long) Pair(long first, long second) => first <= second ? (first, second) : (second, first);
}
