using System.Collections.Generic;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// SE-1420: an imported block re-emits its ComponentData in whatever order its component dictionary enumerates. The
	/// shape is the open cockpit of wreck 'Wrecks-314' (partition 36991, octahedron-test 2026-11-13): the source had
	/// MyInventoryBase before MyMultiTextPanelComponent, the target after it, and the source's inventory carried an
	/// empty SubtypeName the target dropped. That pair's only residual was a hinge's LimitsActive, not the order.
	/// </summary>
	private static void HandoverDriftComponentOrder()
	{
		string Inventory(string subtype = "", string items = "<Items />") => "<ComponentData><TypeId>MyInventoryBase</TypeId>"
			+ "<Component xsi:type=\"MyObjectBuilder_Inventory\">" + subtype + "<CanPutItems>true</CanPutItems>" + items
			+ "<nextItemId>9</nextItemId><Volume>1</Volume><Mass>9223372036854.775807</Mass><MaxItemCount>2147483647</MaxItemCount>"
			+ "<Size xsi:nil=\"true\" /><InventoryFlags>CanSend</InventoryFlags><RemoveEntityOnEmpty>false</RemoveEntityOnEmpty></Component></ComponentData>";
		const string panel = "<ComponentData><TypeId>MyMultiTextPanelComponent</TypeId><Component xsi:type=\"MyObjectBuilder_MultiTextPanelComponent\">"
			+ "<SelectedTexture>0</SelectedTexture></Component></ComponentData>";
		string Cockpit(params string[] components) => DriftHead + "<MyObjectBuilder_EntityBase xsi:type=\"MyObjectBuilder_CubeGrid\"><EntityId>1</EntityId><CubeBlocks>"
			+ "<MyObjectBuilder_CubeBlock xsi:type=\"MyObjectBuilder_Cockpit\"><SubtypeName>OpenCockpitSmall</SubtypeName><EntityId>2</EntityId>"
			+ "<ComponentContainer><Components>" + string.Concat(components) + "</Components></ComponentContainer>"
			+ "</MyObjectBuilder_CubeBlock></CubeBlocks></MyObjectBuilder_EntityBase>" + DriftTail;

		List<string> residuals = ImportRoundTrip.ResidualsOfXml(Cockpit(Inventory("<SubtypeName />"), panel), Cockpit(panel, Inventory()));
		Assert(residuals.Count == 0, "a cockpit's components in swapped order were reported: " + DriftReport(residuals));

		const string ore = "<Items><MyObjectBuilder_InventoryItem><Amount>5</Amount><PhysicalContent xsi:type=\"MyObjectBuilder_Ore\">"
			+ "<SubtypeName>Iron</SubtypeName></PhysicalContent><ItemId>8</ItemId></MyObjectBuilder_InventoryItem></Items>";
		residuals = ImportRoundTrip.ResidualsOfXml(Cockpit(Inventory(items: ore), panel), Cockpit(panel, Inventory()));
		Assert(residuals.Count > 0, "a LOST item in a reordered inventory must stay a residual");
		residuals = ImportRoundTrip.ResidualsOfXml(Cockpit(Inventory(), panel), Cockpit(panel));
		Assert(residuals.Count > 0, "a LOST inventory component must stay a residual");
	}
}
