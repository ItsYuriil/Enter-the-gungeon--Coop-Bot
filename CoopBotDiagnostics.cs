using HarmonyLib;

namespace CoopBot
{
    // Writes every hit the bot takes to BepInEx's log, with its source, so damage can be traced to a cause.
    [HarmonyLib.HarmonyPatch(typeof(HealthHaver), "ApplyDamage")]
    public static class BotDamageLog
    {
        public static void Prefix(HealthHaver __instance, float damage, string sourceName, CoreDamageTypes damageTypes)
        {
            var brain = CoopBotPlugin.Brain;
            if (brain == null || brain.Bot == null || __instance != brain.Bot.healthHaver) return;
            string state = brain.Bot.IsDodgeRolling ? "rolling" : "standing";
            string vulnerable = __instance.IsVulnerable ? "hit" : "ignored";
            CoopBotPlugin.Log(string.Format(
                "Bot damage: {0:0.0} from '{1}' types={2} {3} ({4}) hp={5:0.0}",
                damage, sourceName, damageTypes, state, vulnerable, __instance.GetCurrentHealth()));
        }
    }
}
