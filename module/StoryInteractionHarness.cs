using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using InfinityPBR.Modules;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.AI;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Events;
using _Mirthwood.Scripts.Demo.GameModules;
using _Mirthwood.Scripts.Demo.Items;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.Utils;
using _Mirthwood.Scripts.Demo.Stories;
using _Mirthwood.Scripts.Demo.Weather;
using _Mirthwood.Scripts.Demo.Triggers;
using _Mirthwood.Scripts.Runtime.Monobehaviors;
using _Mirthwood.Scripts.Runtime.Demo.Effects;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        static IEnumerator VerifyFarmHousehold(JObject report)
        {
            var manager=MirthwoodPlayer.Instance.SpouseManager;var type=typeof(SpouseManager);
            var skillField=AccessTools.Field(type,"skillItemList");var original=(SkillsItemList)skillField.GetValue(manager);
            var skills=UnityEngine.Object.Instantiate(original);skills.cropMin=skills.animalMin=1;skills.cropMax=skills.animalMax=2;
            var oldTags=TagSystem.m_taggedObjects;var stage=new GameObject("Echoes isolated household");stage.SetActive(false);
            GameObject Add(string name){var go=new GameObject(name);go.transform.SetParent(stage.transform);return go;}
            var animal=Add("Cow").AddComponent<Animal>();var crop=Add("Crop").AddComponent<Growable>();var chest=Add("Storage").AddComponent<InventoryChest>();
            void Assign(string name,params GameObject[] values){TagSystem.m_taggedObjects[name.GetHashCode()]=values.ToList();}
            try{
                TagSystem.m_taggedObjects=new Dictionary<int,List<GameObject>>(oldTags);
                Assign("playerfarm",stage);Assign("crop",crop.gameObject);Assign("cow",animal.gameObject);Assign("player",animal.gameObject);
                Assign("pig");Assign("goat");Assign("chicken");Assign("playerstorage",chest.gameObject);skillField.SetValue(manager,skills);
                Case(report,"Household chest removal matches the item count",()=>{
                    AccessTools.Field(typeof(Inventory),"startingItems").SetValue(chest,Array.Empty<Item>());
                    AccessTools.Method(typeof(Inventory),"Awake").Invoke(chest,null);chest.SetMaxItems(16);
                    var item=skills.cooking.First(x=>x.item&&x.min>0);Require(chest.AddItem(item.item,Math.Max(item.max,1),true),"Could not stock the test chest.");
                    int before=chest.ItemCount(item.item),seen=Recorded("household");
                    AccessTools.Method(type,"GiveRemoveItem").Invoke(manager,new object[]{new[]{item},false,skills.skillTexts[0]});
                    int removed=before-chest.ItemCount(item.item);
                    Require(removed>0&&Recorded("household")==seen+1&&LastFact("household").Contains(removed+" "+NativeCues.ItemName(item.item)+" was taken"),"No confirmed chest removal.");
                });
                Case(report,"Household animal happiness and its lower limit",()=>{
                    AccessTools.Field(typeof(Animal),"_happiness").SetValue(animal,55f);int seen=Recorded("household");
                    var act=AccessTools.Method(type,"ReduceAnimalsHappiness");act.Invoke(manager,new object[]{skills.skillTexts[0]});
                    Require(animal.Happiness<55f&&Recorded("household")==seen+1,"No observed animal happiness decrease.");
                    AccessTools.Field(typeof(Animal),"_happiness").SetValue(animal,-10f);act.Invoke(manager,new object[]{skills.skillTexts[0]});
                    Require(animal.Happiness==-10f&&Recorded("household")==seen+1,"An unchanged animal generated a memory.");
                });
                int count=Recorded("household");
                AccessTools.Method(type,"WiltRandomCrops").Invoke(manager,new object[]{skills.skillTexts[0]});
                yield return null;
                Case(report,"Native household wilting removes its crop",()=>Require(!crop&&Recorded("household")==count+1&&LastFact("household").Contains("crop wilted"),"No matched wilted crop."));
            }finally{skillField.SetValue(manager,original);TagSystem.m_taggedObjects=oldTags;UnityEngine.Object.Destroy(stage);UnityEngine.Object.Destroy(skills);}
        }

        IEnumerator PassingGestures(JObject report,NPCController npc)
        {
            var player=PlayerCharacterController.Instance;
            var brain=npc.GetComponent<NPCChatController>();
            var relation=npc.CharacterInfo.relationships[player.CharacterInfo.id];
            float original=relation.friendship,reset=brain.resetChatActiveTime;
            bool ready=brain.isChatReady;
            var rows=new JArray();report["passingGestures"]=rows;
            try{
                npc.transform.position=player.transform.position+Vector3.right;
                brain.isChatReady=false;
                try{
                    host.enabled=false;ShowQuickMenu(npc);
                    yield return new WaitForSeconds(.25f);
                    yield return Capture("latest-quick-reference.png",ok=>report["quickReference"]=ok);
                }finally{host.enabled=true;}
                brain.isChatReady=false;ShowQuickMenu(npc);
                yield return new WaitForSeconds(.25f);
                yield return Capture("latest-quick-greetings.png",ok=>report["quickCapture"]=ok);
                foreach(string kind in new[]{"Greet","Jeer"}){
                    var row=new JObject{["action"]=kind};rows.Add(row);
                    npc.transform.position=player.transform.position+Vector3.right;
                    brain.isChatReady=false;relation.friendship=20;
                    int expected=kind=="Greet"?1:-2;
                    Case(report,kind+" uses the native menu and scores the gesture once",()=>{
                        var menu=ShowQuickMenu(npc);int seen=Recorded("social");
                        SelectQuickAction(menu,kind,2);
                        Require(!brain.isChatReady&&relation.friendship==20&&Recorded("social")==seen,"Wrong player triggered the gesture.");
                        SelectQuickAction(menu,kind);
                        row["change"]=relation.friendship-20;
                        Require(brain.isChatReady&&brain.resetChatActiveTime>DayNightCycle.Instance.Now&&relation.friendship==20+expected,"Wrong native cooldown or friendship.");
                        Require(Recorded("social")==seen+1&&LastFact("social").Contains(kind=="Greet"?"greeted me":"jeered at me"),"Incorrect participant or gesture.");
                        Require(!Visible&&passing==null&&!GameState.IsPaused&&!ChatMenuController.IsOpen(),"A passing gesture opened a conversation.");
                    });
                    bool uninterrupted=true;float started=Time.time,until=Time.unscaledTime+10f;
                    while(Time.time-started<5.2f&&Time.unscaledTime<until){
                        uninterrupted&=!Visible&&passing==null&&!GameState.IsPaused&&!ChatMenuController.IsOpen();
                        yield return null;
                    }
                    Case(report,kind+" returns to gameplay and respects greeting availability",()=>{
                        Plugin.Selected.SetValue(player.GetComponent<PlayerChat>(),brain);
                        var after=Choices(player.GetComponent<PlayerChat>());
                        Require(uninterrupted&&Time.time-started>=5.2f&&relation.friendship==20+expected,"The delayed reaction opened a conversation or scored twice.");
                        Require(after.Any(o=>o.Name=="Talk")&&!after.Any(o=>o.Name=="Greet"||o.Name=="Jeer"),"Native gesture cooldown was bypassed.");
                        brain.resetChatActiveTime=DayNightCycle.Instance.Now-1;
                        var expired=Choices(player.GetComponent<PlayerChat>());
                        Require(expired.Any(o=>o.Name=="Greet")&&expired.Any(o=>o.Name=="Jeer"),"Passing actions did not return after expiry.");
                        row["uninterrupted"]=uninterrupted;
                    });
                }
                Case(report,"A nearby resident keeps their own passing remark",()=>{
                    var other=ComparisonResidents().Select(FindResident).First(n=>n && n!=npc);
                    const string aside="I was speaking to you over here.";
                    NativeCues.Recent(other,null,"remark",aside);
                    Require((string)NativeCues.Take(other)?["remark"]?["text"]==aside,"The other resident lost their own remark.");
                });
                yield return ReadCue(report,npc,"I was rude just then. What did I do?");
                Case(report,"Following up on Jeer keeps the original interaction separate",()=>{
                    var turn=((JArray)report["turns"]).Last;
                    string id=(string)NativeCues.Trace.Last(e=>(string)e["kind"]=="social"&&e["people"][0]["description"].ToString().Contains("jeered at me"))["id"];
                    Require((string)turn["context"]["event"]==id&&turn["context"]["remark"]["text"].ToString().Contains("jeered at me"),"The typed turn lost its actual Jeer.");
                });
            }finally{brain.isChatReady=ready;brain.resetChatActiveTime=reset;relation.friendship=original;}
        }

        IEnumerator StoryCases(JObject report,NPCController npc,NPCController next)
        {
            var pc=PlayerCharacterController.Instance;var chat=pc.GetComponent<PlayerChat>();var brain=npc.GetComponent<NPCChatController>();
            Case(report,"Direct Talk keeps native passing gestures and stock controls return when disabled",()=>{
                Plugin.Selected.SetValue(chat,brain);chat.chatTarget=brain;
                var rel=npc.CharacterInfo.relationships[pc.CharacterInfo.id];float friendship=rel.friendship;
                var stock=new List<string>();bool wasEnabled=host.enabled;
                try{
                    host.enabled=false;brain.isChatReady=false;stock=Choices(chat).Select(o=>o.Name).ToList();
                    Require(stock.Contains("Greet")&&stock.Contains("Jeer"),"Disabled module did not expose the native first approach.");
                    brain.isChatReady=true;brain.resetChatActiveTime=DayNightCycle.Instance.Now+1;
                    Choices(chat).Single(o=>o.Name=="Talk").Callback(1);
                    Require(ChatMenuController.IsOpen(),"Disabled module did not open the original card screen.");
                }finally{
                    host.enabled=wasEnabled;GameState.UnPause();GameState.SetCurrentState(1,GameState.State.Gameplay);
                }
                Require(!ChatMenuController.IsOpen(),"Enabling Echoes left the stock card screen visible.");
                foreach(bool ready in new[]{false,true}){
                    brain.isChatReady=ready;brain.resetChatActiveTime=DayNightCycle.Instance.Now+1;var menu=Choices(chat);
                    Require(menu.First().Name=="Talk"&&menu.Count(o=>o.Name=="Talk")==1,"Direct Talk was missing or duplicated.");
                    var expected=ready?stock.Where(n=>n!="Greet"&&n!="Jeer"):stock;
                    Require(menu.Skip(1).Select(o=>o.Name).SequenceEqual(expected),"The native actions or their order changed.");
                }
                brain.isChatReady=false;ChooseTalkInCopy(npc);
                Require(Visible&&GameState.IsPaused&&!ChatMenuController.IsOpen(),"First approach did not open the player bubble directly.");Close();
                Require(rel.friendship==friendship,"Opening Talk applied an automatic greeting score.");
                AccessTools.Method(typeof(PlayerChat),"OpenChatMenu").Invoke(chat,null);
                Require(!ChatMenuController.IsOpen(),"The native deck could still open.");
                var card=brain.dnaData.chatActions.First(x=>x.name=="Rumors");
                var reply=(IEnumerator)AccessTools.Method(typeof(PlayerChat),"ReceiveChat",new[]{typeof(DnaData.Dna),typeof(ChatController),typeof(bool)}).Invoke(chat,new object[]{card,brain,false});
                while(reply.MoveNext()){}
                Require(!ChatMenuController.IsOpen()&&!Visible&&NativeCues.Take(npc)?["remark"]?["kind"]?.ToString()=="card","An NPC approach reopened the old deck or lost its subject.");
            });
            yield return PassingGestures(report,npc);
            Case(report,"Special conversation cards match the native unlocked deck",()=>{
                chat.chatTarget=brain;SimpleMenu<ChatMenuController>.Show(1);var menu=ChatMenuController.Instance;menu.Setup();
                var expected=new List<string>();
                foreach(var group in brain.dnaData.chatActions.Where(x=>x!=null).GroupBy(x=>x.category)){
                    if(!group.Any(x=>new[]{"Rumors","Cheer Up","Complain"}.Contains(x.name)))continue;
                    var deck=menu.deckCards[(int)group.Key-1];if(!deck.Card.interactable)continue;
                    AccessTools.Method(typeof(ChatMenuController),"OpenDeck").Invoke(menu,new object[]{deck,group.Key});
                    expected.AddRange(group.Take(menu.subChoices.Length).Select((x,i)=>new{x,i}).Where(v=>menu.subChoices[v.i].Card.interactable&&new[]{"Rumors","Complain","Cheer Up"}.Contains(v.x.name)).Select(v=>v.x.name));
                }
                Require(expected.Count==3&&expected.OrderBy(x=>x).SequenceEqual(InteractionHooks.Cards(npc).Select(x=>x.name).OrderBy(x=>x)),"Native extras: "+string.Join(", ",expected));
                SimpleMenu<ChatMenuController>.Hide();GameState.UnPause();GameState.SetCurrentState(1,GameState.State.Gameplay);
            });
            yield return ReadCue(report,npc,"Have you heard any rumors or gossip?");
            yield return ReadCue(report,npc,"I can see you are having a hard day. You are doing well and I want to lift your spirits.");
            yield return ReadCue(report,npc,"I need to vent: these leaking boots and this miserable rain have ruined my whole morning.");
            npc.transform.position=pc.transform.position+Vector3.right;yield return null;
            Case(report,"Player-directed native bark and unrelated ambient speech",()=>{
                NativeCues.Clear();brain.ShowBark("Harness ambient control","Ambient",0);
                Require(NativeCues.Take(npc)==null,"A generic renderer set a directed cue.");
                AccessTools.Field(typeof(NPCChatController),"_globalNextBarkTime").SetValue(brain,0f);
                ((Dictionary<string,float>)AccessTools.Field(typeof(NPCChatController),"_barkCooldowns").GetValue(brain)).Clear();
                ((Dictionary<string,float>)AccessTools.Field(typeof(BarkManager),"_globalCooldowns").GetValue(BarkManager.Instance)).Clear();
                AccessTools.PropertySetter(typeof(ChatController),"IsChatting").Invoke(brain,new object[]{false});brain.chatEnabled=true;
                EventUtil.DispatchEvent(GameEvent.PLAYER_SELECTED_NPC,brain);
                Require(NativeCues.Take(npc)?["remark"]!=null,"No native approach bark displayed.");
            });
            var guard=UnityEngine.Object.FindObjectsOfType<NPCController>().FirstOrDefault(n=>n.isActiveAndEnabled&&n.CharacterInfo?.job==CharacterData.Character.Jobs.Guard&&
                n.GetComponent<NPCChatController>()?.GetDialogueData()==null&&n.CharacterInfo!=null&&!string.IsNullOrEmpty(Plugin.Owner(n)))??npc;
            var guardBrain=guard.GetComponent<NPCChatController>();var oldJob=guard.CharacterInfo.job;
            var table=(Dictionary<string,List<BarkData>>)AccessTools.Field(typeof(BarkManager),"_barkDataByEventType").GetValue(BarkManager.Instance);
            var originalBarks=table["PlayerInRange"];
            try{
                guard.transform.position=pc.transform.position+Vector3.right;
                guard.CharacterInfo.job=CharacterData.Character.Jobs.Guard;
                var bark=originalBarks.Single(b=>b.ID=="Bark295");
                table["PlayerInRange"]=new List<BarkData>{bark};
                AccessTools.Field(typeof(NPCChatController),"_globalNextBarkTime").SetValue(guardBrain,0f);
                ((Dictionary<string,float>)AccessTools.Field(typeof(NPCChatController),"_barkCooldowns").GetValue(guardBrain)).Clear();
                ((Dictionary<string,float>)AccessTools.Field(typeof(BarkManager),"_globalCooldowns").GetValue(BarkManager.Instance)).Clear();
                AccessTools.PropertySetter(typeof(ChatController),"IsChatting").Invoke(guardBrain,new object[]{false});guardBrain.chatEnabled=true;
                EventUtil.DispatchEvent(GameEvent.PLAYER_SELECTED_NPC,guardBrain);
                yield return ReadCue(report,guard,"Why did you warn me about trouble? Did you see me do anything wrong?");
                Case(report,"A guard's shipped warning supplies its own words and no witnessed crime",()=>{
                    var turn=((JArray)report["turns"]).Last;
                    Require((string)turn["context"]["remark"]?["text"]==bark.LocalizedText()&&(string)turn["context"]["remark"]["owner"]==Plugin.Owner(guard),"The reply did not bind Bark295 to this guard.");
                    report["guardWarning"]=new JObject{["speaker"]=guard.CharacterInfo.name,["text"]=bark.LocalizedText(),["originalJob"]=oldJob.ToString()};
                });
            }finally{guard.CharacterInfo.job=oldJob;table["PlayerInRange"]=originalBarks;}
            foreach(var type in new[]{typeof(BanditRaidController),typeof(VerminPlagueController)}){
                string kind=type==typeof(BanditRaidController)?"BanditRaid":"VerminPlague";
                Case(report,kind+" warning from the selected speaker",()=>{
                    var teller=Storyteller.Instance;
                    var controller=Resources.FindObjectsOfTypeAll(type).OfType<Component>().FirstOrDefault(c=>c.gameObject.scene.IsValid());
                    if(!controller){
                        var catalog=teller.stories.Concat(teller.scheduledStories.Select(s=>s.story)).Where(s=>s!=null).ToList();
                        var story=catalog.FirstOrDefault(s=>s?.eventData && (s.eventData.name+" "+s.eventData.title).Replace(" ","").IndexOf(kind,StringComparison.OrdinalIgnoreCase)>=0);
                        var prefab=story is SpawnPrefabStory?(GameObject)AccessTools.Field(typeof(SpawnPrefabStory),"prefab").GetValue(story):null;
                        if(prefab)controller=prefab.GetComponentInChildren(type,true);
                    }
                    if(!controller){
                        var prefab=Resources.FindObjectsOfTypeAll(type).OfType<Component>().FirstOrDefault();
                        if(prefab)controller=prefab;
                    }
                    if(!controller){
                        var host=new GameObject("Echoes isolated "+kind+" warning");host.SetActive(false);controller=host.AddComponent(type);
                        var groups=Resources.FindObjectsOfTypeAll<BarkGroup>().Concat(Resources.LoadAll<BarkGroup>(""));
                        var group=groups.FirstOrDefault(x=>x.BarkCount>0&&x.name.Replace(" ","").IndexOf(kind,StringComparison.OrdinalIgnoreCase)>=0);
                        if(!group){group=ScriptableObject.CreateInstance<BarkGroup>();AccessTools.Field(typeof(BarkGroup),"barks").SetValue(group,new[]{"Take care, there is trouble nearby."});report[kind+"Source"]="Native coroutine and display, fixture text";}
                        else report[kind+"Source"]="Native coroutine, shipped bark group and display";
                        AccessTools.Field(type,kind=="BanditRaid"?"banditRaidBarkGroup":"verminPlagueBarkGroup").SetValue(controller,group);
                    }else{
                        if(!controller.gameObject.scene.IsValid()){
                            var stage=new GameObject("Echoes isolated "+kind+" event prefab");stage.SetActive(false);
                            controller=UnityEngine.Object.Instantiate(controller,stage.transform);
                        }
                        report[kind+"Source"]="Native event controller and shipped warning";
                    }
                    ((JObject)(report["path"]??(report["path"]=new JObject())))[kind]=report[kind+"Source"].DeepClone();
                    var list=(List<NPCController>)AccessTools.Field(type,"_townsfolk").GetValue(controller);
                    var old=list.ToArray();int count=Recorded("warning");list.Clear();list.Add(npc);
                    try{
                        var bark=(IEnumerator)AccessTools.Method(type,"CoTrigger"+kind+"Barks").Invoke(controller,null);
                        bark.MoveNext();bark.MoveNext();
                        Require(Recorded("warning")==count+1&&LastFact("warning").Contains("warning:"),"The displayed warning was not captured.");
                    }finally{list.Clear();list.AddRange(old);}
                });
            }
            if(Recorded("warning")>0)yield return ReadCue(report,npc,"What did you just warn me about?");
            Case(report,"Spouse distinguishes a missed visit from a visit",()=>{
                var spouse=MirthwoodPlayer.Instance.SpouseManager;
                var type=typeof(SpouseManager);
                npc.CharacterInfo.relationships.SetSpouse("Player");pc.CharacterInfo.relationships.SetSpouse(npc.CharacterInfo.id);
                AccessTools.Field(type,"npcCharacter").SetValue(spouse,npc);
                AccessTools.Field(type,"npcController").SetValue(spouse,npc);
                AccessTools.Field(type,"_inDivorceMode").SetValue(spouse,false);
                var rel=npc.CharacterInfo.relationships[pc.CharacterInfo.id];rel.happiness=65;
                var visited=AccessTools.Field(type,"_wasVisited");var night=AccessTools.Method(type,"OnNightStart");int count=Recorded("household");
                visited.SetValue(spouse,true);night.Invoke(spouse,null);Require(count==Recorded("household"),"Visited night counted as missed.");
                visited.SetValue(spouse,false);night.Invoke(spouse,null);Require(count+1==Recorded("household")&&rel.happiness<65,"Missing native missed visit.");
            });
            if(Recorded("household")>0)yield return ReadCue(report,npc,"Did you miss me when I did not visit?");
            Case(report,"Spouse household gold matches the observed purse change",()=>{
                var manager=MirthwoodPlayer.Instance.SpouseManager;var t=typeof(SpouseManager);
                AccessTools.Field(t,"player").SetValue(manager,pc);
                var skills=AccessTools.Field(t,"skillItemList").GetValue(manager);
                var texts=(Array)AccessTools.Field(skills.GetType(),"skillTexts").GetValue(skills);
                int before=pc.inventory.gold,count=Recorded("household");
                AccessTools.Method(t,"GiveRemoveGold").Invoke(manager,new object[]{true,texts.GetValue(0)});
                int added=pc.inventory.gold-before;
                Require(added>0&&Recorded("household")==count+1&&LastFact("household").Contains("increased by "+added+"."),"No matching household gold transfer.");
            });
            Case(report,"Spouse items enter the resident's own inventory",()=>{
                var manager=MirthwoodPlayer.Instance.SpouseManager;var t=typeof(SpouseManager);
                var skills=AccessTools.Field(t,"skillItemList").GetValue(manager);
                var items=(Array)AccessTools.Field(skills.GetType(),"cooking").GetValue(skills);
                var texts=(Array)AccessTools.Field(skills.GetType(),"skillTexts").GetValue(skills);
                int count=Recorded("household");AccessTools.Method(t,"GiveRemoveItem").Invoke(manager,new object[]{items,true,texts.GetValue(0)});
                Require(Recorded("household")==count+1&&LastFact("household").Contains("to my household inventory."),"Spouse's native inventory did not receive the item.");
            });
            yield return VerifyFarmHousehold(report);
            Case(report,"Mail sender, opening and attachment",()=>{
                var box=ComponentInScene<Mailbox>();Require(box,"No mailbox in the active scene.");
                var pending=AccessTools.Field(typeof(Mailbox),"_hasNewLetter");pending.SetValue(box,false);
                var characters=((CharacterData)AccessTools.Field(typeof(Mailbox),"characterData").GetValue(box)).characters;
                foreach(var ch in characters.Where(x=>x!=null&&x.id!=pc.CharacterInfo.id))ch.relationships[pc.CharacterInfo.id].friendship=ch.id==next.CharacterInfo.id?90:0;
                int count=Recorded("letter");for(int k=0;k<1800&&!(bool)pending.GetValue(box);k++)box.CheckForNewLetter();
                Require(Recorded("letter")==count+1,"Sender selection produced no attributable letter.");
                AccessTools.Method(typeof(Mailbox),"ReadLetter").Invoke(box,new object[]{1});
                var view=ComponentInScene<NoteView>();Require(view&&view.gameObject.activeInHierarchy,"Letter was not displayed.");view.Hide();
                Require(Recorded("letter")==count+3,"Expected send, open and one delivery.");
                GameState.UnPause();GameState.SetCurrentState(1,GameState.State.Gameplay);
            });
            if(Recorded("letter")>0)yield return ReadCue(report,next,"What did you send in your letter?");
            Case(report,"Generated hand-in records one real journal transition",()=>{
                var gen=MirthwoodPlayer.Instance.QuestGenerator;
                var data=gen.GeneratePantryQuest(QuestType.helping,next);
                MirthwoodQuestTrigger.Instance.GiveQuest(data.quest);
                var entry=MirthwoodPlayer.Instance.QuestList.Get(data.quest);
                Require(entry!=null,"No active generated quest.");
                int count=Recorded("quest");
                string startStatus=entry.status.ToString();bool registered=next.GetComponent<NPCChatController>().ConditionalDialogues.Any(d=>d.quest==data.quest&&d.dialogueData==data.dialogueEnd);
                data.dialogueEnd.GetOnChosenEffect().ApplyEffect(pc,next);
                Require(Recorded("quest")==count+1,"No matching generated hand-in: before="+startStatus+", after="+entry.status+", registered="+registered+", observer="+InteractionHooks.QuestEvidence+", same parent="+ReferenceEquals(entry.Parent(),data.quest));
                data.dialogueEnd.GetOnChosenEffect().ApplyEffect(pc,next);
                Require(Recorded("quest")==count+1,"Repeated completion recorded twice.");
            });
            if(Recorded("quest")>0)yield return ReadCue(report,next,"Did I finish that job for you?");
            Case(report,"Intermediate step, wrong recipient and terminal failure",()=>{
                var generator=MirthwoodPlayer.Instance.QuestGenerator;
                var variant=generator.GeneratePantryQuest(QuestType.freshCatch,npc);
                var q=variant.quest;var original=variant.dialogueEnd.GetOnChosenEffect();
                var trigger=MirthwoodQuestTrigger.Instance;
                var step=new QuestEffect();step.SetEffectData(q,QuestEffect.EffectType.CompleteStep);
                var fail=new QuestEffect();fail.SetEffectData(q,QuestEffect.EffectType.Fail);
                // Exercise the shipped effects on a registered generated hand-in with two steps in the copied game.
                if(q.questSteps.Count==1)q.questSteps.Add(q.questSteps[0].Clone());
                trigger.GiveQuest(q);
                var entry=MirthwoodPlayer.Instance.QuestList.Get(q);int count=Recorded("quest");
                Require(entry!=null&&entry.StepsLeft>=2,"The copy needs a two-step quest.");
                try{
                    variant.dialogueEnd.SetOnChosenEffect(step);int before=entry.StepsLeft;
                    step.ApplyEffect(pc,npc);
                    Require(entry.status==QuestStep.QuestStepStatus.InProgress&&entry.StepsLeft<before&&Recorded("quest")==count+1&&LastFact("quest").Contains("advanced one step"),"The step was not distinguished from a completed quest.");
                    variant.dialogueEnd.SetOnChosenEffect(fail);
                    fail.ApplyEffect(pc,npc);
                    Require(entry.status==QuestStep.QuestStepStatus.Failed&&Recorded("quest")==count+2&&LastFact("quest").Contains("failed"),"No native failure transition.");
                    fail.ApplyEffect(pc,next);Require(Recorded("quest")==count+2,"A wrong recipient recorded the terminal quest.");
                }finally{variant.dialogueEnd.SetOnChosenEffect(original);}
            });
            Case(report,"Funeral guest identity and missing deceased",()=>{
                var funeral=ComponentInScene<FuneralCeremonyController>();
                if(!funeral){var mail=ComponentInScene<Mailbox>();Require(mail&&mail.funeralStory!=null,"No funeral story.");Storyteller.Instance.ForceStartStory(mail.funeralStory);funeral=ComponentInScene<FuneralCeremonyController>();}
                Require(funeral,"No funeral stage after the native story started.");
                int count=Recorded("funeral");
                foreach(string dead in new[]{"",next.CharacterInfo.id}){
                    funeral.SetDeceasedCharacter(dead);
                    var co=(IEnumerator)AccessTools.Method(typeof(FuneralCeremonyController),"CoCeremonyStart").Invoke(funeral,null);co.MoveNext();co.MoveNext();
                    if(dead=="")Require(count==Recorded("funeral"),"The default player ID produced a funeral memory.");
                }
                Require(Recorded("funeral")>count,"No real funeral guest was assigned.");
            });
            GameState.SetCurrentGeneralState(GameState.State.Gameplay);GameState.AllowInput=true;GameState.UnPause();
            var attendees=NativeCues.Trace.Where(x=>(string)x["kind"]=="funeral").Select(x=>(string)x["people"][0]["owner"]).Distinct().ToArray();
            var mourner=attendees.Select(FindResident).Where(x=>x&&OverheadSpeech.Board(x)!=null&&x.GetComponent<NPCChatController>()&&x.GetComponent<NPCChatController>().GetDialogueData()==null)
                .OrderByDescending(x=>x==npc).FirstOrDefault();
            if(mourner){
                Case(report,"Funeral relationships require a recorded link to this deceased resident",()=>{
                    var person=NativeCues.Trace.Last(e=>(string)e["kind"]=="funeral"&&(string)e["people"][0]["owner"]==Plugin.Owner(mourner))["people"][0];
                    Require((string)person["details"]["deceasedId"]==next.CharacterInfo.id&&(string)person["details"]["relationship"]==InteractionHooks.FuneralRelationship(mourner.CharacterInfo.relationships,next.CharacterInfo.id),"The relationship came from the wrong person.");
                    var relations=new Relationships();string id=next.CharacterInfo.id;
                    Require(InteractionHooks.FuneralRelationship(relations,id)==null&&relations.GetSaveData().characterIDs.Count==0,"A missing relationship was created.");
                    relations[id].friendship=40;Require(InteractionHooks.FuneralRelationship(relations,id)=="Friend","The native friendship band was lost.");
                    relations.SetSpouse(id);Require(InteractionHooks.FuneralRelationship(relations,id)=="Spouse","A confirmed spouse was lost.");
                });
                yield return ReadCue(report,mourner,"Did you attend the funeral?");
                yield return ReadCue(report,mourner,"My condolences. What was your relationship with the person who died?");
            }
            var statue=ComponentInScene<Statue>();
            if(statue)Case(report,"Shrine theft supplies no NPC witness",()=>{
                var start=statue.transform.position;int old=NativeCues.Trace.Count;
                npc.transform.position=start+Vector3.right;AccessTools.Method(typeof(Statue),"OnSteal").Invoke(statue,new object[]{1});
                npc.transform.position=start+Vector3.right*60;AccessTools.Method(typeof(Statue),"OnSteal").Invoke(statue,new object[]{1});
                Require(!NativeCues.Trace.Skip(old).Any(x=>(string)x["kind"]=="theft"),"An unsupported witness was recorded.");
            });
        }
    }
}
