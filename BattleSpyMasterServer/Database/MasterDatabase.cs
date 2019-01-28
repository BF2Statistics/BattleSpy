using BattleSpy;
using BattleSpy.Database;
using MySql.Data.MySqlClient;

namespace BattlelogMaster
{
    /// <summary>
    /// A class to provide common tasks against the Gamespy Master Database
    /// </summary>
    public sealed class MasterDatabase : DatabaseDriver
    {
        /// <summary>
        /// Our Database connection parameters
        /// </summary>
        private static MySqlConnectionStringBuilder Builder;

        /// <summary>
        /// Builds the conenction string statically, and just once
        /// </summary>
        static MasterDatabase()
        {
            Builder = new MySqlConnectionStringBuilder
            {
                Server = Config.GetValue("Database", "Hostname"),
                Port = Config.GetType<uint>("Database", "Port"),
                UserID = Config.GetValue("Database", "Username"),
                Password = Config.GetValue("Database", "Password"),
                Database = Config.GetValue("Database", "MasterDatabase"),
                ConvertZeroDateTime = true
            };
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public MasterDatabase() : base(Builder.ConnectionString)
        {
            // Try and Reconnect
            base.Connect();
        }

        /// <summary>
        /// Destructor
        /// </summary>
        ~MasterDatabase()
        {
            if (!base.IsDisposed)
                base.Dispose();
        }

        /// <summary>
        /// Sets a server's online status in the database
        /// </summary>
        /// <param name="server"></param>
        public void UpdateServerOnline(GameServer server)
        {
            // Fetch server ID if we have not already
            if (!server.DatabaseIdAttempted)
            {
                string query = "SELECT COALESCE(id, 0), COUNT(id) FROM server WHERE ip=@P0 AND queryport=@P1";
                server.DatabaseId = base.ExecuteScalar<int>(query, server.AddressInfo.Address, server.QueryPort);
                server.DatabaseIdAttempted = true;
            }

            // Update server status in database only if it already exists!
            if (server.DatabaseId > 0)
            {
                // Update
                base.Execute(
                    "UPDATE server SET online=1, gameport=@P0, `name`=@P1, lastseen=@P2 WHERE id=@P3",
                    server.hostport,
                    server.hostname.Truncate(100),
                    server.LastRefreshed.ToUnixTimestamp(),
                    server.DatabaseId
                );
            }
        }

        /// <summary>
        /// Sets a server's online status in the database
        /// </summary>
        /// <param name="server"></param>
        public void UpdateServerOffline(GameServer server)
        {
            // Check if server exists in database
            if (server.DatabaseId > 0)
            {
                // Update
                string query = "UPDATE server SET online=0, lastseen=@P0 WHERE id=@P1";
                base.Execute(query, server.LastRefreshed.ToUnixTimestamp(), server.DatabaseId);
            }
        }
    }
}
