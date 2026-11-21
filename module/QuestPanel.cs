using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using InfinityPBR.Modules;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.GameModules;
using _Mirthwood.Scripts.Runtime.Demo.Menus.Views;
using _Mirthwood.Scripts.Runtime.Demo.ScriptableObjects;

namespace Mirthwood.Echoes
{
    // Speak has already opened the portrait and run the game's entry effects. Only replace the choice row.
    internal sealed class QuestPanel
    {
        static Plugin host;
        internal static QuestPanel Current {get;private set;}
        static readonly System.Reflection.FieldInfo ShownData=AccessTools.Field(typeof(DialogueView),"_dialogueData");
        readonly DialogueView view;
        readonly DialogueData data;
        readonly ConditionalDialogue condition;
        readonly NPCChatController chat;
        readonly NPCController npc;
        readonly Quest quest;
        readonly string owner,nativeId,key,acceptLabel,session=Guid.NewGuid().ToString();
        readonly JObject world,offer;
        readonly int accept,decline;
        string campaign,actor,parent,spoken;
        GameObject editRow,detailRow;
        TMP_InputField input;
        readonly ConversationSpeech speech;
        TextMeshProUGUI nativeWords,portraitWords;
        bool busy=true,details,spent;
        internal bool Ready=>input!=null&&!busy&&!spent;
        internal JObject Last {get;private set;}
        internal JObject LastRequest {get;private set;}
        internal TMP_InputField Editor=>input;
        internal Button DetailsButton=>detailRow?.GetComponent<Button>();
        internal TextMeshProUGUI Portrait=>portraitWords;
        internal string NativeTerms=>(string)offer["terms"];
        internal static bool StockPreview;
        static string Address=>IsolatedGameRun.Active?.TalkAddress??"http://127.0.0.1:17841/api/";

        internal static void Install(Plugin plugin,Harmony hooks)
        {
            host=plugin;
            hooks.CreateClassProcessor(typeof(Choices)).Patch();
            hooks.CreateClassProcessor(typeof(Show)).Patch();
            hooks.CreateClassProcessor(typeof(Hide)).Patch();
            if(IsolatedGameRun.Active==null||IsolatedGameRun.Active.TalkAddress!=null)StartDelivery();
        }
        [HarmonyPatch(typeof(DialogueView),"ShowChoices")]
        static class Choices
        {
            static void Postfix(DialogueView __instance,DialogueData ____dialogueData)
            {
                try{Open(__instance,____dialogueData);}
                catch(Exception e){Debug.LogWarning("Echoes quest offer kept its native choices. "+e.GetBaseException().Message);Close();}
            }
        }
        [HarmonyPatch(typeof(DialogueView),nameof(DialogueView.ShowDialogue))]
        static class Show {static void Prefix(){Close();}}
        [HarmonyPatch(typeof(DialogueView),nameof(DialogueView.HideDialogue))]
        static class Hide {static void Prefix(){Close();}}

