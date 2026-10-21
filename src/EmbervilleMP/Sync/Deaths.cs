using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EmbervilleMP.Net;
using LiteNetLib.Utils;
using UnityEngine;

namespace EmbervilleMP.Sync
{
    // Death announcements. A player's hits are resolved on their own copy (EnemySync.OnAttack, traps, damage over
    // time), so that copy names what killed them and tells everyone (DeathNotice). Every copy words the line itself
    // ("You were..." / "Guesty was...") and shows it in chat and as a banner. Getting back up and boss kills (decided
    // by the scene's runner) go the same way.
    internal static class Deaths
    {
        public enum Event : byte { Died = 0, Up = 1, BossDefeated = 2 }
        public enum CauseKind : byte { Unknown, Enemy, Dot, Trap, Hazard, Self }
        public enum Style : byte { Melee, Shot, Magic, Blast }

        public class Cause
        {
            public CauseKind Kind;
            public string Killer = "";   // display name, "" if unknown
            public bool Proper;          // a boss or a named thing: no article
            public Style Style;
            public EffectType Effect;
            public int Amount;

            public void Write(NetDataWriter w)
            {
                w.Put((byte)Kind);
                w.Put(Killer ?? "");
                w.Put(Proper);
                w.Put((byte)Style);
                w.Put((int)Effect);
                w.Put(Amount);
            }

            public static Cause Read(NetDataReader r) => new Cause
            {
                Kind = (CauseKind)r.GetByte(),
                Killer = r.GetString(),
                Proper = r.GetBool(),
                Style = (Style)r.GetByte(),
                Effect = (EffectType)r.GetInt(),
                Amount = r.GetInt(),
            };

            public override string ToString() => $"{Kind} '{Killer}' style={Style} effect={Effect} amount={Amount}";
        }

        public static readonly Color DeathColour = new Color(1f, 0.36f, 0.24f);
        public static readonly Color UpColour = new Color(0.62f, 0.85f, 0.62f);
        public static readonly Color BossColour = new Color(1f, 0.82f, 0.36f);
        private const string NameHex = "FFF1E0", KillerHex = "FFC46B", DimHex = "C8B4A8";

        // Every announcement as it's shown here (who, what): the public API's PlayerDied / PlayerRespawned.
        public static event System.Action<byte, Event> Noticed;

        // What the self-tests read: every announcement as plain text (no markup), newest last.
        public static readonly List<string> History = new List<string>();
        public static string Last => History.Count > 0 ? History[History.Count - 1] : "";

        // Set by the patches (Patches/DeathCausePatches.cs) while something deals damage.
        public static Component Source;     // the projectile / damage area / explosion hitting now
        public static Effect DotTick;       // the damage-over-time effect ticking now

        private static Attack pendingAttack;
        private static IDamageable pendingAggressor;
        private static int pendingFrame = -1;
        private static Cause lastCause, lastAttacker;
        private static float lastCauseAt = -100f, lastAttackerAt = -100f;
        private static readonly Dictionary<EffectType, Cause> dotSources = new Dictionary<EffectType, Cause>();

        private static Session session;
        private static int seq;
        private static bool deadAnnounced;
        private static float diedAt;
        private static readonly HashSet<long> seen = new HashSet<long>();
        private static readonly HashSet<int> bossesAnnounced = new HashSet<int>();

        // Tests: a fresh world in the same session (chains). Its bosses can fall again; nothing pending carries over.
        public static void ForgetWorld()
        {
            History.Clear();
            dotSources.Clear();
            bossesAnnounced.Clear();
            pendingAttack = null;
            pendingAggressor = null;
            lastCause = lastAttacker = null;
            deadAnnounced = false;
        }

        private static bool Showing => Plugin.Settings.DeathAnnouncements.Value;
        private static bool Live => session != null && Session.Current == session && session.Ready;

        // Called on every Attach, also again on the same session (chained tests, reconnect): keep its state then.
        public static void Hook(Session s)
        {
            if (s != session)
            {
                session = s;
                seen.Clear();
                deadAnnounced = false;
            }
            s.On(Msg.DeathNotice, OnNotice);
        }

        // ---------------- what hurt us ----------------

        public static bool IsLocalPlayer(IDamageable d)
        {
            var me = Game.LocalPlayer;
            if (me == null || d == null) return false;
            if (d is PlayerMaster pm) return pm == me;
            return d is Component c && c != null && c.GetComponentInParent<PlayerMaster>() == me;
        }

