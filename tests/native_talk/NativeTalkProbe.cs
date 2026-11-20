using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Items;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.Tests;
using _Mirthwood.Scripts.Demo.Weather;

namespace Mirthwood.Echoes.Tests
{
    [BepInPlugin("mirthwood.echoes.test.nativetalk","Mirthwood: Echoes native Talk test","1.0.0")]
    [BepInDependency("mirthwood.echoes")]
    public sealed class NativeTalkProbe : BaseUnityPlugin
    {
        static readonly FieldInfo Selected = AccessTools.Field(typeof(PlayerChat), "SelectedNPC");
        static readonly MethodInfo Options = AccessTools.Method(typeof(PlayerChat), "GenerateOptions");
        static readonly string[] Ordinary = {"Small Talk","Leisure","Intellectual","Deep","Dumb"};
        static readonly Type Echoes = AccessTools.TypeByName("Mirthwood.Echoes.Plugin");

        void Awake()
        {
            if (!Environment.GetCommandLineArgs().Contains("-echoes-harness")) return;
            var harness = AccessTools.TypeByName("Mirthwood.Echoes.IsolatedGameRun");
            var active = AccessTools.Field(harness,"Active").GetValue(null);
            if (active == null || !(bool)AccessTools.Method(harness,"PathsMatch").Invoke(active,null)) return;
            var harmony = new Harmony("mirthwood.echoes.test.nativetalk");
            harmony.Patch(AccessTools.Method(Echoes,"ExerciseHooks"),postfix:new HarmonyMethod(typeof(NativeTalkProbe),nameof(Run)));
        }

        static List<IContextMenu.Option> Choices(PlayerChat chat) =>
            (List<IContextMenu.Option>)Options.Invoke(chat,null);

        static void Run(object __instance)
        {
            var proof = new JObject();
            try {
                var player=PlayerCharacterController.Instance;
                var chat=player.GetComponent<PlayerChat>();
                var residents=UnityEngine.Object.FindObjectsOfType<NPCChatController>()
                    .Where(n=>n!=null && n.isActiveAndEnabled && n.Character is NPCController &&
                        n.Character.CharacterInfo!=null && n.chatEnabled && n.GetDialogueData()==null &&
                        !n.Character.CharacterInfo.relationships.IsPlayersSpouse() && n.dnaData?.chatActions!=null)
                    .OrderBy(n=>(n.transform.position-player.transform.position).sqrMagnitude).ToArray();
                var resident=residents.First();
                proof["run"] = (string)AccessTools.Field(AccessTools.TypeByName("Mirthwood.Echoes.IsolatedGameRun"),"Id")
                    .GetValue(AccessTools.Field(AccessTools.TypeByName("Mirthwood.Echoes.IsolatedGameRun"),"Active").GetValue(null));
                proof["resident"]=resident.Character.CharacterInfo.name;
                proof["offeredActions"]=JArray.FromObject(resident.dnaData.chatActions.Select(a=>new {a.name, deck=a.category.ToString(), localized=a.DnaNameLocalized}));
                proof["menu"]=CheckMenu(__instance,chat,resident);
                proof["scoring"]=CheckScores(resident,player);
            } catch(Exception ex){proof["error"]=ex.GetBaseException().ToString();}
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MirthwoodEchoes","Diagnostics");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,"latest-native-talk.json"),proof.ToString(Formatting.Indented));
        }

