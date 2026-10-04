using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace CoopBot
{
    [BepInPlugin(Guid, "Coop Bot", "1.0.0")]
    public class CoopBotPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.coopbot.etg";

        public static CoopBotPlugin Instance;
        public static ConfigEntry<KeyCode> OrderKey;
        public static ConfigEntry<bool> ShowHints;
        public static ConfigEntry<BotCharacter> Character;
        public static ConfigEntry<KeyCode> CharacterKey;
        public static ConfigEntry<KeyCode> DropKey;
        public static ConfigEntry<KeyCode> ItemKey;
        public static ConfigEntry<KeyCode> DropItemKey;
        public static ConfigEntry<float> CoverPreference;

        private string m_message = "";
        private float m_messageUntil;
        private GUIStyle m_style;

        public static CoopBotBrain Brain;

        private void Awake()
        {
            Instance = this;
            OrderKey = Config.Bind("Controls", "OrderKey", KeyCode.H,
                "H: summon the bot, then order it to open/interact with the thing under the cursor. Shift+H dismisses it.");
            Character = Config.Bind("Bot", "Character", BotCharacter.Cultist, "Which character the bot plays as.");
            CharacterKey = Config.Bind("Controls", "CycleCharacterKey", KeyCode.J, "Cycles the bot's character (respawns the bot if it is active).");
            DropKey = Config.Bind("Controls", "DropWeaponKey", KeyCode.N, "Makes the bot drop its equipped weapon.");
        ItemKey = Config.Bind("Controls", "UseItemKey", KeyCode.U, "Makes the bot use its active item.");
        DropItemKey = Config.Bind("Controls", "DropItemKey", KeyCode.B, "Makes the bot drop its active item.");
        CoverPreference = Config.Bind("Bot", "CoverPreference", 0.5f, new ConfigDescription("How much the bot prefers hiding behind cover (0 = never, 1 = a lot).", new AcceptableValueRange<float>(0f, 1f)));
        ShowHints = Config.Bind("UI", "ShowHints", true, "Show on-screen messages from the bot.");
            new Harmony(Guid).PatchAll(typeof(BotInputPatch)); new Harmony(Guid + ".doors").PatchAll(typeof(DoorDistancePatch)); new Harmony(Guid + ".cam").PatchAll(typeof(BotCameraPatch)); new Harmony(Guid + ".die").PatchAll(typeof(PrimaryDeathPatch)); new Harmony(Guid + ".clear").PatchAll(typeof(ClearGameDataPatch)); new Harmony(Guid + ".clear2").PatchAll(typeof(LoadSelectPatch));
            Logger.LogInfo("Coop Bot loaded. Press " + OrderKey.Value + " in a run.");
        }

        public static void Say(string text)
        {
            if (Instance == null) return;
            Instance.Logger.LogInfo(text);
            Instance.m_message = text;
            Instance.m_messageUntil = Time.realtimeSinceStartup + 3.5f;
        }

        private void Update()
        {
            if (!GameManager.HasInstance) return;
            var gm = GameManager.Instance;

            if (gm.IsFoyer) BotSpawner.Saved = null;

            if (Brain != null && (Brain.Bot == null || gm.IsFoyer || gm.PrimaryPlayer == null))
            {
                BotSpawner.Dismiss(false);
            }

            if (Input.GetKeyDown(CharacterKey.Value) && !gm.IsPaused && !gm.IsLoadingLevel)
            {
                if (Brain != null)
                {
                    Say("Cannot change character while active.");
                    return;
                }
                Character.Value = (BotCharacter)(((int)Character.Value + 1) % Enum.GetValues(typeof(BotCharacter)).Length);
                Say("Bot character: " + Character.Value);
                return;
            }
            if (Input.GetKeyDown(ItemKey.Value) && Brain != null && !gm.IsPaused && !gm.IsLoadingLevel)
            {
                Brain.UseItemNow();
                return;
            }
            if (Input.GetKeyDown(DropItemKey.Value) && Brain != null && !gm.IsPaused && !gm.IsLoadingLevel)
            {
                Brain.DropItemNow();
                return;
            }
            if (Input.GetKeyDown(DropKey.Value) && Brain != null && !gm.IsPaused && !gm.IsLoadingLevel)
            {
                Brain.DropNow();
                return;
            }
            if (!Input.GetKeyDown(OrderKey.Value)) return;
            if (gm.IsPaused || gm.IsLoadingLevel || gm.PrimaryPlayer == null) return;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (Brain == null)
            {
                if (gm.IsFoyer)
                {
                    Say("Start a run first, then press H to call your bot.");
                    return;
                }
                BotSpawner.Spawn();
            }
            else if (shift)
            {
                BotSpawner.Dismiss(true);
            }
            else
            {
                Brain.GiveOrder(CursorWorld());
            }
        }

        private static Vector2 CursorWorld()
        {
            var prim = GameManager.Instance.PrimaryPlayer;
            var cam = GameManager.Instance.MainCameraController;
            if (cam == null || !BraveInput.GetInstanceForPlayer(0).IsKeyboardAndMouse())
            {
                return prim.CenterPosition;
            }
            var camera = cam.GetComponent<Camera>();
            Vector3 p = camera.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, 0f));
            return new Vector2(p.x, p.y);
        }

        private bool m_menuOpen;
        private string m_rebinding;
        private Vector2 m_menuScroll;

        private void DrawBotMenu()
        {
            var gm = GameManager.Instance;
            if (!GameManager.HasInstance || !gm.IsPaused) { m_menuOpen = false; m_rebinding = null; return; }
            if (GUI.Button(new Rect(12, 12, 150, 34), m_menuOpen ? "Close Bot Menu" : "Coop Bot Menu")) { m_menuOpen = !m_menuOpen; m_rebinding = null; }
            if (!m_menuOpen) return;

            var entries = new KeyValuePair<string, ConfigEntry<KeyCode>>[]
            {
                new KeyValuePair<string, ConfigEntry<KeyCode>>("Summon / Order  (Shift+key dismisses)", OrderKey),
                new KeyValuePair<string, ConfigEntry<KeyCode>>("Cycle character", CharacterKey),
                new KeyValuePair<string, ConfigEntry<KeyCode>>("Drop weapon", DropKey),
                new KeyValuePair<string, ConfigEntry<KeyCode>>("Use active item", ItemKey),
                new KeyValuePair<string, ConfigEntry<KeyCode>>("Drop active item", DropItemKey),
            };
            Event e = Event.current;
            if (m_rebinding != null && e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
            {
                if (e.keyCode != KeyCode.Escape)
                {
                    foreach (var kv in entries) if (kv.Key == m_rebinding) kv.Value.Value = e.keyCode;
                }
                m_rebinding = null;
                e.Use();
            }
            GUI.Box(new Rect(12, 52, 420, 80 + entries.Length * 38), "Coop Bot - Key bindings");
            for (int i = 0; i < entries.Length; i++)
            {
                float y = 82 + i * 38;
                GUI.Label(new Rect(24, y, 250, 30), entries[i].Key);
                string label = m_rebinding == entries[i].Key ? "Press a key..." : entries[i].Value.Value.ToString();
                if (GUI.Button(new Rect(290, y, 130, 30), label)) m_rebinding = entries[i].Key;
            }
            GUI.Label(new Rect(24, 86 + entries.Length * 38, 400, 24), "Click a key, then press the new one (Esc cancels).");
            GUI.Label(new Rect(24, 110 + entries.Length * 38, 250, 24), "Cover preference: " + CoverPreference.Value.ToString("0.00"));
            CoverPreference.Value = GUI.HorizontalSlider(new Rect(220, 116 + entries.Length * 38, 190, 20), CoverPreference.Value, 0f, 1f);
        }

        private void OnGUI()
        {
            DrawBotMenu();
            if (!ShowHints.Value || Time.realtimeSinceStartup > m_messageUntil) return;
            if (m_style == null)
            {
                m_style = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.UpperCenter, fontStyle = FontStyle.Bold };
            }
            var rect = new Rect(0, Screen.height * 0.12f, Screen.width, 50);
            GUI.color = Color.black;
            GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), m_message, m_style);
            GUI.color = new Color(1f, 0.9f, 0.4f);
            GUI.Label(rect, m_message, m_style);
        }
    }

    public enum BotCharacter { Cultist, Convict, Marine, Pilot, Robot, Hunter, Bullet, Paradox, Gunslinger }

    public static class BotSpawner
    {
        public static string PrefabName(BotCharacter c)
        {
            switch (c)
            {
                case BotCharacter.Convict: return "PlayerConvict";
                case BotCharacter.Marine: return "PlayerMarine";
                case BotCharacter.Pilot: return "PlayerRogue";
                case BotCharacter.Robot: return "PlayerRobot";
                case BotCharacter.Hunter: return "PlayerGuide";
                case BotCharacter.Bullet: return "PlayerBullet";
                case BotCharacter.Paradox: return "PlayerEevee";
                case BotCharacter.Gunslinger: return "PlayerGunslinger";
                default: return "PlayerCoopCultist";
            }
        }

        public static void Spawn()
        {
            var gm = GameManager.Instance;
            var prim = gm.PrimaryPlayer;

            gm.CurrentGameType = GameManager.GameType.COOP_2_PLAYER;
            prim.ReinitializeMovementRestrictors();

            var prefab = (GameObject)BraveResources.Load(BotSpawner.PrefabName(CoopBotPlugin.Character.Value));
            var go = UnityEngine.Object.Instantiate(prefab, prim.transform.position, Quaternion.identity);
            go.SetActive(true);
            var bot = go.GetComponent<PlayerController>();
            bot.ActorName = "Player ID 1";
            bot.PlayerIDX = 1;
            gm.SecondaryPlayer = bot;
            gm.RefreshAllPlayers();

            var brain = go.AddComponent<CoopBotBrain>();
            brain.Bot = bot;
            CoopBotPlugin.Brain = brain;
            gm.StartCoroutine(FinishSpawn(bot));
            CoopBotPlugin.Say("Bot online. H: order it to interact with what you point at. Shift+H: dismiss.");
        }

        private static System.Collections.IEnumerator FinishSpawn(PlayerController bot)
        {
            yield return null;
            if (bot == null) yield break;
            var gm = GameManager.Instance;
            GameUIRoot.Instance.ConvertCoreUIToCoopMode();
            gm.MainCameraController.ClearPlayerCache();
            PhysicsEngine.Instance.RegisterOverlappingGhostCollisionExceptions(bot.specRigidbody);
            BraveInput.ReassignAllControllers(null);
            bot.ReuniteWithOtherPlayer(gm.PrimaryPlayer, false);
            // Let the character finish giving its starting kit before restoring the saved one.
            float until = Time.realtimeSinceStartup + 0.6f;
            while (Time.realtimeSinceStartup < until) yield return null;
            if (bot != null && Saved != null) Restore(bot, Saved);
        }

        public class Loadout
        {
            public List<int> Guns = new List<int>();
            public List<int> GunAmmo = new List<int>();
            public int CurrentGunIndex = -1;
            public List<int> Passives = new List<int>();
            public List<int> Actives = new List<int>();
            public float Health, Armor;
            public int Blanks;
            public BotCharacter Character;
            public bool CurrentWasStarting;
        }

        public static Loadout Saved;

        private static void Save(PlayerController bot)
        {
            var lo = new Loadout();
            var guns = bot.inventory.AllGuns;
            for (int i = 0; i < guns.Count; i++)
            {
                if (CoopBotBrain.IsStartingGun(bot, guns[i])) continue;
                if (guns[i] == bot.CurrentGun) lo.CurrentGunIndex = lo.Guns.Count;
                lo.Guns.Add(guns[i].PickupObjectId);
                lo.GunAmmo.Add(guns[i].CurrentAmmo);
            }
            foreach (var p in bot.passiveItems) if (!bot.startingPassiveItemIds.Contains(p.PickupObjectId)) lo.Passives.Add(p.PickupObjectId);
            foreach (var a in bot.activeItems) if (!bot.startingActiveItemIds.Contains(a.PickupObjectId)) lo.Actives.Add(a.PickupObjectId);
            lo.Health = bot.healthHaver.GetCurrentHealth();
            lo.Armor = bot.healthHaver.Armor;
            lo.Blanks = bot.Blanks;
            lo.Character = CoopBotPlugin.Character.Value;
            Saved = lo;
        }

        private static void Restore(PlayerController bot, Loadout lo)
        {
            try
            {
                EncounterTrackable.SuppressNextNotification = true;
                foreach (int id in lo.Passives)
                {
                    int have = 0, want = 0;
                    foreach (var p in bot.passiveItems) if (p.PickupObjectId == id) have++;
                    foreach (int w in lo.Passives) if (w == id) want++;
                    if (have >= want) continue;
                    var item = PickupObjectDatabase.GetById(id) as PassiveItem;
                    if (item != null) LootEngine.TryGivePrefabToPlayer(item.gameObject, bot, true);
                }
                foreach (int id in lo.Actives)
                {
                    bool has = false;
                    foreach (var a in bot.activeItems) if (a.PickupObjectId == id) has = true;
                    if (has) continue;
                    var item = PickupObjectDatabase.GetById(id) as PlayerItem;
                    if (item != null) LootEngine.TryGivePrefabToPlayer(item.gameObject, bot, true);
                }
                for (int i = 0; i < lo.Guns.Count; i++)
                {
                    Gun g = null;
                    foreach (var have in bot.inventory.AllGuns) if (have.PickupObjectId == lo.Guns[i]) g = have;
                    if (g == null)
                    {
                        var proto = PickupObjectDatabase.GetById(lo.Guns[i]) as Gun;
                        if (proto != null) g = bot.inventory.AddGunToInventory(proto, false);
                    }
                    if (g != null) g.CurrentAmmo = lo.GunAmmo[i];
                }
                var extras = new List<Gun>();
                
                if (lo.CurrentGunIndex >= 0 && lo.CurrentGunIndex < lo.Guns.Count)
                {
                    foreach (var have in bot.inventory.AllGuns)
                    {
                        if (have.PickupObjectId == lo.Guns[lo.CurrentGunIndex] && have != bot.CurrentGun)
                        {
                            int idx = bot.inventory.AllGuns.IndexOf(have), cur = bot.inventory.AllGuns.IndexOf(bot.CurrentGun);
                            if (idx >= 0 && cur >= 0) bot.inventory.ChangeGun(idx - cur);
                            break;
                        }
                    }
                }
                if (lo.Character == CoopBotPlugin.Character.Value)
                {
                    bot.healthHaver.ForceSetCurrentHealth(Mathf.Min(lo.Health, bot.healthHaver.GetMaxHealth()));
                    bot.healthHaver.Armor = lo.Armor;
                }
                bot.Blanks = lo.Blanks;
            }
            catch (Exception e)
            {
                Debug.LogWarning("Restore loadout failed: " + e);
            }
            finally { EncounterTrackable.SuppressNextNotification = false; }
        }

        public static void Dismiss(bool announce)
        {
            var gm = GameManager.Instance;
            var brain = CoopBotPlugin.Brain;
            CoopBotPlugin.Brain = null;
            if (brain != null && brain.Bot != null)
            {
                if (!gm.IsFoyer && !brain.Bot.healthHaver.IsDead && !brain.Bot.IsGhost)
                {
                    try { Save(brain.Bot); } catch (Exception) { }
                }
                brain.Bot.SetInputOverride("coopbot dismissed");
                UnityEngine.Object.Destroy(brain.Bot.gameObject);
            }
            gm.SecondaryPlayer = null;
            gm.RefreshAllPlayers();
            gm.CurrentGameType = GameManager.GameType.SINGLE_PLAYER;
            if (gm.PrimaryPlayer != null) gm.PrimaryPlayer.ReinitializeMovementRestrictors();
            if (GameUIRoot.Instance != null && GameUIRoot.Instance.heartControllers != null && GameUIRoot.Instance.heartControllers.Count > 1)
            {
                GameUIRoot.Instance.heartControllers[1].GetComponent<dfPanel>().IsVisible = false;
                GameUIRoot.Instance.blankControllers[1].GetComponent<dfPanel>().IsVisible = false;
                GameUIRoot.Instance.ammoControllers[1].GetComponent<dfPanel>().IsVisible = false;
            }
            if (gm.MainCameraController != null) gm.MainCameraController.ClearPlayerCache();
            BraveInput.ReassignAllControllers(null);
            if (announce) CoopBotPlugin.Say("Bot dismissed.");
        }
    }

    // Replaces the bot's InControl update so the brain, not a device, drives its actions.
    [HarmonyPatch(typeof(PlayerActionSet), "Update", new[] { typeof(ulong), typeof(float) })]
    public static class BotInputPatch
    {
        private static readonly FieldInfo ActionsField = AccessTools.Field(typeof(PlayerActionSet), "actions");
        private static readonly FieldInfo TwoAxisField = AccessTools.Field(typeof(PlayerActionSet), "twoAxisActions");
        private static readonly FieldInfo OneAxisField = AccessTools.Field(typeof(PlayerActionSet), "oneAxisActions");
        private static readonly MethodInfo TwoAxisUpdate = AccessTools.Method(typeof(PlayerTwoAxisAction), "Update");
        private static readonly MethodInfo OneAxisUpdate = AccessTools.Method(typeof(PlayerOneAxisAction), "Update");

        public static bool Prefix(PlayerActionSet __instance, ulong updateTick, float deltaTime)
        {
            var brain = CoopBotPlugin.Brain;
            if (brain == null || brain.Bot == null || !BraveInput.HasInstanceForPlayer(1)) return true;
            if (!ReferenceEquals(BraveInput.GetInstanceForPlayer(1).ActiveActions, __instance)) return true;

            var set = (GungeonActions)__instance;
            set.ForceDisable = false;

            var desired = new Dictionary<PlayerAction, float>();
            Vector2 m = brain.Move;
            desired[set.Left] = Mathf.Max(0f, -m.x);
            desired[set.Right] = Mathf.Max(0f, m.x);
            desired[set.Down] = Mathf.Max(0f, -m.y);
            desired[set.Up] = Mathf.Max(0f, m.y);
            desired[set.ShootAction] = brain.Shoot ? 1f : 0f;
            desired[set.DodgeRollAction] = brain.ConsumeDodge() ? 1f : 0f;
            desired[set.UseItemAction] = brain.ConsumeUseItem() ? 1f : 0f;
            desired[set.BlankAction] = brain.ConsumeBlank() ? 1f : 0f;
            desired[set.InteractAction] = brain.ConsumeInteract() ? 1f : 0f;
            desired[set.ReloadAction] = brain.ConsumeReload() ? 1f : 0f;
            desired[set.GunDownAction] = brain.ConsumeSwapGun() ? 1f : 0f;

            var actions = (List<PlayerAction>)ActionsField.GetValue(__instance);
            for (int i = 0; i < actions.Count; i++)
            {
                float v;
                desired.TryGetValue(actions[i], out v);
                actions[i].CommitWithValue(v, updateTick, deltaTime);
            }
            foreach (var a in (List<PlayerOneAxisAction>)OneAxisField.GetValue(__instance))
                OneAxisUpdate.Invoke(a, new object[] { updateTick, deltaTime });
            foreach (var a in (List<PlayerTwoAxisAction>)TwoAxisField.GetValue(__instance))
                TwoAxisUpdate.Invoke(a, new object[] { updateTick, deltaTime });
            return false;
        }
    }
}