        static void Open(DialogueView v,DialogueData d)
        {
            if(Current!=null||StockPreview||!d||!d.isGenerated||d.Choices?.Length!=2||v.choiceButtons?.Length<2||
                IsolatedGameRun.Active!=null&&IsolatedGameRun.Active.TalkAddress==null)return;
            int yes=Array.FindIndex(d.Choices,x=>x.choiceText=="Accept"&&x.onSelected!=null&&!x.nextDialogue);
            int no=Array.FindIndex(d.Choices,x=>x.choiceText=="Decline"&&x.onSelected!=null&&!x.nextDialogue);
            var resident=d.Character as NPCController;var brain=resident?.GetComponent<NPCChatController>();
            var entry=brain?.ConditionalDialogues?.FirstOrDefault(x=>x!=null&&ReferenceEquals(x.dialogueData,d));
            var world=Plugin.World();
            if(yes<0||no<0||!v.choiceButtons[yes]||!v.choiceButtons[no]||!resident||resident.CharacterInfo==null||entry==null||
                !entry.requireQuestNotStarted||!entry.quest||!entry.quest.isGenerated||entry.generatedQuestType!=entry.quest.generatedQuestType||
                world==null||string.IsNullOrEmpty(Plugin.Owner(resident)))return;
            var panel=new QuestPanel(v,d,entry,brain,resident,JObject.FromObject(world),yes,no);
            if(!panel.Eligible())return;
            Current=panel;
            v.choiceButtons[yes].interactable=false;
            v.choiceButtons[yes].GetComponentInChildren<TextMeshProUGUI>(true).text="One moment…";
            v.choiceButtons[no].gameObject.SetActive(false);
            host.StartCoroutine(panel.Begin());
        }
        QuestPanel(DialogueView v,DialogueData d,ConditionalDialogue entry,NPCChatController brain,NPCController resident,JObject w,int yes,int no)
        {
            view=v;data=d;condition=entry;quest=entry.quest;chat=brain;npc=resident;world=w;
            speech=new ConversationSpeech(host);
            accept=yes;decline=no;acceptLabel=v.choiceButtons[yes].GetComponentInChildren<TextMeshProUGUI>(true).text;
            key=quest.Uid();nativeId=npc.CharacterInfo.id;owner=Plugin.Owner(npc);
            string terms=d.Dialogue.Replace("{origin}",PlayerCharacterController.Instance.CharacterInfo.origin.ToString());
            offer=new JObject{["key"]=key,["title"]=quest.FullName,["terms"]=terms,
                ["objectives"]=new JArray(quest.questSteps.Where(s=>s!=null&&!string.IsNullOrWhiteSpace(s.name)&&!s.name.Contains("{")).Select(s=>s.name).Take(12))};
        }
        bool Eligible()
        {
            if(!view||!view.isActiveAndEnabled||!npc||!npc.isActiveAndEnabled||npc.CharacterInfo?.id!=nativeId||
                !chat||chat.ConditionalDialoguesBlocked||!ReferenceEquals(ShownData.GetValue(view),data)||!ReferenceEquals(data.Character,npc)||
                !ReferenceEquals(condition.quest,quest)||!MirthwoodPlayer.Instance||MirthwoodPlayer.Instance.QuestList==null||
                MirthwoodPlayer.Instance.QuestList.Get(quest)!=null)return false;
            var w=Plugin.World();if(w==null)return false;
            var now=JObject.FromObject(w);
            return new[]{"slot","created","player"}.All(k=>JToken.DeepEquals(world[k],now[k]))&&
                ReferenceEquals(chat.ConditionalDialogues.FirstOrDefault(x=>x!=null&&x.IsAvailable(chat)),condition);
        }
        internal static void Tick()
        {
            var p=Current;if(p==null)return;
            if(!p.view||!p.view.isActiveAndEnabled){Close();return;}
            if(p.input!=null&&Input.GetKeyDown(KeyCode.Escape)){p.view.HideDialogue();return;}
            if(p.Ready&&p.input.isFocused&&(Input.GetKeyDown(KeyCode.Return)||Input.GetKeyDown(KeyCode.KeypadEnter)))p.Submit(p.input.text);
        }
        internal static void Close()
        {
            var p=Current;Current=null;
            if(p==null)return;
            if(!p.spent)p.speech.Stop();
            if(p.view&&p.view.choiceButtons[p.accept]){
                var b=p.view.choiceButtons[p.accept];b.interactable=true;
                b.GetComponentInChildren<TextMeshProUGUI>(true).text=p.acceptLabel;
            }
            if(p.nativeWords)p.nativeWords.gameObject.SetActive(true);
            if(p.portraitWords){p.portraitWords.gameObject.SetActive(false);UnityEngine.Object.Destroy(p.portraitWords.gameObject);}
            foreach(var row in new[]{p.editRow,p.detailRow})if(row){row.SetActive(false);UnityEngine.Object.Destroy(row);}
        }
        void Fallback()
        {
            if(Current!=this)return;
            bool valid=Eligible();Close();
            if(!view||!view.isActiveAndEnabled)return;
            if(!valid){view.HideDialogue();return;}
            view.typewriter.ShowText(NativeTerms);
            view.choiceButtons[accept].gameObject.SetActive(true);view.choiceButtons[decline].gameObject.SetActive(true);
            view.choiceButtons[accept].Select();
        }
        static IEnumerator Request(string path,JObject payload,Action<JObject> completed,int timeout)
        {
            using(var r=NativeConversation.Post(Address+path,payload)){
                r.timeout=timeout;yield return r.SendWebRequest();JObject result=null;
                if(r.responseCode==200){try{result=JObject.Parse(r.downloadHandler.text);}catch(Newtonsoft.Json.JsonException){}}
                completed(result);
            }
        }
        IEnumerator Begin()
        {
            JObject identity=null;
            yield return Request("module/resolve",new JObject{["world"]=world.DeepClone(),["owner"]=owner,["nativeId"]=nativeId},r=>identity=r,6);
            if(Current!=this)yield break;
            if(identity==null){Fallback();yield break;}
            campaign=(string)identity["campaign"];actor=(string)identity["actor"];
            yield return Turn("");
        }
        JObject Envelope(string id)=>new JObject{["id"]=id,["campaign"]=campaign,["actor"]=actor,["key"]=key,["session"]=session};
        internal void Submit(string text)
        {
            if(!Ready||Input.GetKeyDown(KeyCode.Escape)||string.IsNullOrWhiteSpace(text)||text.Trim().Length>1200)return;
            busy=true;input.readOnly=true;details=false;input.text=text.Trim();
            host.StartCoroutine(Turn(text.Trim()));
        }
        IEnumerator Turn(string text)
        {
            bool opening=parent==null;
            var body=Envelope(opening?session:Guid.NewGuid().ToString());
            if(opening)body["offer"]=offer.DeepClone();else{body["parent"]=parent;body["text"]=text;}
            if(!opening){
                speech.Stop(true);
                speech.Say((string)body["id"]+"/player",PlayerCharacterController.Instance,text,null);
            }
            LastRequest=body;JObject answer=null;
            for(int i=0;i<3;i++){
                yield return Request("quests/turn",body,r=>answer=r,85);
                if(Current!=this)yield break;
                if(answer!=null&&(string)answer["state"]!="pending")break;
                yield return new WaitForSecondsRealtime(.5f);
            }
            if(Current!=this)yield break;
            Last=answer??new JObject{["state"]="unavailable"};
            if((string)answer?["state"]!="ready"||!Eligible()){Fallback();yield break;}
            var action=(string)answer["action"];string line=(string)answer["reply"];
            if(string.IsNullOrWhiteSpace(line)||action!="continue"&&action!="accept"&&action!="decline"){Fallback();yield break;}
            var ack=Envelope((string)body["id"]);ack["action"]=action;
            if(action!="continue"){
                if(opening||spent){Fallback();yield break;}
                spent=true;
                // The original listener owns the quest journal, marker/spawn work, relationship and portrait close.
                view.choiceButtons[action=="accept"?accept:decline].onClick.Invoke();
                bool started=MirthwoodPlayer.Instance?.QuestList?.Get(quest)!=null;
                if(action=="accept"?started:!started&&chat&&!chat.ConditionalDialogues.Contains(condition)){
                    QueueConfirmation(ack);
                    if(!Plugin.QuestFarewell(npc,(string)body["id"]+"/farewell",line,speech)){
                        speech.Stop();OverheadSpeech.Board(npc)?.ShowOverheadText(Markup(line),null);
                    }
                }else speech.Stop();
                yield break;
            }
            try{
                if(!editRow)BuildEditor();
                string terms=opening?NativeTerms+"\n\n":"";
                spoken=terms+Markup(line);
                details=false;portraitWords.text=terms;input.text="";input.readOnly=true;
                speech.Say((string)body["id"]+"/npc",npc,line,(value,partial)=>{
                    if(Current==this&&!details&&portraitWords)portraitWords.text=terms+Markup(value,partial);
                });
                DetailsButton.GetComponentInChildren<TextMeshProUGUI>(true).text="Quest details";
            }catch(Exception e){Debug.LogWarning("Echoes quest reply editor unavailable. "+e.GetBaseException().Message);Fallback();yield break;}
            bool confirmed=false;yield return Acknowledge(ack,ok=>confirmed=ok);
            if(Current!=this)yield break;
            if(!confirmed){Fallback();yield break;}
            parent=(string)body["id"];busy=false;input.readOnly=false;input.ActivateInputField();input.Select();
        }
        static IEnumerator Acknowledge(JObject ack,Action<bool> done)
        {
            for(int i=0;i<4;i++){
                JObject read=null;yield return Request("quests/finish",ack,r=>read=r,5);
                if((string)read?["state"]=="complete"){done?.Invoke(true);yield break;}
                yield return new WaitForSecondsRealtime(i+1);
            }
            done?.Invoke(false);
        }
        // A recovered receipt only tells the journal what already happened. It cannot invoke a game choice.
        static string ReceiptFolder=>Path.Combine(IsolatedGameRun.Active?.Root??Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MirthwoodEchoes"),"QuestReceipts");
        static bool delivering;
        static void QueueConfirmation(JObject ack)
        {
            try{
                string folder=ReceiptFolder;Directory.CreateDirectory(folder);
                string path=Path.Combine(folder,(string)ack["id"]+".json");
                using(var stream=new FileStream(path+".tmp",FileMode.Create,FileAccess.Write,FileShare.None)){
                    byte[] bytes=System.Text.Encoding.UTF8.GetBytes(ack.ToString(Newtonsoft.Json.Formatting.None));
                    stream.Write(bytes,0,bytes.Length);stream.Flush(true);
                }
                File.Move(path+".tmp",path);StartDelivery();
            }catch(IOException){host.StartCoroutine(Acknowledge(ack,null));}
            catch(UnauthorizedAccessException){host.StartCoroutine(Acknowledge(ack,null));}
        }
        static void StartDelivery()
        {
            if(!delivering&&Directory.Exists(ReceiptFolder))host.StartCoroutine(DeliverConfirmations());
        }
        static IEnumerator DeliverConfirmations()
        {
            delivering=true;
            try{
                while(true){
                    string[] paths=Directory.GetFiles(ReceiptFolder,"*.json");if(paths.Length==0)yield break;
                    foreach(var path in paths){
                        if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path),"D",out _))continue;
                        JObject receipt;
                        try{receipt=JObject.Parse(File.ReadAllText(path));}catch(Newtonsoft.Json.JsonException){continue;}
                        bool ok=false;yield return Acknowledge(receipt,done=>ok=done);
                        if(ok)File.Delete(path);
                    }
                    if(Directory.GetFiles(ReceiptFolder,"*.json").Length>0)yield return new WaitForSecondsRealtime(12);
                }
            }finally{delivering=false;}
        }
        static string Markup(string value,bool partial=false)
        {
            var safe=value.Replace("<","‹").Replace(">","›");
            var text=new System.Text.StringBuilder(safe);
            foreach(var s in ActionNotation.Find(safe,partial).AsEnumerable().Reverse()){
                if(s.Closed)text.Remove(s.End,1);
                text.Insert(s.End,"</i>");text.Remove(s.Open,1).Insert(s.Open,"<i>");
            }
            return text.ToString();
        }
        void BuildEditor()
        {
            // Present the complete exchange in the native portrait's text rectangle, including while the game is paused.
            nativeWords=view.typewriter.GetComponentInChildren<TextMeshProUGUI>(true);
            if(!nativeWords||nativeWords.gameObject==view.gameObject)throw new InvalidOperationException("The portrait has no separate native text surface.");
            var src=nativeWords.rectTransform;
            var rect=new GameObject("Echoes quest dialogue",typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(src.parent,false);rect.anchorMin=src.anchorMin;rect.anchorMax=src.anchorMax;rect.pivot=src.pivot;
            rect.sizeDelta=src.sizeDelta;rect.anchoredPosition3D=src.anchoredPosition3D;rect.localScale=src.localScale;rect.localRotation=src.localRotation;
            portraitWords=rect.gameObject.AddComponent<TextMeshProUGUI>();portraitWords.font=nativeWords.font;
            portraitWords.fontSharedMaterial=nativeWords.fontSharedMaterial;portraitWords.color=nativeWords.color;
            portraitWords.fontSize=nativeWords.fontSize;portraitWords.fontStyle=nativeWords.fontStyle;portraitWords.alignment=nativeWords.alignment;
            portraitWords.margin=nativeWords.margin;portraitWords.enableWordWrapping=true;portraitWords.enableAutoSizing=true;
            portraitWords.fontSizeMax=nativeWords.fontSize;portraitWords.fontSizeMin=Mathf.Max(12,nativeWords.fontSize*.68f);
            portraitWords.raycastTarget=false;nativeWords.gameObject.SetActive(false);
            var source=view.choiceButtons[accept];var second=view.choiceButtons[decline];
            editRow=UnityEngine.Object.Instantiate(source.gameObject,source.transform.parent,false);
            editRow.name="Echoes quest reply";editRow.transform.SetSiblingIndex(source.transform.GetSiblingIndex());
            var original=editRow.GetComponent<Button>();original.enabled=false;
            var words=editRow.GetComponentInChildren<TextMeshProUGUI>(true);
            var editor=new GameObject("Reply field",typeof(RectTransform)).GetComponent<RectTransform>();
            editor.SetParent(editRow.transform,false);editor.anchorMin=Vector2.zero;editor.anchorMax=Vector2.one;
            editor.offsetMin=editor.offsetMax=Vector2.zero;
            var viewport=new GameObject("Text area",typeof(RectTransform),typeof(RectMask2D)).GetComponent<RectTransform>();
            viewport.SetParent(editor,false);viewport.anchorMin=Vector2.zero;viewport.anchorMax=Vector2.one;
            viewport.offsetMin=new Vector2(10,1);viewport.offsetMax=new Vector2(-10,-1);
            words.rectTransform.SetParent(viewport,false);words.rectTransform.anchorMin=Vector2.zero;words.rectTransform.anchorMax=Vector2.one;
            words.rectTransform.offsetMin=words.rectTransform.offsetMax=Vector2.zero;
            var hint=UnityEngine.Object.Instantiate(words,viewport,false);
            hint.name="Reply prompt";hint.text="Your reply…";hint.fontStyle=FontStyles.Italic;
            words.text="";words.richText=false;words.alignment=hint.alignment=TextAlignmentOptions.MidlineLeft;
            var target=editor.gameObject.AddComponent<Image>();target.color=Color.clear;
            input=editor.gameObject.AddComponent<DialogueInputField>();input.targetGraphic=target;input.textViewport=viewport;
            input.textComponent=words;input.placeholder=hint;input.characterLimit=1200;input.richText=true;input.isRichTextEditingAllowed=true;
            words.richText=true;var projection=new DraftProjection();words.textPreprocessor=projection;words.OnPreRenderText+=projection.MapCaret;
            input.lineType=TMP_InputField.LineType.MultiLineSubmit;input.onSubmit.AddListener(Submit);
            detailRow=UnityEngine.Object.Instantiate(second.gameObject,second.transform.parent,false);
            detailRow.name="Echoes quest terms";detailRow.SetActive(true);var detail=detailRow.GetComponent<Button>();detail.onClick=new Button.ButtonClickedEvent();
            var label=detail.GetComponentInChildren<TextMeshProUGUI>(true);label.text="Quest details";
            detail.onClick.AddListener(()=>{
                if(!Ready)return;details=!details;label.text=details?"Back to reply":"Quest details";
                portraitWords.text=details?NativeTerms:spoken;input.ActivateInputField();
            });
            source.gameObject.SetActive(false);second.gameObject.SetActive(false);
            EventSystem.current?.SetSelectedGameObject(editor.gameObject);
        }
    }
}
