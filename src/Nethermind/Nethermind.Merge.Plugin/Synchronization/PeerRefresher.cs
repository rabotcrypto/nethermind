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
//

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;

[assembly: InternalsVisibleTo("Nethermind.Merge.Plugin.Test")]
namespace Nethermind.Merge.Plugin.Synchronization;

public class PeerRefresher : IPeerRefresher, IAsyncDisposable
{
    private readonly IPeerDifficultyRefreshPool _syncPeerPool;
    private static readonly TimeSpan _minRefreshDelay = TimeSpan.FromSeconds(10);
    private DateTime _lastRefresh = DateTime.MinValue;
    private (Keccak headBlockhash, Keccak headParentBlockhash, Keccak finalizedBlockhash) _lastBlockhashes = (Keccak.Zero, Keccak.Zero, Keccak.Zero);
    private readonly ITimer _refreshTimer;
    private readonly ILogger _logger;

    public PeerRefresher(IPeerDifficultyRefreshPool syncPeerPool, ITimerFactory timerFactory, ILogManager logManager)
    {
        _refreshTimer = timerFactory.CreateTimer(_minRefreshDelay);
        _refreshTimer.Elapsed += TimerOnElapsed;
        _refreshTimer.AutoReset = false;
        _syncPeerPool = syncPeerPool;
        _logger = logManager.GetClassLogger(GetType());
    }

    public void RefreshPeers(Keccak headBlockhash, Keccak headParentBlockhash, Keccak finalizedBlockhash)
    {
        _lastBlockhashes = (headBlockhash, headParentBlockhash, finalizedBlockhash);
        TimeSpan timePassed = DateTime.Now - _lastRefresh;
        if (timePassed > _minRefreshDelay)
        {
            Refresh(headBlockhash, headParentBlockhash, finalizedBlockhash);
        }
        else if (!_refreshTimer.Enabled)
        {
            _refreshTimer.Interval = _minRefreshDelay - timePassed;
            _refreshTimer.Start();
        }
    }

    private void TimerOnElapsed(object? sender, EventArgs e)
    {
        Refresh(_lastBlockhashes.headBlockhash, _lastBlockhashes.headParentBlockhash, _lastBlockhashes.finalizedBlockhash);
    }

    private void Refresh(Keccak headBlockhash, Keccak headParentBlockhash, Keccak finalizedBlockhash)
    {
        _lastRefresh = DateTime.Now;
        foreach (PeerInfo peer in _syncPeerPool.AllPeers)
        {
#pragma warning disable CS4014
            StartPeerRefreshTask(peer.SyncPeer, headBlockhash, headParentBlockhash, finalizedBlockhash);
#pragma warning restore CS4014
        }
    }

    private async Task StartPeerRefreshTask(
        ISyncPeer syncPeer,
        Keccak headBlockhash,
        Keccak headParentBlockhash,
        Keccak finalizedBlockhash
    )
    {
        using CancellationTokenSource delaySource = new(Timeouts.Eth);
        try
        {
            await RefreshPeerForFcu(syncPeer, headBlockhash, headParentBlockhash, finalizedBlockhash, delaySource.Token);
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Exception in peer refresh. This is unexpected. {syncPeer}", exception);
        }
    }

    internal async Task RefreshPeerForFcu(
        ISyncPeer syncPeer,
        Keccak headBlockhash,
        Keccak headParentBlockhash,
        Keccak finalizedBlockhash,
        CancellationToken token)
    {
        // headBlockhash is obtained together with headParentBlockhash
        Task<BlockHeader[]> getHeadParentHeaderTask = syncPeer.GetBlockHeaders(headParentBlockhash, 2, 0, token);
        Task<BlockHeader?> getFinalizedHeaderTask = finalizedBlockhash == Keccak.Zero
            ? Task.FromResult<BlockHeader?>(null)
            : syncPeer.GetHeadBlockHeader(finalizedBlockhash, token);

        BlockHeader? headBlockHeader, headParentBlockHeader, finalizedBlockHeader;

        try
        {
            BlockHeader[] headAndParentHeaders = await getHeadParentHeaderTask;
            if (!TryGetHeadAndParent(headBlockhash, headParentBlockhash, headAndParentHeaders, out headBlockHeader, out headParentBlockHeader))
            {
                _syncPeerPool.ReportRefreshFailed(syncPeer, "FCU unexpected response length");
                return;
            }

            finalizedBlockHeader = await getFinalizedHeaderTask;
        }
        catch (AggregateException exception) when (exception.InnerException is OperationCanceledException)
        {
            _syncPeerPool.ReportRefreshFailed(syncPeer, "FCU timeout", exception.InnerException);
            return;
        }
        catch (OperationCanceledException exception)
        {
            _syncPeerPool.ReportRefreshFailed(syncPeer, "FCU timeout", exception);
            return;
        }
        catch (Exception exception)
        {
            _syncPeerPool.ReportRefreshFailed(syncPeer, "FCU faulted", exception);
            return;
        }

        if (_logger.IsTrace) _logger.Trace($"PeerRefreshForFCU received block info from {syncPeer.Node:c} headHeader: {headBlockHeader} headParentHeader: {headParentBlockHeader} finalizedBlockHeader: {finalizedBlockHeader}");

        if (finalizedBlockhash != Keccak.Zero && finalizedBlockHeader == null)
        {
            _syncPeerPool.ReportRefreshFailed(syncPeer, "FCU no finalized block header");
            return;
        }

        if (!CheckHeader(syncPeer, finalizedBlockHeader))
        {
            return;
        }

        if (!CheckHeader(syncPeer, headParentBlockHeader))
        {
            return;
        }

        if (!CheckHeader(syncPeer, headBlockHeader))
        {
            return;
        }

        _syncPeerPool.SignalPeersChanged();
    }

    private bool CheckHeader(ISyncPeer syncPeer, BlockHeader? header)
    {
        if (header is not null)
        {
            if (!HeaderValidator.ValidateHash(header))
            {
                _syncPeerPool.ReportRefreshFailed(syncPeer, "FCU invalid header hash");
                return false;
            }

            _syncPeerPool.UpdateSyncPeerHeadIfHeaderIsBetter(syncPeer, header);
        }

        return true;
    }

    private static bool TryGetHeadAndParent(Keccak headBlockhash, Keccak headParentBlockhash, BlockHeader[] headers, out BlockHeader? headBlockHeader, out BlockHeader? headParentBlockHeader)
    {
        headBlockHeader = null;
        headParentBlockHeader = null;

        if (headers.Length > 2)
        {
            return false;
        }

        if (headers.Length == 1 && headers[0].Hash == headParentBlockhash)
        {
            headParentBlockHeader = headers[0];
        }
        else if (headers.Length == 2)
        {
            // Maybe the head is not the same as we expected. In that case, leave it as null
            if (headBlockhash == headers[1].Hash)
            {
                headBlockHeader = headers[1];
            }

            headParentBlockHeader = headers[0];
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        _refreshTimer.Dispose();
        return default;
    }
}

public interface IPeerRefresher
{
    void RefreshPeers(Keccak headBlockhash, Keccak headParentBlockhash, Keccak finalizedBlockhash);
}
