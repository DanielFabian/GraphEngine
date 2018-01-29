// Graph Engine
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE.md file in the project root for full license information.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Trinity;
using Trinity.Network.Messaging;
using Trinity.Diagnostics;
using System.Runtime.CompilerServices;
using Trinity.Network;
using Trinity.Utilities;

namespace Trinity.Storage
{
    /// <summary>
    /// Provides methods for interacting with the distributed memory store.
    /// </summary>
    public unsafe partial class FixedMemoryCloud : MemoryCloud
    {
        private int server_count = -1;
        private int my_partition_id = -1;
        private int my_proxy_id = -1;
        private IStorage[] m_storageTable;
        internal ClusterConfig cluster_config;

        /// <inheritdoc/>
        protected internal override IList<IStorage> StorageTable => m_storageTable;

        /// <inheritdoc/>
        public override int MyInstanceId => my_partition_id;

        /// <inheritdoc/>
        public override int MyPartitionId => my_partition_id;

        /// <inheritdoc/>
        public override int MyProxyId => my_proxy_id;

        /// <inheritdoc/>
        public override int PartitionCount => cluster_config.Servers.Count;

        /// <inheritdoc/>
        public override int ProxyCount => cluster_config.Proxies.Count;

        /// <inheritdoc/>
        public override IEnumerable<Chunk> MyChunks
        {
            get
            {
                yield return Chunk.FullRangeChunk;
            }
        }

        #region private instance information
        private IList<AvailabilityGroup> _InstanceList(RunningMode mode)
        {
            switch (mode)
            {
                case RunningMode.Proxy:
                return TrinityConfig.Proxies;
                default:
                return TrinityConfig.Servers;
            }
        }
        #endregion

        /// <inheritdoc/>
        public override bool Open(ClusterConfig config, bool nonblocking)
        {
            this.cluster_config = config;

            Console.WriteLine(config.OutputCurrentConfig());

            if (_InstanceList(config.RunningMode).Count == 0)
            {
                Log.WriteLine(LogLevel.Warning, "No distributed instances configured. Turning on local test mode.");
                TrinityConfig.LocalTest = true;
            }
            server_count = cluster_config.Servers.Count;
            my_partition_id = cluster_config.MyPartitionId;
            my_proxy_id = cluster_config.MyProxyId;

            bool server_found = false;
            m_storageTable = new IStorage[server_count];

            if (server_count == 0)
                goto server_not_found;

            for (int i = 0; i < server_count; i++)
            {
                if (cluster_config.RunningMode == RunningMode.Server &&
                    (cluster_config.Servers[i].Has(Global.MyIPAddresses, Global.MyIPEndPoint.Port) || cluster_config.Servers[i].HasLoopBackEndpoint(Global.MyIPEndPoint.Port))
                    )
                {
                    StorageTable[i] = Global.LocalStorage;
                    server_found = true;
                }
                else
                {
                    StorageTable[i] = new RemoteStorage(cluster_config.Servers[i].Instances, TrinityConfig.ClientMaxConn, this, i, nonblocking);
                }
            }

            if (cluster_config.RunningMode == RunningMode.Server && !server_found)
            {
                goto server_not_found;
            }

            StaticGetPartitionByCellId = this.GetServerIdByCellIdDefault;

            if (!nonblocking) { CheckServerProtocolSignatures(); } // TODO should also check in nonblocking setup when any remote storage is connected
            // else this.ServerConnected += (_, __) => {};

            return true;

server_not_found:
            if (cluster_config.RunningMode == RunningMode.Server || cluster_config.RunningMode == RunningMode.Client)
                Log.WriteLine(LogLevel.Warning, "Incorrect server configuration. Message passing via CloudStorage not possible.");
            else if (cluster_config.RunningMode == RunningMode.Proxy)
                Log.WriteLine(LogLevel.Warning, "No servers are found. Message passing to servers via CloudStorage not possible.");
            return false;
        }

        private void CheckServerProtocolSignatures()
        {
            Log.WriteLine("Checking {0}-Server protocol signatures...", cluster_config.RunningMode);
            int my_server_id = (cluster_config.RunningMode == RunningMode.Server) ? MyPartitionId : -1;
            var storage = StorageTable.Where((_, idx) => idx != my_server_id).FirstOrDefault() as RemoteStorage;

            CheckProtocolSignatures_impl(storage, cluster_config.RunningMode, RunningMode.Server);
        }

        private void CheckProxySignatures(IEnumerable<RemoteStorage> proxy_list)
        {
            Log.WriteLine("Checking {0}-Proxy protocol signatures...", cluster_config.RunningMode);
            int my_proxy_id = (cluster_config.RunningMode == RunningMode.Proxy) ? MyProxyId : -1;
            var storage = proxy_list.Where((_, idx) => idx != my_proxy_id).FirstOrDefault();

            CheckProtocolSignatures_impl(storage, cluster_config.RunningMode, RunningMode.Proxy);
        }

        internal int GetServerIdByIPE(IPEndPoint ipe)
        {
            for (int i = 0; i < cluster_config.Servers.Count; i++)
            {
                if (cluster_config.Servers[i].Has(ipe))
                    return i;
            }
            return -1;
        }


        /// <summary>
        /// Gets the Id of the server on which the cell with the specified cell Id is located.
        /// </summary>
        /// <param name="cellId">A 64-bit cell Id.</param>
        /// <returns>The Id of the server containing the specified cell.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetServerIdByCellIdDefault(long cellId)
        {
            return (*(((byte*)&cellId) + 1)) % server_count;
        }

        /// <summary>
        /// Indicates whether the cell with the specified Id is a local cell.
        /// </summary>
        /// <param name="cellId">A 64-bit cell Id.</param>
        /// <returns>true if the cell is in local storage; otherwise, false.</returns>
        public override bool IsLocalCell(long cellId)
        {
            return (StaticGetPartitionByCellId(cellId) == my_partition_id);
        }
    }
}
