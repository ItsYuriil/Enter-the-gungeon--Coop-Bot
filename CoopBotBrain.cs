using System.Collections.Generic;
using Dungeonator;
using Pathfinding;
using UnityEngine;

namespace CoopBot
{
    public class CoopBotBrain : MonoBehaviour
    {
        private enum OrderKind { None, Interact, Break, Regroup }

        public PlayerController Bot;

        // Outputs read by BotInputPatch each input tick.
        public Vector2 Move;
        public bool Shoot;
        private bool m_dodge;
        private bool m_reload;
        private int m_interactTicks;
        private int m_itemTicks;
        private float m_nextItemTime;
        public bool ConsumeUseItem()
        {
            if (m_itemTicks <= 0) return false;
            m_itemTicks--;
            return true;
        }
        private int m_swapTicks;
        private float m_nextSwapTime;
        public bool ConsumeSwapGun()
        {
            if (m_swapTicks <= 0) return false;
            m_swapTicks--;
            return m_swapTicks > 0;
        }

        public bool ConsumeDodge() { bool v = m_dodge; m_dodge = false; return v; }
        public bool ConsumeReload() { bool v = m_reload; m_reload = false; return v; }
        public bool ConsumeInteract()
        {
            if (m_interactTicks <= 0) return false;
            m_interactTicks--;
            return true;
        }

        // Order state
        private OrderKind m_order = OrderKind.None;
        private Component m_orderTarget;
        private IPlayerInteractable m_orderInteractable;
        private Vector2 m_orderCenter;
        private float m_orderExpires;
        private float m_lastInteractTime = -10f;
        private int m_interactAttempts;
        private int m_dropState; // 0 = not yet, 1 = walking to a drop spot, 2 = done
        private Vector2 m_dropSpot;
        private float m_dropDeadline;

        // Navigation state
        private List<Vector2> m_waypoints = new List<Vector2>();
        private Vector2 m_pathGoal = new Vector2(-999f, -999f);
        private float m_nextPathTime;
        private float m_strafeSign = 1f;
        private float m_nextStrafeFlip;
        private float m_dodgeReadyTime;
        private float m_stuckTimer;
        private Vector2 m_lastPos;
        private float m_farTimer;
        private Vector2 m_facing = Vector2.right;

        private const float FollowStart = 4.5f;
        private const float FollowStop = 2.5f;
        private const float Leash = 16f;
        private const float EngageRange = 18f;

        private static int TileMask()
        {
            return CollisionMask.LayerToMask(CollisionLayer.HighObstacle, CollisionLayer.BulletBlocker);
        }

        private static bool TilesBlock(Vector2 from, Vector2 to)
        {
            Vector2 d = to - from;
            float dist = d.magnitude;
            if (dist < 0.01f) return false;
            RaycastResult hit;
            bool blocked = PhysicsEngine.Instance.Raycast(from, d, dist, out hit, true, false, TileMask(), null, false, null, null);
            if (blocked) RaycastResult.Pool.Free(ref hit);
            return blocked;
        }

        private static bool MoveBlocked(Vector2 from, Vector2 to, float width = 0.32f)
        {
            Vector2 d = to - from;
            float dist = d.magnitude;
            if (dist < 0.01f) return false;
            Vector2 side = new Vector2(-d.y, d.x).normalized * width;
            int mask = CollisionMask.LayerToMask(CollisionLayer.HighObstacle, CollisionLayer.LowObstacle, CollisionLayer.BulletBlocker);
            SpeculativeRigidbody self = CoopBotPlugin.Brain != null && CoopBotPlugin.Brain.Bot != null ? CoopBotPlugin.Brain.Bot.specRigidbody : null;
            for (int i = -1; i <= 1; i++)
            {
                RaycastResult hit;
                bool b = PhysicsEngine.Instance.Raycast(from + side * i, d, dist, out hit, true, true, mask, null, false, null, self);
                if (b) { RaycastResult.Pool.Free(ref hit); return true; }
            }
            return false;
        }

        private void Update()
        {
            Move = Vector2.zero;
            Shoot = false;
            var gm = GameManager.Instance;
            var prim = gm.PrimaryPlayer;
            if (Bot == null || prim == null || gm.IsLoadingLevel || gm.IsPaused) return;
            if (Bot.IsGhost || Bot.healthHaver.IsDead) { GhostUpdate(prim); return; }
            if (Bot.CurrentInputState != PlayerInputState.AllInput) return;

            Vector2 pos = Bot.specRigidbody.UnitCenter;
            Vector2 primPos = prim.specRigidbody.UnitCenter;
            RoomHandler room = Bot.CurrentRoom;

            if (Time.time > m_orderExpires && m_order != OrderKind.None) EndOrder("Order timed out.");

            // Teleport back if we are lost or very far from the player.
            float distToPrim = Vector2.Distance(pos, primPos);
            bool sameRoom = room != null && room == prim.CurrentRoom;
            bool botBusy = Bot.IsInCombat && room != null && room.HasActiveEnemies(RoomHandler.ActiveEnemyType.All);
            if (distToPrim > 40f || (!sameRoom && !botBusy && !prim.IsInCombat && distToPrim > 22f))
            {
                m_farTimer += Time.deltaTime;
                if (m_farTimer > 2.5f)
                {
                    Bot.ReuniteWithOtherPlayer(prim, true);
                    m_farTimer = 0f;
                    return;
                }
            }
            else m_farTimer = 0f;

            AIActor enemy = PickEnemy(room, pos);
            Vector2? aim = null;

            // Elevator: every living player must stand inside it, so glue the bot to the player.
            if (enemy == null && ElevatorNearPlayer(primPos))
            {
                if (m_order != OrderKind.None) EndOrder("Bot: heading to the elevator.");
                if (distToPrim > 0.35f)
                {
                    MoveToward(pos, primPos, 0.3f);
                    if (Move.sqrMagnitude < 0.01f) Move = (primPos - pos).normalized;
                    if (distToPrim < 1.5f) Move *= Mathf.Clamp(distToPrim, 0.3f, 1f);
                }
                if (distToPrim > 8f) { m_farTimer += Time.deltaTime; if (m_farTimer > 2f) { Bot.ReuniteWithOtherPlayer(prim, true); m_farTimer = 0f; } }
                Bot.forceAimPoint = pos + m_facing * 5f;
                if (Move.sqrMagnitude > 0.01f) m_facing = Move.normalized;
                UnstickCheck(pos);
                return;
            }

            if (m_order == OrderKind.Interact)
            {
                RunInteractOrder(pos, ref aim);
            }
            else if (m_order == OrderKind.Break)
            {
                RunBreakOrder(pos, ref aim);
            }
            else if (m_order == OrderKind.Regroup)
            {
                if (distToPrim < FollowStop + 0.5f) EndOrder("Regrouped.");
                else MoveToward(pos, primPos, 1.5f);
            }

            bool orderMoves = m_order != OrderKind.None;
            AutoUseItem(room, pos, enemy);
            if (enemy != null)
            {
                Vector2 ep = enemy.specRigidbody.UnitCenter;
                float d = Vector2.Distance(pos, ep);
                bool los = !TilesBlock(pos, ep);
                if (d < EngageRange && los)
                {
                    Vector2 lead = enemy.specRigidbody.Velocity * Mathf.Clamp(d / 18f, 0f, 0.6f);
                    aim = ep + lead + AimError(d);
                    Shoot = true;
                }
                if (!orderMoves && !CoverMove(room, pos, ep, d, los)) CombatMove(pos, ep, d, los, primPos, distToPrim);
            }
            else if (!orderMoves)
            {
                FollowMove(pos, primPos, distToPrim);
            }

            AvoidProjectiles(pos);
            PitGuard(pos);
            HazardGuard(pos);
            DangerZoneGuard(pos);

            if (Move.sqrMagnitude > 0.01f) m_facing = Move.normalized;
            Bot.forceAimPoint = aim ?? (pos + m_facing * 5f);

            HandleReloadAndAmmo(enemy != null);
            ChargeCycle();

            UnstickCheck(pos);
        }

