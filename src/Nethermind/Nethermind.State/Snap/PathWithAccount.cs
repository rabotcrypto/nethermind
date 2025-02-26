using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Snap
{
    public class PathWithAccount
    {
        public PathWithAccount() { }

        public PathWithAccount(Keccak path, Account account)
        {
            Path = path;
            Account = account;
        }

        public Keccak Path { get; set; }
        public Account Account { get; set; }
    }
}
