using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using BattleSpy.Gamespy;

namespace BattlelogMaster
{
    /// <summary>
    /// This class emulates the master.gamespy.com TCP server on port 28910.
    /// This server is responisible for sending server lists to the online
    /// server browser in the BF2 multiplayer menu
    /// </summary>
    public class ServerListRetrieveSocket : GamespyTcpSocket
    {
        /// <summary>
        /// Max number of concurrent open and active connections.
        /// </summary>
        /// <remarks>Connections to the Master server are short lived</remarks>
        public const int MaxConnections = 100;

        /// <summary>
        /// A connection counter, used to create unique connection id's
        /// </summary>
        private long ConnectionCounter = 0;

        /// <summary>
        /// A List of sucessfully active connections (Name => Client Obj) on the MasterServer TCP line
        /// </summary>
        private static ConcurrentDictionary<long, MasterClient> Clients = new ConcurrentDictionary<long, MasterClient>();

        public ServerListRetrieveSocket(IPEndPoint bindTo) : base(bindTo, MaxConnections)
        {
            // Start accepting connections
            MasterClient.OnDisconnect += MasterClient_OnDisconnect;
            base.StartAcceptAsync();
        }

        /// <summary>
        /// Shutsdown the underlying sockets
        /// </summary>
        public void Shutdown()
        {
            // Stop accepting new connections
            base.IgnoreNewConnections = true;

            // Unregister events so we dont get a shit ton of calls
            MasterClient.OnDisconnect -= MasterClient_OnDisconnect;

            // Disconnected all connected clients
            foreach (MasterClient client in Clients.Values)
                client.Dispose(true);

            // Update Connected Clients in the Database
            Clients.Clear();

            // Shutdown the listener socket
            base.ShutdownSocket();

            // Tell the base to dispose all free objects
            base.Dispose();
        }

        /// <summary>
        /// Accepts a TcpClient, and begin the serverlist fetching process for the client. 
        /// This method is executed when the user updates his server browser ingame
        /// </summary>
        protected override void ProcessAccept(GamespyTcpStream Stream)
        {
            // Get our connection id
            long ConID = Interlocked.Increment(ref ConnectionCounter);
            MasterClient client;

            // End the operation and display the received data on  
            // the console.
            try
            {
                // Convert the TcpClient to a MasterClient
                client = new MasterClient(Stream, ConID);
                Clients.TryAdd(client.ConnectionID, client);

                // Start receiving data
                Stream.BeginReceive();
            }
            catch (ObjectDisposedException) // Ignore
            {
                // Remove client
                Clients.TryRemove(ConID, out client);
            }
            catch (IOException) // Connection closed before a TcpClientStream could be made
            {
                // Remove client
                Clients.TryRemove(ConID, out client);
            }
            catch (Exception e)
            {
                // Remove client
                Clients.TryRemove(ConID, out client);

                // Report error
                Program.ErrorLog.Write("NOTICE: An Error occured at [ServerListRetrieveSocket.ProcessAccept] : Generating Exception Log");
                ExceptionHandler.GenerateExceptionLog(e);
            }
        }

        /// <summary>
        /// Callback for when a connection had disconnected
        /// </summary>
        protected void MasterClient_OnDisconnect(MasterClient client)
        {
            // Remove client, and call OnUpdate Event
            try
            {
                // Release this stream's AsyncEventArgs to the object pool
                base.Release(client.Stream);

                // Remove client from online list
                if (Clients.TryRemove(client.ConnectionID, out client) && !client.Disposed)
                    client.Dispose();
            }
            catch (Exception e)
            {
                Program.ErrorLog.Write("An Error occured at [ServerListRetrieveSocket.MasterClient_OnDisconnect] : Generating Exception Log");
                ExceptionHandler.GenerateExceptionLog(e);
            }
        }

        protected override void OnException(Exception e) => ExceptionHandler.GenerateExceptionLog(e);
    }
}
