using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Runtime.Demo.Combat.StateMachines;

namespace Mirthwood.Echoes
{
    internal sealed class GroupGathering
    {
        internal sealed class Guest
        {
            internal NPCController Npc;
            internal NPCChatController Chat;
            internal NavMeshAgent Agent;
            internal Vector3 Spot,PreviousDestination,PreviousFacing;
            internal bool HadPath,WasStopped,Arrived,Resolving,Unique;
            internal float StopDistance,Until;
            internal string Actor,Owner,NativeId;
            internal WorldAwareness.Interruption Interruption;
            internal bool SameResident=>Npc&&Npc.CharacterInfo?.id==NativeId&&Plugin.Owner(Npc)==Owner;
        }

        static readonly System.Reflection.FieldInfo EventLock=AccessTools.Field(typeof(NPCController),"_eventLockActive");
        static readonly System.Reflection.FieldInfo Combat=AccessTools.Field(typeof(NPCController),"inCombatMode");
        static readonly System.Reflection.FieldInfo Flee=AccessTools.Field(typeof(NPCController),"_isFleeingFromThreat");
        static readonly System.Reflection.FieldInfo Quest=AccessTools.Field(typeof(NPCController),"questLocation");
        static GroupGathering current;
        readonly Action<string> trace;
        internal readonly List<Guest> Guests=new List<Guest>();
        internal readonly Vector3 Origin;
        internal readonly string Session=Guid.NewGuid().ToString();
        internal readonly string PlaySession=NativeCues.Session;
        internal GroupGathering(Vector3 origin,Action<string> trace=null){Origin=origin;current=this;this.trace=trace;}

