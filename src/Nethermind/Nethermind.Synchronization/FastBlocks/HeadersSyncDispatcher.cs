//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.FastBlocks
{
    public class HeadersSyncDispatcher : SyncDispatcher<HeadersSyncBatch>
    {
        public HeadersSyncDispatcher(
            ISyncFeed<HeadersSyncBatch> syncFeed,
            ISyncPeerPool syncPeerPool,
            IPeerAllocationStrategyFactory<FastBlocksBatch> peerAllocationStrategy,
            ILogManager logManager)
            : base(syncFeed, syncPeerPool, peerAllocationStrategy, logManager)
        {
        }

        protected override async Task Dispatch(
            PeerInfo peerInfo,
            HeadersSyncBatch batch,
            CancellationToken cancellationToken)
        {
            ISyncPeer peer = peerInfo.SyncPeer;
            batch.ResponseSourcePeer = peerInfo;
            batch.MarkSent();

            try
            {
                batch.Response = await peer.GetBlockHeaders(batch.StartNumber, batch.RequestSize, 0, cancellationToken);
            }
            catch (TimeoutException)
            {
                if (Logger.IsDebug) Logger.Debug($"{batch} - request block header timeout {batch.RequestTime:F2}");
                return;
            }
            if (batch.RequestTime > 1000)
            {
                if (Logger.IsDebug) Logger.Debug($"{batch} - peer is slow {batch.RequestTime:F2}");
            }
        }
    }
}
