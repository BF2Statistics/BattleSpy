using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using BattleSpy;
using BattleSpy.Gamespy;
using BattleSpy.Logging;

namespace BattlelogMaster
{
    /// <summary>
    /// Master.Gamespy.com Server.
    /// Alot of code was borrowed and re-written from the Open Source PRMasterServer located
    /// here: https://github.com/AncientMan2002/PRMasterServer
    /// </summary>
    public class MasterServer : GamespyUdpSocket
    {
        /// <summary>
        /// Max number of concurrent open and active connections.
        /// </summary>
        /// <remarks>
        ///   While fast, the BF2Available requests will shoot out 6-8 times
        ///   per client while starting up BF2, so i set this alittle higher then usual.
        ///   Servers also post their data here, and alot of servers will keep the
        ///   connections rather high.
        /// </remarks>
        public const int MaxConnections = 256;

        /// <summary>
        /// The Server List Retrieve Tcp Socket
        /// </summary>
        protected ServerListRetrieveSocket MasterTcpServer;

        /// <summary>
        /// BF2Available response
        /// </summary>
        private static readonly byte[] BF2AvailableReply = { 0xfe, 0xfd, 0x09, 0x00, 0x00, 0x00, 0x00 };
        
        /// <summary>
        /// BF2Available Message. 09 then 4 00's then battlefield2
        /// </summary>
        private static readonly byte[] BF2AvailableRequest = { 
                0x09, 0x00, 0x00, 0x00, 0x00, 0x62, 0x61, 0x74, 
                0x74, 0x6c, 0x65, 0x66, 0x69, 0x65, 0x6c, 0x64, 0x32, 0x00 
            };

        /// <summary>
        /// Our hardcoded Server Validation code
        /// </summary>
        private static readonly byte[] ServerValidateCode = { 
                0x72, 0x62, 0x75, 0x67, 0x4a, 0x34, 0x34, 0x64, 0x34, 0x7a, 0x2b, 
                0x66, 0x61, 0x78, 0x30, 0x2f, 0x74, 0x74, 0x56, 0x56, 0x46, 0x64, 
                0x47, 0x62, 0x4d, 0x7a, 0x38, 0x41, 0x00 
            };

        /// <summary>
        /// A List of all servers that have sent data to this master server, and are active in the last 30 seconds or so
        /// </summary>
        public static ConcurrentDictionary<string, GameServer> Servers = new ConcurrentDictionary<string, GameServer>();

        /// <summary>
        /// A timer that is used to Poll all the servers, and remove inactive servers from the server list
        /// </summary>
        private static Timer PollTimer;

        /// <summary>
        /// The Time for servers are to remain in the serverlist since the last ping.
        /// Once this value is surpassed, server is presumed offline and is removed
        /// </summary>
        public static int ServerTTL { get; protected set; }

        /// <summary>
        /// Debugging for Development
        /// </summary>
        public static bool Debugging;

        /// <summary>
        /// Our debug log
        /// </summary>
        private LogWriter DebugLog;

        public MasterServer(IPEndPoint bindTo, LogWriter DebugLog) : base(bindTo, MaxConnections)
        {
            // Debugging
            Debugging = Config.GetType<bool>("Debug", "DebugServerlist");
            this.DebugLog = DebugLog;

            // Start TCP listener
            // ==================== Start Master TCP Socket
            Port = Config.GetType<int>("Settings", "MasterServerTcpPort");

            // Close last message
            Console.Write("Success!" + Environment.NewLine);
            Console.Write("<MSTR> Binding to TCP port {0}... ", Port);
            MasterTcpServer = new ServerListRetrieveSocket(new IPEndPoint(bindTo.Address, Port));

            // Setup timer. Remove servers who havent ping'd since ServerTTL
            PollTimer = new Timer(5000);
            PollTimer.Elapsed += (s, e) => CheckServers();
            PollTimer.Start();

            // Server life
            ServerTTL = Config.GetType<int>("Settings", "ServerListTTL");

            // Attempt to bind to port
            base.StartAcceptAsync();
        }

        /// <summary>
        /// Shutsdown the Master server and socket
        /// </summary>
        public void Shutdown()
        {
            // Discard the poll timer
            PollTimer.Stop();
            PollTimer.Dispose();

            // Shutdown parent
            base.ShutdownSocket();
            MasterTcpServer.Shutdown();

            // Clear servers
            Servers.Clear();
            Servers = null;

            // Dispose parent objects
            base.Dispose();
        }

