using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EmbervilleMP.Net;
using EmbervilleMP.Sync;
using EmbervilleMP.Sync.Objects;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // Saves: coordinated saves after items change hands (SaveSync), side files that go with their saves (SideFiles),
        // the world's class lessons for late characters (ClassLessons).

        // What reached the disk, as the game would load it.
        private static PlayerData CharacterOnDisk()
        {
            string json = SaveLoad.Load(GameData.playerFilePrefix + GameData.GetPlayerExtension);
            return json == null ? null : JsonUtility.FromJson<PlayerData>(json);
        }

        private static int BagOnDisk(PlayerData pd, ItemData d) =>
            pd?.playerInventory == null ? -1 : pd.playerInventory.Where(x => x.Key == d.addressKey).Sum(x => x.Amount);

        private static int StashOnDisk(string id, ItemData d)
        {
            string json = SaveLoad.Load(GameData.GetWorldPrefix + GameData.GetWorldExtension);
            var wd = json == null ? null : JsonUtility.FromJson<WorldData>(json);
            var c = wd?.savedChests?.FirstOrDefault(x => x.CollectionId == id);
            return c == null ? 0 : c.CollectionItems.Where(x => x.Key == d.addressKey).Sum(x => x.Amount);
        }

        // In our bag (not an open chest's part of the inventory).
        private static int Bag(ItemData d) =>
            Singleton<Inventory>.main.allItems.Where(i => i?.itemObject?.data != null && i.itemObject.data.IsSame(d)
                && (i.section == InventorySection.Main || i.section == InventorySection.Belt)).Sum(i => i.itemObject.amount);

        // A guest deposits into the world's stash and nobody changes scene: shortly after, the guest's character file
        // on disk no longer has the items and the host's world file has them in the stash (a crash on either side now
        // loses nothing and duplicates nothing). A burst of moves is one save; nothing saves while the stash is open.
        // Fails without SaveSync: neither file changes until a scene change.
        [Scenario("savesync", Timeout = 200, Quick = true)]
        private static IEnumerator SaveSyncTest()
        {
            yield return CoopReady();
            var s = Session.Current;
            var inv = Singleton<Inventory>.main;
            var me = Game.LocalPlayer;
            var ss = SaveSync.Instance;
            if (!Check("saves are coordinated in this session", ss != null)) { yield return Finish(""); yield break; }
            var (itemA, itemB) = TwoItems();
            var c = MakeStash(itemA);
            if (!Check("made a storage chest", c != null && StashSync.IsStorage(c))) { yield return Finish(""); yield break; }
            yield return WaitFor(() => ChestSync.Ready(c) && CountIn(StashHere(c), itemA) >= 10, 15f);
            yield return Barrier("stash-made");

            // 1. Before: the guest's items are in its character file, the host's stash on disk has none of them.
            int had = 0;
            if (!s.IsHost)
            {
                inv.AddItem(new ItemObject(itemB, 3));
                yield return null;
                GuestWorld.SaveCharacter();
                had = Bag(itemB);
                Check("before: the items are in the guest's character file", had >= 3 && BagOnDisk(CharacterOnDisk(), itemB) == had, $"{BagOnDisk(CharacterOnDisk(), itemB)} on disk, {had} in the bag");
            }
            else Check("before: the host's world file has none of them in the stash", StashOnDisk(TestStashId, itemB) == 0, StashOnDisk(TestStashId, itemB).ToString());
            yield return WaitFor(() => !ss.Pending, 15f);   // (whatever an earlier scenario asked for is done)
            yield return Barrier("before");
            int saves0 = ss.Saves, requests0 = ss.Requests;

            // 2. The guest deposits: in, out, in again (a burst), the stash held open past the save delay, then closed.
            if (!s.IsHost)
            {
                c.OnInteract(me.gameObject);
                yield return WaitFor(() => c.isOpened, 6f);
                Check("guest opened the stash", c.isOpened);
                int openSaves = ss.Saves;
                MoveStack(itemB, true);
                yield return null;
                MoveStack(itemB, false);
                yield return null;
                int put = MoveStack(itemB, true);
                yield return new WaitForSecondsRealtime(SaveSync.Delay + 2.5f);
                Check("nothing saved on the guest while the stash is open", ss.Saves == openSaves && ss.Pending, $"{ss.Saves - openSaves} saves, pending {ss.Pending}");
                c.OnInteract(me.gameObject);
                yield return WaitFor(() => !c.isOpened, 3f);
                float closedAt = Time.realtimeSinceStartup;
                Chat.Send("#deposited=" + put);
                yield return WaitFor(() => ss.Saves > openSaves, 15f);
                Check("the guest saved its character soon after closing", ss.Saves > openSaves && ss.LastSaveAt >= closedAt - 0.5f && ss.LastSaveAt - closedAt < 8f,
                    $"{ss.Saves - openSaves} saves, {ss.LastSaveAt - closedAt:F1} s after closing ({ss.LastReason}, {ss.LastMs:F0} ms)");
                int onDisk = BagOnDisk(CharacterOnDisk(), itemB);
                Check("the guest's character file no longer has the deposit", put > 0 && onDisk == had - put && Bag(itemB) == onDisk, $"put {put}: {onDisk} on disk, {Bag(itemB)} in the bag, {had} before");
                Check("the host told the guest it saved", ss.NoticesReceived > 0);
                Check("the guest never wrote the host's world", !File.Exists(SaveLoad.GetPath(GuestWorld.GuestPrefix + GameData.GetWorldExtension)));
            }
            else
            {
                yield return WaitFor(() => peerData.ContainsKey("deposited"), 30f);
                int put = int.Parse(peerData.TryGetValue("deposited", out var dp) ? dp : "0");
                yield return WaitFor(() => StashOnDisk(TestStashId, itemB) == put, 15f);
                Check("the host's world file has the deposit in the stash", put > 0 && StashOnDisk(TestStashId, itemB) == put, $"{StashOnDisk(TestStashId, itemB)}/{put}");
                Check("a burst of moves is one save", ss.Saves - saves0 >= 1 && ss.Saves - saves0 <= 2 && ss.Requests - requests0 >= 2,
                    $"{ss.Saves - saves0} saves for {ss.Requests - requests0} requests ({ss.LastReason}, {ss.LastMs:F0} ms)");
                Check("the host told the guests", ss.NoticesSent > 0);
            }
            yield return Barrier("done");
            yield return Finish("items that change hands reach both disks");
        }

        // Runs a GameData call that would reload the live scene (DeleteWorldSave / DeletePlayerSave end with the game's
        // load listeners) on files of a test slot: the listeners are left out, the current save comes back afterwards.
        private static void Isolated(Action a)
        {
            var handlers = GameData.loadHandlers.ToList();
            var wd = GameData.worldData;
            var pd = GameData.playerData;
            string wp = GameData.GetWorldPrefix, pp = GameData.playerFilePrefix;
            GameData.loadHandlers.Clear();
            try { a(); }
            finally
            {
                GameData.loadHandlers.Clear();
                GameData.loadHandlers.AddRange(handlers);
                GameData.worldData = wd;
                GameData.playerData = pd;
                GameData.SetWorldPrefix(wp);
                GameData.SetPlayerPrefix(pp);
            }
        }

        // The mod's side files follow their saves: deleting a world or a character through the game's own call takes the
        // slot's _mp_* files with it (that slot's only), a new world or character clears its slot's leftovers, a new
        // character's creation time is its own, and the host's world side files name their world (another world's are
        // ignored, an unnamed old one is read and named). Fails without SideFiles: the side files outlive their saves.
        [Scenario("sidefiles", Timeout = 150)]
        private static IEnumerator SideFilesTest()
        {
            yield return CoopReady();
            var s = Session.Current;
            string dir = SideFiles.Folder;
            const string W = "world_slot9", P = "player_slot9";
            void Put(string name, params string[] lines) => File.WriteAllLines(Path.Combine(dir, name), lines);
            bool Has(string name) => File.Exists(Path.Combine(dir, name));
            string Left(string prefix) => string.Join(",", Directory.GetFiles(dir, prefix + "_mp_*").Select(Path.GetFileName));
            void Clean() { foreach (var f in Directory.GetFiles(dir, "*_slot9*")) File.Delete(f); }
            Clean();

            // 1. A world deleted through the game.
            SaveLoad.Save(W + GameData.GetWorldExtension, "{}");
            Put(W + "_mp_objects.txt", "world\tOld world|1");
            Put(W + "_mp_loot.txt", "x");
            Put(W + "_mp_future.txt", "x");   // (any side file a later version adds)
            Put("world_slot90_mp_objects.txt", "x");
            Isolated(() => { GameData.SetWorldPrefix(W); GameData.DeleteWorldSave(); });
            Check("deleting a world deletes its side files", !Has(W + GameData.GetWorldExtension) && Left(W) == "", Left(W));
            Check("... and no other world's", Has("world_slot90_mp_objects.txt"));

            // 2. A character deleted through the game.
            SaveLoad.Save(P + GameData.GetPlayerExtension, "{}");
            Put(P + "_mp_quest_rewards.txt", "character\tSomeone|1");
            Put(P + "_mp_tutorials.txt", "character\tSomeone|1");
            Put("player_slot90_mp_tutorials.txt", "x");
            Isolated(() => { GameData.SetPlayerPrefix(P); GameData.DeletePlayerSave(); });
            Check("deleting a character deletes its side files", !Has(P + GameData.GetPlayerExtension) && Left(P) == "", Left(P));
            Check("... and no other character's", Has("player_slot90_mp_tutorials.txt"));
            Check("the current save is untouched", GameData.HasPlayerData && GameData.HasWorldData && Game.InGame);

            // 3. A new world / character in a slot with leftovers of a save deleted by hand.
            Put(W + "_mp_objects.txt", "Scene\tdoor\t1\tAQ==");
            SideFiles.NewWorld(W);
            Check("a new world clears its slot's leftovers", !Has(W + "_mp_objects.txt"));
            long today = DateTime.Today.ToFileTimeUtc();
            var pd = new PlayerData { playerName = "MP Side", createdTime = today };
            Put(P + "_mp_quest_rewards.txt", "character\tMP Side|" + today);
            SideFiles.NewCharacter(P, pd);
            Check("a new character clears its slot's leftovers", !Has(P + "_mp_quest_rewards.txt"));
            Check("a new character's creation time is its own (same date)", pd.createdTime != today && pd.createdTime - today < TimeSpan.TicksPerSecond
                && DateTime.FromFileTimeUtc(pd.createdTime).Date == DateTime.FromFileTimeUtc(today).Date, $"{today} -> {pd.createdTime}");
            Clean();

            // 4. The host's world side files name their world.
            if (s.IsHost)
            {
                const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var os = ObjectSync.Instance;
                string table = SaveLoad.GetPath(GameData.GetWorldPrefix + "_mp_objects.txt");
                string state = Convert.ToBase64String(new byte[] { 1 });
                os.SaveTable();
                var kept = File.ReadAllLines(table);
                Check("the object table names its world", kept.FirstOrDefault() == SideFiles.WorldHeader, kept.FirstOrDefault());
                var loadTable = typeof(ObjectSync).GetMethod("LoadTable", Any);
                int ignored = SideFiles.Ignored;
                File.WriteAllLines(table, new[] { "world\tAnother world|1", $"MP_StaleScene\tdoor\t1\t{state}" });
                loadTable.Invoke(os, null);
                Check("another world's object table is ignored", !os.KnownScenes.Contains("MP_StaleScene") && SideFiles.Ignored == ignored + 1);
                File.WriteAllLines(table, new[] { $"MP_OldScene\tdoor\t1\t{state}" });
                loadTable.Invoke(os, null);
                Check("an old object table without a world is read", os.KnownScenes.Contains("MP_OldScene"));
                os.Forget("MP_OldScene", "door");
                os.SaveTable();
                Check("... and names its world when next written", File.ReadAllLines(table).FirstOrDefault() == SideFiles.WorldHeader);

                var loot = LootRules.Instance;
                string records = LootRules.RecordsPath;
                string[] keptLoot = File.Exists(records) ? File.ReadAllLines(records) : null;
                string Line(string name) => $"{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(name))}\tMP_TestChest\t{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("[]"))}";
                var loadRecords = typeof(LootRules).GetMethod("LoadRecords", Any);
                File.WriteAllLines(records, new[] { "world\tAnother world|1", Line("MP Stale") });
                loadRecords.Invoke(loot, null);
                Check("another world's chest records are ignored", loot.RecordCount("MP Stale") == 0);
                File.WriteAllLines(records, new[] { Line("MP Old") });
                loadRecords.Invoke(loot, null);
                Check("old chest records without a world are read", loot.RecordCount("MP Old") == 1);
                var all = (IDictionary)typeof(LootRules).GetField("records", Any).GetValue(loot);
                all.Remove("MP Old");
                if (keptLoot != null) File.WriteAllLines(records, keptLoot); else File.Delete(records);
                loot.SaveRecords();
                if (File.Exists(records)) Check("... and they name their world when next written", File.ReadAllLines(records).FirstOrDefault() == SideFiles.WorldHeader);
            }
            yield return Barrier("done");
            yield return Finish("side files follow their saves");
        }

        // A character that comes after the world's one-time class lesson still gets the class: the Mind's Journal
        // teaches the Wanderer only while the humming-sphere quest's objective is active, and that objective is the
        // world's. The guest's character is made one that never took the lesson; nothing is given while the world can
        // still teach it; once the host's world has the objective done, the guest gets the class (known, learned,
        // equipped since it had none), once per character and world. Fails without ClassLessons: no class ever.
        // How long nothing must happen: ClassLessons looks every 2 s, so this sees at least one of its looks.
        private const float ClassLessonsQuiet = 2.6f;

        [Scenario("classlessons", Timeout = 200)]
        private static IEnumerator ClassLessonsTest()
        {
            yield return CoopReady();
            var s = Session.Current;
            var lessons = ClassLessons.Find();
            var lesson = lessons.FirstOrDefault(l => l.unlock.classesToUnlock.Any(c => c != null && c.playerClassType == PlayerClassType.Wanderer));
            if (!Check("the scene has the journal's Wanderer lesson", lesson.unlock != null, string.Join(", ", lessons.Select(l => l.objective))))
            {
                yield return Finish("");
                yield break;
            }
            var cls = lesson.unlock.classesToUnlock.First(c => c != null && c.playerClassType == PlayerClassType.Wanderer);
            string id = cls.playerClassType.GetEmbervilleID();
            bool learned = lesson.unlock.setAsLearned;
            var qm = Singleton<QuestManager>.main;
            var quest = QuestManager.model.Quests.Values.FirstOrDefault(q => q.data.steps.Any(st => st.questObjectives.Any(o => o.ID == lesson.objective)));
            Check("found the lesson's quest", quest != null, lesson.objective);
            var pd = GameData.playerData;
            var stats = Game.LocalPlayer.GetCharacterStats();
            int granted0 = ClassLessons.Granted;
            bool hadNone = stats.equippedClass == null;

            if (!s.IsHost)
            {
                // A character that never took it (test characters may have it from earlier runs).
                pd.KnownClasses.Remove(id);
                pd.LearnedClasses.Remove(id);
                Check("the lesson isn't done in this world yet", qm.GetObjectiveState(lesson.objective) != QuestState.Completed, qm.GetObjectiveState(lesson.objective).ToString());
                yield return new WaitForSecondsRealtime(ClassLessonsQuiet);
                Check("nothing given while the world can still teach it", !pd.KnownClasses.Contains(id) && ClassLessons.Granted == granted0);
            }
            yield return Barrier("fresh");

            if (s.IsHost && quest != null)
            {
                // The host's world gets past the lesson (as a player who took it would leave it): the quest up to it, done.
                if (quest.mainState.Value == QuestState.Inactive) qm.SetQuestState(quest.data.questID, QuestState.Active);
                yield return new WaitForSecondsRealtime(0.5f);
                for (int guard = 0; guard < 10 && qm.GetObjectiveState(lesson.objective) != QuestState.Completed && quest.stepIndex < quest.data.steps.Count; guard++)
                {
                    foreach (var o in quest.data.steps[quest.stepIndex].questObjectives.ToList()) qm.SetObjectiveState(o.ID, QuestState.Completed);
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                Check("the lesson's objective is done in the host's world", qm.GetObjectiveState(lesson.objective) == QuestState.Completed, $"step {quest.stepIndex}");
            }
            else if (!s.IsHost)
            {
                yield return WaitFor(() => pd.KnownClasses.Contains(id), 40f);
                Check("the late character got the class the world already taught", pd.KnownClasses.Contains(id) && (!learned || pd.LearnedClasses.Contains(id)),
                    $"objective {qm.GetObjectiveState(lesson.objective)}, granted {ClassLessons.Granted - granted0}");
                Check("... equipped, since it had none", !hadNone || stats.equippedClass == cls, stats.equippedClass != null ? stats.equippedClass.name : "none");
                Check("... once", ClassLessons.Granted == granted0 + 1, $"{ClassLessons.Granted - granted0}");
                pd.KnownClasses.Remove(id);
                pd.LearnedClasses.Remove(id);
                yield return new WaitForSecondsRealtime(ClassLessonsQuiet);
                Check("never twice for a character in a world", !pd.KnownClasses.Contains(id) && ClassLessons.Granted == granted0 + 1);
                pd.KnownClasses.Add(id);
                if (learned) pd.LearnedClasses.Add(id);
            }
            yield return Barrier("done", 60f);
            if (s.IsHost) Check("the host gets no late lessons (its world is its own)", ClassLessons.Granted == granted0);
            yield return Finish("late characters get the world's class lessons");
        }
    }
}
