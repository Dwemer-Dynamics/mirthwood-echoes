using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

internal sealed class WordAlignment : IDisposable
{
    const string Lib="pocketsphinx";
    IntPtr decoder;
    bool tried;
    [DllImport(Lib)] static extern IntPtr ps_config_init(IntPtr arguments);
    [DllImport(Lib)] static extern IntPtr ps_config_set_str(IntPtr config,string key,string? value);
    [DllImport(Lib)] static extern IntPtr ps_init(IntPtr config);
    [DllImport(Lib)] static extern int ps_config_free(IntPtr config);
    [DllImport(Lib)] static extern int ps_free(IntPtr ps);
    [DllImport(Lib)] static extern int ps_set_align_text(IntPtr ps,string text);
    [DllImport(Lib)] static extern int ps_start_utt(IntPtr ps);
    [DllImport(Lib)] static extern int ps_process_raw(IntPtr ps,short[] samples,nuint length,int noSearch,int full);
    [DllImport(Lib)] static extern int ps_end_utt(IntPtr ps);
    [DllImport(Lib)] static extern IntPtr ps_seg_iter(IntPtr ps);
    [DllImport(Lib)] static extern IntPtr ps_seg_word(IntPtr seg);
    [DllImport(Lib)] static extern void ps_seg_frames(IntPtr seg,out int start,out int end);
    [DllImport(Lib)] static extern IntPtr ps_seg_next(IntPtr seg);

    bool Ready(string root)
    {
        if(tried)return decoder!=IntPtr.Zero;
        tried=true;
        if(!File.Exists(Path.Combine(root,"en-us","mdef")))return false;
        try{
            IntPtr config=ps_config_init(IntPtr.Zero);
            try{
                ps_config_set_str(config,"hmm",Path.Combine(root,"en-us"));
                ps_config_set_str(config,"dict",Path.Combine(root,"cmudict-en-us.dict"));
                ps_config_set_str(config,"loglevel","FATAL");
                decoder=ps_init(config);
            }finally{ps_config_free(config);}
        }catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
        return decoder!=IntPtr.Zero;
    }
    // Unknown names can be absent from the English dictionary. Use the measured amplitude
    // envelope to pace those captions, and identify these timings as estimates in the response.
    static WordCue[] Estimate(string text,float[] samples,int offset,Match[] tokens)
    {
        var energy=new float[Math.Max(1,(samples.Length+239)/240)];
        for(int i=0;i<samples.Length;i++)energy[i/240]+=Math.Abs(samples[i])/240;
        float gate=Math.Max(.0005f,energy.Max()*.05f);
        int first=Math.Max(0,Array.FindIndex(energy,e=>e>gate));
        int last=Math.Max(first,Array.FindLastIndex(energy,e=>e>gate));
        if(tokens.Length==0)return [new(first*10,offset+text.Length)];
        var weights=tokens.Select(t=>Math.Clamp(t.Length,2,12)).ToArray();
        int total=weights.Sum(),used=0,previous=first;
        var result=new WordCue[tokens.Length];
        for(int i=0;i<tokens.Length;i++){
            int frame=first;
            if(i>0){
                int expected=first+(last-first)*used/total;
                int radius=Math.Max(1,(last-first)*Math.Min(weights[i-1],weights[i])/(3*total));
                int lo=Math.Min(last,Math.Max(previous+1,expected-radius));
                int hi=Math.Min(last,Math.Max(lo,expected+radius));
                frame=lo;
                for(int f=lo+1;f<=hi;f++)
                    if(energy[f]<energy[frame])frame=f;
            }
            int end=i+1<tokens.Length?tokens[i+1].Index:text.Length;
            result[i]=new(frame*10,offset+end);previous=frame;used+=weights[i];
        }
        return result;
    }
    public WordCue[] Align(string text,float[] source,int offset,string root,out string timing)
    {
        timing="estimated";
        var tokens=Regex.Matches(text,@"[\p{L}\d]+(?:['’][\p{L}\d]+)*").Cast<Match>().ToArray();
        if(tokens.Length==0||!Ready(root))return Estimate(text,source,offset,tokens);
        var normalized=tokens.Select(t=>t.Value.ToLowerInvariant().Replace('’','\'')).ToArray();
        if(ps_set_align_text(decoder,string.Join(" ",normalized))<0||ps_start_utt(decoder)<0)return Estimate(text,source,offset,tokens);
        // Pocket supplies 24 kHz. Low-pass the input before the 3:2 conversion for the 16 kHz acoustic model.
        var pcm=new short[source.Length*2/3];
        for(int i=0;i<pcm.Length;i++){
            int j=i*3/2;float a=source[Math.Max(0,j-1)],b=source[j],c=source[Math.Min(j+1,source.Length-1)];
            float sample=(i&1)==0?(a+2*b+c)/4:(b+c)/2;
            pcm[i]=(short)Math.Clamp((int)(sample*32767),-32768,32767);
        }
        int processed=ps_process_raw(decoder,pcm,(nuint)pcm.Length,0,1);
        if(ps_end_utt(decoder)<0||processed<0)return Estimate(text,source,offset,tokens);
        var result=new List<WordCue>();bool matches=true;
        for(IntPtr seg=ps_seg_iter(decoder);seg!=IntPtr.Zero;seg=ps_seg_next(seg)){
            string word=Marshal.PtrToStringUTF8(ps_seg_word(seg))??"";
            if(word.StartsWith('<')||word.StartsWith('['))continue;
            word=Regex.Replace(word,@"\(\d+\)$","");
            int index=result.Count;
            if(index>=tokens.Length||word!=normalized[index]){matches=false;continue;}
            ps_seg_frames(seg,out int start,out _);
            // Include following punctuation in its word's reveal.
            int end=index+1==tokens.Length?text.Length:tokens[index+1].Index;
            result.Add(new(start*10,offset+end));
        }
        if(!matches||result.Count!=tokens.Length)return Estimate(text,source,offset,tokens);
        timing="aligned";return result.ToArray();
    }
    public void Dispose(){if(decoder!=IntPtr.Zero){ps_free(decoder);decoder=IntPtr.Zero;}}
}
