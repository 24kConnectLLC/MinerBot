using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static QRCoder.PayloadGenerator;

namespace SteamWebAPIAccess.Murder_Miners_Networking
{
    enum NetSettingDataType { Bool, Byte, Short, UInt }
    //enum JoiningType { Open, Friends, Invite, Closed, NUM_TYPES }
    enum LobbyType { Undefined, CustomGame, CustomSearch, MatchmadeGame, MatchmakingSearch }

    // Should be filtered by an attribute, but easy new enum instead
    // Latest Build, U41.. usually public build or beta build. Beta build takes priority.
    enum NetSettingType
    {
        BEGIN_BOOLS,
        boolIsMaxZombies,
        boolShowInvisibleBlocks,
        boolIsTeams,
        boolIsTeamSwitchingLocked,
        boolIsCTF,
        boolIsGamenight,
        boolUseTeamSpawns,
        boolTeamKilling,
        boolHordeTeamKilling,
        boolIsZombies,
        boolIsInfection, // 9
        boolIsZombienaut,
        boolDeathWallEnabled,
        boolIsEditMode,
        boolAllowDestruction,
        boolSwapSniperWithOPSniper,
        boolSwapShotgunWithDB,
        boolEnableTriggerVolumes,
        boolWeaponOnMap,
        boolVehiclesOnMap,
        boolFlagAtHomeToScore,
        boolIsAlphaFlagHome,
        boolIsOmegaFlagHome,// 17
        boolFlagTouchToReturn,
        boolIsHostSpectator,
        boolStickyGrenades,
        boolIsMurderGames,
        boolIsSlowZombies,
        boolIsTentaclesEnabled,
        boolSpawnAsSpectator,
        //boolSprintingEnabled,
        boolSpawnLasers,
        //boolInfiniteAmmo,
        //boolBottomlessClip,
        //boolNoAmmo,
        boolAssassinationOnly, //Only Backstabs Kill
        boolHeadshotOnly, //Only Headshots Kill
        boolNoHealthRegen, //disables health regen
        boolSniperLasers,
        boolIsWaitingForPlayers,
        boolHasScoreToWin,
        boolHasTimeLimit, // 32
        boolIsSpeedRunTimer,
        boolInstantRespawn,
        boolIsFreeBuild,
        boolInfiniteGrenade,
        boolIsThirdPerson,
        boolZombieRadar,
        boolZombieVomit,
        boolZombieWind,
        boolZombieRegurgitation,
        boolZombieTentacleDelay,
        //boolRam,
        //boolForceRam,
        boolAirDash,
        boolCanWallJump,
        boolZombieCanRide, // convert to byte
        boolSkipWeaponSelect,
        boolShouldNoClip,
        boolUseRandomSong,
        boolIsMurderMeleeTournament,
        boolWeaponPickUpRadar,
        boolIsEggHunt,
        boolIsBigHeadMode,
        boolIsOneInTheChamber,
        boolIsCapturePoint,
        boolIsMurderMining,
        boolFallDamage,
        //boolGiveRandomWeapon,
        boolIsMurderBall,
        boolSpawnClones,
        //boolDiagonalSprinting,
        boolSlide,
        boolWeaponTimers,
        END_BOOLS, // cannot go more than a value of 63, already changed to ulong
        BEGIN_BYTES,
        byteMaxPlayers,
        //byteScoreToWin,
        byteRadarMode,
        byteWeaponWheelType,
        byteFireSpeed,
        byteAmmoFillClipSettings,
        byteAmmoFillReserveSettings,
        byteSwitchWeaponSpawnSetType,
        //byteJoining,
        byteNumInfected,
        byteRankedCountdown,
        byteRespawnTime,
        byteSelfMurderCountDownTime,
        byteMovementSpeed,
        byteGravity, //Gravity Multiplier
        byteMaxHealth, //Max Health Modifier
        byteMaxShields, //Max Shield Modifier
        //bytePlayerSize, //player size modifier
        byteKnockback,
        byteDashXForce,
        byteDashYForce,
        byteLobbyType,
        byteMatchmakingPlaylist,
        byteNumActivePlayers,
        byteNumMaxGrenades,
        byteNumCurrentGrenades,
        byteSentryCooldown,
        byteFlagReturnCountdown,
        byteZombieAlphaStartLevel,
        byteZombieStartLevel,
        byteZombieLevelCap,
        byteWeaponDespawnTime,
        byteRamSettings,
        byteSprintingType,
        byteGiveWeaponType,
        //byteNumMaxJegg,
        END_BYTES,
        BEGIN_SHORTS,
        shortScoreToWin,
        shortTimeLimit,
        shortPlayerScale,
        shortSecondsRemaining,
        END_SHORTS,
        BEGIN_UINTS,
        //uintEnabledWeapon, // Enabled weapons moved to uint from short
        END_UINTS,
        BEGIN_ULONGS,
        ulongEnabledWeapon, // Enabled weapons moved to ulong from uint from short
        ulongInfectedPlayers,
        END_ULONGS
    }

