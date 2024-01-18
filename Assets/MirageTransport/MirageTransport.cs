using System;
using System.Collections.Generic;
using System.Net;
using Mirage.SocketLayer;
using Mirage.Sockets.Udp;
using Mirror;
using UnityEngine;


namespace Mirage.TransportForMirror
{
    public class MirageTransport : Transport
    {
        public string Address = "localhost";
        public ushort Port = 7777;

        [Tooltip("Which socket implementation do you wish to use?\nThe default (automatic) will attempt to use NanoSockets on supported platforms and fallback to C# Sockets if unsupported.")]
        public SocketLib SocketLib = SocketLib.Automatic;

        [Header("NanoSocket-specific Options")]
        public int BufferSize = 256 * 1024;

        [Tooltip("Will not change level on already runner server (stop and start server/client to use new level")]
        [SerializeField] private LogType loglevel;

        /// <summary>
        /// Config to modify BEFORE starting the transport
        /// </summary>
        public Config Config { get; set; } = new Config()
        {
            // set default connections high, Mirror has its own limit anyway
            MaxConnections = 10_000
        };

        private Peer peer;
        private MessageHandler messageHandler;
        private IConnection clientConnection;
        private Dictionary<int, IConnection> serverConnections;
        private UdpSocketFactory socketFactory;

        public override void ServerEarlyUpdate()
        {
            if (ServerActive())
                peer.UpdateReceive();
        }
        public override void ServerLateUpdate()
        {
            if (ServerActive())
                peer.UpdateSent();
        }
        public override void ClientEarlyUpdate()
        {
            if (ClientConnected())
                peer.UpdateReceive();
        }
        public override void ClientLateUpdate()
        {
            if (ClientConnected())
                peer.UpdateSent();
        }

        public override bool Available()
        {
            return !IsWebgl;
        }
        private static bool IsWebgl => Application.platform == RuntimePlatform.WebGLPlayer;

        /// <summary>
        /// we add socket factory at runtime, and set the public properties on it 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        private void CheckSocketFactory()
        {
            if (socketFactory == null)
                socketFactory = gameObject.AddComponent<UdpSocketFactory>();

            socketFactory.Address = Address;
            socketFactory.Port = Port;
            socketFactory.SocketLib = SocketLib;
            socketFactory.BufferSize = BufferSize;
        }
        public override void ClientConnect(string address)
        {
            if (peer != null)
            {
                throw new InvalidOperationException("Peer Already exists");
            }

            CheckSocketFactory();
            var socket = socketFactory.CreateClientSocket();
            messageHandler = new MessageHandler((_, s, c) => OnClientDataReceived.Invoke(s, c));

            peer = new Peer(socket, socketFactory.MaxPacketSize, messageHandler, Config, new Logger(Debug.unityLogger) { filterLogType = loglevel });

            peer.OnConnected += (c) => OnClientConnected.Invoke();
            peer.OnDisconnected += (c, reason) => OnClientDisconnected.Invoke();
            peer.OnConnectionFailed += (c, reason) => OnClientDisconnected.Invoke();
            clientConnection = peer.Connect(socketFactory.GetConnectEndPoint(address));
        }

        public override bool ClientConnected()
        {
            return clientConnection != null;
        }

        public override void ClientDisconnect()
        {
            Shutdown();
        }
        public override void ClientSend(ArraySegment<byte> segment, int channelId)
        {
            SendTo(clientConnection, segment, channelId);
        }

        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId)
        {
            var connection = serverConnections[connectionId];
            SendTo(connection, segment, channelId);
        }

        private static void SendTo(IConnection connection, ArraySegment<byte> segment, int channelId)
        {
            switch (channelId)
            {
                case 0:
                    connection.SendReliable(segment);
                    break;
                case 1:
                    connection.SendUnreliable(segment);
                    break;
                default:
                    throw new NotSupportedException($"Channel {channelId} not supported");
            }
        }