        /// <summary>
        /// Callback method for when the UDP Master socket recieves a connection
        /// </summary>
        protected override void ProcessAccept(GamespyUdpPacket Packet)
        {
            IPEndPoint remote = (IPEndPoint)Packet.AsyncEventArgs.RemoteEndPoint;

            // Need at least 5 bytes
            if (Packet.BytesRecieved.Length < 5)
            {
                base.Release(Packet.AsyncEventArgs);
                return;
            }

            // Handle request in a new thread
            Task.Run(() =>
            {
                // If we dont reply, we must manually release the pool
                bool replied = false;

                try
                {
                    // Both the client and server will send an Gamespy Available Heartbeat Check
                    if (Packet.BytesRecieved.SequenceEqual(BF2AvailableRequest))
                    {
                        if (Debugging) DebugLog.Write("BF2Available Called From {0}:{1}", remote.Address, remote.Port);
                        Packet.SetBufferContents(BF2AvailableReply);
                        base.ReplyAsync(Packet);
                        replied = true;
                    }
                    else if (Packet.BytesRecieved[0] == 0x03)
                    {
                        // this is where server details come in, it starts with 0x03, it happens every 60 seconds or so
                        byte[] uniqueId = new byte[4];
                        Array.Copy(Packet.BytesRecieved, 1, uniqueId, 0, 4);

                        // If we arent validated (initial connection), send a challenge key
                        if (!ParseServerDetails(remote, Packet.BytesRecieved.Skip(5).ToArray()))
                        {
                            // this should be some sort of proper encrypted challenge, but for now i'm just going to hard code 
                            // it because I don't know how the encryption works...
                            Packet.SetBufferContents(new byte[] 
                            { 
                                0xfe, 0xfd, 0x01, uniqueId[0], uniqueId[1], uniqueId[2], uniqueId[3], 0x44, 0x3d, 0x73, 
                                0x7e, 0x6a, 0x59, 0x30, 0x30, 0x37, 0x43, 0x39, 0x35, 0x41, 0x42, 0x42, 0x35, 0x37, 0x34, 
                                0x43, 0x43, 0x00 
                            });
                            base.ReplyAsync(Packet);
                            replied = true;
                        }
                    }
                    else if (Packet.BytesRecieved[0] == 0x01)
                    {
                        // this is a challenge response, it starts with 0x01
                        byte[] uniqueId = new byte[4];
                        Array.Copy(Packet.BytesRecieved, 1, uniqueId, 0, 4);

                        // confirm against the hardcoded challenge
                        byte[] clientResponse = new byte[ServerValidateCode.Length];
                        Array.Copy(Packet.BytesRecieved, 5, clientResponse, 0, clientResponse.Length);

                        // if we validate, reply back a good response
                        if (clientResponse.SequenceEqual(ServerValidateCode))
                        {
                            if (Debugging) DebugLog.Write("Server Challenge... Validated: {0}:{1}", remote.Address, remote.Port);

                            // Send back a good response if we validate successfully
                            Packet.SetBufferContents(new byte[] { 0xfe, 0xfd, 0x0a, uniqueId[0], uniqueId[1], uniqueId[2], uniqueId[3] });
                            base.ReplyAsync(Packet);
                            replied = true;
                            ValidateServer(remote);
                        }
                        else if (Debugging)
                            DebugLog.Write("Server Challenge... FAILED: {0}:{1}", remote.Address, remote.Port);
                    }
                    else if (Packet.BytesRecieved[0] == 0x08)
                    {
                        // this is a server ping, it starts with 0x08, it happens every 20 seconds or so
                        string key = String.Format("{0}:{1}", remote.Address, remote.Port);
                        GameServer server;
                        if (Servers.TryGetValue(key, out server) && server.IsValidated)
                        {
                            if (Debugging) DebugLog.Write("Server Heartbeat Received: " + key);

                            // Update Ping
                            server.LastPing = DateTime.Now;
                            Servers.AddOrUpdate(key, server, (k, old) => { return server; });
                        }
                        else if (Debugging) DebugLog.Write("Server Heartbeat Received from Unvalidated Server: " + key);
                    }
                }
                catch (Exception E)
                {
                    Program.ErrorLog.Write("ERROR: [UdpSock_DataReceived] " + E.Message);
                }
                finally
                {
                    // Release so that we can pool the EventArgs to be used on another connection
                    if (!replied)
                        base.Release(Packet.AsyncEventArgs);
                }
            });
        }

        #region Support Methods

