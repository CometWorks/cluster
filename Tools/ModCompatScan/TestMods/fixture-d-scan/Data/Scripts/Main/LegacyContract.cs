using ProtoBuf;
using Sandbox.Common.Components;

namespace Fixture
{
    [ProtoContract]
    public class LegacyContract
    {
        [ProtoMember]
        public int Count;

        [ProtoMember(2)]
        public bool Enabled;
    }
}
