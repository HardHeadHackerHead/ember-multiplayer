using System.Collections;
using UnityEngine;

namespace EmbervilleMP
{
    internal static partial class SelfTest
    {
        // The in-game pause menu (for the multiplayer entry): open it, dump it, screenshot it.
        [Scenario("pausedump")]
        private static IEnumerator PauseDump()
        {
            yield return WaitInGame();
            yield return new WaitForSecondsRealtime(2f);
            var ui = Object.FindAnyObjectByType<UIManager>();
            if (!Check("UIManager found", ui != null)) { yield return Finish(""); yield break; }
            ui.Pause();
            yield return new WaitForSecondsRealtime(2f);
            DevHarness.DumpHierarchy(ui.panelPauseMenu, "pause-hierarchy.txt");
            yield return DevHarness.Screenshot("pause");
            Check("MULTIPLAYER in the pause menu", Click("Button_Multiplayer"));
            yield return new WaitForSecondsRealtime(1f);
            yield return DevHarness.Screenshot("pause-mp");
            Check("host from the pause menu", Click("MP_Host: LAN / IP"));
            yield return new WaitForSecondsRealtime(1.5f);
            Check("hosting", EmbervilleMP.Net.Session.Current != null && EmbervilleMP.Net.Session.Current.IsHost);
            yield return DevHarness.Screenshot("pause-hosting");
            yield return Finish("pause menu dumped");
        }
    }
}
