using System;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Weather;

namespace Mirthwood.Echoes
{
    internal static class WorldAwareness
    {
        // The activity belongs to the moment the resident was called away. An editor pause
        // does not age it, and later turns must not treat it as their present occupation.
        internal sealed class Interruption
        {
            readonly string activity, load;
            readonly int day;
            readonly float until;

            Interruption(string activity, int day)
            {
                this.activity = activity;
                this.day = day;
                load = NativeCues.Session;
                until = Time.time + 30f;
            }

            internal static Interruption Observe(NPCController speaker)
            {
                var clock = DayNightCycle.Instance;
                if (!InteractionHooks.Compatible || !speaker || clock == null) return null;
                try
                {
                    var activity = SpeakerPerception.InterruptedActivity(speaker);
                    return string.IsNullOrWhiteSpace(activity) ? null : new Interruption(activity, Mathf.FloorToInt(clock.Now));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("Echoes could not identify the interrupted activity. " + e.GetBaseException().Message);
                    return null;
                }
            }

            internal string Recent
            {
                get
                {
                    var clock = DayNightCycle.Instance;
                    return load == NativeCues.Session && Time.time <= until && clock != null && Mathf.FloorToInt(clock.Now) == day ? activity : null;
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