    // The last public build
    // U39
    enum NetSettingType1
    {
        BEGIN_BOOLS,
        boolIsMaxZombies,
        boolShowInvisibleBlocks,
        boolIsTeams,
        boolIsTeamSwitchingLocked,
        boolIsCTF,
        boolIsGamenight,
        boolUseTeamSpawns,
        boolTeamKilling,
        boolIsZombies,
        boolIsInfection,
        boolIsMurdernaut,
        boolDeathWallEnabled,
        boolIsEditMode,
        boolWeaponOnMap,
        boolVehiclesOnMap,
        boolFlagAtHomeToScore,
        boolIsAlphaFlagHome,
        boolIsOmegaFlagHome,
        boolIsHostSpectator,
        boolStickyGrenades,
        boolShootFast,
        boolIsMurderGames,
        boolIsSlowZombies,
        boolIsTentaclesEnabled,
        boolSpawnAsSpectator,
        boolSprintingEnabled,
        boolSpawnLasers,
        boolInfiniteAmmo,
        boolBottomlessClip,
        boolSniperLasers,
        boolIsWaitingForPlayers,
        boolHasScoreToWin,
        boolHasTimeLimit,
        boolIsSpeedRunTimer,
        boolIsFreeBuild,
        boolIsChristmasPlaylist,
        boolInfiniteGrenade,
        boolZombieRadar,
        boolZombieVomit,
        boolZombieWind,
        boolZombieRegurgitation,
        END_BOOLS,
        BEGIN_BYTES,
        byteMaxPlayers,
        byteScoreToWin,
        byteRadarMode,
        byteNumInfected,
        byteRankedCountdown,
        byteRespawnTime,
        byteMovementSpeed,
        byteLobbyType,
        byteMatchmakingPlaylist,
        byteNumActivePlayers,
        byteNumMaxGrenades,
        byteNumCurrentGrenades,
        END_BYTES,
        BEGIN_SHORTS,
        shortTimeLimit,
        shortSecondsRemaining,
        END_SHORTS,
        BEGIN_UINTS,
        uintEnabledWeapon,
#if ISSTEAM
        uintInfectedPlayers,
#endif
        END_UINTS
    }

    class NetSettings
    {
        public const int MAX_PLAYERS_IN_HORDE = 30; // 12

#if XBOXONE
		public const int MAX_PLAYERS = 30;
#else
        public const int MAX_PLAYERS = 30; // Was 20
#endif

        ulong data_bools;
        byte[] data_bytes;
        short[] data_shorts;
        uint[] data_uints;

        public ulong Bools { get { return data_bools; } }

        //public NetSettings() : this(false)
        //{
        //}

        bool disableTouching = false;

        //        public NetSettings(bool disableTouching)
        //        {
        //            this.disableTouching = disableTouching;

        //            int numBytes = (int)NetSettingType.END_BYTES - (int)NetSettingType.BEGIN_BYTES - 1;
        //            data_bytes = new byte[numBytes];

        //            int numShorts = (int)NetSettingType.END_SHORTS - (int)NetSettingType.BEGIN_SHORTS - 1;
        //            data_shorts = new short[numShorts];

        //            int numUints = (int)NetSettingType.END_UINTS - (int)NetSettingType.BEGIN_UINTS - 1;
        //            data_uints = new uint[numUints];

