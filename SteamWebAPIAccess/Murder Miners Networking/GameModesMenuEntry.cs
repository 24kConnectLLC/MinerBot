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

        public static string GetGameModeText(ulong netSettingsBools, int maxChars = 999999, uint version = 90)
        {
            string t = GetGameModeText(netSettingsBools, false, version);

            return t.Length > maxChars ?
                GetGameModeText(netSettingsBools, true, version) :
                t;
        }

        public static string GetGameModeText(ulong netSettingsBools, bool abbreviate, uint version)
        {
            bool a = abbreviate;

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

            if (isOn(NetSettingType.boolIsSpeedRunTimer))
                t += a ? "" : "Speedrun ";

            if (isOn(NetSettingType.boolIsZombienaut))
                t += "Zombienaut ";

            if (isOn(NetSettingType.boolIsZombies))
                t += "Horde ";

            if (isOn(NetSettingType.boolIsInfection))
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
                t += a ? "OneShot " : "One In The Chamber ";

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
                if (version < 90)
                {
                    string netsettingName = nst.ToString();

                    // If there is a rename in the netsetting then we compensate over here
                    if (nst == NetSettingType.boolIsZombienaut)
                        netsettingName = NetSettingType1.boolIsMurdernaut.ToString();

                    try
                    {
                        bool netsetBoolVal = GameSettings.GetBool(netSettingsBools, (int)Enum.Parse<NetSettingType1>(nst.ToString()) - 1);
                        return netsetBoolVal;
                    }
                    catch 
                    {
                        Console.WriteLine($"Could not find net setting in previous version: {nst.ToString()} .\nPlease make sure to update your netsettings in GameModesMenuEntry.cs in Minerbot or in the NetSettings.cs game code.");
                        return false;
                    }
                }
                return GameSettings.GetBool(netSettingsBools, (int)nst - 1);
            }

            bool isMurderMatch()
            {
                return
                    !isOn(NetSettingType.boolIsCTF) &&
                    !isOn(NetSettingType.boolIsInfection) &&
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
                    !isOn(NetSettingType.boolIsMurderBall);
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
