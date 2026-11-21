namespace ClusterPlugin;

/// <summary>
/// SE-1158: the game's DLC app ids (Content/Data/Game/DLCs.sbc, MyDLCs.DLCs) that the Gateway asks Steam about for
/// every player (UserHasLicenseForApp, as a vanilla DS does). Shared by the Gateway and the nodes. A node treats a DLC
/// missing from this list as not owned by anyone, so a DLC released after the list was last updated keeps its blocks
/// unbuildable until it is added here, instead of open to everyone. Test/check_dlc_ids.py compares the list with
/// the game's DLCs.sbc and the Rust Gateway's DLC_APP_IDS.
/// </summary>
public static class KnownDlcs
{
    public static readonly uint[] AppIds =
    [
        573160,
        999999990,
        999999991,
        1049790,
        1084680,
        1135960,
        1167910,
        1241550,
        1307680,
        1374610,
        1475830,
        1676100,
        1783760,
        1958640,
        2504720,
        2569770,
        2914120,
        3066290,
        3601770,
        3858380,
        4116960,
        4502100,
        4835530,
        5070970,
    ];

    public static bool Contains(uint appId) => System.Array.IndexOf(AppIds, appId) >= 0;
}
