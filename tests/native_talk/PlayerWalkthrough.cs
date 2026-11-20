using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using Lowscope.Saving;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Items;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Runtime.Demo.ScriptableObjects;

namespace Mirthwood.Echoes.Tests
{
    // This probe is installed for a test process only. Movement and Talk use the game's
    // keyboard bindings. Reading the selected target or bubble never opens it.
    [BepInPlugin("mirthwood.echoes.test.walkthrough","Mirthwood: Echoes player walkthrough","1.0.0")]
    [BepInDependency("mirthwood.echoes")]
    public sealed class PlayerWalkthrough : BaseUnityPlugin
    {
        string root,id;
        readonly JObject proof=new JObject();
        Keyboard keyboard;
        MonoBehaviour gameplayPlugin;
        static object Field(object target,string name)=>AccessTools.Field(target.GetType(),name).GetValue(target);
        static bool Flag(object target,string name)=>(bool)AccessTools.PropertyGetter(target.GetType(),name).Invoke(target,null);

        void Start()
        {
            if(!Environment.GetCommandLineArgs().Contains("-echoes-harness"))return;
            var type=AccessTools.TypeByName("Mirthwood.Echoes.IsolatedGameRun");
            var run=AccessTools.Field(type,"Active").GetValue(null);
            if(run==null||!(bool)AccessTools.Method(type,"PathsMatch").Invoke(run,null)
                ||AccessTools.PropertyGetter(type,"TalkAddress").Invoke(run,null)==null)return;
            root=(string)Field(run,"Root");id=(string)Field(run,"Id");
            string signal=Path.Combine(root,"walkthrough.input");
            if(!File.Exists(signal)||File.ReadAllText(signal)!=id)return;
            StartCoroutine(Check());
        }

        void Stage(string name)
        {
            proof["stage"]=name;
            File.WriteAllText(Path.Combine(root,"walkthrough.json"),proof.ToString());
        }

        IEnumerator Check()
        {
            var play=Play();
            try{
                while(true){
                    bool more;
                    try{more=play.MoveNext();}
                    catch(Exception e){proof["error"]=e.GetBaseException().Message;break;}
                    if(!more)break;
                    yield return play.Current;
                }
            }finally{
                if(keyboard!=null)InputSystem.QueueStateEvent(keyboard,new KeyboardState());
                if(gameplayPlugin&&!gameplayPlugin.enabled)gameplayPlugin.enabled=true;
                Stage(proof["error"]==null?"finished":"failed");
                File.WriteAllText(Path.Combine(root,"walkthrough.finish"),id);
            }
        }

        IEnumerator CheckHeadwear(PlayerCharacterController player,NPCController resident)
        {
            const SlotData.Slot slot=SlotData.Slot.Head;
            var equipment=player.Equipment;
            var original=equipment.GetItemInSlot(slot,false);
            var originalVanity=equipment.GetItemInSlot(slot,true);
            bool originallyHidden=player.hiddenHelmet;
            var observe=AccessTools.Method(AccessTools.TypeByName("Mirthwood.Echoes.PlayerImpression"),"Observe");
            string VisibleHead()=> (string)((JObject)observe.Invoke(null,new object[]{resident,null}))?["visibleClothing"]?["head"];
            void Redraw(){player.Visuals.SetToBaseSkins();player.ShowRestOfEquipment();}
            var hats=Resources.FindObjectsOfTypeAll<ItemData>()
                .Where(item=>item&&item.slotData!=null&&item.slotData.slot==slot&&!string.IsNullOrWhiteSpace(item.armorSkinName)
                    &&!string.IsNullOrWhiteSpace(item.ItemNameLocalized))
                .OrderByDescending(item=>item.ItemNameLocalized.IndexOf("helm",StringComparison.OrdinalIgnoreCase)>=0)
                .ThenBy(item=>item.armorSkinName,StringComparer.Ordinal)
                .GroupBy(item=>item.armorSkinName).Select(group=>group.First())
                .GroupBy(item=>item.ItemNameLocalized).Select(group=>group.First()).Take(2).ToArray();
            if(hats.Length!=2)throw new InvalidOperationException("Two native head items are required for this comparison.");
            var observations=new JObject();
            proof["headwear"]=observations;
            try{
                player.hiddenHelmet=false;
                equipment.UnEquipItem(slot,true);
                equipment.EquipItem(hats[0],false);Redraw();
                yield return null;
                observations["regular"]=VisibleHead();
                observations["regularItem"]=hats[0].ItemNameLocalized;
                equipment.EquipItem(hats[1],true);Redraw();
                yield return null;
                observations["vanity"]=VisibleHead();
                observations["vanityItem"]=hats[1].ItemNameLocalized;
                player.hiddenHelmet=true;Redraw();
                yield return null;
                string hidden=VisibleHead();
                observations["hidden"]=hidden;
                if((string)observations["regular"]!=hats[0].ItemNameLocalized||(string)observations["vanity"]!=hats[1].ItemNameLocalized
                    ||hidden==hats[0].ItemNameLocalized||hidden==hats[1].ItemNameLocalized)
                    throw new InvalidOperationException("The resident's observation differs from the native headwear selection.");
            }finally{
                equipment.UnEquipItem(slot,true);equipment.UnEquipItem(slot,false);
                if(original)equipment.EquipItem(original,false);
                if(originalVanity)equipment.EquipItem(originalVanity,true);
                player.hiddenHelmet=originallyHidden;Redraw();
            }
        }

