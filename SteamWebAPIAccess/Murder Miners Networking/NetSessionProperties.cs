using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace SteamWebAPIAccess.Murder_Miners_Networking
{
    enum MessageType
    {
        ZeroData,
        ActiveSubPlayers,
        BlockDataName,
        BlockDataPacket,
        BlockDataPacketCount,
        BlockDataMapCounter,
        BlockDataLoadDefault,
        MapRequest,
        BlockEditingStatus,
        RemoveEntity,
        DamageRequest,
        VoxelDamageRequest,
        SetBlockRequest, // all blockmanager bound messages must be between this
        SetBlockRequestPlus,
        SetBlockChain,
        RemoveBlockChain,
        LockedBlocksUpdate,
        RequestBlockRayExplosion, // and this
        ClientMessageTest,
        EnterVehicleRequest,
        ReadyToReceive,
        //BandwidthSpam,
        CurrentTime,
        //FinishedLoadingMap,
        WeaponPickupOrDropRequest,
        FlagScoreRequest,
        CollectJeggRequest,
        DepositJeggRequest,
        PointCaptureRequest,
        MurderBallIncrementRequest,
        InGameMapRequest,
        MapChangeCounter,
        SetLiquidType,
        SetLiquidHeight,
        SetSpawnMin,
        SetSpawnMax,
        SetMapBools,
        SetSkyboxType,
        SetAmbienceType,
        SetMusicType,
        SetBlockAtlasType,
        SetMapFogDistanceType, // If this is filling up max types then check for map changes in level renderer
        SetWeatherType,
        SetWeatherColor_R,
        SetWeatherColor_G,
        SetWeatherColor_B,
        SetWeatherColor_A,
        SetWeatherColorBoolean,
        SetWeatherSpeed,
        SetWeatherHeading,
        SetWeatherGravity,
        SetSnowDensity,
        SetEggHuntTheme,
        SetFogColor_R,
        SetFogColor_G,
        SetFogColor_B,
        SetFogColorBoolean,
        SetFogDistanceOverride,
        SetShadows,
        SetShadowAttenuation,
        SetShadowStrength,
        SetSpawnCam,
        SetMapObject,
        RemoveMapObject,
        SettingsMessage,
        //SettingsLoadingGamersMessage,
        GameRuleMessageKill,
        GameRuleMessageFlagScore,
        GameRuleMessageUpdateComplete,
        GameRuleMessageUpdatePlayer,
        GameRuleMessageInfectGamer,
        GameRuleMessageTeamChange,
        GameRuleMessageUpdateTeamScores,
        GameRuleMessageForceTeamChange,
        GameRuleMessageGameOver,
        EntityGamerIdOfOwner,
        BEGIN_ENTITY_STATES,
        EntityStatePlayer,
        EntityStatePlayerInVehicle,
        EntityStateZombie,
        EntityStateZombieInVehicle,
        EntityStateZombieAI,
        EntityStateZombieAIInVehicle,
        EntityStatePlayerSpectator,
        EntityStatePlayerSpectatorInVehicle,
        EntityStateTrainerPlayer,
        EntityStateTrainerPlayerNoAI,
        EntityStateTrainerPlayerWandering,
        EntityStateTrainerPlayerSidestepper,
        EntityStateGrenade,
        EntityStateStickyGrenade,
        EntityStateVehicleFlyingTransport,
        EntityStateVehicleBike,
        EntityStateWeaponPickup,
        EntityStateLaserPulse,
        EntityStateRocket,
        EntityStateFlag,
        EntityStateMurderBall,
        EntityStateShipMissile,
        EntityStateAcidGrenade,
        EntityStateGasCloud,
        EntityStateSentry,
        EntityStateBelieverBall,
        EntityStateDeathWall,
        EntityStateTPEffect,
        EntityStateExplosionDummy,
        EntityStateVehicleCart,
        EntityStateVehicleCarGreen,
        EntityStateVehicleBikeAprilFools,
        EntityStateVehicleFlyingTransportChristmas,
        EntityStateVehicleBikeChristmas,
        EntityStateVehicleFlyingTransportAnniversary,
        EntityStateVehicleBikeAnniversary,
        EntityStateVehicleBikeBlack,
        EntityStateVehicleBikeBlue,
        EntityStateVehicleBikeDark,
        EntityStateVehicleFlyingTransportBlue,
        EntityStateVehicleFlyingTransportOrange,
        EntityStateVehicleFlyingTransportPurple,
        EntityStateVehicleFlyingTransportRed,
        EntityStateVehicleFlyingTransportWatermelon,
        EntityStateVehicleCarRed,
        EntityStateVehicleCarBlue,
        EntityStateVehicleCarBlack,
        EntityStateVehicleFlyingTransportBloodbreak,
        EntityStateVehicleBikeBloodbreak,
        EntityStateVehicleCarBloodbreak,
        EntityStateVehicleFlyingTransportNightlights,
        EntityStateVehicleBikeNightlights,
        EntityStateVehicleCarNightlights,
        EntityStateVehicleBikeRadiantRemix,
        EntityStateVehicleBikeGreen,
        EntityStateVehicleCarAnniversary,
        EntityStateVehicleCarChristmas,
        EntityStateVehicleCarOriginal,
        EntityStateVehicleCarBrown,
        EndOfPacket
    }

    #region network state requests

    //struct SetBlockRequest
    //{
    //    public byte BitField;
    //    public bool
    //        IsRange,
    //        IsRadius,
    //        IsFloodFill,
    //        CreateExplosion,
    //        IsFireyExplosion,
    //        IsAlreadySetOnHost;

    //    public ushort X, Y, Z;
    //    public byte NewBlockID;

    //    public ushort RangeX, RangeY, RangeZ;
    //    public byte Radius;
    //    public byte FillTargetBlockID;
    //    public byte FillFlags;
    //    public byte CenterDamage; // setting will enable fall of damage, otherwise all blocks in radius are set

    //    public void SetBitField()
    //    {
    //        int field = 0;
    //        NetHelper.AddToBitfield(ref field, 1, IsRange ? 1 : 0);
    //        NetHelper.AddToBitfield(ref field, 1, IsRadius ? 1 : 0);
    //        NetHelper.AddToBitfield(ref field, 1, IsFloodFill ? 1 : 0);
    //        NetHelper.AddToBitfield(ref field, 1, CreateExplosion ? 1 : 0);
    //        NetHelper.AddToBitfield(ref field, 1, IsFireyExplosion ? 1 : 0);
    //        NetHelper.AddToBitfield(ref field, 1, IsAlreadySetOnHost ? 1 : 0);

    //        BitField = (byte)field;
    //    }

    //    public void SetBooleans()
    //    {
    //        int field = BitField;
    //        IsAlreadySetOnHost = (NetHelper.ReadFromBitfield(ref field, 1) == 1);
    //        IsFireyExplosion = (NetHelper.ReadFromBitfield(ref field, 1) == 1);
    //        CreateExplosion = (NetHelper.ReadFromBitfield(ref field, 1) == 1);
    //        IsFloodFill = (NetHelper.ReadFromBitfield(ref field, 1) == 1);
    //        IsRadius = (NetHelper.ReadFromBitfield(ref field, 1) == 1);
    //        IsRange = (NetHelper.ReadFromBitfield(ref field, 1) == 1);
    //    }
    //}

    //struct DamageRequest
    //{
    //    public byte MachineIDOfTarget;
    //    public ushort MachineUniqueIdOfTarget;
    //    public byte MachineIdOfSource;
    //    public ushort MachineUniqueIdOfSource;
    //    public byte Damage;
    //    public byte HeadShotDamage;
    //    public byte DamageType;
    //    public byte WeaponType;
    //    public Vector3 Offset;
    //    public Vector3 Direction;
    //    public Vector3 Normal;

    //    public static DamageRequest Create(Entity source, Entity target) { return Create(source, target, 0); }

    //    public static DamageRequest Create(Entity source, Entity target, byte damage)
    //    {
    //        DamageRequest dr = new DamageRequest();

    //        dr.Damage = damage;

    //        dr.MachineIdOfSource = source.Machine.ID;
    //        dr.MachineUniqueIdOfSource = source.MachineUniqueID;

    //        dr.MachineIDOfTarget = target.Machine.ID;
    //        dr.MachineUniqueIdOfTarget = target.MachineUniqueID;

    //        return dr;
    //    }

    //    public void Serialize(MyPacketWriter writer)
    //    {
    //        writer.Write((byte)MessageType.DamageRequest);

    //        writer.Write(MachineIDOfTarget);
    //        writer.Write(MachineUniqueIdOfTarget);
    //        writer.Write(MachineIdOfSource);
    //        writer.Write(MachineUniqueIdOfSource);
    //        writer.Write(Damage);
    //        writer.Write(HeadShotDamage);
    //        writer.Write(DamageType);
    //        writer.Write(WeaponType);

    //        NetHelper.WriteVector3(writer, Offset, true);

    //        NetHelper.Write16BitUnitVector3(writer, Direction); // 2 bytes
    //        NetHelper.Write16BitUnitVector3(writer, Normal); // 2 bytes
    //    }

    //    public void Deserialize(MyPacketReader reader)
    //    {
    //        MachineIDOfTarget = reader.ReadByte();
    //        MachineUniqueIdOfTarget = reader.ReadUInt16();
    //        MachineIdOfSource = reader.ReadByte();
    //        MachineUniqueIdOfSource = reader.ReadUInt16();
    //        Damage = reader.ReadByte();
    //        HeadShotDamage = reader.ReadByte();

    //        DamageType = reader.ReadByte();
    //        WeaponType = reader.ReadByte();

    //        Offset = NetHelper.ReadVector3(reader, true);

    //        Direction = NetHelper.Read16BitUnitVector3(reader);
    //        Normal = NetHelper.Read16BitUnitVector3(reader);
    //    }
    //}

    //struct VoxelDamageRequest
    //{
    //    public ushort X, Y, Z;
    //    public byte Damage;
    //}

    //struct KillRequest
    //{
    //    public byte SourceGamerID;
    //    public byte SourceSubPlayerIndex;

    //    public byte TargetGamerID;
    //    public byte TargetSubPlayerIndex;

    //    public byte DamageType;
    //    public byte WeaponType;
    //}

    //struct TeamChangeRequest
    //{
    //    public NetworkGamer Gamer;
    //    public byte SubPlayerIndex;
    //    public byte NewTeam;
    //}

    //struct WeaponDropOrPickupRequest
    //{
    //    public WeaponType WeaponType;
    //    public ushort WeaponMachineUniqueID;
    //    public byte RequesterMachineID;
    //    public ushort RequesterMachineUniqueID;
    //    public bool IsDropRequest;
    //    public bool IsResponse;
    //    public bool ResponseSuccess;
    //    public byte AmmoClip;
    //    public ushort AmmoReserve;
    //    public Vector3 Position;
    //    public Vector3 Velocity;
    //    public byte FlagTeamId;
    //    public byte SkinIndex;
    //}

    //struct OwnershipTransferRequest
    //{
    //    public byte OldMachineID;
    //    public ushort OldMachineUniqueID;
    //    public byte NewMachineID;
    //    public ushort NewMachineUniqueID;
    //    public bool SendToAll; // not serialized
    //}

    //struct EnterVehicleRequest
    //{
    //    public bool IsResponse;
    //    public ushort RequesterMachineUniqueID;

    //    public byte RequesterMachineID;
    //    public bool ResponseAnswer;

    //    public bool IsExit; // is Enter or Exit request
    //    public byte VehicleMachineID;
    //    public ushort VehicleMachineUniqueID;
    //    public byte VehicleSeatIndex;
    //}

    //struct FlagScoreRequest
    //{
    //    public byte RequesterMachineID;
    //    public ushort RequesterMachineUniqueID;
    //}

    #endregion network state requests

    //struct ColorByte
    //{
    //    Color c;
    //    byte b;
    //    Vector4 v;

    //    public Color Color { get { return c; } }
    //    public Vector4 Vector { get { return c.ToVector4(); } }

    //    public byte Byte
    //    {
    //        get { return b; }
    //        set { b = value; c = Ast.ColorTable[value]; }
    //    }
    //}

    //struct NetworkState
    //{
    //    public Vector3 Position;
    //    public Vector3 Velocity;
    //    public float Yaw;
    //    public float Pitch;

    //    public Vector3 Center;

    //    public void LerpState(NetworkState state1, NetworkState state2, float amount, Physics physics)
    //    {
    //        Position = Vector3.Lerp(state1.Position, state2.Position, amount);
    //        Velocity = Vector3.Lerp(state1.Velocity, state2.Velocity, amount);
    //        Yaw = JTools.M.RotationLerp(state1.Yaw, state2.Yaw, amount);
    //        Pitch = MathHelper.Lerp(state1.Pitch, state2.Pitch, amount);

    //        Center = Position + physics.Center - physics.Position;
    //    }
    //}

    public enum PropertyUsage
    {
        Team,
        Infection,
        CTF,
        Zombies,
        MurderGames,
        ZMapName0,
        ZMapName1,
        ZMapName2,
        ZMapName3,
        ZMapName4,
        ZVersion,
        MapMaking,
        Official,
        Gamenight,
        NetSettingsBools,
        NetSettingsBoolsExtended,
        NumActivePlayers,
        ZClientHash,
        MurderMelee,
        EggHunt,
        OneInTheChamber,
        CapturePoint,
        MurderMining,
        MurderBall
    }

    public enum NetworkSessionPropertiesGameMode
    {
        Custom,
        CustomNoGuests,
        Ranked,
        MapSharing
    }

    public static class NetPropertiesFunctions
    {
        public static byte GameVersionNumber = 90;
        public static byte GameVersionNumberOld = 87;

        public const int MAP_NAME_LEN = 32;       // max characters
        public const int MAP_NAME_UINT_SLOTS = 8; // 5 uints = 20 bytes

        public static NetworkSessionProperties CreateDefaultProperties()
        {
            NetworkSessionProperties properties = new NetworkSessionProperties();

            properties.SetDefaultProperties();

            return properties;
        }

        public static bool AllowGuests = true;

        public static void SetDefaultProperties(this NetworkSessionProperties properties)
        {
            properties.SetProperty(PropertyUsage.CTF, null);
            properties.SetProperty(PropertyUsage.Infection, null);
            properties.SetProperty(PropertyUsage.MurderGames, null);
            properties.SetProperty(PropertyUsage.MapMaking, null);
            properties.SetProperty(PropertyUsage.Team, null);
            properties.SetProperty(PropertyUsage.Zombies, null);
            properties.SetProperty(PropertyUsage.Gamenight, null);
            properties.SetProperty(PropertyUsage.MurderMelee, null);
            properties.SetProperty(PropertyUsage.EggHunt, null);
            properties.SetProperty(PropertyUsage.OneInTheChamber, null);
            properties.SetProperty(PropertyUsage.CapturePoint, null);
            properties.SetProperty(PropertyUsage.MurderMining, null);
            properties.SetProperty(PropertyUsage.MurderBall, null);

            properties.SetVersion(GameVersionNumber);
        }

        #region Game Values

        public static void SetProperty(this NetworkSessionProperties properties, PropertyUsage prop, bool? value)
        {
            beginMapNameChangeCheck(properties);

            if (prop == PropertyUsage.Zombies || prop == PropertyUsage.MapMaking)
            {
                bool isZomb = false;
                bool isMapMak = false;
                if (properties[(int)PropertyUsage.Zombies].HasValue)
                {
                    int curValue = (int)properties[(int)PropertyUsage.Zombies].Value;
                    if (curValue == 1) isZomb = true;
                    else if (curValue == 2) isMapMak = true;
                    else if (curValue == 3)
                    {
                        isZomb = true;
                        isMapMak = true;
                    }
                }
                else
                {
                    if (!value.HasValue)
                        return;
                    isZomb = false;
                    isMapMak = false;
                }

                if (value.HasValue)
                {
                    if (prop == PropertyUsage.Zombies) isZomb = value.Value;
                    else isMapMak = value.Value;
                }
                else
                {
                    if (prop == PropertyUsage.Zombies) isZomb = false;
                    else isMapMak = false;
                }

                if (isZomb && isMapMak) properties[(int)PropertyUsage.Zombies] = 3;
                else if (isZomb) properties[(int)PropertyUsage.Zombies] = 1;
                else if (isMapMak) properties[(int)PropertyUsage.Zombies] = 2;
                else properties[(int)PropertyUsage.Zombies] = 0;
            }
            else
            {
                if (value.HasValue)
                    properties[(int)prop] = value.Value ? 1u : 0;
                else
                    properties[(int)prop] = null;
            }

            endMapNameChangeCHeck(properties, prop);
        }

        static string oldMapName = "XXX";
        static void beginMapNameChangeCheck(NetworkSessionProperties properties)
        {
            oldMapName = GetMapName(properties);
        }

        static void endMapNameChangeCHeck(NetworkSessionProperties properties, PropertyUsage prop)
        {
            string newMapName = GetMapName(properties);

            if (newMapName != oldMapName)
            {
                System.Diagnostics.Debug.WriteLine($"Map Name Modified by set {prop.ToString()}! \"{oldMapName}\"->\"{newMapName}\"");
            }
        }


        public static ulong GetBoolsFromUints(uint one, uint two)
        {
            ulong oneL = one;
            ulong twoL = two;
            twoL = twoL << 32;
            ulong final = oneL | twoL;
            return final;
        }

        public static void SetIntProperty(this NetworkSessionProperties properties, PropertyUsage prop, uint? value)
        {
            beginMapNameChangeCheck(properties);

            properties[(int)prop] = value;

            endMapNameChangeCHeck(properties, prop);
        }

        public static bool GetProperty(this NetworkSessionProperties properties, PropertyUsage prop)
        {
            if (prop == PropertyUsage.MapMaking)
            {
                uint v = properties[(int)PropertyUsage.Zombies] ?? 0;
                return v == 2 || v == 3;
            }
            else if (prop == PropertyUsage.Zombies)
            {
                uint v = properties[(int)PropertyUsage.Zombies] ?? 0;
                return v == 1 || v == 3;
            }
            else
            {
                return properties[(int)prop] == 1;
            }
        }

        public static uint GetIntProperty(this NetworkSessionProperties properties, PropertyUsage prop)
        {
            return properties[(int)prop] ?? 0;
        }

        static void encode(ref int encoded, int valueRange, int value)
        {
            encoded *= valueRange;
            encoded += value;
        }

        static int decode(ref int bitfield, int valueRange)
        {
            int value = bitfield % valueRange;
            bitfield /= valueRange;
            return value;
        }

        #endregion Game Values

        #region Version

        public static void SetVersion(this NetworkSessionProperties properties, byte version)
        {
            beginMapNameChangeCheck(properties);

            properties[(int)PropertyUsage.ZVersion] = version;

            endMapNameChangeCHeck(properties, PropertyUsage.ZVersion);
        }

        public static uint GetVersion(this NetworkSessionProperties properties)
        {
            return properties[(int)PropertyUsage.ZVersion].Value;
        }

        public static NetworkSessionPropertiesGameMode GetGameMode(this NetworkSessionProperties properties)
        {
            return NetworkSessionPropertiesGameMode.Custom;
            //int decoded = (int)properties[(int)PropertyUsage.ZVersion].Value;
            //NetworkSessionPropertiesGameMode gameMode = (NetworkSessionPropertiesGameMode)NetHelper.ArithmeticDecode(ref decoded, 4);
            //return gameMode;
        }

        #endregion Version

        #region Map Name
        
        public static readonly int MAX_NAME_LENGTH = 19;
        public static readonly char[] ValidCharacters = new char[]
        {
            // Basic Latin
            // ASCII Punctuation & Symbols
            '!', '#', '$', '%', '&', '\'', '(', ')', '+', ',', '-', '.',
            // ASCII Digits
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
            // ASCII Punctuation & Symbols
            ';', '=', '@',
            // Latin Alphabet: Uppercase
            'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z',
            // ASCII Punctuation & Symbols
            '\\', '^', '_', '`', //'[', ']', 
            // Latin Alphabet: Lowercase
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z',
            // ASCII Punctuation & Symbols
            '{', '}', '~',

            ' '

            //// Latin-1 Supplement
            //// Latin-1 Punctuation & Symbols
            //'¡', '¢', '£', '¤', '¥', '¦', '§', '¨', '©', 'ª', '«', '¬', '®', '¯', '°', '±', '²', '³', '´', 'µ', '¶', '·', '¸', '¹', 'º', '»', '¼', '½', '¾', '¿',
            //// Letters: Uppercase
            //'À', 'Á', 'Â', 'Ã', 'Ä', 'Å', 'Æ', 'Ç', 'È', 'É', 'Ê', 'Ë', 'Ì', 'Í', 'Î', 'Ï', 'Ð', 'Ñ', 'Ò', 'Ó', 'Ô', 'Õ', 'Ö',
            //// Math
            //'×',
            //// Letters: Uppercase
            //'Ø', 'Ù', 'Ú', 'Û', 'Ü', 'Ý', 'Þ', 'ß', 'à', 'á', 'â', 'ã', 'ä', 'å', 'æ', 'ç', 'è', 'é', 'ê', 'ë', 'ì', 'í', 'î', 'ï', 'ð', 'ñ', 'ò', 'ó', 'ô', 'õ', 'ö',
            //// Math
            //'÷',
            //// Letters: Lowercase
            //'ø', 'ù', 'ú', 'û', 'ü', 'ý', 'þ', 'ÿ',
        };


        public static void SetMapName(this NetworkSessionProperties properties, string name)
        {
            if (name == null) name = "";

            // Normalize
            name = name.Trim();

            if (name.Length > MAP_NAME_LEN)
                name = name.Substring(0, MAP_NAME_LEN);

            name = name.ToUpperInvariant();

            // Pad to exactly 20 characters.
            // IMPORTANT: The pad char must exist in ValidCharacters.
            name = name.PadRight(MAP_NAME_LEN, ' ');

            // Convert each char to an index (0..ValidCharacters.Length-1)
            // Store index in a byte (ValidCharacters.Length must be <= 256 for this approach)
            if (ValidCharacters.Length > 256)
                throw new InvalidOperationException("ValidCharacters.Length must be <= 256 for byte packing.");

            byte[] indices = new byte[MAP_NAME_LEN];
            for (int i = 0; i < MAP_NAME_LEN; i++)
            {
                int idx = getCharacterIndex(name[i]);

                // If you want to mimic your old behavior, you could do:
                // if (idx < 0) idx = 0;
                if (idx < 0 || idx >= ValidCharacters.Length)
                    throw new InvalidOperationException($"Char '{name[i]}' not in ValidCharacters.");

                indices[i] = (byte)idx;
            }

            // Base property index for the name block
            int baseIndex = (int)PropertyUsage.ZMapName0;

            // Pack 4 bytes into each uint (little-endian)
            for (int block = 0; block < MAP_NAME_UINT_SLOTS; block++)
            {
                int o = block * 4;

                uint packed =
                    (uint)(indices[o + 0]) |
                    ((uint)(indices[o + 1]) << 8) |
                    ((uint)(indices[o + 2]) << 16) |
                    ((uint)(indices[o + 3]) << 24);

                properties[baseIndex + block] = packed;
            }
        }

        public static string GetMapName(this NetworkSessionProperties properties)
        {
            int baseIndex = (int)PropertyUsage.ZMapName0;

            // Ensure all 5 slots exist
            for (int block = 0; block < MAP_NAME_UINT_SLOTS; block++)
            {
                if (!properties[baseIndex + block].HasValue)
                    return "NONAME";
            }

            var sb = new System.Text.StringBuilder(MAP_NAME_LEN);

            for (int block = 0; block < MAP_NAME_UINT_SLOTS; block++)
            {
                uint packed = properties[baseIndex + block].Value;

                byte b0 = (byte)(packed & 0xFF);
                byte b1 = (byte)((packed >> 8) & 0xFF);
                byte b2 = (byte)((packed >> 16) & 0xFF);
                byte b3 = (byte)((packed >> 24) & 0xFF);

                sb.Append(SafeCharFromIndex(b0));
                sb.Append(SafeCharFromIndex(b1));
                sb.Append(SafeCharFromIndex(b2));
                sb.Append(SafeCharFromIndex(b3));
            }

            return sb.ToString();
        }

        private static char SafeCharFromIndex(byte index)
        {
            // Defensive: if remote data is corrupt, don’t throw
            return index < ValidCharacters.Length ? ValidCharacters[index] : '?';
        }

        static int getCharacterIndex(char c)
        {
            for (int i = 0; i < ValidCharacters.Length; i++)
                if (c == ValidCharacters[i])
                    return i;

            return -1;
        }

        static void ArithmeticEncode(ref ulong encoded, ulong valueRange, ulong value)
        {
            encoded = encoded * valueRange + value;
        }

        static int ArithmeticDecode(ref ulong bitfield, ulong valueRange)
        {
            ulong value = bitfield % valueRange;
            bitfield /= valueRange;
            return (int)value;
        }

        #endregion Map Name
    }
}
