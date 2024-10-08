// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;
using Lantern.Discv5.WireProtocol;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Config;
using Lantern.Discv5.Enr.Identity.V4;
using Nethermind.Crypto;
using Nethermind.Network.Config;
using Nethermind.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Nethermind.Db;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Network.Discovery.Discv5;
using NBitcoin.Secp256k1;

namespace Nethermind.Network.Discovery;

public class DiscoveryV5App : IDiscoveryApp
{
    private readonly IDiscv5Protocol _discv5Protocol;
    private readonly IPeerManager? _peerManager;
    private readonly Logging.ILogger _logger;
    private readonly IDiscoveryConfig _discoveryConfig;
    private readonly IDb _discoveryDb;
    private readonly CancellationTokenSource _appShutdownSource = new();
    private readonly DiscoveryReport? _discoveryReport;
    private readonly IServiceProvider _serviceProvider;
    private readonly SessionOptions _sessionOptions;

    public DiscoveryV5App(SameKeyGenerator privateKeyProvider, IIPResolver? ipResolver, IPeerManager? peerManager, INetworkConfig networkConfig, IDiscoveryConfig discoveryConfig, IDb discoveryDb, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(ipResolver);

        _logger = logManager.GetClassLogger();
        _discoveryConfig = discoveryConfig;
        _discoveryDb = discoveryDb;
        _peerManager = peerManager;

        IdentityVerifierV4 identityVerifier = new();

        _sessionOptions = new()
        {
            Signer = new IdentitySignerV4(privateKeyProvider.Generate().KeyBytes),
            Verifier = identityVerifier,
            SessionKeys = new SessionKeys(privateKeyProvider.Generate().KeyBytes),
        };

        string[] bootstrapNodes = [.. (discoveryConfig.Bootnodes ?? "").Split(",", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct()];

        IServiceCollection services = new ServiceCollection()
           .AddSingleton<ILoggerFactory, NullLoggerFactory>()
           .AddSingleton(_sessionOptions.Verifier)
           .AddSingleton(_sessionOptions.Signer);

        EnrFactory enrFactory = new(new EnrEntryRegistry());

        Lantern.Discv5.Enr.Enr[] bootstrapEnrs = [
            .. bootstrapNodes.Where(e => e.StartsWith("enode:"))
                .Select(e => new Enode(e))
                .Select(GetEnr),
            .. bootstrapNodes.Where(e => e.StartsWith("enr:")).Select(enr => enrFactory.CreateFromString(enr, identityVerifier)),
            // TODO: Move to routing table's UpdateFromEnr
            .. _discoveryDb.GetAllValues().Select(enr => enrFactory.CreateFromBytes(enr, identityVerifier))
            ];

        EnrBuilder enrBuilder = new EnrBuilder()
            .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(_sessionOptions.Signer.PublicKey))
            .WithEntry(EnrEntryKey.Ip, new EntryIp(ipResolver.ExternalIp))
            .WithEntry(EnrEntryKey.Tcp, new EntryTcp(networkConfig.P2PPort))
            .WithEntry(EnrEntryKey.Udp, new EntryUdp(networkConfig.DiscoveryPort));

        IDiscv5ProtocolBuilder discv5Builder = new Discv5ProtocolBuilder(services)
            .WithConnectionOptions(new ConnectionOptions
            {
                UdpPort = networkConfig.DiscoveryPort
            })
            .WithSessionOptions(_sessionOptions)
            .WithTableOptions(new TableOptions(bootstrapEnrs.Select(enr => enr.ToString()).ToArray()))
            .WithEnrBuilder(enrBuilder)
            .WithLoggerFactory(new NethermindLoggerFactory(logManager, true))
            .WithServices(s =>
            {
                s.AddSingleton(logManager);
                NettyDiscoveryV5Handler.Register(s);
            });

        _discv5Protocol = discv5Builder.Build();
        _discv5Protocol.NodeAdded += (e) => NodeAddedByDiscovery(e.Record);
        _discv5Protocol.NodeRemoved += NodeRemovedByDiscovery;

        _serviceProvider = discv5Builder.GetServiceProvider();
        _discoveryReport = new DiscoveryReport(_discv5Protocol, logManager, _appShutdownSource.Token);
    }

