using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.ScriptableObjects;

namespace Mirthwood.Echoes
{
    internal static partial class InteractionHooks
    {
        static ChatMenuController deckMenu;
        static readonly string[] CardNames={"Rumors","Complain","Cheer Up"};
        internal static IEnumerable<DnaData.Dna> Cards(NPCController npc)
        {
            var brain=npc?npc.GetComponent<NPCChatController>():null;
            if(!Enabled||!brain||brain.dnaData?.chatActions==null||!brain.chatEnabled)yield break;
            if(!deckMenu)deckMenu=Resources.FindObjectsOfTypeAll<ChatMenuController>().FirstOrDefault(m=>m.gameObject.scene.IsValid());
            if(!deckMenu)deckMenu=Resources.FindObjectsOfTypeAll<UnityEngine.UI.Extensions.MenuManager>().Where(m=>m.gameObject.scene.IsValid()&&m.MenuScreens!=null)
                .SelectMany(m=>m.MenuScreens).OfType<ChatMenuController>().FirstOrDefault();
            if(!deckMenu||deckMenu.deckCards==null||deckMenu.subChoices==null)yield break;
            foreach(var group in brain.dnaData.chatActions.Where(x=>x!=null).GroupBy(x=>x.category)){
                int deck=(int)group.Key-1;
                if(deck<0||deck>=deckMenu.deckCards.Length||deckMenu.deckCards[deck]?.Card==null||!deckMenu.deckCards[deck].Card.interactable||
                    (group.Key==DnaData.Dna.DnaCategories.Romance&&npc.CharacterInfo.isPlayerChild))continue;
                foreach(var card in group.Take(deckMenu.subChoices.Length).Where(x=>CardNames.Contains(x.name)))yield return card;
            }
        }
    }
}
