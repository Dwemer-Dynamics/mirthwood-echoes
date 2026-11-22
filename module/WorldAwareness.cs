using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Weather;

namespace Mirthwood.Echoes
{
    internal static class WorldAwareness
    {
        static readonly Dictionary<NPCController,SpeakerPerception.ApproachedWalk> approaches = new Dictionary<NPCController,SpeakerPerception.ApproachedWalk>();
        internal static void ClearApproaches() => approaches.Clear();

        static WorldAwareness()
        {
            SceneManager.sceneLoaded += (_, __) => ClearApproaches();
            SceneManager.sceneUnloaded += _ => ClearApproaches();
        }

        static SpeakerPerception.ApproachedWalk ObserveWalk(NPCController npc)
        {
            try { return SpeakerPerception.ApproachedWalk.Observe(npc); }
            catch (Exception e) {
                Debug.LogWarning("Echoes could not observe a resident's approach. " + e.GetBaseException().Message);
                return null;
            }
        }

        static void RecordWalk(SpeakerPerception.ApproachedWalk walk)
        {
            if (walk == null) return;
            foreach (var key in approaches.Where(p => p.Value.Expired).Select(p => p.Key).ToArray()) approaches.Remove(key);
            if (approaches.Count < 12 || approaches.ContainsKey(walk.Speaker)) approaches[walk.Speaker] = walk;
        }

        [HarmonyPatch(typeof(PlayerChat), "OnTriggerEnter")]
        internal static class PlayerContact
        {
            static void Prefix(PlayerChat __instance, Collider other)
            {
                // The stock method can immediately react to the approaching player's outfit.
                // Use precisely the contact the game is about to process, before that reaction.
                if (!Plugin.CanObserveApproach || __instance.Character != PlayerCharacterController.Instance || !other) return;
                var chat = other.GetComponent<NPCChatController>();
                if (chat && chat.chatEnabled && !chat.GetIsBusy() && chat.Character is NPCController npc) RecordWalk(ObserveWalk(npc));
            }
        }

        [HarmonyPatch(typeof(PlayerChat), "OnTriggerExit")]
        internal static class PlayerLeft
        {
            static void Prefix(PlayerChat __instance, Collider other)
            {
                if (approaches.Count > 0 && __instance.Character == PlayerCharacterController.Instance &&
                    other && other.GetComponent<NPCChatController>()?.Character is NPCController npc) approaches.Remove(npc);
            }
        }

        [HarmonyPatch(typeof(NPCController), "Update")]
        internal static class BeforeSocialStop
        {
            static void Prefix(NPCController __instance, NPCChatController ____chatController, NavMeshAgent ____agent, bool ___inCombatMode, bool ____eventLockActive,
                out SpeakerPerception.ApproachedWalk __state)
            {
                __state = null;
                if (!Plugin.CanObserveApproach || ___inCombatMode || ____eventLockActive || !____chatController || !____chatController.IsChatting ||
                    ____chatController.GetIsBusy() || GroupGathering.Holds(____chatController) || !____agent || !____agent.isActiveAndEnabled ||
                    !____agent.isOnNavMesh || ____agent.isStopped) return;
                var player = PlayerCharacterController.Instance;
                if (!player || (player.transform.position - __instance.transform.position).sqrMagnitude > 9f) return;
                var chat = player.GetComponent<PlayerChat>();
                approaches.TryGetValue(__instance, out var contact);
                if (chat && (____chatController.chatTarget == chat || ReferenceEquals(Plugin.Selected.GetValue(chat), ____chatController) ||
                    contact != null && !contact.Expired)) __state = ObserveWalk(__instance) ?? contact;
            }

            static void Postfix(SpeakerPerception.ApproachedWalk __state, NavMeshAgent ____agent)
            {
                if (__state == null || !____agent || !____agent.isActiveAndEnabled || !____agent.isOnNavMesh || !____agent.isStopped) return;
                __state.StoppedForChat = true;
                RecordWalk(__state);
            }
        }

        static string TakeApproach(NPCController speaker)
        {
            if (!approaches.TryGetValue(speaker, out var walk)) return null;
            approaches.Remove(speaker);
            var player = PlayerCharacterController.Instance;
            var chat = speaker.GetComponent<NPCChatController>();
            if (!walk.StoppedForChat || !player || !chat || chat.GetIsBusy() || GroupGathering.Holds(chat) ||
                (player.transform.position - speaker.transform.position).sqrMagnitude > 9f ||
                chat.chatTarget is NPCChatController partner && partner.IsChatting && partner.chatTarget == chat) return null;
            return walk.PausedActivity(speaker);
        }

        // The active Talk or group guest owns this observation until the visit ends.
        // It always describes what the resident was doing at the start of that visit.
        internal sealed class Interruption
        {
            readonly string activity, load;
            readonly int day;

            Interruption(string activity, int day)
            {
                this.activity = activity;
                this.day = day;
                load = NativeCues.Session;
            }

            internal static Interruption Observe(NPCController speaker, bool playerApproached = false)
            {
                var clock = DayNightCycle.Instance;
                if (!InteractionHooks.Compatible || !speaker || clock == null) return null;
                try
                {
                    var beforeStop = playerApproached ? TakeApproach(speaker) : null;
                    var activity = SpeakerPerception.InterruptedActivity(speaker) ?? beforeStop;
                    return string.IsNullOrWhiteSpace(activity) ? null : new Interruption(activity, Mathf.FloorToInt(clock.Now));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("Echoes could not identify the interrupted activity. " + e.GetBaseException().Message);
                    return null;
                }
            }

            internal string AtStart
            {
                get
                {
                    var clock = DayNightCycle.Instance;
                    return load == NativeCues.Session && clock != null && Mathf.FloorToInt(clock.Now) == day ? activity : null;
                }
            }
        }

        internal static JObject Capture(NPCController speaker, string interruptedActivity = null)
        {
            if (!InteractionHooks.Compatible || !speaker || !speaker.isActiveAndEnabled || string.IsNullOrEmpty(speaker.CharacterInfo?.id)) return null;
            string owner = Plugin.Owner(speaker);
            var current = Plugin.World();
            if (string.IsNullOrEmpty(owner) || current == null) return null;

            var world = JObject.FromObject(current);
            var snapshot = new JObject
            {
                ["world"] = world, ["day"] = world["day"].DeepClone(), ["owner"] = owner, ["nativeId"] = speaker.CharacterInfo.id
            };
            if (!string.IsNullOrWhiteSpace(interruptedActivity)) snapshot["interruptedActivity"] = interruptedActivity;
            try
            {
                snapshot.Merge(SpeakerPerception.Capture(speaker));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Echoes could not read the resident's surroundings. " + e.GetBaseException().Message);
            }
            try
            {
                WorldCalendar.AddTo(snapshot, speaker, SpeakerPerception.KnowsBrightoak(speaker));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Echoes could not read the resident's calendar. " + e.GetBaseException().Message);
            }
            return snapshot;
        }
    }
}