        //            data_bools = 0;
        //            SetValue(NetSettingType.boolIsMaxZombies, false);
        //            SetValue(NetSettingType.boolShowInvisibleBlocks, false);
        //            SetValue(NetSettingType.boolIsFreeBuild, false);
        //            SetValue(NetSettingType.boolIsChristmasPlaylist, false);
        //            SetValue(NetSettingType.boolIsTeams, false);
        //            SetValue(NetSettingType.boolIsTeamSwitchingLocked, false);
        //            SetValue(NetSettingType.boolIsCTF, false);
        //            SetValue(NetSettingType.boolFlagTouchToReturn, false);
        //            SetValue(NetSettingType.boolFlagAtHomeToScore, false);
        //            SetValue(NetSettingType.boolTeamKilling, false);
        //            SetValue(NetSettingType.boolIsZombies, false);
        //            SetValue(NetSettingType.boolIsInfection, false);
        //            //SetValue(NetSettingType.boolIsMurderball, false);
        //            SetValue(NetSettingType.boolIsMurdernaut, false);
        //            SetValue(NetSettingType.boolIsEditMode, false);
        //            SetValue(NetSettingType.boolWeaponOnMap, true);
        //            SetValue(NetSettingType.boolVehiclesOnMap, true);
        //            SetValue(NetSettingType.boolIsHostSpectator, false);
        //            SetValue(NetSettingType.boolStickyGrenades, true);
        //            SetValue(NetSettingType.boolShootFast, false);
        //            SetValue(NetSettingType.boolIsMurderGames, false);
        //            SetValue(NetSettingType.boolIsSlowZombies, false);
        //            SetValue(NetSettingType.boolIsTentaclesEnabled, true);
        //            SetValue(NetSettingType.boolZombieRadar, true);
        //            SetValue(NetSettingType.boolZombieVomit, true);
        //            SetValue(NetSettingType.boolZombieWind, true);
        //            SetValue(NetSettingType.boolZombieRegurgitation, true);
        //            SetValue(NetSettingType.boolSprintingEnabled, true);
        //            SetValue(NetSettingType.boolSpawnLasers, true);
        //            SetValue(NetSettingType.boolInfiniteAmmo, false);
        //            SetValue(NetSettingType.boolBottomlessClip, false);
        //            SetValue(NetSettingType.boolInfiniteGrenade, false);
        //            SetValue(NetSettingType.boolAssassinationOnly, false);
        //            SetValue(NetSettingType.boolHeadshotOnly, false);
        //            SetValue(NetSettingType.boolNoHealthRegen, false);
        //            SetValue(NetSettingType.boolWeaponTimers, true);
        //            SetValue(NetSettingType.boolSniperLasers, true);
        //            SetValue(NetSettingType.boolSpawnAsSpectator, true);
        //            SetValue(NetSettingType.boolIsSpeedRunTimer, false);
        //            SetValue(NetSettingType.boolIsMinorMode, false);
        //            SetValue(NetSettingType.boolRam, true);
        //            SetValue(NetSettingType.boolSlide, false);
        //            SetValue(NetSettingType.boolCanWallJump, true);
        //            SetValue(NetSettingType.boolZombieCanRide, true);
        //            SetValue(NetSettingType.byteScoreToWin, 25);
        //            SetValue(NetSettingType.byteRespawnTime, (byte)1);
        //            SetValue(NetSettingType.byteSelfMurderCountDownTime, 0);
        //            SetValue(NetSettingType.byteMovementSpeed, (byte)100);
        //            SetValue(NetSettingType.shortPlayerScale, (short)100);
        //            SetValue(NetSettingType.byteRadarMode, (byte)RadarMode.Motion);
        //            SetValue(NetSettingType.byteWeaponWheelType, (byte)WeaponSelectOverlay.WeaponWheelType.Default);
        //            SetValue(NetSettingType.byteMaxPlayers, 20);
        //            SetValue(NetSettingType.byteNumMaxGrenades, (byte)3);
        //            SetValue(NetSettingType.byteNumCurrentGrenades, (byte)2);
        //            SetValue(NetSettingType.byteMaxHealth, (byte)100);
        //            SetValue(NetSettingType.byteMaxShields, (byte)100);
        //            SetValue(NetSettingType.byteGravity, (byte)100);
        //            SetValue(NetSettingType.byteKnockback, (byte)1);