        internal static bool Holds(ChatController chat)=>current!=null&&current.Guests.Any(g=>g.Chat==chat);
        // Other native conversations see an invited resident as occupied. Only the movement
        // controller distinguishes walking to the meeting place from standing in the circle.
        static bool HoldMovement(ChatController chat)
        {
            var guest=current?.Guests.FirstOrDefault(g=>g.Chat==chat);
            return guest==null?chat.IsChatting:guest.Arrived&&Available(guest.Npc);
        }
        [HarmonyPatch(typeof(NPCController),"Update")]
        internal static class Movement
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)=>
                Transpilers.MethodReplacer(instructions,AccessTools.PropertyGetter(typeof(ChatController),nameof(ChatController.IsChatting)),
                    AccessTools.Method(typeof(GroupGathering),nameof(HoldMovement)));
        }
        static bool Reserved(NPCController npc)=>current!=null&&current.Guests.Any(g=>g.Npc==npc);
        static bool Available(NPCController npc)
        {
            return npc&&npc.isActiveAndEnabled&&npc.CharacterInfo!=null&&!npc.IsDead&&!npc.DataContainer.data.InCombat&&
                npc.GetComponent<AiBrain>()?.InCombatMode()!=true&&
                EventLock!=null&&Combat!=null&&Flee!=null&&Quest!=null&&!(bool)EventLock.GetValue(npc)&&
                !(bool)Combat.GetValue(npc)&&!(bool)Flee.GetValue(npc)&&!(UnityEngine.Object)Quest.GetValue(npc);
        }

        // Pause only the gathered residents' routine trees. Their normal Update still plays
        // walking animations, and the original iterator resumes at its prior task on release.
        [HarmonyPatch(typeof(NPCController),"Think")]
        internal static class Routine
        {
            static void Postfix(NPCController __instance,ref IEnumerator __result){__result=Run(__instance,__result);}
            static IEnumerator Run(NPCController npc,IEnumerator stock)
            {
                try{
                    while(npc){
                        if(Reserved(npc)){yield return null;continue;}
                        if(!stock.MoveNext())yield break;
                        yield return stock.Current;
                    }
                }finally{(stock as IDisposable)?.Dispose();}
            }
        }

        internal void Invite()
        {
            var player=PlayerCharacterController.Instance;
            if(!player||!NavMesh.SamplePosition(Origin,out var floor,1.5f,NavMesh.AllAreas)){trace?.Invoke("No navigable ground at the gathering.");return;}
            var center=Origin;
            var residents=UnityEngine.Object.FindObjectsOfType<NPCController>();
            var candidates=residents
                .Where(n=>!Reserved(n)&&(n.transform.position-center).sqrMagnitude<=16f)
                .OrderBy(n=>(n.transform.position-center).sqrMagnitude);
            foreach(var npc in candidates){
                if(Guests.Count>=3)break;
                var chat=npc.GetComponent<NPCChatController>();var agent=npc.GetComponent<NavMeshAgent>();
                if(!Available(npc))continue;
                // Native passing barks use IsChatting too. Only a nearby reciprocal conversation
                // partner has the floor, while scripted and busy residents have their own gates.
                if(!chat||!chat.chatEnabled||chat.GetIsBusy()||chat.GetDialogueData()!=null||
                    chat.IsChatting&&chat.chatTarget&&chat.chatTarget.chatTarget==chat&&chat.chatTarget.IsChatting&&
                    (chat.chatTarget.transform.position-chat.transform.position).sqrMagnitude<=16f||
                    !agent||!agent.isActiveAndEnabled||!agent.isOnNavMesh||string.IsNullOrEmpty(Plugin.Owner(npc))||!OverheadSpeech.Board(npc))continue;
                if(!NavMesh.SamplePosition(agent.nextPosition,out var ground,.65f,agent.areaMask)||
                    Mathf.Abs(ground.position.y-floor.position.y)>.8f||!ClearSight(center,ground.position)){
                    trace?.Invoke("Blocked approach.");continue;
                }
                if(!FindSpot(center,ground.position,agent,out var spot)){trace?.Invoke("No reachable spot for "+npc.name);continue;}
                var guest=new Guest{Npc=npc,Chat=chat,Agent=agent,Owner=Plugin.Owner(npc),NativeId=npc.CharacterInfo.id,
                    Interruption=WorldAwareness.Interruption.Observe(npc),Spot=spot,StopDistance=agent.stoppingDistance,
                    HadPath=agent.hasPath||agent.pathPending,PreviousDestination=agent.destination,PreviousFacing=npc.DataContainer.data.FacingDirection,
                    WasStopped=agent.isStopped,Until=Time.time+7f};
                Guests.Add(guest);
                agent.isStopped=false;agent.stoppingDistance=.12f;
                if(!agent.SetDestination(spot))Release(guest);
            }
        }

        internal static bool ClearSight(Vector3 from,Vector3 to)
        {
            var offset=to-from;
            return !Physics.RaycastAll(from+Vector3.up*.85f,offset.normalized,offset.magnitude,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore)
                .Any(hit=>!hit.collider.GetComponentInParent<Character>());
        }

        bool FindSpot(Vector3 center,Vector3 ground,NavMeshAgent agent,out Vector3 spot)
        {
            var preferred=ground-center;
            float angle=Mathf.Atan2(preferred.z,preferred.x);
            for(int i=0;i<12;i++){
                int offset=(i+1)/2*(i%2==0?-1:1);
                float direction=angle+offset*Mathf.PI/6;
                var point=center+new Vector3(Mathf.Cos(direction),0,Mathf.Sin(direction))*1.7f;
                if(!NavMesh.SamplePosition(point,out var hit,.45f,agent.areaMask)||Math.Abs(hit.position.y-center.y)>.65f||
                    (hit.position-center).sqrMagnitude<1.7f||Guests.Any(g=>(g.Spot-hit.position).sqrMagnitude<1.4f))continue;
                var path=new NavMeshPath();
                if(!agent.CalculatePath(hit.position,path)||path.status!=NavMeshPathStatus.PathComplete||
                    NavMesh.Raycast(ground,hit.position,out _,agent.areaMask)||!ClearSight(center,hit.position))continue;
                spot=hit.position;return true;
            }
            spot=default;return false;
        }

        internal void Tick()
        {
            foreach(var g in Guests.ToArray()){
                if(!Available(g.Npc)||!g.Agent||!g.Agent.isActiveAndEnabled||!g.Agent.isOnNavMesh||
                    !g.Chat.chatEnabled||g.Chat.GetDialogueData()!=null||g.Chat.GetIsBusy()||
                    (g.Arrived&&(g.Npc.transform.position-g.Spot).sqrMagnitude>2.25f)){
                    trace?.Invoke("Released unavailable "+g.Npc?.name+" at "+g.Npc?.transform.position+" spot "+g.Spot+" busy "+g.Chat?.GetIsBusy());
                    Release(g);continue;
                }
                if(g.Arrived)continue;
                if((g.Npc.transform.position-g.Spot).sqrMagnitude<.20f){
                    g.Arrived=true;g.Agent.isStopped=true;Face(g.Npc,Origin);
                }else if(Time.time>g.Until||!g.Agent.pathPending&&g.Agent.pathStatus==NavMeshPathStatus.PathInvalid){
                    trace?.Invoke("Could not arrive "+g.Npc.name+" at "+g.Npc.transform.position+" spot "+g.Spot+" destination "+g.Agent.destination+
                        " speed "+g.Agent.speed+" stopped "+g.Agent.isStopped+" path "+g.Agent.pathStatus+" chatting "+g.Chat.IsChatting);
                    Release(g);
                }else if(!g.Agent.pathPending&&(g.Agent.isStopped||!g.Agent.hasPath||(g.Agent.destination-g.Spot).sqrMagnitude>.16f)){
                    // A routine already in flight can finish its old path after the invitation.
                    g.Agent.isStopped=false;
                    if(!g.Agent.SetDestination(g.Spot))Release(g);
                }
            }
        }
        internal static void Face(NPCController npc,Vector3 towards)
        {
            var delta=towards-npc.transform.position;
            if(Mathf.Abs(delta.x)>.04f)npc.DataContainer.data.SetFacingDirection(new Vector3(delta.x,0,0));
        }
        internal void Release(Guest g)
        {
            Guests.Remove(g);
            if(!g.Agent||!g.Agent.isActiveAndEnabled||!g.Agent.isOnNavMesh)return;
            g.Agent.stoppingDistance=g.StopDistance;
            if(!Available(g.Npc))return;
            g.Npc.DataContainer.data.SetFacingDirection(g.PreviousFacing);
            g.Agent.ResetPath();
            if(g.HadPath)g.Agent.SetDestination(g.PreviousDestination);
            g.Agent.isStopped=g.WasStopped;
        }
        internal void Close()
        {
            foreach(var g in Guests.ToArray())Release(g);
            if(current==this)current=null;
        }
    }
}
