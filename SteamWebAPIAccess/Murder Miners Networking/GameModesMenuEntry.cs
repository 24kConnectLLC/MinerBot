using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace SteamWebAPIAccess.Murder_Miners_Networking
{
    class GameModesMenuEntry
    {

        //protected override void setText()
        //{
        //    string t = GetGameModeText(pNetSettings.Bools, 22); //22

        //    Text = "Mode: " + t;
        //}

        public static string GetGameModeText(ulong netSettingsBools, int bonusBools, int maxChars = 999999, uint version = GameVersions.Current)
        {
            string t = GetGameModeText(netSettingsBools, bonusBools, false, version);

            return t.Length > maxChars ?
                GetGameModeText(netSettingsBools, bonusBools, true, version) :
                t;
        }

        public static string GetGameModeText(ulong netSettingsBools, int bonusBools, bool abbreviate, uint version)
        {
            bool a = abbreviate;

            // Which bools this version has
            LobbyLayout layout = GameVersions.LayoutOf(version);

            bool isMission = (bonusBools & (1 << 0)) != 0;
            bool isInvasion = (bonusBools & (1 << 1)) != 0;
            bool isBTS = (bonusBools & (1 << 2)) != 0;

            string t = "";

            if (isMapMaking())
                t += a ? "MpMk " : "Creative "; // t += a ? "MpMk " : "Map Making "

            if (shouldShowTeamText())
                t += "Team ";

            if (isMurderMatch())
            {
                if (isFreePlay())
                    t += a ? "" : "Free Play ";
                else
                    t += "Murder Match ";
            }

            if (isOn(NetSettingType.boolIsFreeBuild))
                t += a ? "" : "Free-Build ";

            if (isMission)
                t += "Mission ";
            else if (isOn(NetSettingType.boolIsSpeedRunTimer))
                t += a ? "" : "Speedrun ";

            if (isInvasion)
                t += "Invasion ";

            if (isBTS)
                t += a ? "BTS " : "Build to Survive ";

            if (isOn(NetSettingType.boolIsZombienaut))
                t += "Zombienaut ";

            if (isOn(NetSettingType.boolIsZombies))
                t += "Horde ";

            // The official Christmas playlist also sets Infection, and the game calls it the Christmas Event
            if (isChristmasEvent())
                t += "Christmas Event ";
            else if (isOn(NetSettingType.boolIsInfection))
                t += "Infection ";

            if (isOn(NetSettingType.boolIsGamenight))
                t += a ? "Gamenight " : "[ Gamenight ] ";

            if (isOn(NetSettingType.boolIsCTF))
                t += a ? "MTF " : "Murder The Flag ";

            if (isOn(NetSettingType.boolIsMurderMeleeTournament))
                t += a ? "Melee " : "Murder Melee ";

            if (isOn(NetSettingType.boolIsEggHunt))
                t += a ? "EggHunt " : "Egg Hunt ";

            if (isOn(NetSettingType.boolIsOneInTheChamber))
                t += a ? "OneShot " : "One Shot, One Murder ";

            if (isOn(NetSettingType.boolIsCapturePoint))
                t += a ? "KOTH " : "King Of The Hill ";

            if (isOn(NetSettingType.boolIsMurderMining))
                t += a ? "Mining " : "Murder Mining ";

            if (isOn(NetSettingType.boolIsMurderBall))
                t += a ? "FTM " : "Follow The Mask ";

            if (isOn(NetSettingType.boolIsMurderGames))
            {
                if (isOn(NetSettingType.boolDeathWallEnabled))
                {

                    if (t.Length == 0)
                        t += "Murder ";
                    t += "Royale";
                }
                else
                {
                    t += "1-Life ";
                }
            }

            return t;

            bool shouldShowTeamText()
            {
                return
                    !isOn(NetSettingType.boolIsCTF) &&
                    !isOn(NetSettingType.boolIsFreeBuild) &&
                    isOn(NetSettingType.boolIsTeams);
            }

            bool isMapMaking()
            {
                return
                    !isOn(NetSettingType.boolIsFreeBuild) &&
                    isOn((NetSettingType.boolIsEditMode));
            }

            bool isOn(NetSettingType nst)
            {
                if (layout == LobbyLayout.Latest)
                    return GameSettings.GetBool(netSettingsBools, (int)nst - 1);

                if (layout == LobbyLayout.Update41Beta || layout == LobbyLayout.Update41BetaShortName)
                    return isOnInOlderBuild<NetSettingType3>(nst.ToString());

                if (layout == LobbyLayout.Update40)
                    return isOnInOlderBuild<NetSettingType2>(nst.ToString());

                // If there is a rename in the netsetting then we compensate over here
                if (nst == NetSettingType.boolIsZombienaut)
                    return isOnInOlderBuild<NetSettingType1>(NetSettingType1.boolIsMurdernaut.ToString());

                return isOnInOlderBuild<NetSettingType1>(nst.ToString());
            }

            // A netsetting that older build doesn't have yet is simply off
            bool isOnInOlderBuild<TNetSetting>(string netsettingName) where TNetSetting : struct, Enum
            {
                return Enum.TryParse(netsettingName, out TNetSetting olderNst) &&
                    GameSettings.GetBool(netSettingsBools, Convert.ToInt32(olderNst) - 1);
            }

            // Only Update 39 has this flag, and later builds use its bit for other settings
            bool isChristmasEvent()
            {
                return
                    GameVersions.HasChristmasFlag(version) &&
                    GameSettings.GetBool(netSettingsBools, (int)NetSettingType1.boolIsChristmasPlaylist - 1);
            }

            bool isMurderMatch()
            {
                return
                    !isOn(NetSettingType.boolIsCTF) &&
                    !isOn(NetSettingType.boolIsInfection) &&
                    !isChristmasEvent() &&
                    !isOn(NetSettingType.boolIsZombies) &&
                    !isOn(NetSettingType.boolIsMurderGames) &&
                    !isOn(NetSettingType.boolIsZombienaut) &&
                    !isOn(NetSettingType.boolIsFreeBuild) &&
                    !isOn(NetSettingType.boolIsEditMode) &&
                    !isOn(NetSettingType.boolIsSpeedRunTimer) &&
                    !isOn(NetSettingType.boolIsMurderMeleeTournament) &&
                    !isOn(NetSettingType.boolIsEggHunt) &&
                    !isOn(NetSettingType.boolIsOneInTheChamber) &&
                    !isOn(NetSettingType.boolIsCapturePoint) &&
                    !isOn(NetSettingType.boolIsMurderMining) &&
                    !isOn(NetSettingType.boolIsMurderBall) &&
                    !isMission &&
                    !isInvasion;
            }

            bool isFreePlay() // no game end conditions
            {
                return
                    !isOn(NetSettingType.boolIsMurderGames)
                    &&
                    !isOn(NetSettingType.boolHasScoreToWin)
                    &&
                    !isOn(NetSettingType.boolHasTimeLimit);
            }
        }
    }

}
