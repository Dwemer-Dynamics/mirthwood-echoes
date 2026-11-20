using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Items;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Runtime.Demo.ScriptableObjects;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        static ItemData[] TestTorsoArmor(PlayerCharacterController player)
        {
            var skeleton = player.Visuals.Data.Skeleton;
            return Resources.FindObjectsOfTypeAll<ItemData>()
                .Where(item => item && item.slotData != null && item.slotData.slot == SlotData.Slot.ChestArmor &&
                    !string.IsNullOrEmpty(item.armorSkinName) && skeleton.Data.FindSkin(item.armorSkinName) != null &&
                    !string.IsNullOrWhiteSpace(item.ItemNameLocalized))
                .OrderBy(item => item.armorSkinName, StringComparer.Ordinal)
                .GroupBy(item => item.armorSkinName).Select(group => group.First())
                .GroupBy(item => item.ItemNameLocalized).Select(group => group.First()).Take(2).ToArray();
        }

        // Reload the same copied world for each case, including its conversation history and wardrobe.
        IEnumerator ImpressionDialogueCases(string snapshot, string owner, JArray dialogue, params string[] scenarios)
        {
            if (!InCopy() || IsolatedGameRun.Active.TalkAddress == null) yield break;
            for (int i = 0; i < scenarios.Length; i++) {
                string scenario = scenarios[i];
                if (i > 0) yield return LoadSnapshot(snapshot, owner);
                var npc = FindResident(owner);
                var player = PlayerCharacterController.Instance;
                var person = player.CharacterInfo;
                var armor = TestTorsoArmor(player);
                if (armor.Length != 2) throw new InvalidOperationException("The outfit comparison needs two native chest armors.");
                bool known = scenario == "knownParagon" || scenario == "vanityArmor" || scenario == "underlayers";
                npc.CharacterInfo.relationships = new Relationships();
                npc.CharacterInfo.isPlayerChild = false;
                AccessTools.Field(typeof(NPCController), "_hasMetPlayer").SetValue(npc, known);
                if (known) npc.CharacterInfo.relationships[person.id].friendship = 40;
                person.Renown = scenario == "introducedObscure" ? 0 : 380;
                person.Morality = known ? 100 : -100;
                player.Equipment.UnEquipItem(SlotData.Slot.ChestArmor, true);
                player.Equipment.EquipItem(armor[0], false);
                if (scenario == "vanityArmor" || scenario == "underlayers") player.Equipment.EquipItem(armor[1], true);
                player.hiddenChestArmor = player.hiddenLegsArmor = scenario == "underlayers";
                player.Visuals.SetToBaseSkins();
                player.ShowRestOfEquipment();
                yield return null;
                string words = scenario == "anonymousArmor" || scenario == "vanityArmor" ? "What am I wearing on my chest?" :
                    scenario == "underlayers" ? "Which shirt and trousers can you see me wearing?" :
                    (known ? "" : "My name is " + person.name + ". ") + "What does my name mean to you around here?";
                ChooseTalkInCopy(npc);
                yield return Await(() => Ready, "Could not open ordinary Talk for " + scenario + ".");
                var outbox = exchange;
                string c = campaign, a = actor;
                interruptBeforeFinish = false;
                float start = Time.realtimeSinceStartup;
                playerBubble.Input.text = words;
                playerBubble.Input.onSubmit.Invoke(words);
                var intent = outbox.Pending();
                string id = (string)intent["id"];
                yield return Await(() => phase == Phase.Reading && npcBubble.Visible && npcBubble.VisibleText.Length > 0,
                    "No spoken answer for " + scenario + ".");
                int firstMs = Mathf.RoundToInt(1000 * (Time.realtimeSinceStartup - start));
                yield return Await(() => Visible && !busy && phase == Phase.Reading, "Talk did not finish for " + scenario + ".");
                int finishedMs = Mathf.RoundToInt(1000 * (Time.realtimeSinceStartup - start));
                using (var req = Post(Address + "dialogue/prepare", new { id, campaign = c, actor = a, text = words, context = intent["context"] })) {
                    yield return req.SendWebRequest();
                    var packet = JObject.Parse(req.downloadHandler.text);
                    string reply = (string)packet["reply"], cue = (string)packet["handoff"]?["cue"];
                    string bubble = string.IsNullOrEmpty(cue) ? reply : reply + "\n*" + cue.Replace("*", "") + "*";
                    var row = JObject.FromObject(new { scenario, id, campaign = c, actor = a, text = words, resident = npc.CharacterInfo.name,
                        context = intent["context"], observation = intent["playerImpression"], topic = packet["topic"], branch = packet["reaction"],
                        handoff = packet["handoff"], reply, firstVisibleMs = firstMs, finishedMs });
                    row["passed"] = (string)packet["state"] == "complete" && outbox.Pending() == null && npcBubble.VisibleText == new BubbleText(bubble, false).Plain;
                    dialogue.Add(row);
                    if (!(bool)row["passed"]) throw new InvalidOperationException("The displayed and journaled replies differ for " + scenario + ".");
                    if (scenario == "underlayers" || scenario == "introducedInfamous")
                        yield return Capture("latest-talk-" + scenario + "-reply.png", ok => row["capture"] = ok);
                }
                Close();
            }
            yield return LoadSnapshot(snapshot, owner);
        }

        // Runs immediately before the relationship suite reloads its pre-trial copy of the world.
        IEnumerator CheckPlayerImpressions(NPCController npc, TalkExchange outbox, JObject report)
        {
            if (!InCopy() || IsolatedGameRun.Active.TalkAddress == null) yield break;
            var player = PlayerCharacterController.Instance;
            var person = player.CharacterInfo;
            var equipment = player.Equipment;
            const SlotData.Slot torso = SlotData.Slot.ChestArmor;
            var items = TestTorsoArmor(player);
            if (items.Length != 2) throw new InvalidOperationException("Two distinct visible torso garments are needed for the appearance check.");
            var originalRelations = npc.CharacterInfo.relationships;
            bool playerChild = npc.CharacterInfo.isPlayerChild;
            var met = AccessTools.Field(typeof(NPCController), "_hasMetPlayer");
            var previouslyMet = met.GetValue(npc);
            int originalRenown = person.Renown, originalMorality = person.Morality;
            bool previouslyHidden = player.hiddenChestArmor, previouslyHiddenLegs = player.hiddenLegsArmor;
            var originalMain = equipment.GetItemInSlot(torso, false);
            var originalVanity = equipment.GetItemInSlot(torso, true);
            var impressions = new JObject();
            report["playerImpressions"] = impressions;
            try {
                npc.CharacterInfo.relationships = new Relationships();
                npc.CharacterInfo.isPlayerChild = false;
                met.SetValue(npc, false);
                person.Renown = 380;
                person.Morality = -100;
                player.hiddenChestArmor = false;
                equipment.UnEquipItem(torso, true);
                equipment.EquipItem(items[0], false);
                yield return null;

                const string words = "What do you think of my coat?";
                var first = outbox.Accept(words, resident: npc);
                var view = (JObject)first["playerImpression"];
                impressions["first"] = view.DeepClone();
                bool stranger = (string)view["familiarity"] == "Unmet" && npc.CharacterInfo.relationships.GetSaveData().characterIDs.Count == 0;
                bool illRegarded = (string)view["standing"] == "ill-regarded" && (string)view["renown"] == "famous" &&
                    (string)view["title"] == PlayerTitlesData.Instance.GetTitle(person.Renown, person.Morality);
                bool regular = (string)view["visibleClothing"]?["torso"] == items[0].ItemNameLocalized;
                yield return Capture("latest-talk-equipped.png", ok => impressions["equippedCapture"] = ok);

                npc.CharacterInfo.relationships[person.id].friendship = 40;
                person.Morality = 100;
                equipment.EquipItem(items[1], true);
                yield return null;
                bool frozen = JToken.DeepEquals(view, outbox.Accept(words, resident: npc)["playerImpression"]);
                string id = (string)first["id"];
                outbox.Apply(id, "None", npc);
                outbox.Acknowledge(id);
                var next = outbox.Accept(words, resident: npc);
                var second = (JObject)next["playerImpression"];
                impressions["vanity"] = second.DeepClone();
                bool friend = (string)second["familiarity"] == "Friend" && (string)second["standing"] == "well-regarded" &&
                    (string)second["title"] == PlayerTitlesData.Instance.GetTitle(person.Renown, person.Morality);
                bool vanity = (string)second["visibleClothing"]?["torso"] == items[1].ItemNameLocalized &&
                    second["newlySeen"] is JArray noticed && noticed.Values<string>().Contains("torso");
                yield return Capture("latest-talk-vanity.png", ok => impressions["vanityCapture"] = ok);
                id = (string)next["id"];
                outbox.Apply(id, "None", npc);
                outbox.Acknowledge(id);
                var again = outbox.Accept(words, resident: npc);
                bool familiarCoat = again["playerImpression"]?["newlySeen"] is JArray repeats && !repeats.Values<string>().Contains("torso");
                outbox.Acknowledge((string)again["id"]);

                player.hiddenChestArmor = player.hiddenLegsArmor = true;
                player.Visuals.SetToBaseSkins();
                player.ShowRestOfEquipment();
                yield return null;
                var hidden = PlayerImpression.Observe(npc, null);
                impressions["hidden"] = hidden.DeepClone();
                string visible = (string)hidden["visibleClothing"]?["torso"];
                bool covered = visible != items[0].ItemNameLocalized && visible != items[1].ItemNameLocalized;
                var shirt = equipment.GetItemInSlot(SlotData.Slot.Chest, false);
                var trousers = equipment.GetItemInSlot(SlotData.Slot.Legs, false);
                bool underlayers = shirt && trousers && visible == shirt.ItemNameLocalized &&
                    (string)hidden["visibleClothing"]?["legs"] == trousers.ItemNameLocalized;
                bool hiddenHelmet = !player.hiddenHelmet || hidden["visibleClothing"]?["head"] == null;
                yield return Capture("latest-talk-hidden.png", ok => impressions["hiddenCapture"] = ok);
                impressions["checks"] = JObject.FromObject(new { stranger, illRegarded, regular, friend, vanity, frozen, familiarCoat, covered, underlayers, hiddenHelmet });
                impressions["passed"] = stranger && illRegarded && regular && friend && vanity && frozen && familiarCoat && covered && underlayers && hiddenHelmet &&
                    (bool)impressions["equippedCapture"] && (bool)impressions["vanityCapture"] && (bool)impressions["hiddenCapture"];
            } finally {
                npc.CharacterInfo.relationships = originalRelations;
                npc.CharacterInfo.isPlayerChild = playerChild;
                met.SetValue(npc, previouslyMet);
                person.Renown = originalRenown;
                person.Morality = originalMorality;
                equipment.UnEquipItem(torso, true);
                equipment.UnEquipItem(torso, false);
                player.hiddenChestArmor = previouslyHidden;
                player.hiddenLegsArmor = previouslyHiddenLegs;
                if (originalMain) equipment.EquipItem(originalMain, false);
                if (originalVanity) equipment.EquipItem(originalVanity, true);
                player.Visuals.SetToBaseSkins();
                player.ShowRestOfEquipment();
            }
        }
    }
}