namespace CoopBot
{
    // Keeps the camera on the player alone so the bot's movement never drags the view around.
    [HarmonyLib.HarmonyPatch(typeof(PlayerController), "get_IgnoredByCamera")]
    public static class BotCameraPatch
    {
        public static void Postfix(PlayerController __instance, ref bool __result)
        {
            var brain = CoopBotPlugin.Brain;
            if (brain == null || brain.Bot == null) return;
            var prim = GameManager.Instance.PrimaryPlayer;
            bool primDown = prim != null && (prim.IsGhost || prim.healthHaver.IsDead);
            bool botDown = brain.Bot.IsGhost || brain.Bot.healthHaver.IsDead;
            // Follow the bot when the player is down; otherwise follow the player only.
            if (primDown && !botDown) __result = (__instance == prim);
            else if (brain.Bot == __instance) __result = true;
        }
    }

    // Co-op doors require every living player within 0.3 of the door; the bot never counts.
    [HarmonyLib.HarmonyPatch(typeof(DungeonDoorController), "GetDistanceToPlayer")]
    public static class DoorDistancePatch
    {
        public static void Postfix(SpeculativeRigidbody playerRigidbody, ref float __result)
        {
            var brain = CoopBotPlugin.Brain;
            if (brain != null && brain.Bot != null && playerRigidbody == brain.Bot.specRigidbody) __result = 0f;
        }
    }
}



namespace CoopBot
{
    // Outside boss rooms the primary player's death ends the run like single player.
    [HarmonyLib.HarmonyPatch(typeof(PlayerController), "Die")]
    public static class PrimaryDeathPatch
    {
        public static void Prefix(PlayerController __instance)
        {
            var brain = CoopBotPlugin.Brain;
            if (brain == null || __instance == null || __instance == brain.Bot) return;
            var room = __instance.CurrentRoom;
            bool boss = room != null && room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS;
            if (!boss) BotSpawner.Dismiss(false);
        }
    }

    // Never carry the bot across a run reset / return to the menu.
    [HarmonyLib.HarmonyPatch(typeof(GameManager), "ClearActiveGameData")]
    public static class ClearGameDataPatch
    {
        public static void Prefix() { if (CoopBotPlugin.Brain != null) BotSpawner.Dismiss(false); }
    }

    [HarmonyLib.HarmonyPatch(typeof(GameManager), "LoadCharacterSelect")]
    public static class LoadSelectPatch
    {
        public static void Prefix() { if (CoopBotPlugin.Brain != null) BotSpawner.Dismiss(false); }
    }
}
