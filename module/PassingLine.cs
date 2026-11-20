using UnityEngine;
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
            public string Id, Kind, Line, Session;
            public float ReadyAt, Expires;
            public int Frame;
            public GameObject Surface;
            public OverheadSpeech Bubble;
            public ConversationSpeech Speech;
            public bool CanReply;
        }
        PassingLine passing;

        static bool InReplyRange(NPCController speaker)
        {
            var player=PlayerCharacterController.Instance;
            return player&&speaker&&speaker.isActiveAndEnabled&&
                (player.transform.position-speaker.transform.position).sqrMagnitude<=16f;
        }

        internal bool OfferPassingLine(NPCController npc,string id,string kind,string line,float delay,ConversationSpeech queued=null,bool canReply=true)
        {
            if(Visible||!InReplyRange(npc)||string.IsNullOrWhiteSpace(line)||passing?.Id==id)return false;
            DismissPassingLine();
            passing=new PassingLine{Speaker=npc,Id=id,Kind=kind,Line=line,Session=NativeCues.Session,
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
            if(!InReplyRange(passing.Speaker)||passing.Session!=NativeCues.Session||
                (Time.unscaledTime>=passing.Expires && (passing.Bubble==null||passing.Speech?.Busy!=true))){
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
                    remark.Bubble.Show(value,partial);
                    remark.Expires=Time.unscaledTime+8f;
                });
                passing.Bubble.Note(passing.CanReply?"Enter to reply":"");
                passing.Expires=Time.unscaledTime+8f;
                passing.Frame=Time.frameCount;
                if(passing.CanReply)NativeCues.Recent(passing.Speaker,passing.Id,passing.Kind,passing.Line);
            }
            if(!passing.Bubble.Follow()||Input.GetKeyDown(KeyCode.Escape)){DismissPassingLine();return;}
            var menu=PlayerCharacterController.Instance.GetComponent<PlayerChat>()?.ContextMenuUI;
            if(passing.Bubble.QuickMenuBounds(menu) is Rect occupied)passing.Bubble.Avoid(occupied);
            if(passing.CanReply&&Time.frameCount!=passing.Frame&&(Input.GetKeyDown(KeyCode.Return)||Input.GetKeyDown(KeyCode.KeypadEnter)))ReplyToPassingLine();
        }

        void ReplyToPassingLine()
        {
            if(passing?.Bubble==null||!InReplyRange(passing.Speaker))return;
            var npc=passing.Speaker;var world=Plugin.World();
            if(world!=null&&npc.CharacterInfo!=null)Open(npc,new{world,owner=Plugin.Owner(npc),nativeId=npc.CharacterInfo.id});
        }

        void DismissPassingLine()
        {
            if(passing==null)return;
            passing.Speech?.Stop();passing.Bubble?.Dispose();
            if(passing.Surface)UnityEngine.Object.Destroy(passing.Surface);
            passing=null;
        }
    }
}
