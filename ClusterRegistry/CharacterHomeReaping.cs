using ClusterProtocol;

namespace ClusterRegistry;

internal sealed partial class GatewayRegistry
{
    public CharacterReapPermit AuthorizeCharacterReap(ulong client, long identity, long entity,
        string node, long epoch, ulong partition, long generation, long homeVersion, long writeRevision)
    {
        lock (_lock)
        {
            RequireCharacterHomeWriter(partition, node, epoch, generation);
            CharacterHome home = _state.CharacterHomes.GetValueOrDefault(client)
                ?? throw new RegistryConflictException("No character home for reap");
            if (home.Identity != identity || entity == 0 || home.Entity == entity)
                throw new RegistryConflictException("Reap candidate is the current home or belongs to another identity");
            CharacterReapPermit? pending = home.PendingReap;
            if (pending != null)
            {
                if (pending.Entity != entity || pending.Identity != identity)
                    throw new RegistryConflictException("Another character reap is pending");
                if (pending.Node == node && pending.Epoch == epoch && pending.Partition == partition
                    && pending.Generation == generation) return pending;
                if (CharacterReapExecutorLive(pending))
                    throw new RegistryConflictException("Character reap still belongs to its original executor");
            }
            if (home.Version != homeVersion || home.WriteRevision != writeRevision)
                throw new RegistryConflictException("Character home changed before reap authorization");
            var permit = new CharacterReapPermit(pending?.Operation ?? Guid.NewGuid(), entity, identity,
                node, epoch, partition, generation, home.WriteRevision + 1);
            _state.CharacterHomes[client] = home with { PendingReap = permit, PendingReapPartition = partition, WriteRevision = home.WriteRevision + 1 };
            Persist();
            // Unlike soft routing hints, permission for an irreversible close must survive a crash.
            Flush();
            return permit;
        }
    }

    public void ResolveCharacterReap(ulong client, CharacterReapPermit permit)
    {
        lock (_lock)
        {
            CharacterHome? home = _state.CharacterHomes.GetValueOrDefault(client);
            if (home?.PendingReap == null) return; // acknowledgement replay; never changes a later home
            if (home.PendingReap != permit)
                throw new RegistryConflictException("Character reap executor was fenced or replaced");
            _state.CharacterHomes[client] = home with { PendingReap = null, PendingReapPartition = 0, WriteRevision = home.WriteRevision + 1 };
            Persist();
            Flush(); // barrier against reports captured before or during the permit
        }
    }

    public object[] PendingCharacterReaps(string node, long epoch)
    {
        lock (_lock)
        {
            RequireLiveNode(node, epoch);
            var results = new List<object>();
            foreach ((ulong client, CharacterHome home) in _state.CharacterHomes)
            {
                if (home.PendingReap is not { } permit) continue;
                ulong partition = home.PendingReapPartition != 0 ? home.PendingReapPartition
                    : LiveHomePartition(new CharacterHome(permit.Identity, permit.Entity,
                        permit.Partition, permit.Node, home.Updated));
                bool original = permit.Node == node && permit.Epoch == epoch;
                bool assigned = _state.Partitions.TryGetValue(partition, out PartitionOwnership? owner)
                    && owner.Node == node && owner.Epoch == epoch;
                if (original || assigned)
                    results.Add(new { client, permit, partition, homeVersion = home.Version, writeRevision = home.WriteRevision });
            }
            return results.ToArray();
        }
    }

    private void RequireCharacterHomeWriter(ulong partition, string node, long epoch, long generation)
    {
        DateTimeOffset now = _clock();
        NodeRegistration writer = RequireLiveNode(node, epoch);
        PartitionOwnership owner = RequireOwner(partition, node, epoch, generation);
        if (IsRetired(partition) || owner.LeaseExpires <= now || writer.LeaseExpires <= now)
            throw new RegistryConflictException($"Partition {partition} home writer is no longer leased");
    }

    private bool CharacterReapExecutorLive(CharacterReapPermit permit) =>
        !IsRetired(permit.Partition)
        && _state.Partitions.TryGetValue(permit.Partition, out PartitionOwnership? owner)
        && owner.Node == permit.Node && owner.Epoch == permit.Epoch && owner.Generation == permit.Generation
        && owner.LeaseExpires > _clock()
        && _state.Nodes.TryGetValue(permit.Node, out NodeRegistration? node)
        && node.Epoch == permit.Epoch && node.State is NodeState.Active or NodeState.Closed
        && node.LeaseExpires > _clock();
}
