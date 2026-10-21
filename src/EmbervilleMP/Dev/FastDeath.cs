using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EmbervilleMP
{
    // Tests: a death that takes a second or two instead of ~8 (death animation, fade to black, death screen, fade back,
    // respawn). Everything the game and the mod do on a death still runs, in the same order: the death state, the
    // Death event, GameManager.ResetScene (or our co-op replacement), the death screen and its Continue, the respawn;
    // only the game's own waits between those steps are shorter.
    // On with --mp-fast-death (every scenario), or a scenario sets DevHarness.FastDeath (reset between scenarios).
    internal static class FastDeath
    {
        public const int Divider = 4;

        public static bool Active => DevHarness.FastDeath || Args.Has("fast-death");

        // Called in place of the game's constants (see the transpilers below).
        public static int Ms(int ms) => Active ? Math.Max(1, ms / Divider) : ms;
        public static float Sec(float s) => Active ? s / Divider : s;

        internal static IEnumerable<CodeInstruction> Scale(IEnumerable<CodeInstruction> instructions, MethodBase original, int[] ints, float[] floats)
        {
            var ms = AccessTools.Method(typeof(FastDeath), nameof(Ms));
            var sec = AccessTools.Method(typeof(FastDeath), nameof(Sec));
            int found = 0;
            foreach (var ci in instructions)
            {
                yield return ci;
                if (ci.opcode == OpCodes.Ldc_I4 && ci.operand is int i && ints.Contains(i)) { found++; yield return new CodeInstruction(OpCodes.Call, ms); }
                else if (ci.opcode == OpCodes.Ldc_R4 && ci.operand is float f && floats.Any(x => Mathf.Approximately(x, f))) { found++; yield return new CodeInstruction(OpCodes.Call, sec); }
            }
            Plugin.Log.LogInfo($"[dev] Fast deaths: {found} wait(s) in {original.DeclaringType?.Name}.{original.Name}");
        }

        internal static MethodBase MoveNext(Type owner, string method)
        {
            var sm = owner.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(t => t.Name.StartsWith("<" + method + ">") && typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(t));
            return sm == null ? null : AccessTools.Method(sm, "MoveNext");
        }
    }

    // PlayerDead.EnterState: 375 ms hit, then 2.6 s (4.5 s gory) of death animation before the Death event; its
    // callbacks (scorch mark 1.4 s, hide 0.2 s later; gore mark 0.4 s) scale the same, so they keep their order.
    [HarmonyPatch]
    internal static class FastDeathAnimationPatch
    {
        private static bool Prepare() => Args.Test != null;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var moveNext = FastDeath.MoveNext(typeof(PlayerDead), nameof(PlayerDead.EnterState));
            if (moveNext != null) yield return moveNext;
            // the lambdas it schedules (instance methods of PlayerDead, or of a closure class)
            var types = new[] { typeof(PlayerDead) }.Concat(typeof(PlayerDead).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public));
            foreach (var t in types)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (m.Name.StartsWith("<" + nameof(PlayerDead.EnterState) + ">b__")) yield return m;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            FastDeath.Scale(instructions, __originalMethod, new[] { 375, 2600, 4500 }, new[] { 1.4f, 0.4f, 0.2f });
    }

    // GameManager.RespawnPlayer: after the spawn effect, 600 ms until play resumes and 400 ms more of invincibility.
    [HarmonyPatch]
    internal static class FastRespawnPatch
    {
        private static bool Prepare() => Args.Test != null;

        private static MethodBase TargetMethod() => FastDeath.MoveNext(typeof(GameManager), "RespawnPlayer");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            FastDeath.Scale(instructions, __originalMethod, new[] { 600, 400 }, new float[0]);
    }

    // The fades of a death (to black 1.7 s with the death screen, back 0.8 s). The fader steps at most 3% a frame,
    // so a fade still takes ~34 frames.
    [HarmonyPatch(typeof(FaderManager), "Fade")]
    internal static class FastDeathFadePatch
    {
        private static bool Prepare() => Args.Test != null;

        private static void Prefix(ref FadeEvent fade)
        {
            if (fade.Duration > 0.1f && ((FastDeath.Active && GameManager.playerHasDied) || DevHarness.QuickTransition)) fade.Duration = 0.1f;
        }
    }

    // The fader's step cap (3% a frame: ~34 frames a fade, ~0.6-0.9 s at the frame rates of a test copy loading a scene)
    // while the harness moves the game (DevHarness.QuickTransition): a fade takes a frame or two. Every step of it still
    // runs, callbacks in the same order.
    [HarmonyPatch]
    internal static class QuickFadeStepPatch
    {
        private static bool Prepare() => Args.Test != null;

        private static MethodBase TargetMethod() => FastDeath.MoveNext(typeof(FaderManager), "Fade");

        public static float Step(float cap) => DevHarness.QuickTransition ? 1f : cap;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var step = AccessTools.Method(typeof(QuickFadeStepPatch), nameof(Step));
            int found = 0;
            foreach (var ci in instructions)
            {
                yield return ci;
                if (ci.opcode == OpCodes.Ldc_R4 && ci.operand is float f && Mathf.Approximately(f, 0.03f)) { found++; yield return new CodeInstruction(OpCodes.Call, step); }
            }
            Plugin.Log.LogInfo($"[dev] Quick fades: {found} step cap(s) in FaderManager.Fade");
        }
    }
}
