using Sandbox.Game.World;

namespace ClusterTestPlugin;

/// <summary>
/// SE-1976: <c>/helmet-test-state?client=</c> (GET) reads the server's copy of a client's helmet on this node. The
/// client keeps its own copy; the bench compares the two by toggling the helmet from the client.
/// </summary>
internal static class HelmetTestRoutes
{
    public static void Register() =>
        TestRoutes.Add(
            "GET",
            "/helmet-test-state",
            context =>
            {
                ulong client = (ulong)TestRoutes.Long(context, "client");
                TestRoutes.Json(
                    context,
                    200,
                    TestRoutes.OnGameThread<object>(() =>
                    {
                        var character = MySession.Static.Players.TryGetPlayerBySteamId(
                            client,
                            out MyPlayer player
                        )
                            ? player.Character
                            : null;
                        return character == null || character.MarkedForClose
                            ? new { present = false }
                            : new
                            {
                                present = true,
                                entityId = character.EntityId,
                                helmet = character.OxygenComponent?.HelmetEnabled,
                            };
                    })
                );
            }
        );
}
