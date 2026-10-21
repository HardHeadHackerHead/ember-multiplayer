using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EmbervilleMP.Net;
using EmbervilleMP.Patches;
using EmbervilleMP.Sync;
using UnityEngine;
using Object = UnityEngine.Object;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Things one player does that the others should see or get: items thrown away, heal and buff areas,
        // grass and critters brushed by teammates, the same random looks, training dummies and recyclers.

        // Instanced loot (the default): the guest throws an item away from its inventory; it lies on the host's
        // screen too, the host picks it up (only the host has it, it's gone for the guest). A drop the game makes on
        // its own (gathering, a pot) stays the guest's alone, as before.
        [Scenario("throwdrop", Timeout = 200)]
        private static IEnumerator ThrowDropTest()
        {
            yield return LootReady();
            var s = Session.Current;
            var wd = WorldDrops.Instance;
            var inv = Singleton<Inventory>.main;
            var me = Game.LocalPlayer;
            var data = TestItem();
            Plugin.Log.LogInfo($"[test] throw item {data.name} ({data.addressKey})");

            // 1. Not thrown away: stays local with instanced loot.
            var known = new HashSet<int>(wd.Live.Select(kv => kv.Key));
            if (!s.IsHost) data.CreateItem(1).DropToWorld(me.transform.position + Vector3.left * 2f, 1, 0.5f);
            yield return new WaitForSecondsRealtime(2f);
            Check("instanced loot: a drop the game makes itself isn't a network drop", NewestDrop(known) == 0, $"new id {NewestDrop(known):X}");
            yield return Barrier("local-drop");

            // 2. The guest throws one away (the inventory's Drop).
            known = new HashSet<int>(wd.Live.Select(kv => kv.Key));
            int thrown = 0, left = -1;
            if (!s.IsHost)
            {
                inv.AddItem(data.CreateItem(1));
                yield return null;
                var it = inv.allItems.FirstOrDefault(i => (i.section == InventorySection.Main || i.section == InventorySection.Belt) && i.itemObject.data.IsSame(data));
                if (Check("guest carries the item", it != null))
                {
                    thrown = it.itemObject.amount;
                    int before = AmountOf(data);
                    bool ok = inv.DisposeItem(it.slotIndex, it.section);
                    left = AmountOf(data);
                    Check("guest threw it away", ok && left == before - thrown, $"{before} -> {left}, {thrown} thrown");
                }
                Chat.Send("#thrown=" + thrown);
            }
            yield return WaitFor(() => s.IsHost ? peerData.ContainsKey("thrown") : true, 15f);
            if (s.IsHost) thrown = int.Parse(peerData.TryGetValue("thrown", out var tv) ? tv : "0");
            int id = 0;
            ItemDropObject d = null;
            yield return WaitFor(() => (id = NewestDrop(known)) != 0 && (d = wd.Live.FirstOrDefault(kv => kv.Key == id).Value) != null && d.isReady, 10f);
            Check("the thrown item lies here as a network drop", d != null && d.itemObj.data.IsSame(data) && d.itemObj.amount == thrown,
                d == null ? "none" : $"{d.itemObj.data.addressKey} x{d.itemObj.amount}, id {id:X}");
            Check("the scene's runner made it", (id >> 24) == Protocol.HostId, $"id {id:X}");
            yield return Barrier("thrown");

            // 3. The host picks it up: first come, first served.
            if (s.IsHost)
            {
                int had = AmountOf(data);
                if (d != null) d.OnInteract(me.gameObject);
                yield return WaitFor(() => AmountOf(data) > had, 5f);
                Check("the host picked the guest's item up", AmountOf(data) == had + thrown, $"{had} -> {AmountOf(data)}");
                Check("the drop is gone here", d == null || !d.gameObject.activeSelf);
            }
            else
            {
                yield return WaitFor(() => d == null || !d.gameObject.activeSelf, 8f);
                Check("the drop vanished for the guest", d == null || !d.gameObject.activeSelf);
                yield return new WaitForSecondsRealtime(1f);
                Check("the guest didn't get it back", AmountOf(data) == left, $"{left} -> {AmountOf(data)}");
            }
            Plugin.Log.LogInfo($"[loot] drops spawned {wd.Spawned} shown {wd.Shown} requests {wd.Requests}/{wd.RequestsServed} granted {wd.Granted} vanished {wd.Vanished}");
            yield return Barrier("done");
            yield return Finish("thrown items shared with instanced loot");
        }

        // A teammate's heal and buff areas as seen here (copies owned by a stand-in, as SkillFx makes them): they
        // work on our own character, through the area's own tag filter, and never on enemies. No demo skill spawns
        // a friendly one (the Kymia trail's area slows enemies and skips players), so the test makes its own.
        [Scenario("teamheal", Timeout = 150)]
        private static IEnumerator TeamHealTest()
        {
            yield return SessionReady();
            var me = Game.LocalPlayer;
            var hurtbox = me.GetComponentsInChildren<Collider2D>().Where(c => c.enabled && c.isTrigger && !c.CompareTag("PlayerCenter"))
                .OrderBy(c => c.name == "DamageableCollider" ? 0 : 1).FirstOrDefault();
            var enemy = Object.FindObjectsByType<Enemy>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(e => e.GetComponentInChildren<Collider2D>(true) != null).OrderBy(e => NetIds.ScenePath(e.transform)).FirstOrDefault();
            var enemyBox = enemy?.GetComponentsInChildren<Collider2D>(true).OrderBy(c => c.isTrigger ? 0 : 1).First();
            if (!Check("found our hurtbox and an enemy", hurtbox != null && enemy != null, $"{hurtbox?.name}, {enemy?.name}")) { yield return Finish(""); yield break; }

            // Inactive: the areas never start (no pools, no reset list); the test calls their collision handlers.
            var holder = new GameObject("MP_TestCopies");
            holder.SetActive(false);
            var owner = holder.AddComponent<PuppetFxOwner>();
            HealOverTime Heal(params string[] ignore)
            {
                var h = holder.AddComponent<HealOverTime>();
                h.damageData = new DamageData { Amount = 7 };
                h.damageModifiers = new List<Modifier>();
                h.ignoreTags = ignore.ToList();
                h.Owner = owner;
                h.IsExploding = true;
                return h;
            }
            EffectArea Area(params string[] ignore)
            {
                var a = holder.AddComponent<EffectArea>();
                a.hitBoxMask = ~0;
                a.ignoreTags = ignore.ToList();
                a.toAddEffects = new[] { new EffectCreator { name = "MP_TestBuff", onApplied = new List<GenericEffectActionCreator>(), onReapplied = new List<GenericEffectActionCreator>(), onRemoved = new List<GenericEffectActionCreator>() } };
                a.isEnabled = true;
                a.Owner = owner;
                return a;
            }

            try
            {
                // Heal: us yes, an area that skips players no, enemies never.
                me.currentHealth = me.MaxHealthPoints - 30;
                int hp0 = me.CurrentHealthPoints, local0 = SkillFxCopyHitsPatch.LocalAreaHits;
                Heal().HandleCollision(hurtbox);
                Check("a teammate's heal area heals our character", me.CurrentHealthPoints > hp0 && SkillFxCopyHitsPatch.LocalAreaHits > local0, $"{hp0} -> {me.CurrentHealthPoints}");
                int hp1 = me.CurrentHealthPoints;
                Heal("Player").HandleCollision(hurtbox);
                Check("its tag filter still applies (an area that skips players)", me.CurrentHealthPoints == hp1, $"{hp1} -> {me.CurrentHealthPoints}");
                float ehp = enemy.currentHealth;
                int blocked0 = SkillFx.Blocked;
                Heal().HandleCollision(enemyBox);
                Check("a teammate's heal area never heals an enemy here", enemy.currentHealth == ehp && SkillFx.Blocked > blocked0, $"{ehp} -> {enemy.currentHealth}");
            }
            catch (Exception e) { Check("heal area ran", false, e.ToString()); }

            bool added = false;
            var buff = Area();
            try
            {
                // (the Kymia trail's tags: an area meant for enemies)
                Area("PlayerCenter", "Player").OnTriggerEnter2D(hurtbox);
                Check("an area that skips players puts nothing on us", !me.EffectsHandler.HasEffect("MP_TestBuff"));
                buff.OnTriggerEnter2D(hurtbox);
                added = me.EffectsHandler.HasEffect("MP_TestBuff");
                Check("a teammate's buff area buffs our character", added);
                int blocked0 = SkillFx.Blocked;
                Area().OnTriggerEnter2D(enemyBox);
                Check("a teammate's area puts nothing on an enemy here", (enemy.EffectsHandler == null || !enemy.EffectsHandler.HasEffect("MP_TestBuff")) && SkillFx.Blocked > blocked0);
                if (added) buff.OnTriggerExit2D(hurtbox);
            }
            catch (Exception e) { Check("buff area ran", false, e.ToString()); }
            yield return null;
            yield return null;
            if (added) Check("leaving the area takes the buff off", !me.EffectsHandler.HasEffect("MP_TestBuff"));
            Object.Destroy(holder);
            me.ResetToFullHealth();
            yield return Barrier("done");
            yield return Finish("teammates' heal and buff areas reach us");
        }

        // A teammate walking through grass makes it sway on our screen (its puppet has no PlayerCenter trigger, so
        // nothing gets picked up or opened for it); the scene's random looks (enemy colours) are the same everywhere.
        [Scenario("worldtouch", Timeout = 220)]
        private static IEnumerator WorldTouchTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            var me = Game.LocalPlayer;

            // 1. Random pickers: the same pick on every copy.
            var sels = Object.FindObjectsByType<RandomEventSelector>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => !NetIds.IsRuntimeSpawned(x.transform) && x.events != null && x.events.Length > 1)
                .OrderBy(x => NetIds.ScenePath(x.transform)).ToList();
            Check("found the scene's random pickers", sels.Count >= 10, sels.Count + " pickers");
            var picks = sels.Select(RandomEventPickPatch.Index).ToList();
            Check("they don't all pick the same", picks.Distinct().Count() > 1, string.Join("", picks.Take(40)));
            int picked0 = RandomEventPickPatch.Picked, colours = 0;
            var shown = new System.Text.StringBuilder();
            foreach (var x in sels)
            {
                var a = x.GetComponent<Animator>();
                if (a == null) continue;
                x.Trigger();
                colours++;
                shown.Append(a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "-").Append(',');
            }
            Check("picks go through the patch", RandomEventPickPatch.Picked >= picked0 + colours, $"{RandomEventPickPatch.Picked - picked0}/{colours}");
            string sig = NetIds.Hash(string.Join(",", picks) + "|" + shown).ToString();
            Plugin.Log.LogInfo($"[test] {sels.Count} pickers, {colours} on animators: {shown}");
            Chat.Send("#rand=" + sig);
            yield return WaitFor(() => peerData.ContainsKey("rand"), 15f);
            Check("every copy picked the same looks", peerData.TryGetValue("rand", out var theirs) && theirs == sig, $"mine {sig}, theirs {theirs}");

            // 2. The teammate's puppet: no PlayerCenter trigger, but it touches the world.
            const string room = "004_Gather";
            var grass = Object.FindObjectsByType<GrassShaderComponent>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(g => g.tagToCompare == "PlayerCenter" && g.GetComponent<Collider2D>() is Collider2D c && c.enabled && g.GetComponentInParent<GameArea>(true)?.name == room)
                .OrderBy(g => NetIds.ScenePath(g.transform)).FirstOrDefault();
            if (!Check($"found grass in {room}", grass != null)) { yield return Finish(""); yield break; }
            Vector2 at = grass.transform.position;
            yield return TeleportTo(room, at + (s.IsHost ? Vector2.right * 4f : Vector2.down * 2.5f));
            yield return Barrier("at-grass", 60f);
            var presence = MPRunner.Instance.Presence;
            RemotePlayer other = null;
            yield return WaitFor(() => (other = presence.Remotes.Values.FirstOrDefault(r => r.Shown)) != null, 20f);
            if (!Check("the teammate's puppet is shown", other != null)) { yield return Finish(""); yield break; }
            Check("the puppet has no PlayerCenter trigger", other.Puppet.GetComponentsInChildren<Collider2D>(true).All(c => !c.CompareTag("PlayerCenter")));
            Check("the puppet touches the world", other.Puppet.GetComponent<PuppetTouch>() != null);
            yield return Barrier("checked");

            // 3. The guest walks through the tuft; the host sees it sway.
            if (!s.IsHost)
            {
                yield return MoveTo(me, at + Vector2.up * 2.5f, 2.5f);
                Chat.Send("#walked=1");
            }
            else
            {
                int swings0 = PuppetTouch.GrassSwings;
                bool moved = false;
                yield return WaitFor(() =>
                {
                    if (grass.material != null && Mathf.Abs(grass.material.GetFloat("_DistortAmount")) > 0.01f && PuppetTouch.LastGrass == grass) moved = true;
                    return moved;
                }, 10f);
                Check("the grass the teammate walked through swayed here", PuppetTouch.LastGrass == grass && moved, $"{PuppetTouch.GrassSwings - swings0} swing(s), last {(PuppetTouch.LastGrass != null ? PuppetTouch.LastGrass.name : "-")}");
            }
            yield return Barrier("walked");
            Plugin.Log.LogInfo($"[touch] grass {PuppetTouch.GrassSwings} fauna {PuppetTouch.FaunaMoves} ghosts {PuppetTouch.GhostsVanished}");
            yield return Finish("teammates brush the world");
        }

        // The guest hits the training dummy and runs a recycler: the host gets both moments (and plays them where
        // the dummy's room is loaded). Hits that aren't the player's (effect ticks) aren't sent.
        [Scenario("worldmoments", Timeout = 150)]
        private static IEnumerator WorldMomentsTest()
        {
            yield return SessionReady();
            var s = Session.Current;
            var me = Game.LocalPlayer;
            var dummy = Object.FindObjectsByType<TestDummy>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(x => NetIds.ScenePath(x.transform)).FirstOrDefault();
            var station = Object.FindObjectsByType<RecyclingStation>(FindObjectsInactive.Include, FindObjectsSortMode.None).OrderBy(x => NetIds.ScenePath(x.transform)).FirstOrDefault();
            if (!Check("found a training dummy and a recycler", dummy != null && station != null)) { yield return Finish(""); yield break; }
            int dummyId = NetIds.Hash(NetIds.ScenePath(dummy.transform)), stationId = NetIds.Hash(NetIds.ScenePath(station.transform));
            int received0 = WorldMoments.Received, played0 = WorldMoments.Played;
            yield return Barrier("found");

            if (!s.IsHost)
            {
                int sent0 = WorldMoments.Sent;
                // (the dummy's room may not be loaded here: then its Start hasn't run and its own reaction throws)
                try { dummy.ApplyDamage(new DamageData { Amount = 1, Result = DamageResult.Effect }); } catch (Exception) { }
                Check("a hit that isn't our character's isn't sent", WorldMoments.Sent == sent0);
                try { dummy.ApplyDamage(new DamageData { Aggressor = me, Target = dummy, Amount = 3, Result = DamageResult.Success }); } catch (Exception) { }
                Check("our hit on the dummy is sent", WorldMoments.Sent == sent0 + 1);
                yield return new WaitForSecondsRealtime(1f);
                try { station.CheckOnClose(false); } catch (Exception) { }
                Check("a recycler panel closed without recycling isn't sent", WorldMoments.Sent == sent0 + 1);
                try { station.CheckOnClose(true); } catch (Exception) { }
                Check("our recycler run is sent", WorldMoments.Sent == sent0 + 2);
            }
            else
            {
                yield return WaitFor(() => WorldMoments.Received > received0, 10f);
                Check("the host got the guest's dummy hit", WorldMoments.Received > received0 && WorldMoments.LastKind == WorldMoments.Dummy && WorldMoments.LastId == dummyId,
                    $"{WorldMoments.Received - received0} moment(s), kind {WorldMoments.LastKind} id {WorldMoments.LastId:X}");
                bool loaded = dummy.isActiveAndEnabled && dummy.rb != null;
                if (loaded) Check("the dummy reacted here", WorldMoments.Played > played0);
                else Plugin.Log.LogInfo("[test] the dummy's room isn't loaded on the host: nothing to play");
                yield return WaitFor(() => WorldMoments.Received > received0 + 1, 10f);
                Check("the host got the guest's recycler run", WorldMoments.Received == received0 + 2 && WorldMoments.LastKind == WorldMoments.Recycler && WorldMoments.LastId == stationId,
                    $"{WorldMoments.Received - received0} moment(s), kind {WorldMoments.LastKind} id {WorldMoments.LastId:X}");
            }
            yield return Barrier("done");
            yield return Finish("dummy hits and recycler runs shown to everyone");
        }
    }
}
