using HarmonyLib;
using UnityEngine;
using _Mirthwood.Scripts.Demo;

namespace Mirthwood.Echoes
{
    [HarmonyPatch]
    internal static class DraftPause
    {
        static bool active,restorePaused,restoreInput;
        static float restoreSpeed;

        public static void Begin()
        {
            if(active)return;
            restorePaused=GameState.IsPaused;restoreSpeed=Time.timeScale;restoreInput=GameState.AllowInput;
            Freeze();active=true;
        }
        static void Freeze()
        {
            GameState.IsPaused=true;Time.timeScale=0;
            if(GameState.AllowInput)GameState.AllowInput=false;
        }
        public static void Maintain(){if(active&&(!GameState.IsPaused||Time.timeScale!=0))Freeze();}
        public static void End()
        {
            if(!active)return;
            active=false;
            GameState.IsPaused=restorePaused;Time.timeScale=restoreSpeed;GameState.AllowInput=restoreInput;
        }
        // Honor the latest native pause or speed request when the editor releases the clock.
        [HarmonyPostfix,HarmonyPatch(typeof(GameState),nameof(GameState.Pause))]
        static void NativePause(float timeScale){if(active){restorePaused=true;restoreSpeed=timeScale;restoreInput=false;Freeze();}}
        [HarmonyPostfix,HarmonyPatch(typeof(GameState),nameof(GameState.UnPause))]
        static void NativeResume(bool allowInput){if(active){restorePaused=false;restoreSpeed=Time.timeScale;restoreInput=allowInput;Freeze();}}
        [HarmonyPostfix,HarmonyPatch(typeof(GameState),nameof(GameState.SetTimeScale))]
        static void NativeSpeed(float timescale){if(active){restoreSpeed=timescale;Freeze();}}
    }
}
