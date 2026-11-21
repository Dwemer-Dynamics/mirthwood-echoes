using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using _Mirthwood.Scripts.Demo.CharacterOverheadUI;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Menus;
using BoundsRect = UnityEngine.Rect;

namespace Mirthwood.Echoes
{
    internal sealed class OverheadSpeech
    {
        readonly RectTransform surface,frame,body;
        readonly Transform anchor,speaker;
        readonly TextMeshProUGUI source,text,note;
        readonly Image nativePaper,ownPaper,paper;
        readonly TextMeshProUGUI ownWords;
        RectTransform caret;
        readonly RectOffset padding;
        readonly Camera camera;
        readonly bool temporaryAnchor;
        readonly float singleLineHeight,dialogueFontSize,tailInset,capWidth;
        bool wasPaperEnabled,wasTextEnabled,masked;
        BubbleText layout=new BubbleText("",false);
        float previousWidth,previousHeight;
        Vector2 noteSize;
        static readonly System.Reflection.FieldInfo MenuRows=AccessTools.Field(typeof(ContextMenuUI),"optionGameObjects");
        readonly Vector3[] corners=new Vector3[4];
        public string FontName=>text.font.name;
        public string SpriteName=>paper.sprite.name;
        public bool Visible=>frame.gameObject.activeSelf;
        public string VisibleText=>text.GetParsedText();
        internal string ReadRenderedText()
        {
            text.ForceMeshUpdate(false,true);
            return text.GetParsedText().TrimEnd('\u200b','\0').Trim();
        }
        internal string ReadVisibleInk()
        {
            text.ForceMeshUpdate(false,true);
            var ink=new System.Text.StringBuilder();
            var info=text.textInfo;
            for(int i=0;i<info.characterCount;i++){
                var letter=info.characterInfo[i];
                if(!letter.isVisible||char.IsWhiteSpace(letter.character))continue;
                var vertices=info.meshInfo[letter.materialReferenceIndex].vertices;
                if(vertices[letter.vertexIndex]!=vertices[letter.vertexIndex+2])ink.Append(letter.character);
            }
            return ink.ToString();
        }
        public bool UsesNativeStyle=>paper.sprite==nativePaper.sprite&&text.font==source.font&&text.fontSharedMaterial==source.fontSharedMaterial;
        public TMP_InputField Input { get; private set; }
        public bool Editing=>Input!=null&&Input.gameObject.activeSelf;
        public Vector2 Size=>frame.sizeDelta;
        public bool AnchoredTo(Transform who)=>speaker==who&&anchor.IsChildOf(who)&&camera.WorldToScreenPoint(anchor.position).y>camera.WorldToScreenPoint(who.position).y;
        public bool DraftEndInView()
        {
            var label=Input.textComponent;var view=Input.textViewport;
            var glyph=label.textInfo.characterInfo[Mathf.Max(0,label.textInfo.characterCount-2)];
            var mid=(glyph.bottomLeft+glyph.topRight)*.5f;
            return view.rect.Contains(view.InverseTransformPoint(label.rectTransform.TransformPoint(mid)));
        }
        public bool EntireDraftInView()=>InkFits(Input.textComponent,Input.textViewport)&&InkFits(Input.textComponent,frame);
        internal bool EntireSpeechInView()=>InkFits(text,frame);
        internal bool WholeWordsOnLines(string expected)
        {
            var label=Editing?Input.textComponent:text;
            label.ForceMeshUpdate(false,true);
            if(label.GetParsedText().TrimEnd('\u200b','\0')!=expected)return false;
            var glyphs=label.textInfo.characterInfo;
            for(int i=1;i<expected.Length;i++){
                if(!char.IsWhiteSpace(expected[i-1])&&!char.IsWhiteSpace(expected[i])&&glyphs[i-1].lineNumber!=glyphs[i].lineNumber)return false;
            }
            return true;
        }
        internal bool ShowsCompleteMessage(string raw)=>EntireSpeechInView()&&
            ReadVisibleInk()==string.Concat(new BubbleText(raw,false).Plain.Where(c=>!char.IsWhiteSpace(c)));
        internal float RelativeTypeSize=>text.fontSize/source.fontSize;
        internal string InkDetail(bool draft)=>draft?InkError(Input.textComponent,Input.textViewport):InkError(text,body);
        static bool InkFits(TMP_Text label,RectTransform view)
            =>InkError(label,view)==null;
        static string InkError(TMP_Text label,RectTransform view)
        {
            label.ForceMeshUpdate(false,true);
            var bounds=view.rect;
            for(int i=0;i<label.textInfo.characterCount;i++){
                var glyph=label.textInfo.characterInfo[i];if(!glyph.isVisible)continue;
                var vertices=label.textInfo.meshInfo[glyph.materialReferenceIndex].vertices;
                if(vertices[glyph.vertexIndex]==vertices[glyph.vertexIndex+2])return "Empty glyph "+i;
                for(int corner=0;corner<4;corner++){
                    var v=view.InverseTransformPoint(label.rectTransform.TransformPoint(vertices[glyph.vertexIndex+corner]));
                    if(v.x<bounds.xMin-.5f||v.x>bounds.xMax+.5f||v.y<bounds.yMin-.5f||v.y>bounds.yMax+.5f)return "Glyph "+i+" at "+v+" outside "+bounds;
                }
            }
            return null;
        }
        public bool CaretAligned=>caret!=null&&(caret.anchoredPosition-Input.textComponent.rectTransform.anchoredPosition).sqrMagnitude<.01f;