        /// <summary>
        /// When a server connects, it needs to be validated. Once that happens, this
        /// method is called, and it allows the server to bee seen in the Serverlist
        /// </summary>
        /// <param name="remote">The remote IP of the server</param>
        private void ValidateServer(IPEndPoint remote)
        {
            string key = String.Format("{0}:{1}", remote.Address, remote.Port);
            GameServer server;

            // try to fetch the existing server, if its not here... we have bigger problems
            if (!Servers.TryGetValue(key, out server))
            {
                Program.ErrorLog.Write("NOTICE: [MasterServer.ValidateServer] We encountered a strange error trying to fetch a connected server.");
                return;
            }

            // Server is valid
            server.IsValidated = true;
            server.LastRefreshed = DateTime.Now;
            server.LastPing = DateTime.Now;

            // Update or add the new server
            if (Debugging) DebugLog.Write("Adding Validated Server to Serverlist: " + key);
            Servers.AddOrUpdate(key, server, (k, old) => { return server; });

            // Update the Dababase
            try
            {
                using (MasterDatabase Driver = new MasterDatabase())
                {
                    Driver.UpdateServerOnline(server);
                }
            }
            catch(Exception e)
            {
                Program.ErrorLog.Write("ERROR: [MasterDatabase.UpdateServerOnline] " + e.Message);
            }
        }