        //            SetValue(NetSettingType.shortSecondsRemaining, 0);
        //            //SetValue(NetSettingType.byteJoining, (byte)NetworkSessionV);
        //            SetValue(NetSettingType.byteNumInfected, (byte)0);
        //            SetValue(NetSettingType.byteLobbyType, (byte)LobbyType.CustomGame);

        //            SetValue(NetSettingType.shortTimeLimit, (short)0);

        //            SetValue(NetSettingType.uintEnabledWeapon, GetDefaultEnabledWeapons());

        //#if ISSTEAM
        //            SetValue(NetSettingType.uintInfectedPlayers, (uint)0); 
        //#endif
        //            SetValue(NetSettingType.byteZombieAlphaStartLevel, (byte)2);
        //            SetValue(NetSettingType.byteZombieStartLevel, (byte)0);
        //            SetValue(NetSettingType.byteZombieLevelCap, (byte)30);

        //            //int numBytes = (int)NetSettingType.END_BYTES - (int)NetSettingType.BEGIN_BYTES - 1;
        //            //data_bytes = new byte[numBytes];
        //        }

        //public NetSettings(NetSettings cloneSource) : this()
        //{
        //    cloneSource.CopyTo(this);
        //}

        //public static uint GetDefaultEnabledWeapons()
        //{
        //    uint s = uint.MaxValue;

        //    s = GameSettings.SetBool(s, (int)WeaponType.HookShot, false);
        //    s = GameSettings.SetBool(s, (int)WeaponType.RocketLauncher, false);
        //    s = GameSettings.SetBool(s, (int)WeaponType.Sentry, false);
        //    return s;
        //}

//        public Point3D GetMinMaxIncrement(NetSettingType nst)
//        {
//            if ((int)nst < (int)NetSettingType.END_BOOLS)
//                return new Point3D(0, 1, 1);

//            int maxPlayers = 30;
//#if XBOX
//            maxPlayers = 30; // was 20
//#endif

//            switch (nst)
//            {
//                case NetSettingType.byteScoreToWin: return new Point3D(0, 200, 1);
//                case NetSettingType.byteRadarMode: return new Point3D(0, (int)RadarMode.NUM_MODES - 1, 1);
//                case NetSettingType.byteMaxPlayers: return new Point3D(1, GetValueBool(NetSettingType.boolIsZombies) ? MAX_PLAYERS_IN_HORDE : maxPlayers, 1);
//                case NetSettingType.byteSelfMurderCountDownTime: return new Point3D(0, 30, 1);
//                case NetSettingType.byteMovementSpeed: return new Point3D(5, 250, 5);
//                case NetSettingType.byteMaxHealth: return new Point3D(30, 250, 10);
//                case NetSettingType.byteMaxShields: return new Point3D(0, 250, 10);
//                case NetSettingType.byteGravity: return new Point3D(1, 200, 5);
//                case NetSettingType.byteKnockback: return new Point3D(1, 10, 1);
//                case NetSettingType.byteRespawnTime: return new Point3D(0, 30, 1);
//                case NetSettingType.byteNumMaxGrenades: return new Point3D(0, Sts.IsEggHunt ? 20 : 12, 1);
//                case NetSettingType.byteNumCurrentGrenades: return new Point3D(0, Sts.IsEggHunt ? 20 : 12, 1);
//                case NetSettingType.byteSentryCooldown: return new Point3D(0, 120, 1);
//                case NetSettingType.byteFlagReturnCountdown: return new Point3D(0, 120, 1);
//                case NetSettingType.uintEnabledWeapon: return new Point3D(0, int.MaxValue, 1);
//                case NetSettingType.shortTimeLimit:
//                    Point3D v = new Point3D(0, 1800, 60);
//                    //if (value <= 120)
//                    //    v.Z = 1;
//                    //else if (value <= 300)
//                    //    v.Z = 5;
//                    //else if (value <= 
//                    return v;
//                case NetSettingType.shortPlayerScale: return new Point3D(35, 1000, 5);
//                case NetSettingType.byteZombieAlphaStartLevel: return new Point3D(0, 255, 1);
//                case NetSettingType.byteZombieStartLevel: return new Point3D(0, 255, 1);
//                case NetSettingType.byteZombieLevelCap: return new Point3D(0, 255, 1);
//                default: return new Point3D(0, 1, 1);
//            }
//        }

