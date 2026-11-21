using System;
using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        sealed class PassingLine
        {
            public NPCController Speaker;
            public string Id, Kind, Line, Session, EventId;
            public bool Heard;
            public float ReadyAt, Expires;
            public int Frame;
            public GameObject Surface;
            public OverheadSpeech Bubble;
            public ConversationSpeech Speech;
            public bool CanReply;
        }
        PassingLine passing;
        int addressRequest;
        UnityWebRequest pendingAddress;

        internal void OfferNativeAddress(NPCController npc,string card,string cue)
        {
            if(!InReplyRange(npc)||Visible||GroupActive||GameState.GetCurrentState(1)!=GameState.State.Gameplay||
                (IsolatedGameRun.Active!=null&&IsolatedGameRun.Active.TalkAddress==null))return;
            var world=Plugin.World();
            if(world==null||npc.CharacterInfo==null||string.IsNullOrEmpty(Plugin.Owner(npc)))return;
            var target=new{world,owner=Plugin.Owner(npc),nativeId=npc.CharacterInfo.id};
            NativeCues.Recent(npc,null,"card",cue);
            pendingAddress?.Abort();
            string head=NativeCues.Head;
            host.StartCoroutine(GenerateAddress(npc,new{target,card,cue,head=string.IsNullOrEmpty(head)?null:head},++addressRequest,NativeCues.Session,card!="Farewell"));
        }

        IEnumerator GenerateAddress(NPCController npc,object payload,int ticket,string session,bool canReply)
        {
            using(var req=Post(Address+"dialogue/address",payload)){
                pendingAddress=req;
                req.timeout=80;
                var operation=req.SendWebRequest();
                while(!operation.isDone){
                    if(ticket!=addressRequest||session!=NativeCues.Session||Visible||!InReplyRange(npc)){
                        req.Abort();break;
                    }
                    yield return null;
                }
                if(pendingAddress==req)pendingAddress=null;
                if(ticket!=addressRequest||session!=NativeCues.Session||Visible||!InReplyRange(npc)||
                   GameState.GetCurrentState(1)!=GameState.State.Gameplay)yield break;
                if(req.responseCode!=200){Debug.LogWarning(Error(req,"Echoes could not prepare the passing remark."));yield break;}
                string line;
                try{line=(string)JObject.Parse(req.downloadHandler.text)["line"];}
                catch(JsonException){yield break;}
                OfferPassingLine(npc,Guid.NewGuid().ToString(),"address",line,0,canReply:canReply);
            }
        }

        static bool InReplyRange(NPCController speaker)
        {
            var player=PlayerCharacterController.Instance;
            return player&&speaker&&speaker.isActiveAndEnabled&&
                (player.transform.position-speaker.transform.position).sqrMagnitude<=16f;
        }

        internal bool OfferPassingLine(NPCController npc,string id,string kind,string line,float delay,ConversationSpeech queued=null,bool canReply=true)
        {
            if(Visible||GroupActive||!InReplyRange(npc)||string.IsNullOrWhiteSpace(line)||passing?.Id==id)return false;
            DismissPassingLine();
            passing=new PassingLine{Speaker=npc,Id=id,EventId=kind=="address"?null:id,Kind=kind,Line=line,Session=NativeCues.Session,
                ReadyAt=Time.time+delay,Expires=Time.unscaledTime+12f,Speech=queued,CanReply=canReply};
            return true;
        }

        internal void UseSpokenGift(NPCController npc,string id,string line)
        {
            if(passing==null||passing.Speaker!=npc||passing.Id!=id||passing.Bubble!=null)return;
            passing.Line=line;
            passing.Kind="gift";
            passing.ReadyAt=Time.time;
        }

        internal void TickPassingLine()
        {
            if(passing==null)return;
            if(!InReplyRange(passing.Speaker)||passing.Session!=NativeCues.Session){
                DismissPassingLine();return;
            }
            if(Time.unscaledTime>=passing.Expires&&(passing.Bubble==null||passing.Speech?.Busy!=true)){
                DismissPassingLine();return;
            }
            if(GameState.GetCurrentState(1)!=GameState.State.Gameplay||GameState.IsPaused){
                if(passing.Bubble!=null)DismissPassingLine();
                return;
            }
            if(passing.Bubble==null){
                if(Time.time<passing.ReadyAt)return;
                passing.Surface=SpeechSurface("Mirthwood Echoes • Passing remark",false);
                passing.Bubble=OverheadSpeech.Create(passing.Speaker,(RectTransform)passing.Surface.transform);
                if(passing.Bubble==null){DismissPassingLine();return;}
                var remark=passing;
                if(remark.Speech==null)remark.Speech=new ConversationSpeech(host);
                remark.Speech.Say(remark.Id,remark.Speaker,remark.Line,(value,partial)=>{
                    remark.Bubble.ShowSpoken(remark.Line,value,partial);
                    remark.Expires=Time.unscaledTime+ReadTime(remark.Line);
                    if(remark.Kind=="address"&&!remark.Heard){
                        remark.Heard=true;
                        remark.EventId=NativeCues.Fact(remark.Speaker,"address",remark.Line,false,new JObject{["conversation"]=remark.Id});
                        if(remark.EventId!=null)NativeCues.Recent(remark.Speaker,remark.EventId,remark.Kind,remark.Line);
                    }
                });
                passing.Expires=Time.unscaledTime+ReadTime(remark.Line);
                passing.Frame=Time.frameCount;
                if(passing.CanReply&&passing.Kind!="address")NativeCues.Recent(passing.Speaker,passing.EventId,passing.Kind,passing.Line);
            }
            if(!passing.Bubble.Follow()||Input.GetKeyDown(KeyCode.Escape)){DismissPassingLine();return;}
            var bubble=passing.Bubble;
            bubble.Note(passing.CanReply?"Enter to reply":"");
            var menu=PlayerCharacterController.Instance.GetComponent<PlayerChat>()?.ContextMenuUI;
            if(bubble.QuickMenuBounds(menu) is Rect occupied)bubble.Avoid(occupied);
            if(Time.frameCount==passing.Frame)return;
            if(Input.GetKeyDown(KeyCode.Return)||Input.GetKeyDown(KeyCode.KeypadEnter))AdvancePassingLine();
        }
        static float ReadTime(string line)=>Mathf.Max(8f,line.Length/16f);

        void AdvancePassingLine()
        {
            if(passing.CanReply)ReplyToPassingLine();
        }

        void ReplyToPassingLine()
        {
            if(passing?.Bubble==null||!InReplyRange(passing.Speaker))return;
            var npc=passing.Speaker;var world=Plugin.World();
            if(world!=null&&npc.CharacterInfo!=null)Open(npc,new{world,owner=Plugin.Owner(npc),nativeId=npc.CharacterInfo.id});
        }

        void DismissPassingLine()
        {
            addressRequest++;
            pendingAddress?.Abort();pendingAddress=null;
            if(passing==null)return;
            passing.Speech?.Stop();passing.Bubble?.Dispose();
            if(passing.Surface)UnityEngine.Object.Destroy(passing.Surface);
            passing=null;
        }
    }
}
