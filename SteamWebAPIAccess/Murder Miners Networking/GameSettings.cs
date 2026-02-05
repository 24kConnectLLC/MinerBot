using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamWebAPIAccess.Murder_Miners_Networking
{
    enum NetSessionBools
    {
        IsTeams,
        IsZombies,
        IsEditMode,
        IsPrivate,
        IsInfection,
        //IsCTF,
        //IsMurderBall,
        //IsJuggernaut
    }

    public enum NetSessionDataType
    {
        Bool,
        ScoreToWin, // must be first of single attribute values
        RadarMode,
        MaxPlayers,
        //EnabledWeapons,
        NUM_DATA_VALUES
    }

    class GameSettings
    {
        int[] dataValues;

        //int NetBools;
        //int lastNetBools;

        //NetworkSession networkSession;
        //NetworkSessionProperties properties;

        //public NetSettings NetSettings { get { return MachineLocal.Current.SettingSharer.localSettings; } }

        //private GameSettings() { }
        //private GameSettings(NetworkSession networkSession)
        //{

        //}

        public static bool GetBool(ulong source, NetSessionBools index) { return GetBool(source, (int)index); }
        public static bool GetBool(ulong source, int index)
        {
            return (source & ((ulong)1L << index)) != 0;
        }

        public static bool GetBool(long source, NetSessionBools index) { return GetBool(source, (int)index); }
        public static bool GetBool(long source, int index)
        {
            return (source & (1L << index)) != 0;
        }

        public static bool GetBool(uint source, int index)
        {
            return (source & (1u << index)) != 0;
        }

        public static long SetBool(long source, NetSessionBools index, bool value) { return SetBool(source, (int)index, value); }
        public static long SetBool(long source, int index, bool value)
        {
            if (value)
                source |= 1L << index;
            else
                source &= ~(1L << index);

            return source;
        }

        public static ulong SetBool(ulong source, int index, bool value)
        {
            if (value)
                source |= ((ulong)1L << index);
            else
                source = source & ~((ulong)1L << index);

            return source;
        }

        public static bool GetBool(int source, NetSessionBools index) { return GetBool(source, (int)index); }
        public static bool GetBool(int source, int index)
        {
            return (source & (1 << index)) != 0;
        }

        public static int SetBool(int source, NetSessionBools index, bool value) { return SetBool(source, (int)index, value); }
        public static int SetBool(int source, int index, bool value)
        {
            if (value)
                source |= 1 << index;
            else
                source &= ~(1 << index);

            return source;
        }

        public static uint SetBool(uint source, int index, bool value)
        {
            if (value)
                source |= ((uint)1 << index);
            else
                source = source & ~((uint)1 << index);

            return source;
        }

    }
}