        #region set

        //public void SetValue(NetSettingType nst, bool value)
        //{
        //    int n = (int)nst;
        //    int start = (int)NetSettingType.BEGIN_BOOLS;
        //    int end = (int)NetSettingType.END_BOOLS;
        //    if (n > start && n < end && value != GetValueBool(nst))
        //    {
        //        data_bools = GameSettings.SetBool(data_bools, n - (start + 1), value);
        //        touch();
        //    }
        //    //else
        //    //    throw new InvalidOperationException(string.Format("{0} is not a boolean", nst.ToString()));
        //}

        //public void SetValue(NetSettingType nst, byte value)
        //{
        //    if (nst == NetSettingType.byteMaxPlayers)
        //        value = (byte)MathHelper.Clamp(value, 1, MAX_PLAYERS);

        //    int n = (int)nst;
        //    int start = (int)NetSettingType.BEGIN_BYTES;
        //    int end = (int)NetSettingType.END_BYTES;
        //    if (n > start && n < end && value != GetValueByte(nst))
        //    {
        //        data_bytes[n - (start + 1)] = value;
        //        touch();

        //        if (nst == NetSettingType.byteScoreToWin)
        //            SetValue(NetSettingType.boolHasScoreToWin, value != 0);
        //    }
        //    //else
        //    //    throw new InvalidOperationException(string.Format("{0} is not a byte", nst.ToString()));
        //}

        //public void SetValue(NetSettingType nst, short value)
        //{
        //    int n = (int)nst;
        //    int start = (int)NetSettingType.BEGIN_SHORTS;
        //    int end = (int)NetSettingType.END_SHORTS;
        //    if (n > start && n < end && value != GetValueShort(nst))
        //    {
        //        data_shorts[n - (start + 1)] = value;
        //        touch();

        //        if (nst == NetSettingType.shortTimeLimit)
        //            SetValue(NetSettingType.boolHasTimeLimit, value != 0);
        //    }
        //    //else
        //    //    throw new InvalidOperationException(string.Format("{0} is not a short", nst.ToString()));
        //}

        //public void SetValue(NetSettingType nst, uint value)
        //{
        //    int n = (int)nst;
        //    int start = (int)NetSettingType.BEGIN_UINTS;
        //    int end = (int)NetSettingType.END_UINTS;
        //    if (n > start && n < end && value != GetValueUint(nst))
        //    {
        //        data_uints[n - (start + 1)] = value;
        //        touch();
        //    }
        //    //else
        //    //    throw new InvalidOperationException(string.Format("{0} is not a uint", nst.ToString()));
        //}

        #endregion set

        #region get

        public bool GetValueBool(NetSettingType nst)
        {
            int n = (int)nst;
            int start = (int)NetSettingType.BEGIN_BOOLS;
            int end = (int)NetSettingType.END_BOOLS;
            if (n > start && n < end)
                return GameSettings.GetBool(data_bools, n - (start + 1));
            else
                throw new InvalidOperationException(string.Format("{0} is not a boolean", nst.ToString()));
        }

        public byte GetValueByte(NetSettingType nst)
        {
            int n = (int)nst;
            int start = (int)NetSettingType.BEGIN_BYTES;
            int end = (int)NetSettingType.END_BYTES;
            if (n > start && n < end)
                return data_bytes[n - (start + 1)];
            else
                throw new InvalidOperationException(string.Format("{0} is not a byte", nst.ToString()));
        }

        public short GetValueShort(NetSettingType nst)
        {
            int n = (int)nst;
            int start = (int)NetSettingType.BEGIN_SHORTS;
            int end = (int)NetSettingType.END_SHORTS;
            if (n > start && n < end)
                return data_shorts[n - (start + 1)];
            else
                throw new InvalidOperationException(string.Format("{0} is not a short", nst.ToString()));
        }

