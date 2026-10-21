using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        private class FxItem
        {
            public string Label;
            public Attack Attack;   // a weapon attack / attack object
            public Skill Skill;     // or a skill, used through the skill bar
        }

        // Other players' skills and spells: the guest uses attacks with attack objects, a projectile and its skills
        // through the game's own code; the host sees a copy of each at the guest's puppet, and nothing is hit twice.
        [Scenario("spells", Timeout = 150)]
        private static IEnumerator SpellsTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            if (s == null) { yield return Finish(""); yield break; }
            var es = EnemySync.Instance;
            var fx = SkillFx.Instance;
            var pfx = PlayerFx.Instance;
            if (!Check("skill effects sync is running", fx != null && pfx != null)) { yield return Finish(""); yield break; }
            yield return TeleportTo(CombatArea, s.IsHost ? new Vector2(51.8f, 76.3f) : new Vector2(53f, 77f));
            yield return Barrier("arrived", 40f);
            var me = Game.LocalPlayer;
            me.Invincible = true;   // enemies will notice us; nobody may die halfway (copies are checked by counters)

            if (s.IsHost)
            {
                // Stand next to wherever the guest goes, so the screenshots show both.
                yield return WaitFor(() => peerData.ContainsKey("gpos"), 20f);
                if (peerData.TryGetValue("gpos", out var gp))
                {
                    var p = gp.Split(':');
                    var g = new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
                    yield return MoveTo(me, g + new Vector2(-3f, -0.5f), 0.8f);
                }
                yield return new WaitForSecondsRealtime(0.5f);
                float notFromGuests0 = SkillFx.EnemyDamageNotFromGuests;
                int hits0 = es.HitsApplied, leaks0 = SkillFx.Leaks, errors0 = fx.Errors, blocked0 = SkillFx.Blocked;
                // Counters can carry over from earlier scenarios (chained runs): everything below is since now.
                var shownBase = new Dictionary<string, int>(fx.ShownByKey);
                int projBase = pfx.ProjectilesShown;
                System.Func<string, int> shownOf = key => key == "proj" ? pfx.ProjectilesShown - projBase
                    : (fx.ShownByKey.TryGetValue(key, out int c) ? c : 0) - (shownBase.TryGetValue(key, out int b) ? b : 0);
                yield return Barrier("baseline");

                int checkedItems = 0, mirrored = 0;
                for (int n = 0; ; n++)
                {
                    string go = "fxgo" + n, sent = "fxsent" + n;
                    yield return WaitFor(() => peerData.ContainsKey(go) || peerData.ContainsKey("fxall"), 25f);
                    if (!peerData.TryGetValue(go, out var label)) break;
                    int shownAtGo = fx.Shown + pfx.ProjectilesShown;
                    Chat.Send($"#fxready{n}=1");   // counters noted: the guest may go
                    yield return WaitFor(() => fx.Shown + pfx.ProjectilesShown > shownAtGo, 2f);
                    yield return DevHarness.Screenshot($"spells-host-{n:00}", 0.15f);
                    yield return WaitFor(() => peerData.ContainsKey(sent), 12f);
                    if (!Check($"{label}: the guest reported what it sent", peerData.ContainsKey(sent))) { Chat.Send($"#fxack{n}=1"); continue; }
                    var want = ParseSent(peerData.TryGetValue(sent, out var st) ? st : "");
                    // The keys this item sent, with the guest's totals since the baseline (so a late copy from the item
                    // before doesn't count twice); "+new" is how many objects this item sent, not a key.
                    var thisItem = want.Where(kv => !kv.Key.StartsWith("+")).ToList();
                    System.Func<bool> allShown = () => thisItem.All(kv => shownOf(kv.Key) >= kv.Value);
                    yield return WaitFor(allShown, 3f);
                    checkedItems++;
                    if (want.TryGetValue("+new", out int fresh) && fresh == 0)
                        Plugin.Log.LogInfo($"[test] {label}: spawned nothing to mirror (animations and the extra-effects animator only)");
                    else
                    {
                        string have = string.Join(", ", thisItem.Select(kv => $"{kv.Key} {shownOf(kv.Key)}/{kv.Value}"));
                        if (Check($"{label}: a copy shows on the host", allShown(), have)) mirrored++;
                        if (thisItem.Any(kv => kv.Key == fx.LastShownKey) && fx.LastShownCopy != null)
                        {
                            Check($"{label}: the copy is at the guest's puppet", fx.LastShownDistance >= 0f && fx.LastShownDistance < 8f, $"{fx.LastShownDistance:F2} units from it");
                            var copy = fx.LastShownCopy;
                            Plugin.Log.LogInfo($"[test] {label}: copy '{copy.name}' active={copy.activeSelf} renderers={copy.GetComponentsInChildren<Renderer>(false).Count(x => x.enabled)} animators={copy.GetComponentsInChildren<Animator>(false).Length} particles={copy.GetComponentsInChildren<ParticleSystem>(false).Count(x => x.isPlaying)}");
                        }
                    }
                    Chat.Send($"#fxack{n}=1");
                }
                Check("the guest went through its attacks and skills", checkedItems > 0, $"{checkedItems} items");
                Check("at least one effect object was mirrored", mirrored > 0, $"{mirrored} items with copies; {FxCopies.Created} copies made, {fx.Missing} not loaded here, {fx.Skipped} skipped");
                yield return new WaitForSecondsRealtime(2f);   // areas and beams linger
                Check("copies dealt no damage anywhere", SkillFx.Leaks == leaks0, $"{SkillFx.Leaks - leaks0} leaked, {SkillFx.Blocked - blocked0} hits/effects of copies stopped");
                Check("enemies lost health only from the guest's real hits", SkillFx.EnemyDamageNotFromGuests - notFromGuests0 < 0.01f,
                    $"{SkillFx.EnemyDamageNotFromGuests - notFromGuests0} other damage, {es.HitsApplied - hits0} guest hits applied");
                Check("no errors showing copies", fx.Errors == errors0, $"{fx.Errors - errors0} errors");
                // Same health on both sides: nothing was applied twice on either.
                yield return WaitFor(() => peerData.ContainsKey("ehp"), 10f);
                if (peerData.TryGetValue("ehp", out var ehp) && ehp.Length > 0)
                {
                    var p = ehp.Split(':');
                    var e = es.ById(int.Parse(p[0]));
                    float theirs = float.Parse(p[1], CultureInfo.InvariantCulture);
                    if (e != null)
                        Check("the target's health is the same on both", Mathf.Abs(e.CurrentHealth - theirs) < 0.01f || (e.IsDead && theirs <= 0f), $"host {e.CurrentHealth}, guest {theirs}, max {e.MaxHealth}");
                }
            }
            else
            {
                WriteSkillSurvey(me);
                // Next to the nearest enemy, facing it: the real hits land on it (and go to the host).
                var target = NearestAlive(CombatArea, me.transform.position);
                Vector2 aim = Vector2.right;
                if (target != null)
                {
                    yield return MoveTo(me, (Vector2)target.transform.position + Vector2.left * 1.5f, 0.8f);
                    aim = ((Vector2)(target.transform.position - me.transform.position)).normalized;
                }
                Chat.Send("#gpos=" + me.transform.position.x.ToString("F2", CultureInfo.InvariantCulture) + ":" + me.transform.position.y.ToString("F2", CultureInfo.InvariantCulture));
                yield return Barrier("baseline");

                var items = PickFxItems();
                Plugin.Log.LogInfo($"[test] spells: {items.Count} items: {string.Join(", ", items.Select(i => i.Label))}");
                Check("found attacks and skills to use", items.Count > 0);
                int leaks0 = SkillFx.Leaks, hits0 = es.HitsSent, mirrorable = 0;
                var sentBase = new Dictionary<string, int>(fx.SentByKey);
                int projSentBase = pfx.ProjectilesSent;
                for (int n = 0; n < items.Count; n++)
                {
                    var item = items[n];
                    var before = new Dictionary<string, int>(fx.SentByKey);
                    int spawns0 = fx.SpawnsSent, proj0 = pfx.ProjectilesSent;
                    if (target != null && !target.IsDead) aim = ((Vector2)(target.transform.position - me.transform.position)).normalized;
                    Chat.Send($"#fxgo{n}={item.Label}");
                    string ready = "fxready" + n;
                    yield return WaitFor(() => peerData.ContainsKey(ready), 2f);   // the host notes its counters first
                    if (item.Skill != null) yield return UseSkill(me, item.Skill, aim, $"spells-guest-{n:00}");
                    else yield return UseAttack(me, item.Attack, aim, $"spells-guest-{n:00}");
                    int fresh = fx.SpawnsSent - spawns0 + pfx.ProjectilesSent - proj0;
                    if (fresh > 0) mirrorable++;
                    // Cumulative totals of the keys this item sent (see the host's side).
                    var parts = fx.SentByKey.Where(kv => !before.TryGetValue(kv.Key, out int b) || b != kv.Value)
                        .Select(kv => $"{kv.Key}*{kv.Value - (sentBase.TryGetValue(kv.Key, out int b0) ? b0 : 0)}").ToList();
                    if (pfx.ProjectilesSent != proj0) parts.Add($"proj*{pfx.ProjectilesSent - projSentBase}");
                    parts.Add($"+new*{fresh}");
                    Plugin.Log.LogInfo($"[test] {item.Label}: {fresh} effect objects sent");
                    Chat.Send($"#fxsent{n}={string.Join("|", parts)}");
                    string ack = "fxack" + n;
                    yield return WaitFor(() => peerData.ContainsKey(ack), 10f);
                    if (me.IsDead) yield return WaitFor(() => !me.IsDead, 20f);
                }
                Chat.Send("#fxall=" + items.Count);
                Check("some attack or skill spawned effect objects", mirrorable > 0, $"{mirrorable} of {items.Count}");
                Plugin.Log.LogInfo($"[test] spells: {es.HitsSent - hits0} real hits sent to the host");
                yield return new WaitForSecondsRealtime(2.5f);
                Check("no copy's damage got through here either", SkillFx.Leaks == leaks0, $"{SkillFx.Leaks - leaks0}");
                Chat.Send("#ehp=" + (target != null ? es.IdOf(target) + ":" + target.CurrentHealth.ToString(CultureInfo.InvariantCulture) : ""));
            }
            Plugin.Log.LogInfo($"[skillfx] sent={fx.SpawnsSent}/{fx.UpdatesSent} shown={fx.Shown} updates={fx.Updates} missing={fx.Missing} skipped={fx.Skipped} errors={fx.Errors} blocked={SkillFx.Blocked} leaks={SkillFx.Leaks} copies={FxCopies.Created} projectiles={pfx.ProjectilesSent}/{pfx.ProjectilesShown}");
            yield return DevHarness.Screenshot(s.IsHost ? "spells-host" : "spells-guest");
            yield return Barrier("done", 40f);
            yield return Finish("other players' skills and spells show, harmlessly");
        }

        private static Dictionary<string, int> ParseSent(string text)
        {
            var d = new Dictionary<string, int>();
            foreach (var part in text.Split('|'))
            {
                int star = part.LastIndexOf('*');
                if (star > 0 && int.TryParse(part.Substring(star + 1), out int v)) d[part.Substring(0, star)] = v;
            }
            return d;
        }

        // What the demo's characters can do: player attacks with an attack object (one per prefab: magic beam, magic
        // missile, magic shot, split shot), a projectile, and the classes' active skills (those that spawn something
        // first; the "+" upgrades and passives are left out). Enemy skills (acolytes) are not player skills.
        private static List<FxItem> PickFxItems()
        {
            var items = new List<FxItem>();
            var playerAttacks = Resources.FindObjectsOfTypeAll<Attack>().Where(a => a != null && a.attackFor == Attack.Target.Player).OrderBy(a => a.name).ToList();
            var seen = new HashSet<GameObject>();
            foreach (var a in playerAttacks.Where(a => (a.attackHit & AttackHit.AttackObject) != 0 && a.attackSpawner != null))
                if (seen.Add(a.attackSpawner) && items.Count < 5) items.Add(new FxItem { Label = $"attack {a.name} ({a.attackSpawner.name})", Attack = a });
            var proj = playerAttacks.FirstOrDefault(a => (a.attackHit & AttackHit.Projectile) != 0 && a.projectile != null);
            if (proj != null) items.Add(new FxItem { Label = $"projectile {proj.name}", Attack = proj });
            var classSkills = new HashSet<Skill>();
            foreach (var c in Resources.FindObjectsOfTypeAll<PlayerCombatClass>())
                if (c != null && c.ClassSkillTree != null)
                    foreach (var node in c.ClassSkillTree.skillNodes)
                        if (node != null && node.skill != null) classSkills.Add(node.skill);
            var skills = classSkills
                .Where(k => k.activationType != Skill.ActivationType.Passive && k.skillType != Skill.PerformType.Dodge && k.improvedFromSkill == null)
                .OrderBy(k => k.skillType == Skill.PerformType.HoldAttack ? 0 : k.skillType == Skill.PerformType.Graph && k.behaviorGraph != null ? 1 : 2)
                .ThenBy(k => k.ID)
                .Take(5);
            foreach (var k in skills) items.Add(new FxItem { Label = $"skill {k.ID} ({k.skillType})", Skill = k });
            return items;
        }

        // The game's own calls can throw for odd skills; the scenario must go on (the host waits for every item).
        private static bool Try(string what, System.Action a)
        {
            try { a(); return true; }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[test] {what}: {e.GetType().Name}: {e.Message}"); return false; }
        }

        // A weapon attack the way the game runs it (attack state, its animation, the hit), with the hit forced if the
        // animation doesn't get there.
        private static IEnumerator UseAttack(PlayerMaster me, Attack a, Vector2 aim, string shot)
        {
            yield return WaitIdle(me);
            Face(me, aim);
            me.performedAttackHit = false;
            me.releasedAttack = false;
            me.currentAttack = a;
            Try("attack state", () => me.ChangeState(me.stateAttack));
            yield return WaitFor(() => me.performedAttackHit || me.currentState != me.stateAttack, 1.2f);
            if (!me.performedAttackHit)
            {
                Plugin.Log.LogInfo($"[test] {a.name}: the animation didn't reach its hit; hitting directly");
                me.currentAttack = a;
                Try("attack hit", () => me.PerformAttackHit());
            }
            yield return new WaitForSecondsRealtime(0.15f);
            yield return DevHarness.Screenshot(shot);
            yield return new WaitForSecondsRealtime(0.25f);
            // A held attack (beam): hold a moment, then let go the way the game does.
            if (a.attackSpawner != null && a.attackSpawner.GetComponent<IHoldAttack>() != null)
            {
                yield return new WaitForSecondsRealtime(0.8f);
                Try("release", () => a.OnReleaseCallback?.InvokeAndClear());
            }
            yield return WaitFor(() => me.currentState != me.stateAttack, 1.5f);
            if (me.currentState == me.stateAttack) Try("idle", () => me.ChangeState(me.stateIdle));
            me.currentAttack = null;
            yield return new WaitForSecondsRealtime(0.4f);
        }

        // A skill from the skill bar, the way pressing its key does.
        private static IEnumerator UseSkill(PlayerMaster me, Skill k, Vector2 aim, string shot)
        {
            yield return WaitIdle(me);
            var sh = me.SkillHandler;
            bool had = sh.GetSkillLevel(k) >= 0;
            if (!had && !Try("AddSkill " + k.ID, () => sh.AddSkill(k, 1))) yield break;
            yield return null;
            me.RestoreKymiaPercentage(1f);
            me.currentKymia = Mathf.Max(me.currentKymia, 999f);   // a fresh character may not have enough for every skill
            Face(me, aim);
            SkillHandler.CanUseSkill = true;
            me.CanPerformSkills = true;
            var cont = sh.equipped.FirstOrDefault(c => c.skill == k);
            int index = cont?.slot != null ? sh.mainSlots.IndexOf(cont.slot) : -1;
            int spawns0 = SkillFx.Instance.SpawnsSent;
            if (index >= 0) Try("CallSkill", () => sh.CallSkill(index));
            else Try("PerformSkill", () => me.PerformSkill(k));
            Plugin.Log.LogInfo($"[test] skill {k.ID}: slot {index}, current={(me.currentSkill != null ? me.currentSkill.ID : "-")} state={me.currentState?.GetType().Name}");
            yield return new WaitForSecondsRealtime(0.5f);
            yield return DevHarness.Screenshot(shot);
            yield return new WaitForSecondsRealtime(0.7f);
            // An attack skill whose animation never reached its hit (headless): hit directly.
            if ((k.skillType == Skill.PerformType.Attack || k.skillType == Skill.PerformType.HoldAttack) && k.attack != null && !me.performedAttackHit && me.currentAttack == k.attack && SkillFx.Instance.SpawnsSent == spawns0)
                Try("skill hit", () => me.PerformAttackHit());
            if (k.canHold || k.skillType == Skill.PerformType.HoldAttack)
            {
                yield return new WaitForSecondsRealtime(0.6f);
                Try("ReleaseSkill", () => me.ReleaseSkill(k));
            }
            yield return new WaitForSecondsRealtime(1.2f);
            if (k.activationType == Skill.ActivationType.Toggle && index >= 0) Try("toggle off", () => sh.CallSkill(index));
            yield return WaitFor(() => me.currentSkill == null && me.currentState != me.stateAttack, 2.5f);
            if (me.currentSkill == k) Try("ForceSkillEnd", () => me.ForceSkillEnd(k));
            if (me.currentState == me.stateAttack) Try("idle", () => me.ChangeState(me.stateIdle));
            if (!had) Try("RemoveSkill", () => sh.RemoveSkill(k));
            yield return new WaitForSecondsRealtime(0.4f);
        }

        private static IEnumerator WaitIdle(PlayerMaster me)
        {
            yield return WaitFor(() => me.currentState != me.stateAttack && me.currentState != me.stateDodge, 2f);
            if (me.currentState == me.stateAttack || me.currentState == me.stateDodge) Try("idle", () => me.ChangeState(me.stateIdle));
        }

        private static void Face(PlayerMaster me, Vector2 aim)
        {
            me.lookDir = aim;
            me.lastLookDir = aim;
            me.direction = aim.ToDirection();
        }

        // The skills and attacks in this build, and what they spawn (for planning and for reading test results).
        private static void WriteSkillSurvey(PlayerMaster me)
        {
            var sb = new StringBuilder();
            foreach (var k in Resources.FindObjectsOfTypeAll<Skill>().OrderBy(k => k.ID))
            {
                var a = k.attack;
                sb.AppendLine($"skill {k.ID} '{k.name}' activation={k.activationType} type={k.skillType} hold={k.canHold} anim={k.animation} extraFx={k.extraFxAnimation} graph={(k.behaviorGraph != null ? k.behaviorGraph.name : "-")} " +
                              $"attack={(a != null ? $"{a.name} hit={a.attackHit} spawner={(a.attackSpawner != null ? a.attackSpawner.name : "-")} projectile={(a.projectile != null ? a.projectile.name : "-")}" : "-")} effects={k.effects?.Length ?? 0} improves={(k.improvedFromSkill != null ? k.improvedFromSkill.ID : "-")}");
            }
            foreach (var a in Resources.FindObjectsOfTypeAll<Attack>().Where(a => a.attackFor == Attack.Target.Player).OrderBy(a => a.name))
            {
                string comps = a.attackSpawner != null ? string.Join(",", a.attackSpawner.GetComponentsInChildren<Component>(true).Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).Distinct()) : "";
                sb.AppendLine($"attack {a.name} anim={a.animation} hit={a.attackHit} spawner={(a.attackSpawner != null ? a.attackSpawner.name + " [" + comps + "]" : "-")} at={a.attackObjectSpawnPointName} projectile={(a.projectile != null ? a.projectile.name : "-")} at={a.spawnPointName} trigger={!a.preventTrigger}");
            }
            foreach (var c in Resources.FindObjectsOfTypeAll<PlayerCombatClass>())
                sb.AppendLine($"class {c.name} tree={(c.ClassSkillTree != null ? string.Join(",", c.ClassSkillTree.skillNodes.Where(x => x != null && x.skill != null).Select(x => x.skill.ID)) : "-")}");
            foreach (var r in me.SkillHandler.skillReceivers)
            {
                var targets = new List<string>();
                if (r.OnStart != null)
                    for (int i = 0; i < r.OnStart.GetPersistentEventCount(); i++)
                        targets.Add((r.OnStart.GetPersistentTarget(i) != null ? r.OnStart.GetPersistentTarget(i).name : "null") + "." + r.OnStart.GetPersistentMethodName(i));
                sb.AppendLine($"receiver {(r.Owner != null ? r.Owner.ID : "-")} onStart=[{string.Join(", ", targets)}]");
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(DevHarness.OutDir, "skills-survey.txt"), sb.ToString());
            Plugin.Log.LogInfo($"[test] skill survey written ({sb.Length} chars)");
        }
    }
}
