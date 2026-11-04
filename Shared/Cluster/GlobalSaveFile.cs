using System;
using System.IO;
using System.Text;
using VRage.Game;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterPlugin;

internal static class GlobalSaveFile
{
	private const uint BodyMagic = 0x32565347;

	public static byte[] Write(MyObjectBuilder_Checkpoint checkpoint, uint generation, uint sequence, byte kind, long clusterTime,
		string commandLedger = null, string characterRecord = null, string timeOfDayRecord = null, string shipStorageRecord = null,
		string adminSafeZonesRecord = null, string contractRecord = null, string offlineRatesRecord = null, string promotionRecord = null)
	{
		if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
		using MemoryStream checkpointBody = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(checkpointBody, checkpoint))
			throw new InvalidDataException("Failed to serialize the World Authority checkpoint");
		byte[] body = checkpointBody.ToArray();
		if (commandLedger != null)
		{
			byte[] ledger = Encoding.UTF8.GetBytes(commandLedger);
			using MemoryStream combined = new();
			using (BinaryWriter writer = new(combined, Encoding.UTF8, leaveOpen: true))
			{
				writer.Write(BodyMagic);
				writer.Write(body.Length);
				writer.Write(body);
				writer.Write(ledger.Length);
				writer.Write(ledger);
				// Optional sections in fixed order, appended rather than framed into the header so a
				// file written before either still reads: the reader takes whatever sections are
				// present. WorldAuthority.md 5.5's settings store, §8.2's time-of-day state, the stored ships
				// (SE1-0233), the admin safe zones (SE1-0252), then §6.3's accepted contracts (SE1-0247); a later
				// section forces empty placeholders for the earlier ones it follows. The optional
				// offline-rates slot precedes the promotion record for save compatibility.
				string[] sections = { characterRecord, timeOfDayRecord, shipStorageRecord, adminSafeZonesRecord, contractRecord, offlineRatesRecord, promotionRecord };
				int count = Array.FindLastIndex(sections, section => section != null) + 1;
				for (int i = 0; i < count; i++)
				{
					byte[] record = Encoding.UTF8.GetBytes(sections[i] ?? string.Empty);
					writer.Write(record.Length);
					writer.Write(record);
				}
			}
			body = combined.ToArray();
		}
		return PartitionSaveFile.Write(0, generation, sequence, kind, default(BoundingSphereD), false, 0, body, clusterTime);
	}

	public static MyObjectBuilder_Checkpoint Read(byte[] file, uint? generation = null, uint? sequence = null)
		=> ReadBundle(file, generation, sequence).Checkpoint;

	public static Bundle ReadBundle(byte[] file, uint? generation = null, uint? sequence = null)
	{
		PartitionSaveFile.Parsed parsed = PartitionSaveFile.Read(file);
		if (parsed.PartitionId != 0 || parsed.EntityCount != 0)
			throw new InvalidDataException("Global save has an invalid MPS envelope");
		if (generation.HasValue) PartitionSaveFile.ValidateEnvelope(parsed, 0, generation.Value, sequence);
		byte[] checkpointBody = parsed.Body;
		string ledger = null, characterRecord = null, timeOfDayRecord = null, shipStorageRecord = null, adminSafeZonesRecord = null,
			contractRecord = null, offlineRatesRecord = null, promotionRecord = null;
		if (parsed.Body.Length >= 12 && BitConverter.ToUInt32(parsed.Body) == BodyMagic)
		{
			using MemoryStream combined = new(parsed.Body, writable: false);
			using BinaryReader reader = new(combined, Encoding.UTF8);
			reader.ReadUInt32();
			int checkpointLength = reader.ReadInt32();
			if (checkpointLength <= 0 || checkpointLength > combined.Length - combined.Position - 4)
				throw new InvalidDataException("Invalid World Authority checkpoint body length");
			checkpointBody = reader.ReadBytes(checkpointLength);
			int ledgerLength = reader.ReadInt32();
			if (ledgerLength < 0 || ledgerLength > 16 * 1024 * 1024 || ledgerLength > combined.Length - combined.Position)
				throw new InvalidDataException("Invalid World Authority command ledger length");
			ledger = Encoding.UTF8.GetString(reader.ReadBytes(ledgerLength));
			if (combined.Position < combined.Length)
			{
				int recordLength = reader.ReadInt32();
				if (recordLength < 0 || recordLength > combined.Length - combined.Position)
					throw new InvalidDataException("Invalid World Authority character record length");
				characterRecord = recordLength == 0 ? null : Encoding.UTF8.GetString(reader.ReadBytes(recordLength));
			}
			if (combined.Position < combined.Length)
			{
				int recordLength = reader.ReadInt32();
				if (recordLength < 0 || recordLength > combined.Length - combined.Position)
					throw new InvalidDataException("Invalid World Authority time-of-day record length");
				timeOfDayRecord = recordLength == 0 ? null : Encoding.UTF8.GetString(reader.ReadBytes(recordLength));
			}
			if (combined.Position < combined.Length)
			{
				int recordLength = reader.ReadInt32();
				if (recordLength < 0 || recordLength > combined.Length - combined.Position)
					throw new InvalidDataException("Invalid World Authority ship storage record length");
				shipStorageRecord = recordLength == 0 ? null : Encoding.UTF8.GetString(reader.ReadBytes(recordLength));
			}
			if (combined.Position < combined.Length)
			{
				int recordLength = reader.ReadInt32();
				if (recordLength < 0 || recordLength > combined.Length - combined.Position)
					throw new InvalidDataException("Invalid World Authority admin safe zone record length");
				adminSafeZonesRecord = recordLength == 0 ? null : Encoding.UTF8.GetString(reader.ReadBytes(recordLength));
			}
			if (combined.Position < combined.Length)
			{
				int recordLength = reader.ReadInt32();
				if (recordLength < 0 || recordLength > combined.Length - combined.Position)
					throw new InvalidDataException("Invalid World Authority contract record length");
				contractRecord = recordLength == 0 ? null : Encoding.UTF8.GetString(reader.ReadBytes(recordLength));
			}
			if (combined.Position < combined.Length)
			{
				int recordLength = reader.ReadInt32();
				if (recordLength < 0 || recordLength > 16 * 1024 * 1024 || recordLength > combined.Length - combined.Position)
					throw new InvalidDataException("Invalid World Authority offline rates record length");
				offlineRatesRecord = recordLength == 0 ? null : Encoding.UTF8.GetString(reader.ReadBytes(recordLength));
			}
			if (combined.Position < combined.Length)
			{
				int recordLength = reader.ReadInt32();
				if (recordLength < 0 || recordLength > 4 * 1024 * 1024 || recordLength != combined.Length - combined.Position)
					throw new InvalidDataException("Invalid World Authority promotion record length");
				promotionRecord = recordLength == 0 ? null : Encoding.UTF8.GetString(reader.ReadBytes(recordLength));
			}
		}
		using MemoryStream body = new MemoryStream(checkpointBody, writable: false);
		if (!MyObjectBuilderSerializerKeen.DeserializeXML(body, out MyObjectBuilder_Checkpoint checkpoint))
			throw new InvalidDataException("Failed to deserialize the World Authority checkpoint");
		return new Bundle(checkpoint, ledger, characterRecord, timeOfDayRecord, shipStorageRecord, adminSafeZonesRecord, contractRecord, offlineRatesRecord, promotionRecord);
	}

	internal sealed record Bundle(MyObjectBuilder_Checkpoint Checkpoint, string CommandLedger, string CharacterRecord = null,
		string TimeOfDayRecord = null, string ShipStorageRecord = null, string AdminSafeZonesRecord = null,
		string ContractRecord = null, string OfflineRatesRecord = null, string PromotionRecord = null);
}
