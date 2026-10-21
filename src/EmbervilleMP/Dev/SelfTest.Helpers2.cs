using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP
{
    // Tests: the Matriarch's intro in less time, for the scenarios that wake her to fight her, not to watch her intro.
    // Her intro (MatriarchBossCinematic.OnTriggerEnter2D) holds the player 1 s, puts them in the arena, then runs
    // 6.6 s of camera and 0.8 s more before she comes out: the last two waits are 3x shorter. Everything in it still
    // runs, in order (as FastDeath does for deaths); the 1 s before the player is put in the arena stays (a test
    // walking the player onto the trigger is still moving it during that second).
    // A scenario opts in at its start (FastBossIntro.On()); it ends with that scenario (nothing to reset).
    internal static class FastBossIntro
    {
        public const int Divider = 3;
        private static string scenario;

        public static void On() => scenario = SelfTest.Current;
        public static bool Active => Args.Test != null && scenario != null && scenario == SelfTest.Current;

        public static float Sec(float s) => Active ? s / Divider : s;

        // How long the second player waits before walking into her intro: its own intro still starts well before
        // the first one's ends (which comes out with her, and a player whose intro hasn't played would join her
        // fight instead: BossArena.Join).
        public static float SecondPlayerDelay => Active ? 1f : 2f;
    }

    [HarmonyPatch]
    internal static class FastBossIntroPatch
    {
        private static bool Prepare() => Args.Test != null && TargetMethod() != null;

        private static MethodBase TargetMethod() => FastDeath.MoveNext(typeof(MatriarchBossCinematic), "OnTriggerEnter2D");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var sec = AccessTools.Method(typeof(FastBossIntro), nameof(FastBossIntro.Sec));
            var waits = new[] { 6.6f, 0.8f };
            int found = 0;
            foreach (var ci in instructions)
            {
                yield return ci;
                if (ci.opcode == OpCodes.Ldc_R4 && ci.operand is float f && waits.Any(x => Mathf.Approximately(x, f)))
                {
                    found++;
                    yield return new CodeInstruction(OpCodes.Call, sec);
                }
            }
            Plugin.Log.LogInfo($"[dev] Fast boss intro: {found} wait(s) in {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}");
        }
    }
}
