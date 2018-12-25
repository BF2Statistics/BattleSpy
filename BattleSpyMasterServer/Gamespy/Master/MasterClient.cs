using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BattleSpy;
using BattleSpy.Gamespy;

namespace BattlelogMaster
{
    public class MasterClient : IDisposable
    {
        /// <summary>
        /// A unqie identifier for this connection
        /// </summary>
        public long ConnectionID;

        /// <summary>
        /// Indicates whether this object is disposed
        /// </summary>
        public bool Disposed { get; protected set; } = false;

        /// <summary>
        /// The clients socket network stream
        /// </summary>
        public GamespyTcpStream Stream { get; protected set; }        

        /// <summary>
        /// Event fired when the connection is closed
        /// </summary>
        public static event MstrConnectionClosed OnDisconnect;

        /// <summary>
        /// Contains a list of filterable properties
        /// </summary>
        protected static List<string> FilterableProperties { get; set; } = new List<string>();

        static MasterClient()
        {
            // get all the properties that aren't "[NonFilter]"
            PropertyInfo[] properties = typeof(GameServer).GetProperties();
            foreach (var property in properties)
            {
                if (property.GetCustomAttributes(false).Any(x => x.GetType().Name == "NonFilterAttribute"))
                    continue;

                FilterableProperties.Add(property.Name);
            }
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="client"></param>
        public MasterClient(GamespyTcpStream client, long connectionId)
        {
            // Generate a unique name for this connection
            ConnectionID = connectionId;

            // Init a new client stream class
            Stream = client;
            Stream.OnDisconnect += () => Dispose();
            Stream.DataReceived += (receivedData) =>
            {
                // lets split up the message based on the delimiter
                string[] messages = receivedData.Split(new string[] { "\x00\x00\x00\x00" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string message in messages)
                {
                    // Ignore Non-BF2 related queries
                    if (message.StartsWith("battlefield2"))
                        ParseRequest(message);
                }
            };
        }

        /// <summary>
        /// Destructor
        /// </summary>
        ~MasterClient()
        {
            if (!Disposed)
                Dispose(false);
        }

        public void Dispose()
        {
            if (!Disposed)
                Dispose(false);
        }

        /// <summary>
        /// Dispose method to be called by the server
        /// </summary>
        public void Dispose(bool DisposeEventArgs = false)
        {
            // Only dispose once
            if (Disposed) return;

            // Preapare to be unloaded from memory
            Disposed = true;

            // If connection is still alive, disconnect user
            if (!Stream.SocketClosed)
                Stream.Close(DisposeEventArgs);

            // Call disconnect event
            OnDisconnect?.Invoke(this);
        }

        /// <summary>
        /// Takes a message sent through the Stream and sends back a respose
        /// </summary>
        /// <param name="message"></param>
        protected void ParseRequest(string message)
        {
            string[] data = message.Split(new char[] { '\x00' }, StringSplitOptions.RemoveEmptyEntries);

            string gamename = data[1].ToLowerInvariant();
            string validate = data[2].Substring(0, 8);
            string filter = data[2].Substring(8);
            string[] fields = data[3].Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

            // Fix filter because the filter from BF2 can be messed up, missing spaces and such
            string fixedFilter = FixFilter(filter);
            if (String.IsNullOrEmpty(fixedFilter))
            {
                fixedFilter = "";
            }

            // Send the encrypted serverlist to the client
            byte[] unencryptedServerList = PackServerList(fixedFilter, fields);
            Stream.SendAsync(
                Enctypex.Encode(
                    Encoding.UTF8.GetBytes("hW6m9a"), // Battlfield 2 Handoff Key
                    Encoding.UTF8.GetBytes(validate),
                    unencryptedServerList,
                    unencryptedServerList.LongLength
                )
            );
        }

        /// <summary>
        /// Packs and prepares the response to a Server List request from the clients game.
        /// </summary>
        /// <param name="filter"></param>
        /// <param name="fields"></param>
        /// <returns></returns>
        private byte[] PackServerList(string filter, string[] fields)
        {
            IPEndPoint remoteEndPoint = ((IPEndPoint)Stream.RemoteEndPoint);

            byte fieldsCount = (byte)fields.Length;
            byte[] ipBytes = remoteEndPoint.Address.GetAddressBytes();
            byte[] value2 = BitConverter.GetBytes((ushort)6500);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(value2, 0, value2.Length);

            List<byte> data = new List<byte>();
            data.AddRange(ipBytes);
            data.AddRange(value2);
            data.Add(fieldsCount);
            data.Add(0);

            foreach (var field in fields)
            {
                data.AddRange(Encoding.UTF8.GetBytes(field));
                data.AddRange(new byte[] { 0, 0 });
            }

            // Execute query right here in memory
            IQueryable<GameServer> servers = MasterServer.Servers.Select(x => x.Value).Where(x => x.IsValidated).AsQueryable();
            if (!String.IsNullOrWhiteSpace(filter))
            {
                try
                {
                    // Apply Filter
                    servers = servers.Where(filter);
                }
                catch (Exception e)
                {
                    Program.ErrorLog.Write("ERROR: [MasterClient.PackServerList] " + e.Message);
                    Program.ErrorLog.Write(" - Filter Used: " + filter);
                }
            }

            // Add Servers
            foreach (GameServer server in servers)
            {
                // Get port bytes
                byte[] portBytes = BitConverter.GetBytes((ushort)server.QueryPort);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(portBytes, 0, portBytes.Length);

                data.Add(81); // it could be 85 as well, unsure of the difference, but 81 seems more common...
                data.AddRange(server.AddressInfo.Address.GetAddressBytes());
                data.AddRange(portBytes);
                data.Add(255);

                for (int i = 0; i < fields.Length; i++)
                {
                    data.AddRange(Encoding.UTF8.GetBytes(GetField(server, fields[i])));
                    if (i < fields.Length - 1)
                        data.AddRange(new byte[] { 0, 255 }); // Field Seperator
                }

                data.Add(0);
            }

            data.AddRange(new byte[] { 0, 255, 255, 255, 255 });
            return data.ToArray();
        }

        /// <summary>
        /// Fetches a property by fieldName from the provided Server Object
        /// </summary>
        /// <param name="server">The server we are fetching the field value from</param>
        /// <param name="fieldName">the field value we want</param>
        /// <returns></returns>
        private static string GetField(GameServer server, string fieldName)
        {
            object value = typeof(GameServer).GetProperty(fieldName).GetValue(server, null);
            if (value == null)
                return String.Empty;
            else if (value is Boolean)
                return (bool)value ? "1" : "0";
            else
                return value.ToString();
        }

        /// <summary>
        /// A simple method of getting the value of the passed parameter key,
        /// from the returned array of data from the client
        /// </summary>
        /// <param name="parts">The array of data from the client</param>
        /// <param name="parameter">The parameter</param>
        /// <returns>The value of the paramenter key</returns>
        private string GetParameterValue(string[] parts, string parameter)
        {
            bool next = false;
            foreach (string part in parts)
            {
                if (next)
                    return part;
                else if (part == parameter)
                    next = true;
            }
            return "";
        }


        /// <summary>
        /// Credits to gavrant @ Github
        /// </summary>
        /// <seealso cref="https://github.com/realitymod/PRMasterServer/pull/4/commits/ac6d7c86657e37313b5557675a8d6d9b30775e6f"/>
        /// <param name="filter"></param>
        /// <returns></returns>
        private static string FixFilter(string filter)
        {
            // escape [
            filter = filter.Replace("[", "[[]");

            StringBuilder filterBuilder = new StringBuilder();
            int len = filter.Length;
            var prevWordTrueType = FilterWordTypes.None;
            var curWordType = FilterWordTypes.None;
            int curWordStart = 0;
            int endOfString = -1;
            for (int i = 0; i < len; i++)
            {
                FilterWordTypes newWordType;
                if (i <= endOfString)
                {
                    newWordType = FilterWordTypes.String;
                }
                else
                {
                    char ch = filter[i];
                    if (ch == '\'' || ch == '"')
                    {
                        newWordType = FilterWordTypes.String;

                        // Search for the trailing quote
                        // This is a nightmare, since they forgot to escape filter strings in the BF2 client, so you can easily get something like that:
                        //	  hostname like 'flyin' high'
                        int quotes = filter.Substring(i + 1).Count(x => x == ch);
                        if (quotes == 0)
                            endOfString = len - 1; // No trailing quote
                        else if (quotes == 1)
                            endOfString = filter.IndexOf(ch, i + 1);
                        else // quotes > 1
                        {
                            endOfString = i;
                            bool doPercentCheck = (filter[i + 1] == '%');
                            for (int j = 1; j <= quotes; j++)
                            {
                                endOfString = filter.IndexOf(ch, endOfString + 1);
                                if (j == quotes) // Last quote?
                                    break;

                                if (doPercentCheck)
                                {
                                    if (endOfString <= (i + 2))
                                        continue;
                                    if (filter[endOfString - 1] != '%')
                                        continue;
                                }

                                string trailStr = filter.Substring(endOfString + 1).TrimStart();
                                bool isTerminated = (trailStr.StartsWith(")")
                                                        || trailStr.StartsWith("(")
                                                        || trailStr.StartsWith("and ", StringComparison.InvariantCultureIgnoreCase)
                                                        || trailStr.StartsWith("or ", StringComparison.InvariantCultureIgnoreCase));
                                if (isTerminated == false)
                                {
                                    foreach (var property in FilterableProperties)
                                    {
                                        if (trailStr.StartsWith(property))
                                        {
                                            isTerminated = true;
                                            break;
                                        }
                                    }
                                }

                                if (isTerminated)
                                    break;
                            }
                        }
                    }
                    else if (ch <= ' ')
                        newWordType = FilterWordTypes.None; // Skip whitespaces
                    else if (ch == '(')
                        newWordType = FilterWordTypes.OpenBracket;
                    else if (ch == ')')
                        newWordType = FilterWordTypes.CloseBracket;
                    else if (ch == '=' || ch == '!' || ch == '<' || ch == '>')
                        newWordType = FilterWordTypes.Comparison;
                    //else if (ch == '&' || ch == '|') // No idea how these C logical operators can get into a BF2 filter, but they were in the original...
                    //	newWordType = FilterWordTypes.Logical;
                    else
                        newWordType = FilterWordTypes.Other;
                }

                if (newWordType != curWordType || newWordType == FilterWordTypes.OpenBracket || newWordType == FilterWordTypes.CloseBracket)
                {
                    if (curWordType != FilterWordTypes.None)
                    {
                        prevWordTrueType = AddFilterWord(filterBuilder, filter, curWordStart, i, curWordType, prevWordTrueType, FilterableProperties);
                    }

                    curWordType = newWordType;
                    curWordStart = i;
                }
            }

            if (curWordType != FilterWordTypes.None && curWordStart < len)
            {
                AddFilterWord(filterBuilder, filter, curWordStart, len, curWordType, prevWordTrueType, FilterableProperties);
            }

            return filterBuilder.ToString();
        }

        private static FilterWordTypes AddFilterWord(
            StringBuilder filterBuilder, 
            string filter, 
            int wordStart, 
            int nextWordStart, 
            FilterWordTypes wordType, 
            FilterWordTypes prevWordType, 
            List<string> filterableProperties)
        {
            string word = filter.Substring(wordStart, nextWordStart - wordStart);

            if (wordType == FilterWordTypes.Other)
            {
                // Try to fix properties merged with other stuff
                foreach (var property in filterableProperties)
                {
                    int propIndex = word.IndexOf(property);
                    if (propIndex < 0)
                        continue;

                    if (propIndex > 0)
                        prevWordType = AddFilterWord(filterBuilder, word.Substring(0, propIndex), FilterWordTypes.Other, prevWordType);

                    prevWordType = AddFilterWord(filterBuilder, property, FilterWordTypes.Other, prevWordType);

                    int trailIndex = propIndex + property.Length;
                    if (trailIndex < word.Length)
                        prevWordType = AddFilterWord(filterBuilder, word.Substring(trailIndex), FilterWordTypes.Other, prevWordType);

                    return prevWordType;
                }
            }

            return AddFilterWord(filterBuilder, word, wordType, prevWordType);
        }

        private static FilterWordTypes AddFilterWord(StringBuilder filterBuilder, string word, FilterWordTypes wordType, FilterWordTypes prevWordType)
        {
            if (wordType == FilterWordTypes.Other)
            {
                if (word.Equals("and", StringComparison.InvariantCultureIgnoreCase))
                    wordType = FilterWordTypes.Logical;
                else if (word.Equals("or", StringComparison.InvariantCultureIgnoreCase))
                    wordType = FilterWordTypes.Logical;
                else if (word.Equals("like", StringComparison.InvariantCultureIgnoreCase))
                    wordType = FilterWordTypes.Comparison;
                else if (word.Equals("not", StringComparison.InvariantCultureIgnoreCase))
                    wordType = FilterWordTypes.Comparison;
            }

            // Not the first word or start/end of a group
            if (prevWordType != FilterWordTypes.None && prevWordType != FilterWordTypes.OpenBracket && wordType != FilterWordTypes.CloseBracket)
            {
                filterBuilder.Append(' ');

                // fix an issue in the BF2 main menu where filter expressions aren't joined properly
                // i.e. "numplayers > 0gametype like '%gpm_cq%'"
                // becomes "numplayers > 0 and gametype like '%gpm_cq%'"
                if (wordType == FilterWordTypes.Other)
                {
                    if (prevWordType != FilterWordTypes.Logical && prevWordType != FilterWordTypes.Comparison)
                        filterBuilder.Append("and ");
                }
                else if (wordType == FilterWordTypes.OpenBracket)
                {
                    if (prevWordType == FilterWordTypes.Other || prevWordType == FilterWordTypes.String)
                        filterBuilder.Append("and ");
                }
            }

            if (wordType == FilterWordTypes.String)
            {
                char quote = word[0];
                filterBuilder.Append(quote);
                if (word.Length > 2)
                {
                    string strContent = word.Substring(1, word.Length - 2);
                    filterBuilder.Append(strContent.Replace(quote, '_')); // replace quote characters inside the string with a wildcard character
                }
                filterBuilder.Append(quote);
            }
            else
                filterBuilder.Append(word);

            return wordType;
        }
    }
}
