using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;

namespace Mirthwood.Echoes
{
    // The input field owns the raw message. Wrapping and italic tags exist only in the rendered projection.
    internal sealed class BubbleText
    {
        public const int Columns=36;
        public readonly string Plain, Rich;
        readonly List<int> offsets=new List<int>(), lengths=new List<int>();

        public BubbleText(string raw,bool editing,bool partial=false)
        {
            var spans=ActionNotation.Find(raw,editing||partial);
            var visible=new StringBuilder(raw.Length);
            var italic=new List<bool>();var source=new List<int>();int span=0;
            for(int i=0;i<raw.Length;i++){
                while(span<spans.Count&&i>spans[span].End)span++;
                bool marked=span<spans.Count&&i>spans[span].Open&&i<spans[span].End;
                bool delimiter=span<spans.Count&&(i==spans[span].Open||(spans[span].Closed&&i==spans[span].End));
                bool pendingStar=partial&&i==raw.Length-1&&raw[i]=='*'&&(i==0||raw[i-1]!='*');
                if(!editing&&(delimiter||pendingStar))continue;
                visible.Append(raw[i]);source.Add(i);italic.Add(marked);
            }
            string words=visible.ToString();
            var starts=StringInfo.ParseCombiningCharacters(words);
            var breaks=new HashSet<int>();var replaced=new HashSet<int>();
            int line=0,lastSpace=-1;
            for(int g=0;g<starts.Length;g++){
                int p=starts[g];char c=words[p];
                if(c=='\r'||c=='\n'){line=g+1;lastSpace=-1;continue;}
                if(c=='\u200b')continue; // TMP appends an invisible caret sentinel.
                if(c==' '||c=='\t')lastSpace=g;
                if(g-line<Columns)continue;
                if(lastSpace>line){replaced.Add(starts[lastSpace]);line=lastSpace+1;}
                else{breaks.Add(p);line=g;}
                lastSpace=-1;
            }
            var plain=new StringBuilder();var rich=new StringBuilder();bool active=false;
            for(int i=0;i<words.Length;){
                if(breaks.Contains(i)){plain.Append('\n');rich.Append('\n');offsets.Add(source[i]);lengths.Add(0);}
                if(italic[i]!=active){active=italic[i];rich.Append(active?"<i>":"</i>");}
                int count=char.IsSurrogatePair(words,i)?2:1;
                offsets.Add(source[i]);lengths.Add(count);
                if(replaced.Contains(i)){plain.Append('\n');rich.Append('\n');}
                else{
                    plain.Append(words,i,count);
                    if(words[i]=='<')rich.Append("<noparse><</noparse>");else rich.Append(words,i,count);
                }
                i+=count;
            }
            if(active)rich.Append("</i>");
            Plain=plain.ToString();Rich=rich.ToString();
        }

        public void MapCaret(TMP_TextInfo info)
        {
            // Rich text, hidden delimiters and synthetic line feeds must never become editable source characters.
            int count=Math.Min(info.characterCount,offsets.Count);
            for(int i=0;i<count;i++){info.characterInfo[i].index=offsets[i];info.characterInfo[i].stringLength=lengths[i];}
        }
    }

    internal sealed class DraftProjection : ITextPreprocessor
    {
        string last;
        BubbleText layout;
        public string PreprocessText(string value)
        {
            if(last!=value||layout==null){last=value;layout=new BubbleText(value,true);}
            return layout.Rich;
        }
        public void MapCaret(TMP_TextInfo info){layout?.MapCaret(info);}
    }
}