        // CombatSystem.Resolve on our character: the attack asset doesn't make it into the damage the player receives.
        public static void OnResolve(DamageData d)
        {
            if (!IsLocalPlayer(d.Target)) return;
            pendingAttack = d.Attack;
            pendingAggressor = d.Aggressor;
            pendingFrame = Time.frameCount;
        }

        // PlayerMaster.ApplyDamage on our character (before the game applies it).
        public static void OnLocalDamage(DamageData d)
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                Cause c;
                if (d.Result == DamageResult.Effect && DotTick != null)
                {
                    c = new Cause { Kind = CauseKind.Dot, Effect = DotTick.Type };
                    var from = dotSources.TryGetValue(DotTick.Type, out var src) ? src : now - lastAttackerAt < 20f ? lastAttacker : null;
                    if (from != null) { c.Killer = from.Killer; c.Proper = from.Proper; }
                }
                else
                {
                    var attack = d.Attack;
                    if (attack == null && pendingFrame == Time.frameCount && ReferenceEquals(pendingAggressor, d.Aggressor)) attack = pendingAttack;
                    c = Describe(d.Aggressor, Source, attack);
                }
                c.Amount = d.Amount;
                if (c.Kind == CauseKind.Enemy || c.Kind == CauseKind.Trap || c.Kind == CauseKind.Hazard)
                {
                    lastAttacker = c;
                    lastAttackerAt = now;
                    // Effects that hurt over time remember who gave them ("poisoned by a Toxic Shroom").
                    if (d.Effects != null)
                        foreach (var e in d.Effects)
                            if (e.DamagePerSec > 0) dotSources[e.Effect] = c;
                }
                lastCause = c;
                lastCauseAt = now;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[death] cause: " + e.Message); }
        }

        private static Cause Describe(IDamageable aggressor, Component source, Attack attack)
        {
            var c = new Cause();
            var aggressorComp = aggressor as Component;
            if (aggressorComp == null) aggressorComp = null;   // a destroyed object: a real null, so ?? works below
            string attackName = attack != null ? attack.name : "";
            if (source is Explosion || source is ExplosionOverTime || Has(attackName, "SelfDestruct", "Explo", "Bomb")) c.Style = Style.Blast;
            else if (source is Projectile || (attack != null && attack.projectile != null) || Has(attackName, "Projectile", "Shoot", "Shot")) c.Style = Style.Shot;
            else if (attack != null && attack.hitType == DamageHitType.Magic) c.Style = Style.Magic;

            Component trap = FindTrap(source) ?? FindTrap(aggressorComp);
            if (trap != null || (attack != null && Objects.TrapSync.IsHazard(new DamageData { Attack = attack })))
            {
                c.Kind = CauseKind.Trap;
                c.Killer = TrapName(trap, trap is GroundTrap ? "spike trap" : "arrow trap");
                if (trap is GroundTrap) c.Style = Style.Melee;
                else if (trap is ShooterTrap || trap == null) c.Style = Style.Shot;
                return c;
            }
            var enemy = aggressor as Enemy;
            if (enemy == null && aggressorComp != null) enemy = aggressorComp.GetComponentInParent<Enemy>();
            if (enemy == null && source != null) enemy = source.GetComponentInParent<Enemy>();
            if (enemy != null)
            {
                c.Kind = CauseKind.Enemy;
                c.Killer = EnemyName(enemy, out c.Proper);
                return c;
            }
            if (aggressor is PlayerMaster) { c.Kind = CauseKind.Self; return c; }
            var thing = aggressorComp != null ? aggressorComp : source;
            string name = thing != null ? ObjectName(thing.transform) : null;
            if (!string.IsNullOrEmpty(name)) { c.Kind = CauseKind.Hazard; c.Killer = name; }
            return c;
        }

        private static Component FindTrap(Component c)
        {
            if (c == null) return null;
            return (Component)c.GetComponentInParent<GroundTrap>(true) ?? c.GetComponentInParent<ShooterTrap>(true);
        }

        private static string TrapName(Component trap, string fallback)
        {
            string n = trap != null ? CleanName(trap.name).ToLowerInvariant() : "";
            if (n.Contains("spike")) return "spike trap";
            if (n.Contains("arrow")) return "arrow trap";
            if (n.Contains("dart")) return "dart trap";
            if (n.Contains("fire") || n.Contains("flame")) return "fire trap";
            return fallback;
        }

