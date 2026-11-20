using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Spine;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Items;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Runtime.Demo.ScriptableObjects;
using WearSlot = _Mirthwood.Scripts.Demo.Items.SlotData.Slot;

namespace Mirthwood.Echoes
{
    internal static class PlayerImpression
    {
        static readonly HashSet<string> warned = new HashSet<string>();
        static readonly string[] renownBands = { "obscure", "little known", "known by name", "notable", "widely known", "famous" };

        internal static JObject Observe(NPCController resident, JObject lastClothing)
        {
            var player = PlayerCharacterController.Instance;
            if (!InteractionHooks.Compatible || !player || !resident || player.CharacterInfo == null || resident.CharacterInfo == null) return null;
            var impression = new JObject();
            try {
                impression["name"] = player.CharacterInfo.name;
                impression["familiarity"] = Familiarity(resident, player.CharacterInfo.id);
                ReadReputation(player.CharacterInfo, impression);
            } catch (Exception e) { WarnOnce("reputation", e); }
            try {
                var clothes = VisibleClothing(player);
                if (clothes.Count > 0) {
                    impression["visibleClothing"] = clothes;
                    impression["newlySeen"] = new JArray(clothes.Properties()
                        .Where(piece => !JToken.DeepEquals(piece.Value, lastClothing?[piece.Name])).Select(piece => piece.Name));
                }
            } catch (Exception e) { WarnOnce("clothing", e); }
            return impression;
        }

        static void WarnOnce(string subject, Exception error)
        {
            if (warned.Add(subject)) Debug.LogWarning("Echoes could not read the player's " + subject + " for Talk. " + error.GetBaseException().Message);
        }

        static string Familiarity(NPCController resident, string playerId)
        {
            var info = resident.CharacterInfo;
            if (info.isPlayerChild) return "Child";
            var relationships = info.relationships;
            if (relationships == null || string.IsNullOrEmpty(playerId)) return null;
            if (relationships.GetSpouse() == playerId) return "Spouse";
            // GetRelationship reads the dictionary. Indexing it would introduce the two characters.
            relationships.GetRelationship(playerId, out float friendship, out float romance);
            if (friendship == 0 && romance == 0 && !resident.HasMetPlayer) return "Unmet";
            return InteractionHooks.FriendshipBand(friendship);
        }

        static void ReadReputation(CharacterData.Character player, JObject impression)
        {
            var titles = PlayerTitlesData.Instance;
            var ranks = titles ? titles.renownThresholds : null;
            if (ranks == null || ranks.Length == 0 || ranks.Any(rank => rank == null)) return;
            // The native title lookup treats the last entry as an open-ended band in both dimensions.
            int rankIndex = Array.FindIndex(ranks, rank => player.Renown <= rank.renownMax);
            if (rankIndex < 0) rankIndex = ranks.Length - 1;
            impression["renown"] = renownBands[Math.Min(rankIndex, renownBands.Length - 1)];
            var variants = ranks[rankIndex].titles;
            if (rankIndex == 0 || variants == null || variants.Length == 0 || variants.Any(v => v == null)) return;
            int Band(int morality) {
                int index = Array.FindIndex(variants, v => morality <= v.moralityMax);
                return index >= 0 ? index : variants.Length - 1;
            }
            int standing = Band(player.Morality).CompareTo(Band(0));
            impression["standing"] = standing < 0 ? "ill-regarded" : standing > 0 ? "well-regarded" : "unremarked";
            var title = titles.GetTitle(player.Renown, player.Morality);
            if (!string.IsNullOrWhiteSpace(title) && !title.StartsWith("Title_", StringComparison.Ordinal)) impression["title"] = title;
        }

        static JObject VisibleClothing(PlayerCharacterController player)
        {
            var result = new JObject();
            var visuals = player.Visuals;
            var skeleton = visuals?.Data?.Skeleton;
            var equipment = player.Equipment;
            if (!visuals || !visuals.isActiveAndEnabled || skeleton?.Skin == null || visuals.SkinController == null || !equipment) return result;
            visuals.SkinController.GetSkinNamesAndColors(out var baseSkins, out var colors);
            ReadSlot("head", WearSlot.Head, WearSlot.None, "Headwear/", player.hiddenHelmet);
            ReadSlot("torso", WearSlot.ChestArmor, WearSlot.Chest, "Shirts/", player.hiddenChestArmor);
            ReadSlot("legs", WearSlot.LegsArmor, WearSlot.Legs, "Pants/", player.hiddenLegsArmor);
            return result;

            string Equipped(WearSlot slot) {
                if (slot == WearSlot.None) return null;
                return DrawnLabel(equipment.GetItemInSlot(slot, true)) ?? DrawnLabel(equipment.GetItemInSlot(slot, false));
            }

            string DrawnLabel(ItemData item) {
                if (!item || !IsDrawn(skeleton, item.armorSkinName)) return null;
                var label = item.ItemNameLocalized;
                return string.IsNullOrWhiteSpace(label) ? SkinLabel(item.armorSkinName, false) : label;
            }

            void ReadSlot(string part, WearSlot outer, WearSlot garment, string baseFolder, bool hidden) {
                // Shirts and trousers occupy their own equipment slots beneath optional armor.
                var worn = (hidden ? null : Equipped(outer)) ?? Equipped(garment);
                if (worn != null) { result[part] = worn; return; }
                if (baseSkins == null) return;
                for (int i = baseSkins.Count - 1; i >= 0; i--) {
                    string name = baseSkins[i];
                    if (name == null || !name.StartsWith(baseFolder, StringComparison.Ordinal) || !IsDrawn(skeleton, name)) continue;
                    bool tinted = colors != null && i < colors.Count && colors[i].color != default(Color) && colors[i].color != Color.white;
                    result[part] = SkinLabel(name, tinted);
                    return;
                }
            }
        }

        static string SkinLabel(string path, bool tinted)
        {
            var style = path.Substring(path.LastIndexOf('/') + 1);
            // A dyed base garment may have a different colour from the one in the stock style's name.
            int separator = style.IndexOf('-');
            if (tinted && separator > 0) style = style.Substring(0, separator);
            return Regex.Replace(style.Replace('-', ' ').Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ").Trim();
        }

        static bool IsDrawn(Skeleton skeleton, string skinName)
        {
            if (string.IsNullOrEmpty(skinName)) return false;
            var skin = skeleton.Data.FindSkin(skinName);
            if (skin == null) return false;
            foreach (var entry in skin.Attachments) {
                var slot = skeleton.Slots.Items[entry.SlotIndex];
                if (!ReferenceEquals(slot.Attachment, entry.Attachment) || slot.A <= 0 || !slot.Bone.Active) continue;
                string atlasPath;
                if (entry.Attachment is RegionAttachment region && region.A > 0) atlasPath = region.Path;
                else if (entry.Attachment is MeshAttachment mesh && mesh.A > 0) atlasPath = mesh.Path;
                else continue;
                // Stock clothing skins also attach an invisible 'empty' image to suppress covered body parts.
                if (!string.IsNullOrEmpty(atlasPath) && !string.Equals(atlasPath, "empty", StringComparison.OrdinalIgnoreCase)
                    && !atlasPath.EndsWith("/empty", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