    private void NodeAddedByDiscovery(IEnr newEntry)
    {
        if (!TryGetNodeFromEnr(newEntry, out Node? newNode))
        {
            return;
        }

        NodeAdded?.Invoke(this, new NodeEventArgs(newNode));

        _discoveryReport?.NodeFound();

        if (_logger.IsDebug) _logger.Debug($"A node discovered via discv5: {newEntry} = {newNode}.");
    }

    private void NodeRemovedByDiscovery(NodeTableEntry removedEntry)
    {
        if (!TryGetNodeFromEnr(removedEntry.Record, out Node? removedNode))
        {
            return;
        }

        NodeRemoved?.Invoke(this, new NodeEventArgs(removedNode));

        if (_logger.IsDebug) _logger.Debug($"Node removed from discovered via discv5: {removedEntry.Record} = {removedNode}.");
    }

    private bool TryGetNodeFromEnr(IEnr enr, [NotNullWhen(true)] out Node? node)
    {
        static PublicKey? GetPublicKeyFromEnr(IEnr entry)
        {
            byte[] keyBytes = entry.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
            return Context.Instance.TryCreatePubKey(keyBytes, out _, out ECPubKey? key) ? new PublicKey(key.ToBytes(false)) : null;
        }

        node = null;
        if (!enr.HasKey(EnrEntryKey.Tcp))
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, no TCP port.");
            return false;
        }
        if (!enr.HasKey(EnrEntryKey.Ip))
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, no IP.");
            return false;
        }
        if (!enr.HasKey(EnrEntryKey.Secp256K1))
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, no signature.");
            return false;
        }
        if (enr.HasKey(EnrEntryKey.Eth2))
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, ETH2 detected.");
            return false;
        }

        PublicKey? key = GetPublicKeyFromEnr(enr);
        if (key is null)
        {
            if (_logger.IsTrace) _logger.Trace($"Enr declined, unable to extract public key.");
            return false;
        }

        IPAddress ip = enr.GetEntry<EntryIp>(EnrEntryKey.Ip).Value;
        int tcpPort = enr.GetEntry<EntryTcp>(EnrEntryKey.Tcp).Value;

        node = new(key, ip.ToString(), tcpPort);
        return true;
    }

    private Lantern.Discv5.Enr.Enr GetEnr(Enode node) => new EnrBuilder()
        .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
        .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
        .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(Context.Instance.CreatePubKey(node.PublicKey.PrefixedBytes).ToBytes(false)))
        .WithEntry(EnrEntryKey.Ip, new EntryIp(node.HostIp))
        .WithEntry(EnrEntryKey.Tcp, new EntryTcp(node.Port))
        .WithEntry(EnrEntryKey.Udp, new EntryUdp(node.DiscoveryPort))
        .Build();

    private Lantern.Discv5.Enr.Enr GetEnr(Node node) => new EnrBuilder()
        .WithIdentityScheme(_sessionOptions.Verifier, _sessionOptions.Signer)
        .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
        .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(node.Id.PrefixedBytes))
        .WithEntry(EnrEntryKey.Ip, new EntryIp(node.Address.Address))
        .WithEntry(EnrEntryKey.Tcp, new EntryTcp(node.Address.Port))
        .WithEntry(EnrEntryKey.Udp, new EntryUdp(node.Address.Port))
        .Build();

    public event EventHandler<NodeEventArgs>? NodeAdded;
    public event EventHandler<NodeEventArgs>? NodeRemoved;

    public void Initialize(PublicKey masterPublicKey) { }

    public void InitializeChannel(IChannel channel)
    {
        var handler = _serviceProvider.GetRequiredService<NettyDiscoveryV5Handler>();
        handler.InitializeChannel(channel);
        channel.Pipeline.AddLast(handler);
    }

    public Task StartAsync()
    {
        _ = DiscoverViaCustomRandomWalk();
        return Task.CompletedTask;
    }

    private async Task DiscoverViaCustomRandomWalk()
    {
        async Task DiscoverAsync(IEnumerable<IEnr> getAllNodes, byte[] nodeId, CancellationToken token)
        {
            static int[] GetDistances(byte[] srcNodeId, byte[] destNodeId)
            {
                const int WiderDistanceRange = 3;

                int[] distances = new int[WiderDistanceRange];
                distances[0] = TableUtility.Log2Distance(srcNodeId, destNodeId);

                for (int n = 1, i = 1; n < WiderDistanceRange; i++)
                {
                    if (distances[0] - i > 0)
                    {
                        distances[n++] = distances[0] - i;
                    }
                    if (distances[0] + i <= 256)
                    {
                        distances[n++] = distances[0] + i;
                    }
                }

                return distances;
            }

            Queue<IEnr> nodesToCheck = new(getAllNodes);
            HashSet<IEnr> checkedNodes = [];

            while (!token.IsCancellationRequested)
            {
                if (!nodesToCheck.TryDequeue(out IEnr? newEntry))
                {
                    return;
                }

                NodeAddedByDiscovery(newEntry);

                if (!checkedNodes.Add(newEntry))
                {
                    continue;
                }

                IEnumerable<IEnr>? newNodesFound = (await _discv5Protocol.SendFindNodeAsync(newEntry, GetDistances(newEntry.NodeId, nodeId)))?.Where(x => !checkedNodes.Contains(x));

                if (newNodesFound is not null)
                {
                    foreach (IEnr? node in newNodesFound)
                    {
                        nodesToCheck.Enqueue(node);
                    }
                }
            }
        }

        IEnumerable<IEnr> GetStartingNodes() => _discv5Protocol.GetAllNodes;

        Random random = new();
        await _discv5Protocol.InitAsync();

        if (_logger.IsDebug) _logger.Debug($"Initially discovered {_discv5Protocol.GetActiveNodes.Count()} active peers, {_discv5Protocol.GetAllNodes.Count()} in total.");

        const int RandomNodesToLookupCount = 3;

        byte[] randomNodeId = new byte[32];

        while (!_appShutdownSource.IsCancellationRequested)
        {
            try
            {
                await DiscoverAsync(GetStartingNodes(), _discv5Protocol.SelfEnr.NodeId, _appShutdownSource.Token);

                for (int i = 0; i < RandomNodesToLookupCount; i++)
                {
                    random.NextBytes(randomNodeId);
                    await DiscoverAsync(GetStartingNodes(), randomNodeId, _appShutdownSource.Token);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error($"Discovery via custom random walk failed.", ex);
            }

            if (_peerManager?.ActivePeers is { Count: > 0 })
            {
                await Task.Delay(_discoveryConfig.DiscoveryInterval, _appShutdownSource.Token);
            }
        }
    }

    public async Task StopAsync()
    {
        if (_peerManager is null)
        {
            return;
        }

        HashSet<EntrySecp256K1> activeNodes = _peerManager.ActivePeers.Select(x => new EntrySecp256K1(Context.Instance.CreatePubKey(x.Node.Id.PrefixedBytes).ToBytes(false)))
                                                                          .ToHashSet(new EntrySecp256K1EqualityComparer());

        IEnumerable<IEnr> activeNodeEnrs = _discv5Protocol.GetAllNodes.Where(x => activeNodes.Contains(x.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1)));

        IWriteBatch? batch = null;
        try
        {
            foreach (IEnr enr in activeNodeEnrs)
            {
                batch ??= _discoveryDb.StartWriteBatch();
                batch[enr.NodeId] = enr.EncodeRecord();
            }
        }
        finally
        {
            batch?.Dispose();
        }


        await _discv5Protocol.StopAsync();
        await _appShutdownSource.CancelAsync();
        _discoveryDb.Clear();
    }

    public void AddNodeToDiscovery(Node node)
    {
        var routingTable = _serviceProvider.GetRequiredService<IRoutingTable>();
        routingTable.UpdateFromEnr(GetEnr(node));
    }

    class EntrySecp256K1EqualityComparer : IEqualityComparer<EntrySecp256K1>
    {
        public bool Equals(EntrySecp256K1? x, EntrySecp256K1? y)
        {
            return !(x is null ^ y is null) && (x is null || x.Value.SequenceEqual(y!.Value));
        }

        public int GetHashCode([DisallowNull] EntrySecp256K1 entry)
        {
            var hash = new HashCode();
            hash.AddBytes(entry.Value);
            return hash.ToHashCode();
        }
    }
}
