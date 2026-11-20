using System.Collections.Generic;

namespace Mirthwood.Echoes
{
    internal static class ActionNotation
    {
        internal readonly struct Span
        {
            public readonly int Open, End;
            public readonly bool Closed;
            public Span(int open,int end,bool closed){Open=open;End=end;Closed=closed;}
        }

        // A single pair marks narration. Leave empty, doubled and unmatched final markers literal.
        public static List<Span> Find(string value,bool preview=false)
        {
            var spans=new List<Span>();int open=-1;
            for(int i=0;i<value.Length;i++){
                char c=value[i];
                if(c=='\n'||c=='\r'){open=-1;continue;}
                if(c!='*')continue;
                if((i>0&&value[i-1]=='*')||(i+1<value.Length&&value[i+1]=='*'))continue;
                if(open>=0&&i>open+1&&!char.IsWhiteSpace(value[i-1])){
                    spans.Add(new Span(open,i,true));open=-1;
                }else if(i+1<value.Length&&!char.IsWhiteSpace(value[i+1])&&
                    !(i>0&&char.IsDigit(value[i-1])&&char.IsDigit(value[i+1])))open=i;
            }
            if(preview&&open>=0)spans.Add(new Span(open,value.Length,false));
            return spans;
        }
    }
}