        public uint GetValueUint(NetSettingType nst)
        {
            int n = (int)nst;
            int start = (int)NetSettingType.BEGIN_UINTS;
            int end = (int)NetSettingType.END_UINTS;
            if (n > start && n < end)
                return data_uints[n - (start + 1)];
            else
                throw new InvalidOperationException(string.Format("{0} is not a uint", nst.ToString()));
        }

        #endregion get

        //public void DisableWeapons(params WeaponType[] weapons)
        //{
        //    uint weaponsValue = GetValueUint(NetSettingType.uintEnabledWeapon);
        //    for (int i = 0; i < weapons.Length; i++)
        //    {
        //        weaponsValue = (uint)GameSettings.SetBool(weaponsValue, (int)weapons[i], false);
        //    }

        //    SetValue(NetSettingType.uintEnabledWeapon, weaponsValue);
        //}

        //public void EnableWeapons(params WeaponType[] weapons)
        //{
        //    uint weaponsValue = GetValueUint(NetSettingType.uintEnabledWeapon);
        //    for (int i = 0; i < weapons.Length; i++)
        //    {
        //        weaponsValue = (uint)GameSettings.SetBool(weaponsValue, (int)weapons[i], true);
        //    }

        //    SetValue(NetSettingType.uintEnabledWeapon, weaponsValue);
        //}

        public static NetSettingDataType GetDataType(NetSettingType nst)
        {
            if ((int)nst < (int)NetSettingType.END_BOOLS)
                return NetSettingDataType.Bool;
            else if ((int)nst < (int)NetSettingType.END_BYTES)
                return NetSettingDataType.Byte;
            else if ((int)nst < (int)NetSettingType.END_SHORTS)
                return NetSettingDataType.Short;
            else
                return NetSettingDataType.UInt;
        }

        //public void touch()
        //{
        //    if (disableTouching)
        //        return;

        //    NetworkSession ns = NetworkSessionComponent.CurrentNetworkSession;
        //    if (ns != null && ns.IsHost)
        //    {
        //        ns.SessionProperties.SetProperty(PropertyUsage.CTF, GetValueBool(NetSettingType.boolIsCTF));
        //        ns.SessionProperties.SetProperty(PropertyUsage.Infection, GetValueBool(NetSettingType.boolIsInfection));
        //        ns.SessionProperties.SetProperty(PropertyUsage.MapMaking, GetValueBool(NetSettingType.boolIsEditMode));
        //        ns.SessionProperties.SetProperty(PropertyUsage.MurderGames, GetValueBool(NetSettingType.boolIsMurderGames));
        //        ns.SessionProperties.SetProperty(PropertyUsage.Team, GetValueBool(NetSettingType.boolIsTeams));
        //        ns.SessionProperties.SetProperty(PropertyUsage.Zombies, GetValueBool(NetSettingType.boolIsZombies));
        //        ns.SessionProperties.SetProperty(PropertyUsage.Gamenight, GetValueBool(NetSettingType.boolIsGamenight));
        //        ns.SessionProperties[(int)PropertyUsage.NetSettingsBools] = (uint)Bools;
        //        ns.SessionProperties[(int)PropertyUsage.NetSettingsBoolsExtended] = (uint)(Bools >> 32);
        //    }
        //}

        public bool IsDifferent(NetSettings other)
        {
            if (other.data_bools != data_bools) return true;

            for (int i = 0; i < other.data_bytes.Length; i++)
            {
                if (data_bytes[i] != other.data_bytes[i])
                {
                    return true;
                }
            }

            for (int i = 0; i < other.data_shorts.Length; i++)
            {
                if (data_shorts[i] != other.data_shorts[i])
                {
                    return true;
                }
            }

            for (int i = 0; i < other.data_uints.Length; i++)
            {
                if (data_uints[i] != other.data_uints[i])
                {
                    return true;
                }
            }

            return false;
        }

        //public void CopyTo(NetSettings target)
        //{
        //    target.data_bools = data_bools;

        //    if (data_bytes != null && target.data_bytes != null)
        //    {
        //        for (int i = 0; i < data_bytes.Length; i++)
        //            target.data_bytes[i] = data_bytes[i];
        //    }