        private ElevatorDepartureController m_elevator;
        private float m_nextElevatorScan;

        private bool ElevatorNearPlayer(Vector2 primPos)
        {
            if (Time.time > m_nextElevatorScan)
            {
                m_nextElevatorScan = Time.time + 1.5f;
                m_elevator = Object.FindObjectOfType<ElevatorDepartureController>();
            }
            if (m_elevator == null || !m_elevator.gameObject.activeInHierarchy) return false;
            return Vector2.Distance(m_elevator.transform.position.XY(), primPos) < 9f;
        }

        // Ghost: stay close to the player and spend the ghost blank on incoming bullets until revived.
        private int m_ghostFrame;
        private void GhostUpdate(PlayerController prim)
        {
            if (Bot == null || Bot.specRigidbody == null) return;
            Vector2 pos = Bot.specRigidbody.UnitCenter;
            Vector2 primPos = prim.specRigidbody.UnitCenter;
            float d = Vector2.Distance(pos, primPos);
            if (d > 2.5f)
            {
                Move = (primPos - pos).normalized * Mathf.Min(1f, (d - 2f) / 2f);
            }
            if (d > 30f) Bot.ReuniteWithOtherPlayer(prim, true);

            bool threat = false;
            var projectiles = StaticReferenceManager.AllProjectiles;
            for (int i = 0; i < projectiles.Count && !threat; i++)
            {
                Projectile p = projectiles[i];
                if (p == null || p.specRigidbody == null || !p.collidesWithPlayer || p.Owner is PlayerController) continue;
                Vector2 bp = p.specRigidbody.UnitCenter;
                Vector2 v = p.specRigidbody.Velocity;
                if (v.sqrMagnitude < 0.25f) continue;
                Vector2 r = primPos - bp;
                float t = Mathf.Clamp(Vector2.Dot(r, v) / v.sqrMagnitude, 0f, 0.8f);
                if ((r - v * t).magnitude < 1.4f) threat = true;
            }
            // The ghost blank triggers on a fresh press, so alternate the button every frame.
            m_ghostFrame++;
            Shoot = threat && (m_ghostFrame % 2 == 0);
            Bot.forceAimPoint = pos + Vector2.right;
        }

        private void HandleReloadAndAmmo(bool hasEnemy)
        {
            Gun g = Bot.CurrentGun;
            if (g == null || g.IsReloading || Bot.IsDodgeRolling) return;
            if (g.InfiniteAmmo || g.CurrentAmmo > 0)
            {
                int cap = g.ClipCapacity;
                int left = g.ClipShotsRemaining;
                if (cap <= 0 || cap > 200) return;
                if (!g.InfiniteAmmo && g.CurrentAmmo <= left) return;
                // Empty: always. Low: whenever not actively firing. Idle: top off.
                if (left <= 0 || (!Shoot && left < cap * 0.6f) || (!hasEnemy && left < cap))
                {
                    m_reload = true;
                }
            }
            else if (Time.time > m_nextSwapTime && Bot.inventory != null && Bot.inventory.AllGuns.Count > 1)
            {
                // Out of ammo: drop the empty weapon (never a starting one) and let the inventory move on.
                if (CanDropHeld())
                {
                    try { Bot.ForceDropGun(g); } catch (System.Exception) { }
                }
                else
                {
                    m_swapTicks = 3;
                }
                m_nextSwapTime = Time.time + 1.0f;
            }
        }

        // ---- Targeting ----

        private AIActor m_lockedEnemy;

        private AIActor PickEnemy(RoomHandler room, Vector2 pos)
        {
            if (m_lockedEnemy != null)
            {
                var le = m_lockedEnemy;
                if (le.healthHaver == null || le.healthHaver.IsDead || !le.isActiveAndEnabled || le.specRigidbody == null) m_lockedEnemy = null;
                else return le;
            }
            if (room == null) return null;
            List<AIActor> list = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
            if (list == null) return null;
            AIActor best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                AIActor e = list[i];
                if (e == null || e.healthHaver == null || e.healthHaver.IsDead || e.IsHarmlessEnemy) continue;
                if (e.CompanionOwner != null || !e.isActiveAndEnabled || e.specRigidbody == null) continue;
                Vector2 ep = e.specRigidbody.UnitCenter;
                float d = Vector2.Distance(pos, ep);
                float score = d + (TilesBlock(pos, ep) ? 12f : 0f);
                if (score < bestScore) { bestScore = score; best = e; }
            }
            return best;
        }

        // ---- Active items ----

        public void DropItemNow()
        {
            if (Bot == null || Bot.healthHaver.IsDead || Bot.IsGhost) return;
            var it = Bot.CurrentItem;
            if (it == null) { CoopBotPlugin.Say("Bot: no active item."); return; }
            try { Bot.DropActiveItem(it); CoopBotPlugin.Say("Bot: dropped its item."); }
            catch (System.Exception) { }
        }


        public void UseItemNow()
        {
            if (Bot == null || Bot.healthHaver.IsDead || Bot.IsGhost) return;
            var it = Bot.CurrentItem;
            if (it == null) { CoopBotPlugin.Say("Bot: no active item."); return; }
            if (it.IsOnCooldown || !it.CanBeUsed(Bot)) { CoopBotPlugin.Say("Bot: item not ready."); return; }
            m_itemTicks = 2;
            CoopBotPlugin.Say("Bot: using its item.");
        }

