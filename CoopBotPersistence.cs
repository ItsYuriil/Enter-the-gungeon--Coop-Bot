using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CoopBot
{
    // Per-run record written next to the game's own mid-game save: which character the bot was.
    [Serializable]
    public class BotRunRecord
    {
        public string MidGameSaveGuid;
        public BotCharacter Character;
    }

    // Files in the game's save folder. The bot's gear for the game's own save lives in that save (playerTwoData);
    // this file only keeps the loadout of a dismissed bot so it survives quitting the game.
    public static class BotPersistence
    {
        private static string LoadoutPath { get { return Path.Combine(SaveManager.SavePath, "coopbot_loadout.json"); } }
        private static string RunPath(string guid) { return Path.Combine(SaveManager.SavePath, "coopbot_run_" + guid + ".json"); }

        public static void WriteLoadout(BotSpawner.Loadout lo)
        {
            try { File.WriteAllText(LoadoutPath, JsonUtility.ToJson(lo)); }
            catch (Exception e) { Debug.LogWarning("Coop Bot: could not write loadout: " + e.Message); }
        }

        public static BotSpawner.Loadout ReadLoadout()
        {
            try
            {
                if (!File.Exists(LoadoutPath)) return null;
                return JsonUtility.FromJson<BotSpawner.Loadout>(File.ReadAllText(LoadoutPath));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Coop Bot: could not read loadout: " + e.Message);
                return null;
            }
        }

        public static void DeleteLoadout()
        {
            try { if (File.Exists(LoadoutPath)) File.Delete(LoadoutPath); }
            catch (Exception e) { Debug.LogWarning("Coop Bot: could not delete loadout: " + e.Message); }
        }

        public static void WriteRun(string guid, BotCharacter character)
        {
            try { File.WriteAllText(RunPath(guid), JsonUtility.ToJson(new BotRunRecord { MidGameSaveGuid = guid, Character = character })); }
            catch (Exception e) { Debug.LogWarning("Coop Bot: could not write run record: " + e.Message); }
        }

        public static BotRunRecord ReadRun(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            try
            {
                if (!File.Exists(RunPath(guid))) return null;
                return JsonUtility.FromJson<BotRunRecord>(File.ReadAllText(RunPath(guid)));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Coop Bot: could not read run record: " + e.Message);
                return null;
            }
        }
    }

    // Runs right after the game writes its own mid-game save (elevators, shortcuts, checkpoints).
    // The record uses the same guid, so the game's save and the bot record always pair up.
    [HarmonyLib.HarmonyPatch(typeof(GameManager), "DoMidgameSave")]
    public static class MidgameSaveHook
    {
        public static void Postfix()
        {
            var brain = CoopBotPlugin.Brain;
            if (brain == null || brain.Bot == null) return;
            string guid = GameStatsManager.Instance.midGameSaveGuid;
            if (string.IsNullOrEmpty(guid)) return;
            BotPersistence.WriteRun(guid, brain.Character);
        }
    }

    // Continue: the game rebuilds a co-op partner from the save. Swap that placeholder for the bot.
    [HarmonyLib.HarmonyPatch(typeof(GameManager), "GeneratePlayersFromMidGameSave")]
    public static class ContinueBotHook
    {
        public static void Postfix(MidGameSaveData loadedSave)
        {
            if (loadedSave == null || loadedSave.savedGameType != GameManager.GameType.COOP_2_PLAYER) return;
            // Co-op saves only come from this mod (the game has no second human here), so always put the bot back.
            // Without a record the default character is used.
            BotRunRecord record = BotPersistence.ReadRun(loadedSave.midGameSaveGuid);
            BotCharacter character = record != null ? record.Character : CoopBotPlugin.Character.Value;
            BotSpawner.AdoptForContinue(character);
        }
    }

    // A new run starts with a clean slate: the dismissed loadout belongs to the previous run.
    [HarmonyLib.HarmonyPatch(typeof(GameStatsManager), "BeginNewSession")]
    public static class NewRunHook
    {
        public static void Postfix()
        {
            BotSpawner.Saved = null;
            BotPersistence.DeleteLoadout();
        }
    }
}
