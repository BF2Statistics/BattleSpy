using System;
using System.Collections.Generic;
using BattleSpy.Database;
using MySql.Data.MySqlClient;
using BattleSpy;
using System.Data.Common;

namespace Server.Database
{
    /// <summary>
    /// A class to provide common tasks against the Gamespy Login Database
    /// </summary>
    public sealed class GamespyDatabase : DatabaseDriver, IDisposable
    {
        /// <summary>
        /// Our Database connection parameters
        /// </summary>
        private static MySqlConnectionStringBuilder Builder;

        /// <summary>
        /// For locking while creating a new account
        /// </summary>
        private static Object _sync = new Object();

        /// <summary>
        /// Builds the conenction string statically, and just once
        /// </summary>
        static GamespyDatabase()
        {
            Builder = new MySqlConnectionStringBuilder
            {
                Server = Config.GetValue("Database", "Hostname"),
                Port = Config.GetType<uint>("Database", "Port"),
                UserID = Config.GetValue("Database", "Username"),
                Password = Config.GetValue("Database", "Password"),
                Database = Config.GetValue("Database", "LoginDatabase"),
                ConvertZeroDateTime = true
            };
        }

        /// <summary>
        /// Constructor
        /// </summary>
        public GamespyDatabase() : base(Builder.ConnectionString)
        {
            // Try and Reconnect
            base.Connect();
        }

        /// <summary>
        /// Destructor
        /// </summary>
        ~GamespyDatabase()
        {
            if (!base.IsDisposed)
                base.Dispose();
        }

        /// <summary>
        /// Fetches an account from the gamespy database
        /// </summary>
        /// <param name="Nick">The user's Nick</param>
        /// <returns></returns>
        public Dictionary<string, object> GetUser(string Nick)
        {
            // Fetch the user
            var Rows = base.Query("SELECT id, email, password, country, permban FROM player WHERE `name`=@P0", Nick);
            return (Rows.Count == 0) ? null : Rows[0];
        }

        /// <summary>
        /// Fetches an account from the gamespy database
        /// </summary>
        /// <param name="Email">Account email</param>
        /// <param name="Password">Account Password Un-Hashed</param>
        /// <returns></returns>
        public List<Dictionary<string, object>> GetUsersByEmailPass(string Email, string Password)
        {
            return base.Query("SELECT name FROM player WHERE LOWER(email)=@P0 AND password=@P1", Email.ToLowerInvariant(), Password.GetMD5Hash());
        }

        /// <summary>
        /// Returns wether an account exists from the provided Nick
        /// </summary>
        /// <param name="Nick"></param>
        /// <returns></returns>
        public bool UserExists(string Nick)
        {
            // Fetch the user
            return (base.Query("SELECT id FROM player WHERE `name`=@P0", Nick).Count != 0);
        }

        /// <summary>
        /// Returns wether an account exists from the provided Account Id
        /// </summary>
        /// <param name="PID"></param>
        /// <returns></returns>
        public bool UserExists(int PID)
        {
            // Fetch the user
            return (base.Query("SELECT `name` FROM player WHERE id=@P0", PID).Count != 0);
        }

        /// <summary>
        /// Creates a new Gamespy Account
        /// </summary>
        /// <remarks>Used by the login server when a create account request is made</remarks>
        /// <param name="Nick">The Account Name</param>
        /// <param name="Pass">The UN-HASHED Account Password</param>
        /// <param name="Email">The Account Email Address</param>
        /// <param name="Country">The Country Code for this Account</param>
        /// <returns>Returns the Player ID if sucessful, 0 otherwise</returns>
        public int CreateUser(string Nick, string Pass, string Email, string Country)
        {
            string Sql = "INSERT INTO player(`name`, `password`, `email`, `country`, `rank_id`) VALUES(@P0, @P1, @P2, @P3, 0)";
            using (MySqlCommand Command = (MySqlCommand)this.CreateCommand(Sql))
            {
                DbParameter Param = base.CreateParam();
                Param.ParameterName = "@P0";
                Param.Value = Nick;
                Command.Parameters.Add(Param);

                Param = base.CreateParam();
                Param.ParameterName = "@P1";
                Param.Value = Pass.GetMD5Hash(false);
                Command.Parameters.Add(Param);

                Param = base.CreateParam();
                Param.ParameterName = "@P2";
                Param.Value = Email.ToLowerInvariant();
                Command.Parameters.Add(Param);

                Param = base.CreateParam();
                Param.ParameterName = "@P3";
                Param.Value = Country;
                Command.Parameters.Add(Param);

                Command.ExecuteNonQuery();
                return (int)Command.LastInsertedId;
            }
        }

        /// <summary>
        /// Updates an Accounts Country Code
        /// </summary>
        /// <param name="Nick"></param>
        /// <param name="Country"></param>
        public void UpdateUser(int Id, string Country)
        {
            base.Execute("UPDATE player SET country=@P0 WHERE `id`=@P1", Country, Id);
        }

        /// <summary>
        /// Updates an Account's information by ID
        /// </summary>
        /// <param name="Id">The Current Account ID</param>
        /// <param name="NewPid">New Account ID</param>
        /// <param name="NewNick">New Account Name</param>
        /// <param name="NewPassword">New Account Password that is UN-Hashed</param>
        /// <param name="NewEmail">New Account Email Address</param>
        public void UpdateUser(int Id, int NewPid, string NewNick, string NewPassword, string NewEmail)
        {
            base.Execute("UPDATE player SET id=@P0, `name`=@P1, password=@P2, email=@P3 WHERE id=@P4", 
                NewPid, NewNick, NewPassword.GetMD5Hash(), NewEmail.ToLowerInvariant(), Id);
        }

        /// <summary>
        /// Deletes a Gamespy Account
        /// </summary>
        /// <param name="Nick"></param>
        /// <returns></returns>
        public int DeleteUser(string Nick)
        {
            return base.Execute("DELETE FROM player WHERE `name`=@P0", Nick);
        }

        /// <summary>
        /// Deletes a Gamespy Account
        /// </summary>
        /// <param name="Nick"></param>
        /// <returns></returns>
        public int DeleteUser(int Pid)
        {
            return base.Execute("DELETE FROM player WHERE id=@P0", Pid);
        }

        /// <summary>
        /// Fetches a Gamespy Account id from an account name
        /// </summary>
        /// <param name="Nick"></param>
        /// <returns></returns>
        public int GetPlayerId(string Nick)
        {
            var Rows = base.Query("SELECT id FROM player WHERE `name`=@P0", Nick);
            return (Rows.Count == 0) ? 0 : Int32.Parse(Rows[0]["id"].ToString());
        }

        /// <summary>
        /// Sets the Account (Player) Id for an account by Name
        /// </summary>
        /// <param name="Nick">The account Nick we are setting the new Pid for</param>
        /// <param name="Pid">The new Pid</param>
        /// <returns></returns>
        public int SetPID(string Nick, int Pid)
        {
            // If no user exists, return code -1
            if (!UserExists(Nick))
                return -1;

            // If the Pid already exists, return -2
            if (UserExists(Pid))
                return -2;

            // If PID is false, the PID is not taken
            return base.Execute("UPDATE player SET id=@P0 WHERE `name`=@P1", Pid, Nick);
        }

        /// <summary>
        /// Returns the number of accounts in the database
        /// </summary>
        /// <returns></returns>
        public int GetNumAccounts()
        {
            return base.ExecuteScalar<int>("SELECT COUNT(id) FROM player");
        }
    }
}
