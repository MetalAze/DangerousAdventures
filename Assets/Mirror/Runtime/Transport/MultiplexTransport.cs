using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Mirror
{
    // a transport that can listen to multiple underlying transport at the same time
    public class MultiplexTransport : Transport
    {
        public Transport[] transports;

        private Transport available;

        // used to partition recipients for each one of the base transports
        // without allocating a new list every time
        private List<int>[] recipientsCache;

        public void Awake()
        {
            if (transports == null || transports.Length == 0)
                Debug.LogError("Multiplex transport requires at least 1 underlying transport");
            InitClient();
            InitServer();
        }

        public override bool Available()
        {
            // available if any of the transports is available
            foreach (var transport in transports)
                if (transport.Available())
                    return true;
            return false;
        }

        #region Client

        // clients always pick the first transport
        private void InitClient()
        {
            // wire all the base transports to my events
            foreach (var transport in transports)
            {
                transport.OnClientConnected.AddListener(OnClientConnected.Invoke);
                transport.OnClientDataReceived.AddListener(OnClientDataReceived.Invoke);
                transport.OnClientError.AddListener(OnClientError.Invoke);
                transport.OnClientDisconnected.AddListener(OnClientDisconnected.Invoke);
            }
        }

        public override void ClientConnect(string address)
        {
            foreach (var transport in transports)
                if (transport.Available())
                {
                    available = transport;
                    transport.ClientConnect(address);
                    return;
                }

            throw new Exception("No transport suitable for this platform");
        }

        public override void ClientConnect(Uri uri)
        {
            foreach (var transport in transports)
                if (transport.Available())
                    try
                    {
                        transport.ClientConnect(uri);
                        available = transport;
                        return;
                    }
                    catch (ArgumentException)
                    {
                        // transport does not support the schema, just move on to the next one
                    }

            throw new Exception("No transport suitable for this platform");
        }

        public override bool ClientConnected()
        {
            return (object) available != null && available.ClientConnected();
        }

        public override void ClientDisconnect()
        {
            if ((object) available != null)
                available.ClientDisconnect();
        }

        public override bool ClientSend(int channelId, ArraySegment<byte> segment)
        {
            return available.ClientSend(channelId, segment);
        }

        #endregion

        #region Server

        // connection ids get mapped to base transports
        // if we have 3 transports,  then
        // transport 0 will produce connection ids [0, 3, 6, 9, ...]
        // transport 1 will produce connection ids [1, 4, 7, 10, ...]
        // transport 2 will produce connection ids [2, 5, 8, 11, ...]
        private int FromBaseId(int transportId, int connectionId)
        {
            return connectionId * transports.Length + transportId;
        }

        private int ToBaseId(int connectionId)
        {
            return connectionId / transports.Length;
        }

        private int ToTransportId(int connectionId)
        {
            return connectionId % transports.Length;
        }

        private void InitServer()
        {
            recipientsCache = new List<int>[transports.Length];

            // wire all the base transports to my events
            for (var i = 0; i < transports.Length; i++)
            {
                recipientsCache[i] = new List<int>();

                // this is required for the handlers,  if I use i directly
                // then all the handlers will use the last i
                var locali = i;
                var transport = transports[i];

                transport.OnServerConnected.AddListener(baseConnectionId =>
                {
                    OnServerConnected.Invoke(FromBaseId(locali, baseConnectionId));
                });

                transport.OnServerDataReceived.AddListener((baseConnectionId, data, channel) =>
                {
                    OnServerDataReceived.Invoke(FromBaseId(locali, baseConnectionId), data, channel);
                });

                transport.OnServerError.AddListener((baseConnectionId, error) =>
                {
                    OnServerError.Invoke(FromBaseId(locali, baseConnectionId), error);
                });
                transport.OnServerDisconnected.AddListener(baseConnectionId =>
                {
                    OnServerDisconnected.Invoke(FromBaseId(locali, baseConnectionId));
                });
            }
        }

        // for now returns the first uri,
        // should we return all available uris?
        public override Uri ServerUri()
        {
            return transports[0].ServerUri();
        }


        public override bool ServerActive()
        {
            // avoid Linq.All allocations
            foreach (var transport in transports)
                if (!transport.ServerActive())
                    return false;
            return true;
        }

        public override string ServerGetClientAddress(int connectionId)
        {
            var baseConnectionId = ToBaseId(connectionId);
            var transportId = ToTransportId(connectionId);
            return transports[transportId].ServerGetClientAddress(baseConnectionId);
        }

        public override bool ServerDisconnect(int connectionId)
        {
            var baseConnectionId = ToBaseId(connectionId);
            var transportId = ToTransportId(connectionId);
            return transports[transportId].ServerDisconnect(baseConnectionId);
        }

        public override bool ServerSend(List<int> connectionIds, int channelId, ArraySegment<byte> segment)
        {
            // the message may be for different transports,
            // partition the recipients by transport
            foreach (var list in recipientsCache) list.Clear();

            foreach (var connectionId in connectionIds)
            {
                var baseConnectionId = ToBaseId(connectionId);
                var transportId = ToTransportId(connectionId);
                recipientsCache[transportId].Add(baseConnectionId);
            }

            var result = true;
            for (var i = 0; i < transports.Length; ++i)
            {
                var baseRecipients = recipientsCache[i];
                if (baseRecipients.Count > 0) result &= transports[i].ServerSend(baseRecipients, channelId, segment);
            }

            return result;
        }

        public override void ServerStart()
        {
            foreach (var transport in transports) transport.ServerStart();
        }

        public override void ServerStop()
        {
            foreach (var transport in transports) transport.ServerStop();
        }

        #endregion

        public override int GetMaxPacketSize(int channelId = 0)
        {
            // finding the max packet size in a multiplex environment has to be
            // done very carefully:
            // * servers run multiple transports at the same time
            // * different clients run different transports
            // * there should only ever be ONE true max packet size for everyone,
            //   otherwise a spawn message might be sent to all tcp sockets, but
            //   be too big for some udp sockets. that would be a debugging
            //   nightmare and allow for possible exploits and players on
            //   different platforms seeing a different game state.
            // => the safest solution is to use the smallest max size for all
            //    transports. that will never fail.
            var mininumAllowedSize = int.MaxValue;
            foreach (var transport in transports)
            {
                var size = transport.GetMaxPacketSize(channelId);
                mininumAllowedSize = Mathf.Min(size, mininumAllowedSize);
            }

            return mininumAllowedSize;
        }

        public override void Shutdown()
        {
            foreach (var transport in transports) transport.Shutdown();
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            foreach (var transport in transports) builder.AppendLine(transport.ToString());
            return builder.ToString().Trim();
        }
    }
}