        public override int GetMaxPacketSize(int channelId = 0)
        {
            CheckSocketFactory();
            switch (channelId)
            {
                case Mirror.Channels.Reliable:
                    // note: Reliable supports fragmenting message
                    //       but mirror's max size is for batching
                    //       so we want to return the size for 1 packet here to avoid fragmenting messages
                    return socketFactory.MaxPacketSize - AckSystem.MIN_RELIABLE_HEADER_SIZE;
                case Mirror.Channels.Unreliable:
                    return socketFactory.MaxPacketSize - 1;
                default:
                    throw new NotSupportedException($"Channel {channelId} not supported");
            }
        }

        public override bool ServerActive()
        {
            return serverConnections != null;
        }

        public override void ServerDisconnect(int connectionId)
        {
            serverConnections[connectionId].Disconnect();
        }

        public override string ServerGetClientAddress(int connectionId)
        {
            return serverConnections[connectionId].EndPoint.ToString();
        }

        public override void ServerStart()
        {
            if (peer != null)
            {
                throw new InvalidOperationException("Peer Already exists");
            }

            CheckSocketFactory();
            var socket = socketFactory.CreateServerSocket();
            messageHandler = new MessageHandler((conn, s, chan) => OnServerDataReceived.Invoke(conn, s, chan));

            peer = new Peer(socket, socketFactory.MaxPacketSize, messageHandler, Config, new Logger(Debug.unityLogger) { filterLogType = loglevel });

            serverConnections = new Dictionary<int, IConnection>();
            peer.OnConnected += (c) => { serverConnections.Add(c.GetHashCode(), c); OnServerConnected.Invoke(c.GetHashCode()); };
            peer.OnDisconnected += (c, reason) => { serverConnections.Remove(c.GetHashCode()); OnServerDisconnected.Invoke(c.GetHashCode()); };
            peer.Bind(socketFactory.GetBindEndPoint());
        }

        public override void ServerStop()
        {
            Shutdown();
        }

        public override Uri ServerUri()
        {
            var endPoint = socketFactory.GetBindEndPoint();


            var builder = new UriBuilder();
            builder.Host = Dns.GetHostName();

            if (endPoint is EndPointWrapper wrapper)
            {
                builder.Port = ((IPEndPoint)wrapper.inner).Port;
            }
            else if (endPoint is NanoEndPoint nano)
            {
                builder.Port = nano.address.port;
            }
            return builder.Uri;
        }

        public override void Shutdown()
        {
            if (peer == null)
            {
                return;
            }

            peer.Close();
            peer = null;

            Destroy(socketFactory);
            socketFactory = null;

            messageHandler = null;
            serverConnections = null;
            clientConnection = null;
        }


        // Notify support
        // user will need to call these directly, rather than using NetworkConnection send methods
        // the connection id can found inside NetworkConnection
        public void ClientSendNotify(ArraySegment<byte> segment, INotifyCallBack callBacks)
        {
            clientConnection.SendNotify(segment, callBacks);
        }
        public void ServerSendNotify(int connectionId, ArraySegment<byte> segment, INotifyCallBack callBacks)
        {
            var connection = serverConnections[connectionId];
            connection.SendNotify(segment, callBacks);
        }

        public INotifyToken ClientSendNotify(ArraySegment<byte> segment)
        {
            return clientConnection.SendNotify(segment);
        }
        public INotifyToken ServerSendNotify(int connectionId, ArraySegment<byte> segment)
        {
            var connection = serverConnections[connectionId];
            return connection.SendNotify(segment);
        }

        private class MessageHandler : IDataHandler
        {
            private readonly Action<int, ArraySegment<byte>, int> handler;

            public MessageHandler(Action<int, ArraySegment<byte>, int> handler)
            {
                this.handler = handler;
            }

            public void ReceiveMessage(IConnection connection, ArraySegment<byte> message)
            {
                // find way to get channel?
                handler.Invoke(connection.GetHashCode(), message, 0);
            }
        }
    }
}