        static JObject CheckMenu(object plugin, PlayerChat chat, NPCChatController npc)
        {
            var previous=Selected.GetValue(chat);var oldTarget=chat.chatTarget;
            var ready=npc.isChatReady;var expires=npc.resetChatActiveTime;
            var dialogue=AccessTools.Field(Echoes,"dialogue").GetValue(plugin);
            var type=dialogue.GetType();
            var close=AccessTools.Method(type,"Close");
            bool clicked=false;
            try {
                Selected.SetValue(chat,npc);chat.chatTarget=npc;
                npc.resetChatActiveTime=DayNightCycle.Instance.Now+1; npc.isChatReady=false;
                var before=Choices(chat).Select(o=>o.Name).ToArray();
                npc.isChatReady=true;
                var stockRound=AccessTools.Field(type,"stockRound");
                var previousRound=stockRound.GetValue(null);
                List<IContextMenu.Option> original;
                try{stockRound.SetValue(null,true);original=Choices(chat);}
                finally{stockRound.SetValue(null,previousRound);}
                var modified=Choices(chat);
                var talk=modified.Single(o=>o.Name=="Talk");
                talk.Callback(2);
                bool guarded=!(bool)AccessTools.PropertyGetter(type,"Visible").Invoke(dialogue,null);
                talk.Callback(1);
                bool visible=(bool)AccessTools.PropertyGetter(type,"Visible").Invoke(dialogue,null);
                clicked=visible;
                var actual=AccessTools.Field(type,"resident").GetValue(dialogue) as NPCController;
                return JObject.FromObject(new {beforeGreeting=before,afterGreeting=modified.Select(o=>o.Name),
                    sameOptions=original.Select(o=>o.Name).SequenceEqual(modified.Select(o=>o.Name)),
                    otherCallbacksPreserved=original.Where(o=>o.Name!="Talk").All(o=>modified.Single(m=>m.Name==o.Name).Callback.Method==o.Callback.Method),
                    guarded,clicked,visible,correctTarget=actual==npc.Character,paused=GameState.IsPaused && Time.timeScale==0});
            } finally {
                if(clicked)close.Invoke(dialogue,null);
                Selected.SetValue(chat,previous);chat.chatTarget=oldTarget;
                npc.isChatReady=ready;npc.resetChatActiveTime=expires;
            }
        }

        static JArray CheckScores(NPCChatController npc, PlayerCharacterController player)
        {
            var self=npc.Character.CharacterInfo;var speaker=player.CharacterInfo;
            var rel=self.relationships[speaker.id];var initialMood=self.mood;
            float friendship=rel.friendship,romance=rel.romance,happiness=rel.happiness;
            var initialRandom=UnityEngine.Random.state;
            var rows=new JArray();
            void Restore(){rel.friendship=friendship;rel.romance=romance;rel.happiness=happiness;self.mood=initialMood;}
            try {
                foreach(int seed in new[]{31,32,33,34,35,36}) {
                    foreach(var topic in Ordinary) {
                        Restore();
                        var action=npc.dnaData.chatActions.Single(a=>a.name==topic && a.category==DnaData.Dna.DnaCategories.Chat);
                        UnityEngine.Random.InitState(seed);
                        var state=UnityEngine.Random.state;
                        float roll=UnityEngine.Random.Range(-.5f,.5f);
                        // Reuse the identical draw inside the game's real scorer on this isolated frame.
                        UnityEngine.Random.state=state;
                        var combined=(self.personality+speaker.personality*.25f).Normalized();
                        float estimate=combined.DotDistance(action)+self.mood.DotDistance(action)*(action.negativeMoodComparison?-.5f:.5f)+roll;
                        DnaTesting.ScoreAction(action,self,speaker,out float actual,true);
                        var delta=rel.friendship-friendship;
                        rows.Add(JObject.FromObject(new {seed,topic,roll,estimate,actual,friendshipBefore=friendship,
                            friendshipAfter=rel.friendship,delta,romanceDelta=rel.romance-romance,happinessDelta=rel.happiness-happiness,
                            reaction=estimate>.75f?"loved":estimate>.25f?"liked":estimate<-.75f?"hated":estimate<-.25f?"disliked":"neutral",
                            matches=Math.Sign(Math.Abs(estimate)<=.25f?0:estimate)==Math.Sign(actual),
                            moodChanged=!self.mood.values.SequenceEqual(initialMood.values)}));
                    }
                }
                return rows;
            } finally {Restore();UnityEngine.Random.state=initialRandom;}
        }
    }
}