        // A readable name for something that isn't an enemy or a trap (walks up past "Hitbox"-like children).
        private static string ObjectName(Transform t)
        {
            for (int i = 0; i < 3 && t != null; i++, t = t.parent)
            {
                string n = CleanName(t.name);
                string low = n.ToLowerInvariant();
                if (n.Length > 0 && !low.Contains("collider") && !low.Contains("hitbox") && !low.Contains("damage") && !low.Contains("pool") && !low.Contains("area"))
                    return n.ToLowerInvariant();
            }
            return null;
        }

        private static bool Has(string s, params string[] parts) => parts.Any(p => s.IndexOf(p, System.StringComparison.OrdinalIgnoreCase) >= 0);

        // ---------------- names ----------------

        private static int bossScene = -1;
        private static readonly Dictionary<Enemy, BossDamageEventTrigger> bossTriggers = new Dictionary<Enemy, BossDamageEventTrigger>();

        public static BossDamageEventTrigger BossTrigger(Enemy e)
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene != bossScene || (bossTriggers.Count > 0 && bossTriggers.Keys.Any(k => k == null)))
            {
                bossScene = scene;
                bossTriggers.Clear();
                foreach (var t in Object.FindObjectsByType<BossDamageEventTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (t.enemy != null) bossTriggers[t.enemy] = t;
            }
            return e != null && bossTriggers.TryGetValue(e, out var found) ? found : null;
        }

        public static bool IsBoss(Enemy e) => BossTrigger(e) != null || (e != null && e.name.StartsWith("Boss"));

        public static string EnemyName(Enemy e) => EnemyName(e, out _);

        // The boss bar's (localized) name; otherwise the game's own name for the kind of enemy; otherwise the prefab's.
        public static string EnemyName(Enemy e, out bool proper)
        {
            proper = false;
            if (e == null) return "";
            var boss = BossTrigger(e);
            if (boss != null || e.name.StartsWith("Boss")) proper = true;
            if (boss != null)
                try
                {
                    if (UnityEngine.Localization.Settings.LocalizationSettings.InitializationOperation.IsDone)
                    {
                        // By reflection: LocalizedString drags in UI Toolkit types we don't reference.
                        var ls = HarmonyLib.AccessTools.Field(typeof(BossDamageEventTrigger), "name")?.GetValue(boss);
                        var get = ls != null ? HarmonyLib.AccessTools.Method(ls.GetType(), "GetLocalizedString", System.Type.EmptyTypes) : null;
                        if (get?.Invoke(ls, null) is string n && !string.IsNullOrWhiteSpace(n)) return n.Trim();
                    }
                }
                catch { }
            // "Toxic" alone reads badly in a sentence; the rest of the game's names work as they are.
            if (e.enemyType == EnemyType.Mob_Toxic) return "Toxic Shroom";
            string typeName = e.enemyType.GetName();
            if (!string.IsNullOrEmpty(typeName)) return typeName;
            string clean = CleanName(e.name);
            return clean.Length > 0 ? clean : "monster";
        }

        private static readonly HashSet<string> junkTokens = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { "Mob", "Boss", "Aco", "Sc", "Attack", "Tut", "Tutorial", "RandomColor", "Variant", "Clone", "Prefab", "Pool" };

        // "Mob_Toxic_Tut_03" -> "Toxic", "Boss_SpiderMatriarch" -> "Spider Matriarch".
        public static string CleanName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string n = raw.Replace("(Clone)", " ");
            var words = new List<string>();
            foreach (var token in n.Split(new[] { '_', ' ', '-', '.', '(', ')' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                if (junkTokens.Contains(token) || token.All(char.IsDigit)) continue;
                words.Add(Regex.Replace(token, "(?<=[a-z])(?=[A-Z])", " "));
            }
            return string.Join(" ", words).Trim();
        }

        // ---------------- events ----------------

        // PlayerMaster.Die on our character.
        public static void OnLocalDie()
        {
            try
            {
                if (!Live || deadAnnounced) return;
                deadAnnounced = true;
                diedAt = Time.realtimeSinceStartup;
                var cause = lastCause != null && Time.realtimeSinceStartup - lastCauseAt < 2f ? lastCause : new Cause();
                // A blow bigger than a whole health bar (scripted kills, instant-death hits) says nothing useful.
                var me = Game.LocalPlayer;
                if (me != null && cause.Amount >= me.MaxHealthPoints) cause.Amount = 0;
                Plugin.Log.LogInfo($"[death] We died: {cause}");
                int n = ++seq;
                Show(session.LocalId, true, Event.Died, n, cause, null, null);
                session.Send(Msg.DeathNotice, w => { w.Put((byte)Event.Died); w.Put(n); cause.Write(w); });
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("[death] announce: " + e.Message); }
        }

        // Enemy.SetKilled: the scene's runner announces a boss's defeat (with whoever is in the room).
        public static void OnEnemyKilled(Enemy e)
        {
            try
            {
                if (!Live || e == null || !EnemySync.IsAuthority || !IsBoss(e)) return;
                if (!bossesAnnounced.Add(e.GetInstanceID())) return;
                string boss = EnemyName(e);
                string area = EnemySync.Instance?.AreaOf(e) ?? "";
                var names = new List<string> { session.LocalName };
                var presence = MPRunner.Instance?.Presence;
                if (presence != null)
                    foreach (var r in presence.Remotes.Values.OrderBy(r => r.Info.Id))
                        if (r.Latest != null && r.Latest.Scene == Game.SceneName && (area == "" || r.Latest.Area == "" || r.Latest.Area == area))
                            names.Add(r.Info.Name);
                int n = ++seq;
                Show(session.LocalId, true, Event.BossDefeated, n, null, boss, names);
                session.Send(Msg.DeathNotice, w =>
                {
                    w.Put((byte)Event.BossDefeated);
                    w.Put(n);
                    w.Put(boss);
                    w.Put((byte)names.Count);
                    foreach (var name in names) w.Put(name);
                });
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[death] boss: " + ex.Message); }
        }

        // Every frame (MPRunner): getting back up, the banner.
        public static void Update()
        {
            DeathBanner.Update();
            if (!deadAnnounced) return;
            if (!Live) { deadAnnounced = false; return; }
            var me = Game.LocalPlayer;
            if (me == null || me.IsDead || !Game.InGame || Time.realtimeSinceStartup - diedAt < 1f) return;
            deadAnnounced = false;
            dotSources.Clear();
            lastCause = null;
            int n = ++seq;
            Show(session.LocalId, true, Event.Up, n, null, null, null);
            session.Send(Msg.DeathNotice, w => { w.Put((byte)Event.Up); w.Put(n); });
        }

        private static void OnNotice(byte from, NetDataReader r)
        {
            var ev = (Event)r.GetByte();
            int n = r.GetInt();
            Cause cause = null;
            string boss = null;
            List<string> names = null;
            if (ev == Event.Died) cause = Cause.Read(r);
            else if (ev == Event.BossDefeated)
            {
                boss = r.GetString();
                int count = r.GetByte();
                names = new List<string>();
                for (int i = 0; i < count; i++) names.Add(r.GetString());
            }
            if (from == session.LocalId || !seen.Add(((long)from << 32) | (uint)n)) return;
            PartyWipe.Instance?.OnNotice(from, ev);   // who's down, for room events and party wipes
            Show(from, false, ev, n, cause, boss, names);
        }

        // ---------------- wording ----------------

        private static void Show(byte who, bool local, Event ev, int n, Cause cause, string boss, List<string> names)
        {
            string name = local ? "You" : session.Players.TryGetValue(who, out var p) ? p.Name : "Player " + who;
            string subject = $"<b><color=#{NameHex}>{Esc(name)}</color></b>";
            string line, title = null, subtitle = null;
            Color colour;
            switch (ev)
            {
                case Event.Died:
                    Phrase(cause, n, out string aux, out string rest);
                    string dmg = cause.Amount > 0 && cause.Kind != CauseKind.Dot && cause.Kind != CauseKind.Unknown && cause.Kind != CauseKind.Self
                        ? $" <color=#{DimHex}>({cause.Amount} damage)</color>" : "";
                    line = $"{subject} {Join(Conj(aux, local), Their(rest, local))}{dmg}";
                    title = local ? "You have fallen" : $"{name} has fallen!";
                    subtitle = cause.Kind == CauseKind.Unknown ? "" : Capital(Their(rest, local));
                    colour = DeathColour;
                    break;
                case Event.Up:
                    line = local ? "<b>You</b> are back on your feet." : $"{subject} is back on their feet.";
                    colour = UpColour;
                    break;
                default:
                    string killers = names == null || names.Count == 0 ? "" : " by " + JoinNames(names.Select(x => $"<b><color=#{NameHex}>{Esc(x)}</color></b>").ToList());
                    line = $"<b><color=#{KillerHex}>{Esc(boss)}</color></b> was defeated{killers}!";
                    title = $"{boss} defeated!";
                    subtitle = names == null || names.Count == 0 ? "" : "by " + JoinNames(names);
                    colour = BossColour;
                    break;
            }
            string plain = Plain(line);
            try { Noticed?.Invoke(who, ev); } catch (System.Exception e) { Plugin.Log.LogWarning("[death] listener: " + e.Message); }
            History.Add(plain);
            if (History.Count > 100) History.RemoveAt(0);
            Plugin.Log.LogInfo($"[death] {plain}");
            if (!Showing) return;
            Chat.AddSystem((ev == Event.Up ? "" : SystemLine.Skull + " ") + line, colour);
            if (title != null) DeathBanner.Show(title, subtitle, colour, ev == Event.BossDefeated ? 5f : 4f);
        }

        // "was" + "slain by a <b>Glowbat</b>"; aux is "was", "has" or "" (a plain verb).
        private static void Phrase(Cause c, int n, out string aux, out string rest)
        {
            string k = Killer(c);
            bool known = c.Killer.Length > 0;
            aux = "was";
            switch (c.Kind)
            {
                case CauseKind.Enemy:
                    if (c.Style == Style.Blast) rest = $"blown up by {k}";
                    else if (c.Style == Style.Shot) rest = $"shot down by {k}";
                    else if (c.Style == Style.Magic) rest = $"blasted by {k}";
                    else rest = (c.Proper ? "slain" : new[] { "slain", "killed", "struck down" }[n % 3]) + " by " + k;
                    return;
                case CauseKind.Dot:
                    switch (c.Effect)
                    {
                        case EffectType.Poison: if (known) rest = $"poisoned by {k}"; else { aux = ""; rest = "succumbed to poison"; } return;
                        case EffectType.Burn: aux = ""; rest = "burned to death" + (known ? $", set alight by {k}" : ""); return;
                        case EffectType.Bleed: aux = ""; rest = "bled out" + (known ? $" from {Possessive(c)} wounds" : ""); return;
                        case EffectType.Shock: rest = "electrocuted" + (known ? $" by {k}" : ""); return;
                        case EffectType.Freeeze: aux = ""; rest = "froze to death" + (known ? $", chilled by {k}" : ""); return;
                        case EffectType.Kymia: rest = "drained of Kymia" + (known ? $" by {k}" : ""); return;
                        default: aux = ""; rest = "succumbed to {their} wounds"; return;
                    }
                case CauseKind.Trap:
                    if (c.Style == Style.Shot) rest = $"skewered by {k}";
                    else { aux = ""; rest = $"fell to {k}"; }
                    return;
                case CauseKind.Hazard:
                    rest = $"killed by {k}";
                    return;
                case CauseKind.Self:
                    rest = "undone by {their} own attack";
                    return;
                default:
                    aux = "has";
                    rest = "fallen";
                    return;
            }
        }

        private static string Killer(Cause c)
        {
            if (c.Killer.Length == 0) return "something";
            string b = $"<b><color=#{KillerHex}>{Esc(c.Killer)}</color></b>";
            return c.Proper ? b : Article(c.Killer) + " " + b;
        }

        private static string Possessive(Cause c)
        {
            string k = Killer(c);
            return c.Killer.EndsWith("s") ? k + "'" : k + "'s";
        }

        private static string Article(string word) => word.Length > 0 && "AEIOUaeiou".IndexOf(word[0]) >= 0 ? "an" : "a";

        private static string Conj(string aux, bool you) => !you ? aux : aux == "was" ? "were" : aux == "has" ? "have" : aux;
        private static string Their(string s, bool you) => s.Replace("{their}", you ? "your" : "their");
        private static string Join(string aux, string rest) => aux.Length > 0 ? aux + " " + rest : rest;
        private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        private static string Esc(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");   // a name can't open a tag

        private static string JoinNames(List<string> names) =>
            names.Count <= 1 ? string.Join("", names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];

        public static string Plain(string rich) => Regex.Replace(rich ?? "", "<[^>]*>", "");
    }
}