        private void AutoUseItem(RoomHandler room, Vector2 pos, AIActor enemy)
        {
            if (enemy == null || Time.time < m_nextItemTime || m_itemTicks > 0 || Bot.IsDodgeRolling) return;
            var it = Bot.CurrentItem;
            if (it == null || it.IsCurrentlyActive || it.IsOnCooldown || !it.CanBeUsed(Bot)) return;
            int near = 0;
            var list = room != null ? room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All) : null;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (e == null || e.healthHaver == null || e.healthHaver.IsDead || e.specRigidbody == null) continue;
                    if (Vector2.Distance(pos, e.specRigidbody.UnitCenter) < 12f) near++;
                }
            }
            float hp = Bot.healthHaver.GetCurrentHealthPercentage();
            bool boss = enemy.healthHaver.IsBoss;
            if (near >= 3 || boss || (near >= 2 && hp < 0.7f) || hp < 0.4f)
            {
                m_itemTicks = 2;
                m_nextItemTime = Time.time + 2f;
            }
        }

        // ---- Cover ----

        private Vector2 m_coverSpot;
        private bool m_hasCover;
        private float m_nextCoverSearch;
        private readonly HashSet<FlippableCover> m_triedTables = new HashSet<FlippableCover>();
        private FlippableCover m_table;
        private float m_tableDeadline;

        private static bool CoverBlocks(Vector2 from, Vector2 to)
        {
            Vector2 d = to - from;
            float dist = d.magnitude;
            if (dist < 0.01f) return false;
            int mask = CollisionMask.LayerToMask(CollisionLayer.HighObstacle, CollisionLayer.LowObstacle, CollisionLayer.BulletBlocker);
            RaycastResult hit;
            bool b = PhysicsEngine.Instance.Raycast(from, d, dist, out hit, true, true, mask, null, false, null, null);
            if (b) RaycastResult.Pool.Free(ref hit);
            return b;
        }

        private float m_coverSince = -1f;
        private float m_coverCooldownUntil;
        private float m_lastHealth = -1f;
        private bool BulletsIncoming { get { return m_bullets.Count > 0; } }

        private bool WantsCover()
        {
            float hpNow = Bot.healthHaver.GetCurrentHealth() + Bot.healthHaver.Armor;
            bool hurt = m_lastHealth >= 0f && hpNow < m_lastHealth;
            m_lastHealth = hpNow;
            if (hurt && m_coverSince >= 0f) { m_coverSince = -1f; m_coverCooldownUntil = Time.time + 6f; }
            if (Time.time < m_coverCooldownUntil) return false;
            bool want = WantsCoverRaw();
            if (!want) { m_coverSince = -1f; return false; }
            if (m_coverSince < 0f) m_coverSince = Time.time;
            // Never stay hidden long: take cover briefly, then go after the enemies.
            if (Time.time - m_coverSince > 2.5f) { m_coverSince = -1f; m_coverCooldownUntil = Time.time + 5f; return false; }
            return true;
        }

        private bool WantsCoverRaw()
        {
            float pref = Mathf.Clamp01(CoopBotPlugin.CoverPreference.Value);
            Gun g = Bot.CurrentGun;
            if (g != null && g.IsReloading && pref > 0.05f) return true;
            if (Bot.healthHaver.GetCurrentHealthPercentage() < Mathf.Lerp(0.2f, 0.9f, pref)) return true;
            // Higher preference means a larger share of every 5 s cycle is spent behind cover.
            return (Time.time % 5f) / 5f < pref * 0.6f;
        }

        // Returns true when cover logic is driving movement this frame.
        private bool CoverMove(RoomHandler room, Vector2 pos, Vector2 ep, float d, bool los)
        {
            if (room == null) return false;
            bool hide = WantsCover();

            // Flip a nearby table early in a fight so there is real cover to use.
            if (m_table == null && d > 5f && CoopBotPlugin.CoverPreference.Value > 0.1f && Time.time > m_nextCoverSearch)
            {
                m_nextCoverSearch = Time.time + 1f;
                var list = room.GetRoomInteractables();
                float bestD = 8f;
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i] as FlippableCover;
                    if (t == null || t.IsFlipped || t.IsBroken || m_triedTables.Contains(t)) continue;
                    float td = Vector2.Distance(pos, t.specRigidbody.UnitCenter);
                    if (td < bestD) { bestD = td; m_table = t; m_tableDeadline = Time.time + 6f; }
                }
            }
            if (m_table != null)
            {
                if (m_table.IsFlipped || m_table.IsBroken || Time.time > m_tableDeadline) { m_triedTables.Add(m_table); m_table = null; }
                else
                {
                    // Stand on the player's side so the table flips toward the enemy.
                    Vector2 tp = m_table.specRigidbody.UnitCenter;
                    Vector2 side = (tp - ep).sqrMagnitude > 0.01f ? (tp - ep).normalized : Vector2.up;
                    Vector2 stand = tp - side * 1.1f;
                    if (m_table.GetDistanceToPoint(Bot.CenterPosition) < 0.85f)
                    {
                        m_interactTicks = 3;
                        m_triedTables.Add(m_table);
                        m_table = null;
                    }
                    else if (Vector2.Distance(pos, stand) > 0.5f && m_table.GetDistanceToPoint(Bot.CenterPosition) > 1.6f)
                    {
                        MoveToward(pos, stand, 0.3f);
                        return true;
                    }
                    else
                    {
                        MoveToward(pos, tp, 0.2f);
                        if (Move.sqrMagnitude < 0.01f) Move = (tp - pos).normalized;
                        return true;
                    }
                }
            }

            if (!hide) { m_hasCover = false; return false; }

            if (!m_hasCover || Time.time > m_nextCoverSearch)
            {
                m_nextCoverSearch = Time.time + 0.6f;
                m_hasCover = FindCover(room, pos, out m_coverSpot);
            }
            if (!m_hasCover) return false;
            if (BulletsIncoming && Vector2.Distance(pos, m_coverSpot) < 0.8f) return false; // let dodging and fighting take over
            if (!CoverBlocks(m_coverSpot, ep) && !los) { m_hasCover = false; return false; }
            if (Vector2.Distance(pos, m_coverSpot) > 0.45f)
            {
                MoveToward(pos, m_coverSpot, 0.3f);
                if (Move.sqrMagnitude < 0.01f) Move = (m_coverSpot - pos).normalized;
            }
            return true;
        }

        private bool FindCover(RoomHandler room, Vector2 pos, out Vector2 spot)
        {
            spot = pos;
            var enemies = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
            if (enemies == null) return false;
            var shooters = new List<Vector2>();
            for (int i = 0; i < enemies.Count && shooters.Count < 6; i++)
            {
                var e = enemies[i];
                if (e == null || e.healthHaver == null || e.healthHaver.IsDead || e.specRigidbody == null || e.IsHarmlessEnemy) continue;
                Vector2 p = e.specRigidbody.UnitCenter;
                if (Vector2.Distance(p, pos) < 20f) shooters.Add(p);
            }
            if (shooters.Count == 0) return false;
            var prim = GameManager.Instance.PrimaryPlayer;
            Vector2 primPos = prim.specRigidbody.UnitCenter;
            float best = float.MaxValue;
            bool found = false;
            for (int r = 0; r < 3; r++)
            {
                float rad = 2f + r * 2.2f;
                for (int k = 0; k < 16; k++)
                {
                    Vector2 c = pos + (Vector2)(Quaternion.Euler(0f, 0f, k * 22.5f) * Vector3.right) * rad;
                    if (Pit(c) || MoveBlocked(pos, c)) continue;
                    int hidden = 0;
                    float nearest = float.MaxValue;
                    for (int i = 0; i < shooters.Count; i++)
                    {
                        nearest = Mathf.Min(nearest, Vector2.Distance(c, shooters[i]));
                        if (CoverBlocks(c, shooters[i])) hidden++;
                    }
                    if (hidden < shooters.Count || nearest < 3.5f) continue;
                    float score = rad + Vector2.Distance(c, primPos) * 0.25f + Random.value * 0.5f;
                    if (score < best) { best = score; spot = c; found = true; }
                }
                if (found) break;
            }
            return found;
        }

        // ---- Movement helpers ----

        private void FollowMove(Vector2 pos, Vector2 primPos, float distToPrim)
        {
            if (distToPrim > FollowStart || (distToPrim > FollowStop && m_waypoints.Count > 0 && Move.sqrMagnitude > 0f))
            {
                MoveToward(pos, primPos, FollowStop);
            }
            else if (distToPrim < 1.2f)
            {
                // Step out of the player's way.
                Vector2 away = (pos - primPos);
                if (away.sqrMagnitude < 0.01f) away = Vector2.right;
                Move = away.normalized * 0.7f;
            }
        }

        private void CombatMove(Vector2 pos, Vector2 ep, float d, bool los, Vector2 primPos, float distToPrim)
        {
            if (distToPrim > Leash)
            {
                MoveToward(pos, primPos, FollowStop);
                return;
            }
            if (!los || d > 11f)
            {
                MoveToward(pos, ep, 8f);
                return;
            }
            Vector2 toEnemy = (ep - pos).normalized;
            if (d < 4.5f)
            {
                Vector2 dir = -toEnemy;
                if (MoveBlocked(pos, pos + dir * 1.5f)) dir = Perp(toEnemy) * m_strafeSign;
                Move = dir;
                return;
            }
            if (Time.time > m_nextStrafeFlip)
            {
                m_strafeSign = Random.value < 0.5f ? -1f : 1f;
                m_nextStrafeFlip = Time.time + Random.Range(1.2f, 2.8f);
            }
            Vector2 strafe = Perp(toEnemy) * m_strafeSign;
            if (MoveBlocked(pos, pos + strafe * 1.5f))
            {
                m_strafeSign = -m_strafeSign;
                strafe = -strafe;
            }
            Move = strafe * 0.8f;
        }

        private static Vector2 Perp(Vector2 v) { return new Vector2(-v.y, v.x); }

        private void MoveToward(Vector2 pos, Vector2 goal, float stopDistance)
        {
            if (Vector2.Distance(pos, goal) <= stopDistance) { m_waypoints.Clear(); return; }

            bool direct = !MoveBlocked(pos, goal, 0.45f) && Vector2.Distance(pos, goal) < 6f;
            if (direct)
            {
                m_waypoints.Clear();
                Move = (goal - pos).normalized;
                return;
            }

            if (Time.time > m_nextPathTime || (goal - m_pathGoal).sqrMagnitude > 4f || m_waypoints.Count == 0)
            {
                RecomputePath(pos, goal);
            }
            while (m_waypoints.Count > 0 && Vector2.Distance(pos, m_waypoints[0]) < (m_thin ? 0.3f : 0.45f)) m_waypoints.RemoveAt(0);
            if (m_waypoints.Count > 0)
            {
                // Cut corners when the next-next waypoint is visible.
                if (!m_thin && m_waypoints.Count > 1 && Vector2.Distance(pos, m_waypoints[1]) < 2.2f && !MoveBlocked(pos, m_waypoints[1], 0.5f)) m_waypoints.RemoveAt(0);
                Move = (m_waypoints[0] - pos).normalized;
            }
            else
            {
                Move = (goal - pos).normalized;
            }
        }

        private void RecomputePath(Vector2 pos, Vector2 goal)
        {
            m_nextPathTime = Time.time + 0.5f;
            m_pathGoal = goal;
            m_waypoints.Clear();
            if (!Pathfinder.HasInstance) return;
            IntVector2 s = pos.ToIntVector2(VectorConversions.Floor);
            IntVector2 e = goal.ToIntVector2(VectorConversions.Floor);
            Path path;
            bool ok = false;
            path = null;
            if (!m_thin) ok = Pathfinder.Instance.GetPath(s, e, out path, new IntVector2(2, 2), CellTypes.FLOOR, null, null, false);
            if (!ok) ok = Pathfinder.Instance.GetPath(s, e, out path, IntVector2.One, CellTypes.FLOOR, null, null, false);
            if (!ok)
            {
                // Goal cell may be occupied (chest, table): path to a free neighbour instead.
                for (int r = 1; r <= 2 && !ok; r++)
                {
                    for (int dx = -r; dx <= r && !ok; dx++)
                    {
                        for (int dy = -r; dy <= r && !ok; dy++)
                        {
                            if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue;
                            ok = Pathfinder.Instance.GetPath(s, new IntVector2(e.x + dx, e.y + dy), out path, IntVector2.One, CellTypes.FLOOR, null, null, false);
                        }
                    }
                }
            }
            if (!ok || path == null || path.Positions == null) return;
            foreach (IntVector2 node in path.Positions)
            {
                m_waypoints.Add(Pathfinder.GetClearanceOffset(node, path.Clearance));
            }
        }

        private bool m_thin;
        private float m_thinUntil;
        private int m_unstickStage;

        private void UnstickCheck(Vector2 pos)
        {
            if (m_thin && Time.time > m_thinUntil) m_thin = false;
            bool wantsMove = Move.sqrMagnitude > 0.25f;
            if (wantsMove && (pos - m_lastPos).sqrMagnitude < 0.0004f)
            {
                m_stuckTimer += Time.deltaTime;
                if (m_stuckTimer > 0.35f)
                {
                    int stage = m_stuckTimer < 0.9f ? 1 : (m_stuckTimer < 1.6f ? 2 : (m_stuckTimer < 2.6f ? 3 : 4));
                    if (stage != m_unstickStage)
                    {
                        m_unstickStage = stage;
                        m_waypoints.Clear();
                        m_nextPathTime = 0f;
                        m_thin = true;
                        m_thinUntil = Time.time + 4f;
                    }
                    if (stage == 1)
                    {
                        // Recentre on the current tile: corridors and doorways are only about one tile wide.
                        Vector2 centre = new Vector2(Mathf.Floor(pos.x) + 0.5f, Mathf.Floor(pos.y) + 0.5f);
                        Vector2 toC = centre - pos;
                        if (toC.magnitude > 0.08f) Move = toC.normalized;
                    }
                    else if (stage == 2)
                    {
                        // Slide along whichever axis is free.
                        Vector2 want = Move;
                        Vector2 ax = new Vector2(Mathf.Sign(want.x) * (Mathf.Abs(want.x) > 0.2f ? 1f : 0f), 0f);
                        Vector2 ay = new Vector2(0f, Mathf.Sign(want.y) * (Mathf.Abs(want.y) > 0.2f ? 1f : 0f));
                        if (ax != Vector2.zero && !MoveBlocked(pos, pos + ax * 0.6f, 0.2f)) Move = ax;
                        else if (ay != Vector2.zero && !MoveBlocked(pos, pos + ay * 0.6f, 0.2f)) Move = ay;
                        else Move = Perp(want) * m_strafeSign;
                    }
                    else if (stage == 3)
                    {
                        Move = Perp(Move) * m_strafeSign;
                        m_strafeSign = -m_strafeSign;
                    }
                    else
                    {
                        m_stuckTimer = 0f;
                        m_unstickStage = 0;
                        var prim = GameManager.Instance.PrimaryPlayer;
                        if (m_order != OrderKind.None) EndOrder("Bot: stuck, giving up.");
                        else if (prim != null && Vector2.Distance(pos, prim.specRigidbody.UnitCenter) > 3f) Bot.ReuniteWithOtherPlayer(prim, true);
                    }
                }
            }
            else { m_stuckTimer = 0f; m_unstickStage = 0; }
            m_lastPos = pos;
        }

        private float m_nextBlankTime;
        public bool WantBlank;
        public bool ConsumeBlank() { bool v = WantBlank; WantBlank = false; return v; }

        private static bool SafeStep(Vector2 pos, Vector2 dir, float len)
        {
            if (MoveBlocked(pos, pos + dir * len)) return false;
            var dungeon = GameManager.Instance.Dungeon;
            if (dungeon == null) return true;
            for (int i = 1; i <= 3; i++)
            {
                Vector2 p = pos + dir * (len * i / 3f);
                if (Pit(p)) return false;
            }
            return true;
        }

        private struct Bullet { public Vector2 pos, vel; }
        private readonly List<Bullet> m_bullets = new List<Bullet>();
        private Vector2 m_evadeDir;
        private float m_evadeUntil;
        private float m_weaveSign = 1f;
        private const float BulletR = 1.1f;

        private static bool Pit(Vector2 p)
        {
            var d = GameManager.Instance.Dungeon;
            if (d == null || d.data == null) return true;
            IntVector2 c = p.ToIntVector2(VectorConversions.Floor);
            if (!d.data.CheckInBounds(c)) return true;
            CellData cell = d.data[c];
            return cell == null || cell.type == CellType.PIT;
        }

        private static bool PathPits(Vector2 pos, Vector2 dir, float len)
        {
            for (int i = 1; i <= 4; i++)
                if (Pit(pos + dir * (len * i / 4f))) return true;
            return false;
        }

        private float MoveSpeed { get { return Mathf.Max(4f, Bot.stats.GetStatValue(PlayerStats.StatType.MovementSpeed)); } }

        // Cost of a plan: how deep into bullet hit radii we end up over the next moments.
        private float PlanCost(Vector2 pos, Vector2 dir, float speed, float invulnUntil)
        {
            float cost = 0f;
            for (float t = 0.05f; t <= 0.75f; t += 0.05f)
            {
                if (t < invulnUntil) continue;
                Vector2 me = pos + dir * speed * t;
                for (int i = 0; i < m_bullets.Count; i++)
                {
                    Vector2 bp = m_bullets[i].pos + m_bullets[i].vel * t;
                    float dd = (bp - me).magnitude;
                    if (dd < BulletR) cost += (BulletR - dd) + 0.5f;
                }
            }
            return cost;
        }

        private readonly Dictionary<Projectile, float> m_reactAt = new Dictionary<Projectile, float>();
        private float m_nextReactCleanup;

        // Each bullet gets its own human-ish reaction delay; rarely it is slow enough to slip through.
        private float NewReaction()
        {
            float d = 0.04f + Random.value * 0.12f;
            if (Random.value < 0.03f) d += 0.25f;
            return Time.time + d;
        }

        private void AvoidProjectiles(Vector2 pos)
        {
            if (Bot.IsDodgeRolling) return;
            m_bullets.Clear();
            var projectiles = StaticReferenceManager.AllProjectiles;
            int imminent = 0;
            for (int i = 0; i < projectiles.Count; i++)
            {
                Projectile p = projectiles[i];
                if (p == null || p.specRigidbody == null || !p.collidesWithPlayer || p.Owner is PlayerController) continue;
                Vector2 v = p.specRigidbody.Velocity;
                if (v.sqrMagnitude < 0.25f) continue;
                Vector2 bp = p.specRigidbody.UnitCenter;
                if ((bp - pos).sqrMagnitude > 16f * 16f) continue;
                float reactAt;
                if (!m_reactAt.TryGetValue(p, out reactAt)) { reactAt = NewReaction(); m_reactAt[p] = reactAt; }
                if (Time.time < reactAt) continue;
                m_bullets.Add(new Bullet { pos = bp, vel = v });
            }
            if (Time.time > m_nextReactCleanup)
            {
                m_nextReactCleanup = Time.time + 3f;
                var dead = new List<Projectile>();
                foreach (var kv in m_reactAt) if (kv.Key == null) dead.Add(kv.Key);
                foreach (var k in dead) m_reactAt.Remove(k);
                if (m_reactAt.Count > 600) m_reactAt.Clear();
            }
            if (m_bullets.Count == 0) { m_evadeUntil = 0f; return; }

            bool urgent = false;
            for (int i = 0; i < m_bullets.Count; i++)
            {
                Vector2 r = pos - m_bullets[i].pos;
                float sp = m_bullets[i].vel.magnitude;
                float t = Mathf.Max(0f, Vector2.Dot(r, m_bullets[i].vel) / (sp * sp));
                Vector2 off = r - m_bullets[i].vel * t;
                if (t < 0.55f && off.magnitude < BulletR + 0.5f) { urgent = true; imminent++; }
            }

            float speed = MoveSpeed * 0.75f; // walking needs time to accelerate
            float stayCost = PlanCost(pos, Vector2.zero, 0f, 0f);
            // Keep the last evasion for a moment unless it stopped working: gives irregular but not twitchy motion.
            if (!urgent && Time.time < m_evadeUntil && PlanCost(pos, m_evadeDir, speed, 0f) <= 0.01f && SafeStep(pos, m_evadeDir, 1.6f))
            {
                Move = m_evadeDir;
                return;
            }
            if (stayCost <= 0.01f) { m_evadeUntil = 0f; return; }

            // Walking options: 16 headings plus noise, so we sometimes thread a bullet instead of fleeing it.
            Vector2 best = Vector2.zero;
            float bestScore = float.MaxValue, bestRawCost = float.MaxValue;
            for (int k = 0; k < 16; k++)
            {
                Vector2 d = Quaternion.Euler(0f, 0f, k * 22.5f) * Vector3.right;
                if (!SafeStep(pos, d, 0.9f)) continue;
                float c = PlanCost(pos, d, speed, 0f);
                float score = c * 10f + Random.value * 0.8f;
                if (score < bestScore) { bestScore = score; best = d; bestRawCost = c; }
            }

            if (bestRawCost <= 0.01f)
            {
                Move = best;
                m_evadeDir = best;
                m_evadeUntil = Time.time + 0.12f + Random.value * 0.2f;
            }

            // Walking is not enough (or too late): roll. Rolls are invulnerable early, so rolling forward through the bullets is fine.
            // Rolling is the reliable answer to anything about to land; walking only for bullets that are still far off.
            bool needRoll = bestRawCost > 0.01f || (urgent && Random.value < 0.9f);
            if (needRoll && Time.time >= m_dodgeReadyTime)
            {
                float rollSpeed = Mathf.Max(8f, Bot.rollStats.GetModifiedDistance(Bot) / Mathf.Max(0.2f, Bot.rollStats.GetModifiedTime(Bot)));
                float rollLen = Bot.rollStats.GetModifiedDistance(Bot);
                Vector2 rd = Vector2.zero;
                float rs = float.MaxValue;
                for (int k = 0; k < 16; k++)
                {
                    Vector2 d = Quaternion.Euler(0f, 0f, k * 22.5f) * Vector3.right;
                    if (MoveBlocked(pos, pos + d * Mathf.Min(rollLen, 1.2f))) continue;
                    if (Pit(pos + d * rollLen) || Pit(pos + d * rollLen * 0.5f) && false) continue;
                    float c = PlanCost(pos, d, rollSpeed, 0.4f);
                    float score = c * 10f + Random.value * 1.5f;
                    if (score < rs) { rs = score; rd = d; }
                }
                if (rd != Vector2.zero)
                {
                    Move = rd;
                    m_dodge = true;
                    m_dodgeReadyTime = Time.time + 0.05f;
                    m_evadeUntil = 0f;
                }
            }

            if (imminent >= 6 && Bot.Blanks > 0 && Time.time >= m_nextBlankTime && bestRawCost > 0.01f)
            {
                WantBlank = true;
                m_nextBlankTime = Time.time + 2.5f;
            }
        }

        // Slowly drifting aim error so the bot misses a little, like a person.
        private Vector2 m_aimErr;
        private float m_aimErrUntil;
        private Vector2 AimError(float dist)
        {
            if (Time.time > m_aimErrUntil)
            {
                m_aimErrUntil = Time.time + Random.Range(0.25f, 0.6f);
                m_aimErr = Random.insideUnitCircle * (0.12f + 0.03f * Mathf.Min(dist, 12f));
            }
            return m_aimErr;
        }

        // Harmful goop (fire, poison, acid, webs, charm, cheese, oil-drain...) at a position.
        private static bool Hazard(Vector2 p)
        {
            var map = DeadlyDeadlyGoopManager.allGoopPositionMap;
            if (map == null || map.Count == 0) return false;
            IntVector2 key = (p / DeadlyDeadlyGoopManager.GOOP_GRID_SIZE).ToIntVector2(VectorConversions.Floor);
            DeadlyDeadlyGoopManager m;
            if (!map.TryGetValue(key, out m) || m == null || m.goopDefinition == null) return false;
            if (!m.IsPositionInGoop(p)) return false;
            if (m.IsPositionOnFire(p)) return true;
            var g = m.goopDefinition;
            return g.damagesPlayers || g.AppliesDamageOverTime || g.DrainsAmmo || g.AppliesCharm || g.AppliesCheese || g.AppliesSpeedModifier;
        }

        private static int HazardAlong(Vector2 a, Vector2 b)
        {
            int n = 0;
            for (int i = 0; i <= 8; i++) if (Hazard(Vector2.Lerp(a, b, i / 8f))) n++;
            return n;
        }

        private static bool HazardNear(Vector2 p)
        {
            return Hazard(p) || Hazard(p + new Vector2(0.3f, 0f)) || Hazard(p - new Vector2(0.3f, 0f)) || Hazard(p + new Vector2(0f, 0.3f)) || Hazard(p - new Vector2(0f, 0.3f));
        }

        // Leave harmful goop and put out fire by rolling; cross fire only when there is no clean way out.
        private void HazardGuard(Vector2 pos)
        {
            if (Bot.IsDodgeRolling || Bot.IsFalling) return;
            bool burning = Bot.IsOnFire;
            bool here = Hazard(pos);
            Vector2 dir = Move.sqrMagnitude > 0.01f ? Move.normalized : Vector2.zero;
            bool ahead = !here && dir != Vector2.zero && (Hazard(pos + dir * 0.6f) || Hazard(pos + dir * 1.0f));
            if (!burning && !here && !ahead) return;

            float rollLen = Bot.rollStats.GetModifiedDistance(Bot);
            Vector2 bestDir = Vector2.zero;
            float bestScore = float.MaxValue;
            for (int k = 0; k < 24; k++)
            {
                Vector2 d = Quaternion.Euler(0f, 0f, k * 15f) * Vector3.right;
                Vector2 land = pos + d * rollLen;
                if (Pit(land) || Pit(pos + d * rollLen * 0.5f) || Pit(land + d * 0.5f)) continue;
                if (MoveBlocked(pos, land)) continue;
                if (HazardNear(land)) continue;
                int crossing = HazardAlong(pos, land) - (here ? 1 : 0);
                float score = Mathf.Max(0, crossing) * 3f - (dir == Vector2.zero ? 0f : Vector2.Dot(d, dir)) + Random.value * 0.2f;
                if (score < bestScore) { bestScore = score; bestDir = d; }
            }

            if (ahead && !burning)
            {
                // Walk around the goop if possible; otherwise roll across it.
                for (int k = 0; k < 16; k++)
                {
                    Vector2 d = Quaternion.Euler(0f, 0f, k * 22.5f) * Vector3.right;
                    if (Vector2.Dot(d, dir) > 0.1f && SafeStep(pos, d, 1.4f) && !Hazard(pos + d * 0.7f) && !Hazard(pos + d * 1.3f)) { Move = d; return; }
                }
                if (Time.time >= m_dodgeReadyTime && bestDir != Vector2.zero && Vector2.Dot(bestDir, dir) > 0.5f)
                {
                    Move = bestDir; m_dodge = true; m_dodgeReadyTime = Time.time + 0.4f;
                }
                else Move = Vector2.zero;
                return;
            }

            if (bestDir == Vector2.zero || Time.time < m_dodgeReadyTime) return;
            Move = bestDir;
            m_dodge = true;
            m_dodgeReadyTime = Time.time + 0.4f;
        }

        // Charged weapons fire on release: hold for the full charge, let go briefly, repeat.
        private float m_chargeStart = -1f;
        private float m_releaseUntil;
        private void ChargeCycle()
        {
            if (!Shoot) { m_chargeStart = -1f; return; }
            Gun g = Bot.CurrentGun;
            if (g == null || g.DefaultModule == null || g.DefaultModule.shootStyle != ProjectileModule.ShootStyle.Charged) { m_chargeStart = -1f; return; }
            if (Time.time < m_releaseUntil) { Shoot = false; return; }
            if (m_chargeStart < 0f) m_chargeStart = Time.time;
            float need = Mathf.Clamp(g.DefaultModule.LongestChargeTime, 0.25f, 3f) + 0.15f;
            if (Time.time - m_chargeStart > need)
            {
                m_releaseUntil = Time.time + 0.15f;
                m_chargeStart = -1f;
                Shoot = false;
            }
        }

        // ---- Telegraphed danger zones (sky rockets, target reticles) ----
        private struct Zone { public Vector2 c; public float r; }
        private readonly List<Zone> m_zones = new List<Zone>();
        private float m_nextZoneScan;
        private static readonly System.Reflection.FieldInfo RocketPos = typeof(SkyRocket).GetField("m_targetLandPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.FieldInfo RocketState = typeof(SkyRocket).GetField("m_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private void ScanZones()
        {
            m_zones.Clear();
            try
            {
                foreach (SkyRocket r in Object.FindObjectsOfType<SkyRocket>())
                {
                    if (r == null || !r.gameObject.activeInHierarchy || r.ExplosionData == null) continue;
                    if (RocketPos == null || RocketState == null || (int)RocketState.GetValue(r) == 0) continue;
                    Vector3 t = (Vector3)RocketPos.GetValue(r);
                    m_zones.Add(new Zone { c = new Vector2(t.x, t.y), r = Mathf.Max(1.5f, r.ExplosionData.GetDefinedDamageRadius()) });
                }
                foreach (ReticleRiserEffect e in Object.FindObjectsOfType<ReticleRiserEffect>())
                {
                    if (e == null || !e.gameObject.activeInHierarchy) continue;
                    var sp = e.GetComponent<tk2dSlicedSprite>();
                    float r = sp != null ? Mathf.Max(sp.dimensions.x, sp.dimensions.y) / 32f : 1.5f;
                    m_zones.Add(new Zone { c = e.transform.position.XY(), r = Mathf.Clamp(r, 1f, 6f) });
                }
            }
            catch (System.Exception) { }
        }

        private float ZoneDepth(Vector2 p, float margin)
        {
            float worst = 0f;
            for (int i = 0; i < m_zones.Count; i++)
            {
                float d = m_zones[i].r + margin - Vector2.Distance(p, m_zones[i].c);
                if (d > worst) worst = d;
            }
            return worst;
        }

        private void DangerZoneGuard(Vector2 pos)
        {
            if (Time.time > m_nextZoneScan) { m_nextZoneScan = Time.time + 0.15f; ScanZones(); }
            if (m_zones.Count == 0 || Bot.IsDodgeRolling || Bot.IsFalling) return;

            Vector2 dir = Move.sqrMagnitude > 0.01f ? Move.normalized : Vector2.zero;
            float depth = ZoneDepth(pos, 0.5f);
            if (depth <= 0f)
            {
                // do not walk into a zone
                if (dir != Vector2.zero && ZoneDepth(pos + dir * 0.8f, 0.3f) > 0f) Move = Vector2.zero;
                return;
            }

            float rollLen = Bot.rollStats.GetModifiedDistance(Bot);
            Vector2 best = Vector2.zero; float bestScore = float.MaxValue;
            for (int k = 0; k < 24; k++)
            {
                Vector2 d = Quaternion.Euler(0f, 0f, k * 15f) * Vector3.right;
                float len = depth > 1.3f ? rollLen : Mathf.Min(depth + 0.6f, 2.5f);
                Vector2 land = pos + d * len;
                if (Pit(land) || Pit(pos + d * len * 0.5f) || MoveBlocked(pos, land)) continue;
                float score = ZoneDepth(land, 0.5f) * 3f + HazardAlong(pos, land) * 0.3f + Random.value * 0.2f;
                if (score < bestScore) { bestScore = score; best = d; }
            }
            if (best == Vector2.zero) return;
            Move = best;
            if (depth > 1.0f && Time.time >= m_dodgeReadyTime)
            {
                m_dodge = true;
                m_dodgeReadyTime = Time.time + 0.4f;
            }
        }

        // Never walk into a pit; if we are over or about to enter one, roll to firm ground.
        private void PitGuard(Vector2 pos)
        {
            if (Bot.IsDodgeRolling || Bot.IsFalling) return;
            bool here = Pit(pos);
            Vector2 dir = Move.sqrMagnitude > 0.01f ? Move.normalized : Vector2.zero;
            bool ahead = dir != Vector2.zero && (Pit(pos + dir * 0.7f) || Pit(pos + dir * 1.1f));
            if (!here && !ahead) return;

            float rollLen = Bot.rollStats.GetModifiedDistance(Bot);
            Vector2 bestDir = Vector2.zero;
            float bestScore = float.MaxValue;
            for (int k = 0; k < 24; k++)
            {
                Vector2 d = Quaternion.Euler(0f, 0f, k * 15f) * Vector3.right;
                // landing spot and its surroundings must be solid
                bool ok = true;
                foreach (float e in new[] { 0f, 0.5f, -0.5f })
                {
                    Vector2 q = pos + d * rollLen + Perp(d) * e;
                    if (Pit(q)) { ok = false; break; }
                }
                if (!ok || MoveBlocked(pos, pos + d * rollLen)) continue;
                float score = (dir == Vector2.zero ? 0f : -Vector2.Dot(d, dir)) + Random.value * 0.1f;
                if (score < bestScore) { bestScore = score; bestDir = d; }
            }
            if (ahead && !here)
            {
                // Prefer simply stopping or turning along the edge when there is room; roll only to cross a gap.
                Vector2 alt = Vector2.zero;
                for (int k = 0; k < 16; k++)
                {
                    Vector2 d = Quaternion.Euler(0f, 0f, k * 22.5f) * Vector3.right;
                    if (SafeStep(pos, d, 1.4f) && Vector2.Dot(d, dir) > -0.2f) { alt = d; break; }
                }
                if (alt != Vector2.zero && Vector2.Dot(alt, dir) > 0.3f) { Move = alt; return; }
                if (Time.time >= m_dodgeReadyTime && bestDir != Vector2.zero && Vector2.Dot(bestDir, dir) > 0.7f)
                {
                    Move = bestDir; m_dodge = true; m_dodgeReadyTime = Time.time + 0.3f; return;
                }
                Move = Vector2.zero;
                return;
            }
            if (here && bestDir != Vector2.zero)
            {
                Move = bestDir;
                m_dodge = true;
                m_dodgeReadyTime = Time.time + 0.1f;
            }
        }

        // ---- Orders ----

        public void GiveOrder(Vector2 cursor)
        {
            var prim = GameManager.Instance.PrimaryPlayer;
            if (Bot == null || prim == null) return;
            if (Bot.healthHaver.IsDead || Bot.IsGhost)
            {
                CoopBotPlugin.Say("Your bot is down. Revive it first.");
                return;
            }

            Vector2 primPos = prim.specRigidbody.UnitCenter;

            RoomHandler eroom = prim.CurrentRoom;
            if (eroom != null)
            {
                var foes = eroom.GetActiveEnemies(RoomHandler.ActiveEnemyType.All);
                AIActor pick = null; float pd = 2.2f;
                if (foes != null)
                    for (int i = 0; i < foes.Count; i++)
                    {
                        AIActor e = foes[i];
                        if (e == null || e.healthHaver == null || e.healthHaver.IsDead || e.IsHarmlessEnemy || e.CompanionOwner != null || e.specRigidbody == null) continue;
                        float d = Vector2.Distance(e.specRigidbody.UnitCenter, cursor);
                        if (d < pd) { pd = d; pick = e; }
                    }
                if (pick != null)
                {
                    m_lockedEnemy = pick;
                    if (m_order != OrderKind.None) EndOrder(null);
                    CoopBotPlugin.Say("Bot: targeting that enemy.");
                    return;
                }
            }

            Component bestComp = null;
            IPlayerInteractable bestIx = null;
            float bestScore = float.MaxValue;
            OrderKind kind = OrderKind.None;

            RoomHandler room = prim.CurrentRoom;
            if (room != null)
            {
                var list = room.GetRoomInteractables();
                for (int i = 0; i < list.Count; i++)
                {
                    IPlayerInteractable ix = list[i];
                    var comp = ix as Component;
                    if (comp == null || !comp.gameObject.activeInHierarchy || !IsOrderable(ix)) continue;
                    Vector2 cp = PositionOf(comp);
                    float score = Vector2.Distance(cp, cursor);
                    if (ix is Chest) score -= 1.5f;
                    if (score < bestScore) { bestScore = score; bestComp = comp; bestIx = ix; kind = OrderKind.Interact; }
                }
            }

            foreach (IPlayerInteractable ix in RoomHandler.unassignedInteractableObjects)
            {
                var comp = ix as Component;
                if (comp == null || !comp.gameObject.activeInHierarchy || !IsOrderable(ix)) continue;
                float score = Vector2.Distance(PositionOf(comp), cursor) - (ix is PickupObject ? 1.0f : 0f);
                if (score < bestScore) { bestScore = score; bestComp = comp; bestIx = ix; kind = OrderKind.Interact; }
            }

            foreach (var b in StaticReferenceManager.AllMinorBreakables)
            {
                if (!IsBreakTarget(b)) continue;
                float score = Vector2.Distance(PositionOf(b), cursor) + 0.5f;
                if (score < bestScore) { bestScore = score; bestComp = b; bestIx = null; kind = OrderKind.Break; }
            }
            foreach (var b in StaticReferenceManager.AllMajorBreakables)
            {
                if (!IsBreakTarget(b)) continue;
                float score = Vector2.Distance(PositionOf(b), cursor) + 0.5f;
                if (score < bestScore) { bestScore = score; bestComp = b; bestIx = null; kind = OrderKind.Break; }
            }

            float maxReach = 6f;
            if (bestComp == null || bestScore > maxReach)
            {
                m_order = OrderKind.Regroup;
                m_orderExpires = Time.time + 12f;
                m_orderTarget = null;
                m_waypoints.Clear();
                CoopBotPlugin.Say("Bot: coming to you.");
                return;
            }

            m_order = kind;
            m_orderTarget = bestComp;
            m_orderInteractable = bestIx;
            m_orderCenter = PositionOf(bestComp);
            m_orderExpires = Time.time + (kind == OrderKind.Break ? 40f : 25f);
            m_interactAttempts = 0;
            m_lastInteractTime = -10f;
            m_dropState = 0;
            m_waypoints.Clear();
            m_nextPathTime = 0f;
            CoopBotPlugin.Say(kind == OrderKind.Break ? "Bot: smashing stuff." : (bestComp is Gun ? "Bot: grabbing the gun." : "Bot: on it (" + bestComp.GetType().Name + ")."));
        }

        private static bool IsOrderable(IPlayerInteractable ix)
        {
            if (ix is TalkDoerLite || ix is TalkDoer || ix is ShopItemController) return false;
            string n = ix.GetType().Name;
            if (n.Contains("Elevator") || n.Contains("Teleporter") || n.Contains("Exit") || n.Contains("Descent")) return false;
            var chest = ix as Chest;
            if (chest != null && (chest.IsOpen || chest.IsBroken || chest.IsTruthChest)) return false;
            return true;
        }

        private static bool IsBreakTarget(MinorBreakable b)
        {
            return b != null && !b.IsBroken && b.isActiveAndEnabled && !b.OnlyBrokenByCode && !b.isInvulnerableToGameActors && !b.IsDecorativeOnly;
        }

        private static bool IsBreakTarget(MajorBreakable b)
        {
            return b != null && !b.IsDestroyed && b.isActiveAndEnabled && b.specRigidbody != null;
        }

        private static Vector2 PositionOf(Component c)
        {
            var rb = c.GetComponent<SpeculativeRigidbody>();
            return rb != null ? rb.UnitCenter : (Vector2)c.transform.position;
        }

        private void EndOrder(string message)
        {
            m_order = OrderKind.None;
            m_orderTarget = null;
            m_orderInteractable = null;
            m_waypoints.Clear();
            if (!string.IsNullOrEmpty(message)) CoopBotPlugin.Say(message);
        }

        public static bool IsStartingGun(PlayerController p, Gun g)
        {
            return g != null && ((p.startingGunIds != null && p.startingGunIds.Contains(g.PickupObjectId)) ||
                                 (p.startingAlternateGunIds != null && p.startingAlternateGunIds.Contains(g.PickupObjectId)));
        }

        private bool CanDropHeld()
        {
            Gun cur = Bot.CurrentGun;
            return cur != null && !IsStartingGun(Bot, cur) && !cur.PreventStartingOwnerFromDropping && !Bot.inventory.GunLocked.Value;
        }

        // N key: drop the equipped weapon right here (never a starting weapon).
        public void DropNow()
        {
            if (Bot == null || Bot.healthHaver.IsDead || Bot.IsGhost) return;
            if (!CanDropHeld()) { CoopBotPlugin.Say("Bot: can't drop that weapon."); return; }
            try
            {
                Bot.ForceDropGun(Bot.CurrentGun);
                CoopBotPlugin.Say("Bot: dropped its weapon.");
            }
            catch (System.Exception) { }
        }

        // Returns true while the bot is still busy making room (it must not pick up the target gun yet).
        private bool HandleDropBeforePickup(Vector2 pos, Vector2 target)
        {
            if (m_dropState == 2) return false;
            if (!CanDropHeld()) { m_dropState = 2; return false; }
            if (m_dropState == 0)
            {
                Vector2 away = pos - target;
                if (away.sqrMagnitude < 0.01f) away = Vector2.right;
                away.Normalize();
                m_dropSpot = pos;
                if ((pos - target).magnitude < 3.5f)
                {
                    for (int k = 0; k < 16; k++)
                    {
                        float ang = (k % 2 == 0 ? 1 : -1) * (k / 2) * 22.5f;
                        Vector2 d = Quaternion.Euler(0f, 0f, ang) * (Vector3)away;
                        Vector2 spot = target + d * 4f;
                        if (!Pit(spot) && !Pit(pos + (spot - pos) * 0.5f) && !MoveBlocked(pos, spot)) { m_dropSpot = spot; break; }
                    }
                }
                m_dropState = 1;
                m_dropDeadline = Time.time + 4f;
            }
            if (Vector2.Distance(pos, m_dropSpot) > 0.6f && Time.time < m_dropDeadline)
            {
                MoveToward(pos, m_dropSpot, 0.4f);
                if (Move.sqrMagnitude < 0.01f) Move = (m_dropSpot - pos).normalized;
                return true;
            }
            if (Vector2.Distance(pos, target) >= 2.5f)
            {
                try { Bot.ForceDropGun(Bot.CurrentGun); } catch (System.Exception) { }
            }
            m_dropState = 2;
            return true;
        }

        private void RunInteractOrder(Vector2 pos, ref Vector2? aim)
        {
            if (m_orderTarget == null) { EndOrder("Bot: done."); return; }
            var chest = m_orderTarget as Chest;
            if (chest != null && (chest.IsOpen || chest.IsBroken))
            {
                EndOrder("Bot: opened it.");
                return;
            }
            if (m_lastInteractTime > 0f && Time.time - m_lastInteractTime < 0.8f) return;
            if (m_lastInteractTime > 0f && chest == null)
            {
                EndOrder("Bot: done.");
                return;
            }
            if (m_lastInteractTime > 0f && m_interactAttempts >= 3)
            {
                EndOrder("Bot: can't open that (locked?).");
                return;
            }

            if (m_orderTarget is Gun && HandleDropBeforePickup(pos, PositionOf(m_orderTarget))) return;

            Vector2 reachFrom = Bot.CenterPosition;
            float dist = m_orderInteractable.GetDistanceToPoint(reachFrom);
            if (dist < 0.85f)
            {
                m_interactTicks = 3;
                m_interactAttempts++;
                m_lastInteractTime = Time.time;
                return;
            }
            MoveToward(pos, PositionOf(m_orderTarget), 0.4f);
            if (Move.sqrMagnitude < 0.01f) Move = (PositionOf(m_orderTarget) - pos).normalized;
        }

        private void RunBreakOrder(Vector2 pos, ref Vector2? aim)
        {
            Component target = m_orderTarget;
            bool valid = target != null && ((target is MinorBreakable && IsBreakTarget((MinorBreakable)target)) ||
                                            (target is MajorBreakable && IsBreakTarget((MajorBreakable)target)));
            if (!valid)
            {
                target = NextBreakable(pos);
                m_orderTarget = target;
                if (target == null) { EndOrder("Bot: all smashed."); return; }
            }
            Vector2 tp = PositionOf(target);
            float d = Vector2.Distance(pos, tp);
            bool los = !TilesBlock(pos, tp);
            if (los && d < 8f)
            {
                aim = tp;
                Shoot = true;
                if (d > 6.5f) MoveToward(pos, tp, 5f);
            }
            else
            {
                MoveToward(pos, tp, 5f);
            }
        }

        private Component NextBreakable(Vector2 pos)
        {
            Component best = null;
            float bestD = float.MaxValue;
            foreach (var b in StaticReferenceManager.AllMinorBreakables)
            {
                if (!IsBreakTarget(b)) continue;
                Vector2 p = PositionOf(b);
                if (Vector2.Distance(p, m_orderCenter) > 5f) continue;
                float d = Vector2.Distance(p, pos);
                if (d < bestD) { bestD = d; best = b; }
            }
            foreach (var b in StaticReferenceManager.AllMajorBreakables)
            {
                if (!IsBreakTarget(b)) continue;
                Vector2 p = PositionOf(b);
                if (Vector2.Distance(p, m_orderCenter) > 5f) continue;
                float d = Vector2.Distance(p, pos);
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }
    }
}



