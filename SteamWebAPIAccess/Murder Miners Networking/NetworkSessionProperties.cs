using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Text;
using SteamKit2;

namespace SteamWebAPIAccess.Murder_Miners_Networking
{
    public class NetworkSessionProperties : IList<uint?>, ICollection<uint?>, IEnumerable<uint?>, IEnumerable
    {
        private const int PropertyCount = 30;
        private uint?[] data = new uint?[PropertyCount];

        public uint? this[int index]
        {
            get
            {
                if (index < 0 || index >= PropertyCount)
                    throw new ArgumentOutOfRangeException("index");
                else
                    return this.data[index];
            }
            set
            {
                if (index < 0 || index >= PropertyCount)
                    throw new ArgumentOutOfRangeException("index");

                if (this.data[index] != value)
                {
                    this.data[index] = value;

                    if (this.PropertyChanging != null)
                        this.PropertyChanging(index, value);
                }
            }
        }

        public int Count { get { return PropertyCount; } }

        bool ICollection<uint?>.IsReadOnly { get { return false; } }

        internal static NetworkSessionProperties CreateReadOnly()
        {
            NetworkSessionProperties sessionProperties = new NetworkSessionProperties();
            sessionProperties.PropertyChanging += (NetworkSessionProperties.PropertyChangeHandler)delegate
            {
                throw new NotSupportedException("these properties are read-only");
            };
            return sessionProperties;
        }

        static string keyString = "prop_{0}";
        //internal static void WriteProperties(NetworkSessionProperties properties, SteamMatchmaking.Lobby lobby)
        //{
        //    //lobby.SetData("testkey", "");
        //    for (int i = 0; i < PropertyCount; i++)
        //    {
        //        if (properties[i].HasValue)
        //            lobby.Metadata.(string.Format("prop_{0}", i), properties[i].Value.ToString()); // Can just copy and paste this formatting when creating a lobby
        //    }
        //}

        //internal void ReadProperties(SteamID lobbyID) => ReadProperties(SteamMatchmaking.Lobby.Create(lobbyID));
        internal void ReadProperties(SteamMatchmaking.Lobby lobby) => ReadProperties(lobby.Metadata);

        internal void ReadProperties(IReadOnlyDictionary<string,string> lobbyData)
        {
            for (int i = 0; i < PropertyCount; i++)
            {
                if (lobbyData.TryGetValue(string.Format(keyString, i), out string valueString))
                {
                    if (valueString != null && uint.TryParse(valueString, out uint valueInt))
                        data[i] = valueInt;
                }
            }
        }

        /// <summary>
        ///  Creates a new Lobby and copies Properties from an existing Lobby
        /// </summary>
        /// <param name="lobbyID"></param>
        //public static NetworkSessionProperties CreateFromLobby(SteamID lobbyID)
        //{
        //    var props = new NetworkSessionProperties();
        //    props.ReadProperties(lobbyID);
        //    return props;
        //}

        internal delegate void PropertyChangeHandler(int propertyIndex, uint? newValue);
        internal event NetworkSessionProperties.PropertyChangeHandler PropertyChanging;

        public IEnumerator<uint?> GetEnumerator() { return ((IEnumerable<uint?>)data).GetEnumerator(); }

        IEnumerator IEnumerable.GetEnumerator() { return data.GetEnumerator(); }

        int IList<uint?>.IndexOf(uint? item) { return ((IList<uint?>)data).IndexOf(item); }

        bool ICollection<uint?>.Contains(uint? item) { return ((ICollection<uint?>)data).Contains(item); }

        void ICollection<uint?>.CopyTo(uint?[] array, int arrayIndex) { data.CopyTo((Array)array, arrayIndex); }

        void ICollection<uint?>.Add(uint? item) { throw new NotSupportedException(); }
        void IList<uint?>.Insert(int index, uint? item) { throw new NotSupportedException(); }
        bool ICollection<uint?>.Remove(uint? item) { throw new NotSupportedException(); }
        void IList<uint?>.RemoveAt(int index) { throw new NotSupportedException(); }
        void ICollection<uint?>.Clear() { throw new NotSupportedException(); }
    }
}