        //    if (data_shorts != null && target.data_shorts != null)
        //    {
        //        for (int i = 0; i < data_shorts.Length; i++)
        //            target.data_shorts[i] = data_shorts[i];
        //    }

        //    if (data_uints != null && target.data_uints != null)
        //    {
        //        for (int i = 0; i < data_uints.Length; i++)
        //            target.data_uints[i] = data_uints[i];
        //    }

        //    target.touch();
        //}

        //public bool ReplicateDifference(LocalNetworkGamer sender, NetworkGamer recipient, MyPacketWriter writer, NetSettings target)
        //{
        //    long writeFlags = 0;

        //    if (target.data_bools != data_bools)
        //        writeFlags = GameSettings.SetBool(writeFlags, 0, true);

        //    for (int i = 0; i < target.data_bytes.Length; i++)
        //    {
        //        if (data_bytes[i] != target.data_bytes[i])
        //        {
        //            writeFlags = GameSettings.SetBool(writeFlags, 1, true);
        //            break;
        //        }
        //    }

        //    for (int i = 0; i < target.data_shorts.Length; i++)
        //    {
        //        if (data_shorts[i] != target.data_shorts[i])
        //        {
        //            writeFlags = GameSettings.SetBool(writeFlags, 2, true);
        //            break;
        //        }
        //    }

        //    for (int i = 0; i < target.data_uints.Length; i++)
        //    {
        //        if (data_uints[i] != target.data_uints[i])
        //        {
        //            writeFlags = GameSettings.SetBool(writeFlags, 3, true);
        //            break;
        //        }
        //    }

        //    if (writeFlags > 0)
        //    {
        //        writer.Write((byte)MessageType.SettingsMessage);
        //        writer.Write((byte)writeFlags);

        //        if (GameSettings.GetBool(writeFlags, 0))
        //            writer.Write(data_bools);

        //        if (GameSettings.GetBool(writeFlags, 1))
        //        {
        //            for (int i = 0; i < data_bytes.Length; i++)
        //            {
        //                writer.Write(data_bytes[i]);
        //            }
        //        }

        //        if (GameSettings.GetBool(writeFlags, 2))
        //        {
        //            for (int i = 0; i < data_shorts.Length; i++)
        //            {
        //                writer.Write(data_shorts[i]);
        //            }
        //        }

        //        if (GameSettings.GetBool(writeFlags, 3))
        //        {
        //            for (int i = 0; i < data_uints.Length; i++)
        //            {
        //                writer.Write(data_uints[i]);
        //            }
        //        }

        //        writer.Write((byte)MessageType.EndOfPacket);
        //        sender.SendData(writer, SendDataOptions.ReliableInOrder, recipient);

        //        //NetworkSessionComponent.DisplayMessage("Sending Settings Change", true);

        //        CopyTo(target);

        //        return true;
        //    }

        //    return false;
        //}

        //public void RecieveReplication(MyPacketReader reader, bool isHost)
        //{
        //    int flags = reader.ReadByte();

        //    // FLAGS:
        //    // 0 bools
        //    // 1 bytes
        //    // 2 shorts
        //    // 3 uints

        //    if (GameSettings.GetBool(flags, 0))
        //    {
        //        ulong n = reader.ReadUInt64();
        //        if (!isHost) data_bools = n;
        //    }

        //    if (GameSettings.GetBool(flags, 1))
        //    {
        //        for (int i = 0; i < data_bytes.Length; i++)
        //        {
        //            byte n = reader.ReadByte();
        //            if (!isHost) data_bytes[i] = n;
        //        }
        //    }

        //    if (GameSettings.GetBool(flags, 2))
        //    {
        //        for (int i = 0; i < data_shorts.Length; i++)
        //        {
        //            short n = reader.ReadInt16();
        //            if (!isHost) data_shorts[i] = n;
        //        }
        //    }

        //    if (GameSettings.GetBool(flags, 3))
        //    {
        //        for (int i = 0; i < data_uints.Length; i++)
        //        {
        //            uint n = reader.ReadUInt32();
        //            if (!isHost) data_uints[i] = n;
        //        }
        //    }
        //}
    }
}