        public static OverheadUIController Board(Component actor)=>actor?.GetComponentInChildren<OverheadUIController>(true);
        static Image Paper(OverheadUIController board)=>board==null?null:(AccessTools.Field(typeof(OverheadUIController),"overheadTextCanvasGroup")?.GetValue(board) as CanvasGroup)?.GetComponent<Image>();

        public static OverheadSpeech Create(NPCController resident,RectTransform surface,PlayerCharacterController player=null)
        {
            var reference=Board(resident);var native=Paper(reference);var camera=Camera.main;
            if(native==null||native.sprite==null||reference.OverheadText?.font==null||camera==null)return null;
            var who=player==null?resident.transform:player.transform;
            var own=player==null?reference:Board(player);var ownImage=Paper(own);
            Transform anchor=ownImage?.transform;
            if(anchor==null){
                // Player prefabs without a bark inherit the resident's measured head offset and retain their own anchor.
                anchor=new GameObject("Echoes player speech anchor").transform;anchor.SetParent(who,false);
                anchor.position=who.position+(native.transform.position-resident.transform.position);
            }
            return new OverheadSpeech(surface,native,reference.OverheadText,who,anchor,ownImage,own?.OverheadText,camera);
        }
        static RectTransform Rect(string name,Transform parent)
        {
            var r=new GameObject(name,typeof(RectTransform)).GetComponent<RectTransform>();r.SetParent(parent,false);
            r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.offsetMin=r.offsetMax=Vector2.zero;return r;
        }
        TextMeshProUGUI Words(RectTransform r)
        {
            var t=r.gameObject.AddComponent<TextMeshProUGUI>();t.font=source.font;t.fontSharedMaterial=source.fontSharedMaterial;t.enableAutoSizing=false;t.fontSize=dialogueFontSize;
            t.fontStyle=source.fontStyle;t.color=source.color;t.margin=source.margin;t.lineSpacing=source.lineSpacing;
            t.characterSpacing=source.characterSpacing;t.wordSpacing=source.wordSpacing;t.alignment=source.alignment;
            t.richText=true;t.parseCtrlCharacters=false;t.raycastTarget=false;t.enableWordWrapping=true;
            var shadow=source.GetComponent<Shadow>();
            if(shadow!=null){var ink=r.gameObject.AddComponent<Shadow>();ink.effectColor=shadow.effectColor;ink.effectDistance=shadow.effectDistance;ink.useGraphicAlpha=shadow.useGraphicAlpha;}
            return t;
        }
        OverheadSpeech(RectTransform surface,Image native,TextMeshProUGUI source,Transform speaker,Transform anchor,Image own,TextMeshProUGUI ownWords,Camera camera)
        {
            this.surface=surface;this.source=source;this.camera=camera;this.speaker=speaker;this.anchor=anchor;nativePaper=native;ownPaper=own;this.ownWords=ownWords;temporaryAnchor=own==null;
            int tailRoom=native.GetComponent<HorizontalLayoutGroup>()?.padding.bottom??27;
            padding=new RectOffset(8,8,5,Mathf.Clamp(tailRoom,18,30)+5);
            dialogueFontSize=source.fontSize*.85f;
            singleLineHeight=dialogueFontSize;
            frame=Rect("Overhead speech",surface);frame.anchorMin=frame.anchorMax=surface.pivot;
            frame.pivot=new Vector2(0,native.rectTransform.pivot.y);
            paper=frame.gameObject.AddComponent<Image>();paper.sprite=native.sprite;paper.type=native.type;paper.material=native.material;
            paper.color=native.color;paper.pixelsPerUnitMultiplier=native.pixelsPerUnitMultiplier;paper.raycastTarget=false;
            // The native sliced paper keeps the tail in its left cap as the right edge grows.
            float spriteUnits=native.pixelsPerUnit*native.pixelsPerUnitMultiplier;
            tailInset=native.sprite.border.x/spriteUnits*.5f;
            capWidth=(native.sprite.border.x+native.sprite.border.z)/spriteUnits;
            body=Rect("Ink",frame);body.anchorMin=body.anchorMax=body.pivot=new Vector2(0,1);
            text=Words(Rect("Words",body));text.alignment=TextAlignmentOptions.TopLeft;text.overflowMode=TextOverflowModes.Overflow;
            text.margin=new Vector4(5,0,5,0);
            var top=Rect("Keys and messages",frame);top.anchorMin=top.anchorMax=new Vector2(0,1);top.pivot=new Vector2(0,0);
            top.anchoredPosition=new Vector2(2,1);top.sizeDelta=new Vector2(495,65);
            note=Words(top);note.richText=false;note.fontSize=25;note.alignment=TextAlignmentOptions.BottomLeft;note.color=new Color32(255,247,216,255);
            var shadowNote=top.GetComponent<Shadow>()??top.gameObject.AddComponent<Shadow>();shadowNote.effectColor=new Color32(29,24,15,255);shadowNote.effectDistance=new Vector2(2,-2);
            frame.gameObject.SetActive(false);
        }
        public void CreateEditor(Action<string> submit)
        {
            var edit=Rect("Player input",body);
            var viewport=Rect("Viewport",edit);viewport.gameObject.AddComponent<RectMask2D>();
            var target=edit.gameObject.AddComponent<Image>();target.color=Color.clear;
            var typed=Words(Rect("Draft",viewport));typed.margin=text.margin;typed.alignment=TextAlignmentOptions.TopLeft;typed.overflowMode=TextOverflowModes.Overflow;
            var hint=Words(Rect("Hint",viewport));hint.margin=text.margin;hint.text="Speak…";hint.alignment=TextAlignmentOptions.TopLeft;hint.color=new Color(source.color.r,source.color.g,source.color.b,.72f);
            Input=edit.gameObject.AddComponent<DialogueInputField>();Input.targetGraphic=target;Input.textViewport=viewport;Input.textComponent=typed;Input.placeholder=hint;
            Input.richText=true;Input.isRichTextEditingAllowed=true;Input.characterLimit=1200;Input.lineType=TMP_InputField.LineType.MultiLineSubmit;
            Input.onFocusSelectAll=false;Input.customCaretColor=true;Input.caretColor=source.color;Input.caretWidth=2;
            var projection=new DraftProjection();typed.textPreprocessor=projection;typed.OnPreRenderText+=projection.MapCaret;
            Input.onValueChanged.AddListener(value=>ResizeDraft(value));Input.onSubmit.AddListener(value=>submit(value));
            edit.gameObject.SetActive(false);
        }
        public void Edit(string draft)
        {
            SetVisible(true);text.gameObject.SetActive(false);Input.gameObject.SetActive(true);Input.SetTextWithoutNotify(draft);
            if(caret==null)caret=Input.GetComponentInChildren<TMP_SelectionCaret>(true)?.rectTransform;
            ResizeDraft(draft);Follow();
            EventSystem.current?.SetSelectedGameObject(Input.gameObject);Input.ActivateInputField();Input.stringPosition=draft.Length;
        }
        void ResizeDraft(string value)
        {
            layout=new BubbleText(value.Length==0?"Speak…":value,true);
            // TMP finishes positioning the caret after its value-changed event.
            previousWidth=0;
        }
        public void Show(string value,bool partial=false,int spokenStart=-1,int spokenEnd=-1)
        {
            if(Input!=null&&Input.gameObject.activeSelf){Input.DeactivateInputField();Input.gameObject.SetActive(false);}
            SetVisible(true);text.gameObject.SetActive(true);
            layout=new BubbleText(value,false,partial,spokenStart,spokenEnd);previousWidth=0;Follow();
        }
        internal void ShowSpoken(string line,string heard,bool partial)
        {
            int end=partial&&line.StartsWith(heard,StringComparison.Ordinal)?heard.Length:0;
            while(end>0&&char.IsWhiteSpace(line[end-1]))end--;
            int start=end;
            while(start>0&&!char.IsWhiteSpace(line[start-1]))start--;
            Show(line,false,start,end);
        }
        public void SetVisible(bool visible)
        {
            if(visible&&!masked&&ownPaper!=null){
                wasPaperEnabled=ownPaper.enabled;wasTextEnabled=ownWords!=null&&ownWords.enabled;
                ownPaper.enabled=false;if(ownWords!=null)ownWords.enabled=false;masked=true;
            }else if(!visible&&masked){
                if(ownPaper!=null)ownPaper.enabled=wasPaperEnabled;if(ownWords!=null)ownWords.enabled=wasTextEnabled;masked=false;
            }
            frame.gameObject.SetActive(visible);
        }
        public void Note(string message)
        {
            if(note.text==message)return;
            note.text=message;previousHeight=0;
            noteSize=string.IsNullOrEmpty(message)?Vector2.zero:note.GetPreferredValues(message,note.rectTransform.rect.width,Mathf.Infinity);
        }
        void FitMessage(float width,float height)
        {
            float size=dialogueFontSize;
            float sides=text.margin.x+text.margin.z;
            TMP_Text label=Editing?(Input.text.Length==0?(TMP_Text)Input.placeholder:Input.textComponent):text;
            BoundsRect ink;
            for(;;){
                text.fontSize=size;
                var natural=text.GetPreferredValues(layout.Rich,Mathf.Infinity,Mathf.Infinity);
                float w=Mathf.Min(width,Mathf.Ceil(natural.x)+sides+2);
                var wrapped=text.GetPreferredValues(layout.Rich,Mathf.Max(1,w-sides),Mathf.Infinity);
                body.sizeDelta=new Vector2(w,Mathf.Max(size,Mathf.Ceil(wrapped.y))+6);
                if(Editing){
                    Input.textComponent.fontSize=size;((TMP_Text)Input.placeholder).fontSize=size;
                    Input.ForceLabelUpdate();Input.textComponent.rectTransform.anchoredPosition=Vector2.zero;
                }else text.text=layout.Rich;
                label.ForceMeshUpdate(true,true);
                ink=LetterBounds(label,Editing);
                if(ink.height<=height&&ink.width<=width||size<=.1f)break;
                float ratio=Mathf.Min(height/Mathf.Max(1,ink.height),width/Mathf.Max(1,ink.width));
                size=Mathf.Max(.1f,size*Mathf.Clamp(ratio*.98f,.03f,.93f));
            }
            // Keep the measured wrap width. Trimming the parchment must not reflow its last word.
            body.anchoredPosition=new Vector2(padding.left-ink.xMin,-padding.top-ink.yMax);
            frame.sizeDelta=new Vector2(Mathf.Ceil(ink.width)+padding.horizontal,Mathf.Ceil(ink.height)+padding.vertical);
        }
        BoundsRect LetterBounds(TMP_Text label,bool includeCaret)
        {
            Vector2 lo=Vector2.positiveInfinity,hi=Vector2.negativeInfinity;
            var toBody=body.worldToLocalMatrix*label.rectTransform.localToWorldMatrix;
            void Include(Vector3 local){
                Vector2 point=toBody.MultiplyPoint3x4(local);
                lo=Vector2.Min(lo,point);hi=Vector2.Max(hi,point);
            }
            var info=label.textInfo;
            for(int i=0;i<info.characterCount;i++){
                var c=info.characterInfo[i];
                if(c.isVisible){
                    var v=info.meshInfo[c.materialReferenceIndex].vertices;
                    for(int k=0;k<4;k++)Include(v[c.vertexIndex+k]);
                }
                if(includeCaret){
                    Include(new Vector3(c.origin,c.descender));Include(new Vector3(c.xAdvance+Input.caretWidth,c.ascender));
                }
            }
            return lo.x<=hi.x?BoundsRect.MinMaxRect(lo.x,lo.y,hi.x,hi.y):new BoundsRect(0,-label.fontSize,1,label.fontSize);
        }
        internal Vector4 PaperMargins()
        {
            text.ForceMeshUpdate(false,true);
            var ink=LetterBounds(text,false);
            Vector2 origin=(Vector2)frame.InverseTransformPoint(body.TransformPoint(Vector3.zero));
            var box=frame.rect;
            return new Vector4(origin.x+ink.xMin-box.xMin,box.yMax-origin.y-ink.yMax,
                box.xMax-origin.x-ink.xMax,origin.y+ink.yMin-box.yMin-(padding.bottom-5));
        }
        public bool Follow()
        {
            if(anchor==null||speaker==null||nativePaper==null||source==null||camera==null||
                !speaker.gameObject.activeInHierarchy||!anchor.gameObject.scene.isLoaded)return false;
            if(!Visible)return true;
            var pixel=camera.WorldToScreenPoint(anchor.position);
            if(pixel.z<=0)return false;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(surface,pixel,null,out var target);
            var unit=camera.WorldToScreenPoint(anchor.position+nativePaper.transform.TransformVector(Vector3.up*100));
            var scale=Mathf.Clamp(Vector2.Distance(pixel,unit)/(100*surface.GetComponent<Canvas>().scaleFactor),.38f,1.2f);
            frame.localScale=new Vector3(scale,scale,1);
            float width=Mathf.Clamp((surface.rect.width-24)/scale-padding.horizontal,112,1050);
            float height=Mathf.Max(singleLineHeight+10,(surface.rect.height-24)/scale-padding.vertical-noteSize.y-2);
            if(Mathf.Abs(previousWidth-width)>.1f||Mathf.Abs(previousHeight-height)>.5f){
                FitMessage(width,height);
                previousWidth=width;previousHeight=height;
            }
            if(Editing){
                var r=Input.textComponent.rectTransform;var pos=r.anchoredPosition;
                if(pos.sqrMagnitude>.001f)r.anchoredPosition=Vector2.zero;
                if(caret!=null&&caret.anchoredPosition.sqrMagnitude>.001f)caret.anchoredPosition=Vector2.zero;
            }
            // Unity also compresses both sliced borders when a short message is narrower than their sum.
            float capScale=Editing||capWidth<=0?1:Mathf.Min(1,frame.sizeDelta.x/capWidth);
            target.x-=tailInset*scale*capScale;
            frame.anchoredPosition=Constrain(target,scale);return true;
        }
        BoundsRect InkRect()
        {
            var box=frame.rect;
            var offset=note.rectTransform.anchoredPosition;
            float right=noteSize.x>0?Mathf.Max(box.xMax,box.xMin+offset.x+noteSize.x):box.xMax;
            float top=box.yMax+(noteSize.y>0?offset.y+noteSize.y:0);
            return BoundsRect.MinMaxRect(box.xMin,box.yMin,right,top);
        }
        internal BoundsRect Footprint {
            get {var ink=InkRect();float scale=frame.localScale.x;return new BoundsRect(frame.anchoredPosition+ink.min*scale,ink.size*scale);}
        }
        Vector2 Constrain(Vector2 point,float scale)
        {
            var box=InkRect();var bounds=surface.rect;
            return new Vector2(Mathf.Clamp(point.x,bounds.xMin+8-box.xMin*scale,bounds.xMax-8-box.xMax*scale),
                Mathf.Clamp(point.y,bounds.yMin+8-box.yMin*scale,bounds.yMax-8-box.yMax*scale));
        }
        internal void Avoid(BoundsRect obstacle)
        {
            var ink=Footprint;
            if(!Visible||!obstacle.Overlaps(ink))return;
            var origin=frame.anchoredPosition;var best=origin;
            float area=float.MaxValue,distance=float.MaxValue;
            void Try(Vector2 offset){
                var at=Constrain(origin+offset,frame.localScale.x);
                var moved=new BoundsRect(ink.position+at-origin,ink.size);
                float overlap=Mathf.Max(0,Mathf.Min(moved.xMax,obstacle.xMax)-Mathf.Max(moved.xMin,obstacle.xMin))*
                    Mathf.Max(0,Mathf.Min(moved.yMax,obstacle.yMax)-Mathf.Max(moved.yMin,obstacle.yMin));
                float delta=(at-origin).sqrMagnitude;
                if(overlap<area||overlap==area&&delta<distance){area=overlap;distance=delta;best=at;}
            }
            Try(Vector2.up*(obstacle.yMax-ink.yMin+6));Try(Vector2.down*(ink.yMax-obstacle.yMin+6));
            Try(Vector2.right*(obstacle.xMax-ink.xMin+6));Try(Vector2.left*(ink.xMax-obstacle.xMin+6));
            frame.anchoredPosition=best;
        }
        internal BoundsRect? QuickMenuBounds(ContextMenuUI menu)
        {
            if(!menu||!menu.IsVisible||!menu.gameObject.activeInHierarchy||!(MenuRows?.GetValue(menu) is Queue<GameObject> rows))return null;
            Vector2 min=Vector2.positiveInfinity,max=Vector2.negativeInfinity;
            foreach(var row in rows){
                if(!row.activeInHierarchy||!(row.transform is RectTransform rect))continue;
                var canvas=row.GetComponentInParent<Canvas>();
                if(!canvas||!canvas.isActiveAndEnabled)continue;
                var eye=canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:canvas.worldCamera??camera;
                rect.GetWorldCorners(corners);
                foreach(var corner in corners){
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(surface,RectTransformUtility.WorldToScreenPoint(eye,corner),null,out var p);
                    min=Vector2.Min(min,p);max=Vector2.Max(max,p);
                }
            }
            return min.x<max.x&&min.y<max.y?new BoundsRect(min,max-min):(BoundsRect?)null;
        }
        static bool OnScreen(Vector3 world)
        {
            var p=RectTransformUtility.WorldToScreenPoint(null,world);
            return p.x>=0&&p.x<=Screen.width&&p.y>=0&&p.y<=Screen.height;
        }
        public bool FitsScreen()
        {
            var corners=new Vector3[4];frame.GetWorldCorners(corners);
            foreach(var c in corners)if(!OnScreen(c))return false;
            if(string.IsNullOrEmpty(note.text))return true;
            note.ForceMeshUpdate(false,true);
            var ink=note.textBounds;
            return OnScreen(note.transform.TransformPoint(ink.min))&&OnScreen(note.transform.TransformPoint(ink.max));
        }
        public bool ChecksEdges()
        {
            var pos=frame.anchoredPosition;var bounds=surface.rect;bool fits=true;
            foreach(float x in new[]{bounds.xMin,bounds.xMax})foreach(float y in new[]{bounds.yMin,bounds.yMax}){
                frame.anchoredPosition=Constrain(new Vector2(x,y),frame.localScale.x);fits&=FitsScreen();
            }
            frame.anchoredPosition=pos;return fits;
        }
        public bool ChecksTracking()
        {
            var start=anchor.localPosition;var position=frame.anchoredPosition;
            try{anchor.position+=camera.transform.right*.35f;return Follow()&&Vector2.Distance(position,frame.anchoredPosition)>.2f;}
            finally{anchor.localPosition=start;Follow();}
        }
        public bool Styled(string word,bool italic)
        {
            var label=Editing?Input.textComponent:text;label.ForceMeshUpdate(false,true);
            var parsed=label.GetParsedText();int start=parsed.IndexOf(word,StringComparison.Ordinal);
            if(start<0)return false;
            for(int i=start;i<start+word.Length;i++)
                if(((label.textInfo.characterInfo[i].style&FontStyles.Italic)!=0)!=italic)return false;
            return true;
        }
        internal bool SpokenWordMarked()=>text.textInfo.characterInfo.Take(text.textInfo.characterCount)
            .Any(c=>c.isVisible&&(c.style&FontStyles.Underline)!=0);
        public void Dispose(){SetVisible(false);if(temporaryAnchor&&anchor!=null)UnityEngine.Object.Destroy(anchor.gameObject);}
    }
}
