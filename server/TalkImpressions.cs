using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mirthwood.Echoes;

internal static class TalkImpressions
{
    internal const string Instructions = """
        Character's scores describe the resident. Player impression describes the speaker. Renown guides recognition and address. Only publicStanding and publicTitle tell you what people have heard about the player. If asked about their reputation, keep that public assessment consistent in every reaction. You can be kind to an ill-regarded stranger or wary of a celebrated one in your own voice. Invent no balancing rumor, specific deed or private belief. Give actual firsthand experiences more weight in your personal response. Use a conditional introduction only when the player introduces themself. Answer questions about dress from currently visible garments. Unlisted details are unknown. Dress alone establishes no wealth, occupation or allegiance. Volunteer at most one newly seen detail when it fits. Keep facts consistent in farewells and choose the topic from the player's words.
        """;

    internal static JsonObject? ForResident(JsonNode? input, string words)
    {
        if (input is not JsonObject source) return null;
        var view = new JsonObject();
        string? familiarity = Label(source["familiarity"]);
        bool known = familiarity is "Acquaintance" or "Friend" or "Buddy" or "Confidant" or "Hostile" or "Unfriendly" or "Spouse" or "Child";
        if (known || familiarity == "Unmet") view["relationshipToPlayer"] = familiarity;
        string? name = Label(source["name"], 160);
        bool namedInSpeech = !known && name != null && SpokenName(words, name);
        if (known || namedInSpeech) {
            var identity = new JsonObject();
            if (name != null) identity["name"] = name;
            string? reach = Label(source["renown"]);
            if (reach is "obscure" or "little known" or "known by name" or "notable" or "widely known" or "famous") {
                identity["nameRecognition"] = reach;
                if (reach != "obscure" && (known || reach != "little known")) {
                    string? standing = Label(source["standing"]);
                    if (standing is "ill-regarded" or "well-regarded" or "unremarked") identity["publicStanding"] = standing;
                    if (Label(source["title"]) is { } title) identity["publicTitle"] = title;
                }
            }
            if (identity.Count > 0) view[known ? "knownPlayer" : "onlyIfIntroducingThemself"] = identity;
        }

        if (source["visibleClothing"] is JsonObject supplied) {
            var clothes = new JsonObject();
            var newlySeen = new JsonArray();
            var first = source["newlySeen"] as JsonArray;
            foreach (var part in new[] { "head", "torso", "legs" }) {
                if (Label(supplied[part]) is not { } garment) continue;
                clothes[part] = garment;
                if (first is { Count: <= 3 } && first.Any(slot => Label(slot) == part)) newlySeen.Add(part);
            }
            if (clothes.Count > 0) {
                view["visibleClothing"] = clothes;
                view["newlySeen"] = newlySeen;
            }
        }
        return view.Count == 0 ? null : view;
    }

    static string? Label(JsonNode? node, int limit = 100)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)
            || text.Length > limit || text.Any(char.IsControl)) return null;
        return text.Trim();
    }

    static bool SpokenName(string words, string name)
    {
        var narration = ActionNotation.Find(words);
        var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])";
        return Regex.Matches(words, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Any(match => !narration.Any(span => span.Open < match.Index + match.Length && span.End >= match.Index));
    }
}