        IEnumerator Play()
        {
            float until=Time.realtimeSinceStartup+80;
            while((PlayerCharacterController.Instance==null||GameState.GetCurrentState(1)!=GameState.State.Gameplay)
                &&Time.realtimeSinceStartup<until)yield return null;
            var player=PlayerCharacterController.Instance;
            if(!player)throw new InvalidOperationException("The copied world did not load.");
            yield return new WaitForSecondsRealtime(2);
            var chat=player.GetComponent<PlayerChat>();
            var target=FindObjectsOfType<NPCChatController>()
                .Where(n=>n.isActiveAndEnabled&&n.Character is NPCController npc&&!npc.IsDead&&n.chatEnabled&&n.GetDialogueData()==null)
                .OrderBy(n=>(n.transform.position-player.transform.position).sqrMagnitude).First();
            var origin=player.transform.position;
            proof["resident"]=target.Character.CharacterInfo.name;
            var headwear=CheckHeadwear(player,(NPCController)target.Character);
            while(headwear.MoveNext())yield return headwear.Current;
            keyboard=Keyboard.current;
            if(keyboard==null)throw new InvalidOperationException("Unity has no active keyboard.");
            Stage("walking");
            until=Time.realtimeSinceStartup+20;
            bool InRange()=>ReferenceEquals(Field(player,"_currentContextMenu"),chat.ContextMenuUI)
                &&ReferenceEquals(Field(chat,"SelectedNPC"),target)&&chat.ContextMenuUI.IsVisible&&chat.ContextMenuUI.CanInteract();
            while(!InRange()&&Time.realtimeSinceStartup<until){
                var delta=target.transform.position-player.transform.position;
                var keys=new List<Key>();
                if(Math.Abs(delta.x)>.18f)keys.Add(delta.x>0?Key.D:Key.A);
                if(Math.Abs(delta.z)>.18f)keys.Add(delta.z>0?Key.W:Key.S);
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(keys.ToArray()));
                yield return new WaitForSecondsRealtime(.04f);
            }
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());
            proof["distanceWalked"]=Vector3.Distance(origin,player.transform.position);
            proof["distanceToResident"]=Vector3.Distance(player.transform.position,target.transform.position);
            if(!InRange())throw new InvalidOperationException("Walking did not select the live resident's interaction menu.");
            yield return new WaitForSecondsRealtime(.45f);
            var labels=chat.ContextMenuUI.GetComponentsInChildren<TMP_Text>().Select(t=>t.GetParsedText()).ToArray();
            proof["menu"]=JArray.FromObject(labels);
            if(!labels.Contains("Talk"))throw new InvalidOperationException("The player's menu did not show Talk.");
            Stage("native menu");
            ScreenCapture.CaptureScreenshot(Path.Combine(root,"walkthrough-menu.png"));
            yield return new WaitForSecondsRealtime(.75f);
            InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.E));
            yield return new WaitForSecondsRealtime(.12f);
            InputSystem.QueueStateEvent(keyboard,new KeyboardState());
            var plugin=AccessTools.Field(AccessTools.TypeByName("Mirthwood.Echoes.Plugin"),"instance").GetValue(null);
            var dialogue=Field(plugin,"dialogue");
            until=Time.realtimeSinceStartup+15;
            while(!Flag(dialogue,"Ready")&&Time.realtimeSinceStartup<until)yield return null;
            if(!Flag(dialogue,"Ready")||!ReferenceEquals(Field(dialogue,"resident"),target.Character))
                throw new InvalidOperationException("The keyboard Talk action did not open the selected resident.");
            var playerBubble=Field(dialogue,"playerBubble");
            var input=(TMP_InputField)AccessTools.PropertyGetter(playerBubble.GetType(),"Input").Invoke(playerBubble,null);
            if(!input.isFocused||Time.timeScale!=0)throw new InvalidOperationException("The player's draft did not receive focus and pause time.");
            proof["draftPaused"]=true;
            Stage("ready to type");
            // The desktop pilot can now type any line. Unattended runs send individual
            // character events through the same focused TMP editor and native Return key.
            until=Time.realtimeSinceStartup+30;
            while(input.isFocused&&string.IsNullOrEmpty(input.text)&&Time.realtimeSinceStartup<until)yield return null;
            if(input.isFocused&&string.IsNullOrEmpty(input.text)){
                const string text="*I look down at my coat.* What clothes can you see me wearing?";
                foreach(char letter in text){
                    input.ProcessEvent(new Event{type=EventType.KeyDown,character=letter});
                    yield return null;
                }
                if(input.text!=text)throw new InvalidOperationException("The focused editor lost typed characters.");
                proof["input"]="TMP character events and Return";
                ScreenCapture.CaptureScreenshot(Path.Combine(root,"walkthrough-draft.png"));
                yield return new WaitForSecondsRealtime(.5f);
                var run=AccessTools.TypeByName("Mirthwood.Echoes.IsolatedGameRun");
                if(!(bool)AccessTools.Method(run,"PostReturn").Invoke(null,null))throw new InvalidOperationException("Return did not reach Mirthwood.");
            }else proof["input"]="desktop keyboard";
            until=Time.realtimeSinceStartup+110;
            while((Field(dialogue,"phase").ToString()!="Reading"||(bool)Field(dialogue,"busy"))&&Time.realtimeSinceStartup<until)yield return null;
            if(Field(dialogue,"phase").ToString()!="Reading"||(bool)Field(dialogue,"busy"))throw new TimeoutException("The resident did not finish speaking.");
            var npcBubble=Field(dialogue,"npcBubble");
            proof["bubble"]=(string)AccessTools.PropertyGetter(npcBubble.GetType(),"VisibleText").Invoke(npcBubble,null);
            proof["bubbleSource"]=(string)Field(npcBubble,"lastSpeech");
            if((string)proof["bubble"]!=(string)Field(Field(npcBubble,"layout"),"Plain"))
                throw new InvalidOperationException("The text on screen differs from the game's wrapped bubble text.");
            Stage("resident reply");
            ScreenCapture.CaptureScreenshot(Path.Combine(root,"walkthrough-reply.png"));
            yield return new WaitForSecondsRealtime(3);
            // Reload the marked save with the Echoes component disabled. The two path
            // overrides keep all native reads and writes inside this verified copy.
            AccessTools.Method(dialogue.GetType(),"Close").Invoke(dialogue,null);
            string playerId=player.CharacterInfo.id;
            const string history="mirthwood.echoes.history";
            string marker=SaveMaster.GetString(history);
            if(!Guid.TryParse(marker,out _))throw new InvalidOperationException("The played exchange left no native history marker.");
            GameState.SaveGame();
            gameplayPlugin=(MonoBehaviour)plugin;
            gameplayPlugin.enabled=false;
            gameplayPlugin.StopAllCoroutines();
            var sceneHandler=(UnityEngine.Events.UnityAction<Scene,Scene>)Delegate.CreateDelegate(typeof(UnityEngine.Events.UnityAction<Scene,Scene>),
                plugin,AccessTools.Method(plugin.GetType(),"OnSceneChanged"));
            SceneManager.activeSceneChanged-=sceneHandler;
            const string owner="mirthwood.echoes";
            var patches=new Harmony("mirthwood.echoes.test.walkthrough");
            var remaining=new List<string>();
            foreach(var method in Harmony.GetAllPatchedMethods().Where(m=>Harmony.GetPatchInfo(m).Owners.Contains(owner)).ToArray()){
                if(method.DeclaringType?.Name=="TestSaveFolder"&&(method.Name=="get_Root"||method.Name=="get_LastUsedSlotKey"))remaining.Add(method.Name);
                else patches.Unpatch(method,HarmonyPatchType.All,owner);
            }
            proof["remainingIsolationHooks"]=JArray.FromObject(remaining);
            if(remaining.Count!=2||Harmony.GetAllPatchedMethods().Count(m=>Harmony.GetPatchInfo(m).Owners.Contains(owner))!=2)
                throw new InvalidOperationException("Echoes gameplay hooks could not be fully detached from the isolated copy.");
            Stage("native reload");
            GameState.QuitGameplay();SceneManager.LoadScene(0);
            until=Time.realtimeSinceStartup+50;
            while(!FindObjectOfType<TitleScreenController>()&&Time.realtimeSinceStartup<until)yield return null;
            yield return new WaitForSecondsRealtime(1.5f);
            if(SaveMaster.IsSlotLoaded())SaveMaster.ClearSlot(false,false);
            FindObjectOfType<TitleScreenController>().Continue();
            until=Time.realtimeSinceStartup+65;
            while((PlayerCharacterController.Instance==player||PlayerCharacterController.Instance?.CharacterInfo==null||
                GameState.GetCurrentState(1)!=GameState.State.Gameplay)&&Time.realtimeSinceStartup<until)yield return null;
            proof["nativeReload"]=PlayerCharacterController.Instance!=player&&PlayerCharacterController.Instance?.CharacterInfo?.id==playerId&&
                GameState.GetCurrentState(1)==GameState.State.Gameplay&&SaveMaster.GetString(history)==marker&&!gameplayPlugin.enabled;
            if(!(bool)proof["nativeReload"])throw new InvalidOperationException("The marked save did not return to native gameplay.");
        }
    }
}
