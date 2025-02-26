using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class PathWithStorageSlot
    {
        public PathWithStorageSlot(Keccak keyHash, byte[] slotRlpValue)
        {
            Path = keyHash;
            SlotRlpValue = slotRlpValue;
        }

        public Keccak Path { get; set; }
        public byte[] SlotRlpValue { get; set; }
    }
}
