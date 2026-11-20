using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using InfinityPBR.Modules;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.GameModules;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Runtime.Demo.Menus.Views;
using _Mirthwood.Scripts.Runtime.Demo.ScriptableObjects;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        static IEnumerator SetTestBackend(bool online)
        {
            var run = IsolatedGameRun.Active;
            if (!InCopy() || run.TalkAddress == null) throw new InvalidOperationException("The outage check requires a verified private backend.");
            string prefix = Path.Combine(run.Root, online ? "online" : "offline");
            File.WriteAllText(prefix + ".request", run.Id);
            yield return Await(() => File.Exists(prefix + ".ready") && File.ReadAllText(prefix + ".ready") == run.Id,
                "The disposable backend did not signal " + (online ? "online." : "offline."), 35);
            File.Delete(prefix + ".request");
            File.Delete(prefix + ".ready");
        }

        IEnumerator CheckBackendOutage(string[] owners, JObject report)
        {
            var run = IsolatedGameRun.Active;
            string snapshot = Path.Combine(run.Root, "BeforeOutage");
            var proof = new JObject();
            report["outage"] = proof;
            try {
                CaptureSlot(snapshot);
                var npc = FindResident(owners[0]);
                ChooseTalkInCopy(npc);
                yield return Await(() => Ready, "The resident was unavailable before the outage.");
                string c = campaign, a = actor;
                var outbox = exchange;
                float friendship = Friendship(npc);
                yield return SetTestBackend(false);
                const string words = "Which do you prefer for fun, chess or fishing?";
                playerBubble.Input.text = words;
                playerBubble.Input.onSubmit.Invoke(words);
                var intent = outbox.Pending();
                string id = (string)intent["id"];
                yield return Await(() => Visible && !busy && phase == Phase.Draft, "The disconnected Talk did not return to editing.", 16);
                proof["draftRetained"] = playerBubble.Input.text == words && (string)outbox.Pending()?["id"] == id &&
                    (string)outbox.Pending()?["stage"] == "new" && Friendship(npc) == friendship;
                Close();
                proof["returnedToGame"] = GameState.GetCurrentState(1) == GameState.State.Gameplay;
                yield return OfflineAuthoredSpeak(owners[2], proof);
                var offers = new JArray();
                proof["nativeOffers"] = offers;
                yield return OfflineQuestChoice(owners[1], QuestType.helping, true, offers);
                yield return OfflineQuestChoice(owners[2], QuestType.hunting, false, offers);
                yield return SetTestBackend(true);
                ChooseTalkInCopy(npc);
                yield return Await(() => Visible && !busy && phase == Phase.Reading, "The original Talk did not recover after the backend restarted.");
                using (var req = Post(Address + "dialogue/prepare", new { id, campaign=c, actor=a, text=words, context=intent["context"] })) {
                    yield return req.SendWebRequest();
                    var packet = JObject.Parse(req.downloadHandler.text);
                    string reply = (string)packet["reply"], cue = (string)packet["handoff"]?["cue"];
                    string bubble = string.IsNullOrEmpty(cue) ? reply : reply + "\n*" + cue.Replace("*","") + "*";
                    proof["talk"] = new JObject {["id"]=id,["reply"]=reply,["text"]=words,["topic"]=packet["topic"],
                        ["passed"]=(string)packet["state"]=="complete" && outbox.SavedResult(id)!=null &&
                            outbox.Pending()==null && npcBubble.VisibleText==new BubbleText(bubble,false).Plain};
                }
                Close();
                yield return LoadSnapshot(snapshot, owners[0]);
                var reputations = new JArray();
                proof["reputations"] = reputations;
                foreach (string owner in owners.Skip(1))
                    yield return ImpressionDialogueCases(snapshot, owner, reputations, "introducedInfamous");
                proof["passed"] = (bool)proof["draftRetained"] && (bool)proof["returnedToGame"] && (bool)proof["authored"]["passed"] &&
                    (bool)proof["talk"]["passed"] && offers.Count==2 && offers.All(o=>(bool)o["passed"]) &&
                    reputations.Count==2 && reputations.All(o=>(bool)o["passed"]);
                RequireQuest((bool)proof["passed"], "An outage check did not complete.");
            } finally { RemoveSnapshot(run.Root,snapshot); }
        }

        IEnumerator OfflineAuthoredSpeak(string owner,JObject proof)
        {
            var npc = FindResident(owner);
            var brain = npc.GetComponent<NPCChatController>();
            // Place a real authored game asset on this resident for a repeatable Speak option in the copy.
            var source = Resources.FindObjectsOfTypeAll<DialogueData>()
                .Where(d => d && !d.isGenerated && !string.IsNullOrWhiteSpace(d.Dialogue) && d.Choices?.Length == 1 &&
                    !string.IsNullOrWhiteSpace(d.Choices[0].choiceText) && d.Choices[0].nextDialogue)
                .OrderBy(d => d.name,StringComparer.Ordinal).First();
            var dialogue = UnityEngine.Object.Instantiate(source);
            var condition = new ConditionalDialogue { dialogueData = dialogue };
            brain.AddConditionalDialogue(condition);
            try {
                var view = ChooseSpeak(npc);
                yield return null; view.Continue();
                yield return Await(() => view.choiceButtons[0].isActiveAndEnabled, "The authored choice did not open offline.");
                bool native = QuestPanel.Current == null && ReferenceEquals(AccessTools.Field(typeof(DialogueView),"_dialogueData").GetValue(view),dialogue);
                string label = view.choiceButtons[0].GetComponentInChildren<TextMeshProUGUI>(true).text;
                view.choiceButtons[0].onClick.Invoke();
                yield return null;
                bool advanced = ReferenceEquals(AccessTools.Field(typeof(DialogueView),"_dialogueData").GetValue(view),dialogue.Choices[0].nextDialogue);
                proof["authored"] = JObject.FromObject(new { asset=source.name, choice=label, native, advanced, passed=native && advanced && !string.IsNullOrWhiteSpace(label) });
                if (view.isActiveAndEnabled) view.HideDialogue();
            } finally { brain.RemoveConditionalDialogue(condition); UnityEngine.Object.Destroy(dialogue); }
        }

        IEnumerator OfflineQuestChoice(string owner, QuestType type, bool accept, JArray results)
        {
            var npc = FindResident(owner);
            var brain = npc.GetComponent<NPCChatController>();
            var generated = MirthwoodPlayer.Instance.QuestGenerator.GeneratePantryQuest(type,npc);
            var quest = generated.quest;
            var start = generated.dialogueStart;
            var view = ChooseSpeak(npc);
            yield return null; view.Continue();
            yield return Await(() => QuestPanel.Current==null && view.choiceButtons[1].isActiveAndEnabled,
                "The native buttons did not return when the quest service was offline.", 18);
            var buttons = view.choiceButtons.Take(2).ToArray();
            var labels = buttons.Select(b=>b.GetComponentInChildren<TextMeshProUGUI>(true).text).ToArray();
            bool stock = labels.SequenceEqual(new[]{"Accept","Decline"}) && buttons.All(b=>b.isActiveAndEnabled && b.interactable);
            int choice = accept?0:1;
            RequireQuest(stock,"The offline quest buttons did not restore their native labels and actions.");
            buttons[choice].onClick.Invoke();
            yield return null;
            int count = MirthwoodPlayer.Instance.QuestList.CountQuestsInProgress(quest);
            bool removed = !brain.ConditionalDialogues.Any(x=>ReferenceEquals(x.dialogueData,start));
            results.Add(JObject.FromObject(new { resident=npc.CharacterInfo.name, type=type.ToString(), action=labels[choice],
                journalCount=count, removed, passed=!view.isActiveAndEnabled && (accept?count==1:removed && count==0) }));
        }
    }
}