        /// <summary>
        /// Executed every 60 seconds... Every 3rd ping, the BF2 server sends a full list
        /// of data that describes its current state, and this method is used to parse that
        /// data, and update the server in the Servers list
        /// </summary>
        /// <param name="remote">The servers remote address</param>
        /// <param name="data">The data we must parse, sent by the server</param>
        /// <returns>Returns whether or not the server needs to be validated, so it can be seen in the Server Browser</returns>
        private bool ParseServerDetails(IPEndPoint remote, byte[] data)
        {
            // Format key
            string key = String.Format("{0}:{1}", remote.Address, remote.Port);

            // split by 000 (info/player separator) and 002 (players/teams separator)
            // the players/teams separator is really 00, but because 00 may also be used elsewhere (an empty value for example), we hardcode it to 002
            // the 2 is the size of the teams, for BF2 this is always 2.
            string receivedData = Encoding.UTF8.GetString(data);
            string[] sections = receivedData.Split(new string[] { "\x00\x00\x00", "\x00\x00\x02" }, StringSplitOptions.None);
            if (sections.Length != 3 && !receivedData.EndsWith("\x00\x00"))
            {
                if (Debugging) DebugLog.Write("Invalid Server Data Received From {0} :: {1}", key, sections[0]);
                return true; // true means we don't send back a response
            }

            // We only care about the server section
            string serverVars = sections[0];
            string[] serverVarsSplit = serverVars.Split(new string[] { "\x00" }, StringSplitOptions.None);
            if (Debugging)
            {
                DebugLog.Write("Server Data Received From {0}", key);
                for (int i = 0; i < sections.Length; i++)
                    DebugLog.Write("    DataString {0}: {1}", i, sections[i]);
            }

            // Start a new Server Object
            GameServer server = new GameServer(remote);
            List<Dictionary<string, object>> Rows = null;

            // set the country based off ip address if its IPv4
            server.country = (remote.Address.AddressFamily == AddressFamily.InterNetwork) 
                ? GeoIP.GetCountryCode(remote.Address).ToUpperInvariant() 
                : "??";

            // Try and Fetch server and provider from the database
            try
            {
                using (MasterDatabase Driver = new MasterDatabase())
                {
                    string q = "SELECT s.*, p.authorized, p.plasma FROM server AS s JOIN stats_provider AS p ON s.provider_id = p.id WHERE ip=@P0 AND queryport=@P1";
                    Rows = Driver.Query(q, remote.Address, remote.Port);
                }
            }
            catch (Exception e)
            {
                Program.ErrorLog.Write("WARNING: [MasterServer.ParseServerDetails] " + e.Message);
            }

            // Set server vars
            for (int i = 0; i < serverVarsSplit.Length - 1; i += 2)
            {
                // Fetch the property
                PropertyInfo property = typeof(GameServer).GetProperty(serverVarsSplit[i]);
                if (property == null)
                {
                    continue;
                }
                else if (property.Name == "hostname")
                {
                    // strip consecutive whitespace from hostname
                    property.SetValue(server, Regex.Replace(serverVarsSplit[i + 1], @"\s+", " ").Trim(), null);
                }
                else if (property.Name == "bf2_plasma")
                {
                    // Does the server exist in the database?
                    if (Rows != null && Rows.Count > 0)
                    {
                        // For some damn reason, the driver is returning a bool instead of an integer?
                        if (Rows[0]["plasma"] is bool)
                            property.SetValue(server, Rows[0]["plasma"], null);
                        else
                            property.SetValue(server, Int32.Parse(Rows[0]["plasma"].ToString()) > 0, null);
                    }
                    else
                    {
                        property.SetValue(server, false, null);
                    }
                }
                else if (property.Name == "bf2_ranked")
                {
                    // Does the server exist in the database?
                    if (Rows != null && Rows.Count > 0)
                    {
                        // For some damn reason, the driver is returning a bool instead of an integer?
                        if (Rows[0]["authorized"] is bool)
                            property.SetValue(server, Rows[0]["authorized"], null);
                        else
                            property.SetValue(server, Int32.Parse(Rows[0]["authorized"].ToString()) > 0, null);
                    }
                    else
                    {
                        property.SetValue(server, false, null);
                    }
                }
                else if (property.Name == "bf2_pure")
                {
                    // we're always a pure server
                    property.SetValue(server, true, null);
                }
                else if (property.PropertyType == typeof(Boolean))
                {
                    // parse string to bool (values come in as 1 or 0)
                    int value;
                    if (Int32.TryParse(serverVarsSplit[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        property.SetValue(server, value != 0, null);
                    }
                }
                else if (property.PropertyType == typeof(Int32))
                {
                    // parse string to int
                    int value;
                    if (Int32.TryParse(serverVarsSplit[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        property.SetValue(server, value, null);
                    }
                }
                else if (property.PropertyType == typeof(Double))
                {
                    // parse string to double
                    double value;
                    if (Double.TryParse(serverVarsSplit[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        property.SetValue(server, value, null);
                    }
                }
                else if (property.PropertyType == typeof(String))
                {
                    // parse string to string
                    property.SetValue(server, serverVarsSplit[i + 1], null);
                }
            }

            // you've got to have all these properties in order for your server to be valid
            if (!String.IsNullOrWhiteSpace(server.hostname) &&
                !String.IsNullOrWhiteSpace(server.gamevariant) &&
                !String.IsNullOrWhiteSpace(server.gamever) &&
                !String.IsNullOrWhiteSpace(server.gametype) &&
                !String.IsNullOrWhiteSpace(server.mapname) &&
                !String.IsNullOrWhiteSpace(server.gamename) &&
                server.gamename.Equals("battlefield2", StringComparison.InvariantCultureIgnoreCase) &&
                server.hostport > 1024 && server.hostport <= UInt16.MaxValue &&
                server.maxplayers > 0)
            {
                // Determine if we need to send a challenge key to the server for validation
                bool IsValidated = Servers.ContainsKey(key) && Servers[key].IsValidated;
                if (Debugging) DebugLog.Write("Server Data Parsed Successfully... Needs Validated: " + ((IsValidated) ? "false" : "true"));

                // Add / Update Server
                server.IsValidated = IsValidated;
                server.LastPing = DateTime.Now;
                server.LastRefreshed = DateTime.Now;
                Servers.AddOrUpdate(key, server, (k, old) => { return server; });

                // Tell the requester if we are good to go
                return IsValidated;
            }

            // If we are here, the server information is invalid. Return true to ignore server
            return true;
        }

        /// <summary>
        /// Executed every 5 seconds or so... Removes all servers that haven't
        /// reported in awhile
        /// </summary>
        protected void CheckServers()
        {
            // Create a list of servers to update in the database
            List<GameServer> ServersToRemove = new List<GameServer>();
            var span = TimeSpan.FromSeconds(ServerTTL);

            // Remove servers that havent talked to us in awhile from the server list
            foreach (string key in Servers.Keys)
            {
                GameServer value;
                if (Servers.TryGetValue(key, out value))
                {
                    if (value.LastPing < DateTime.Now - span)
                    {
                        if (Debugging) DebugLog.Write("Removing Server for Expired Ping: " + key);
                        if (Servers.TryRemove(key, out value))
                            ServersToRemove.Add(value);
                        else
                            Program.ErrorLog.Write("ERROR: [MasterServer.CheckServers] Unable to remove server from server list: " + key);
                    }
                }
            }

            // If we have no servers to update, return
            if (ServersToRemove.Count == 0) return;

            // Update servers in database
            try
            {
                // Wrap this all in a database transaction, as this will speed
                // things up alot if there are alot of rows to update
                using (MasterDatabase Driver = new MasterDatabase())
                using (DbTransaction Transaction = Driver.BeginTransaction())
                {
                    try
                    {
                        foreach (GameServer server in ServersToRemove)
                            Driver.UpdateServerOffline(server);

                        Transaction.Commit();
                    }
                    catch
                    {
                        Transaction.Rollback();
                        throw;
                    }
                }
            }
            catch(Exception e)
            {
                Program.ErrorLog.Write("ERROR: [MasterDatabase.UpdateServerOffline] Unable to update servers status: " + e.Message);
            }
        }

        protected override void OnException(Exception e) => ExceptionHandler.GenerateExceptionLog(e);

        #endregion Support Methods
    }
}
