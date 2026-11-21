using System;
using System.Collections.Generic;
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
        readonly float singleLineHeight;
        bool wasPaperEnabled,wasTextEnabled,masked;
        BubbleText layout=new BubbleText("",false);
        float previousWidth,draftWidth,draftOverflow;
        Vector2 noteSize;
        string lastDraft="",lastSpeech="";
        int page=1;
        static readonly System.Reflection.FieldInfo MenuRows=AccessTools.Field(typeof(ContextMenuUI),"optionGameObjects");
        readonly Vector3[] corners=new Vector3[4];
        public int Pages=>Mathf.Max(1,text.textInfo.pageCount);
        public int Page=>page;
        public string PageHint=>Pages>1?"PgUp / PgDn  "+Page+" / "+Pages:"";
        public string FontName=>text.font.name;
        public string SpriteName=>paper.sprite.name;
        public bool Visible=>frame.gameObject.activeSelf;
        public string VisibleText=>text.GetParsedText();
        internal string ReadRenderedText()
        {
            text.ForceMeshUpdate(false,true);
            return text.GetParsedText().TrimEnd('\u200b','\0').Trim();
        }
        internal string ReadPageInk()
        {
            text.ForceMeshUpdate(false,true);
            var ink=new System.Text.StringBuilder();
            var info=text.textInfo;
            for(int i=0;i<info.characterCount;i++){
                var letter=info.characterInfo[i];
                if(!letter.isVisible||letter.pageNumber!=page-1||char.IsWhiteSpace(letter.character))continue;
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
        public bool EntireDraftInView()
        {
            var label=Input.textComponent;var view=Input.textViewport;
            for(int i=0;i<label.textInfo.characterCount;i++){
                var glyph=label.textInfo.characterInfo[i];if(!glyph.isVisible)continue;
                var mid=(glyph.bottomLeft+glyph.topRight)*.5f;
                if(!view.rect.Contains(view.InverseTransformPoint(label.rectTransform.TransformPoint(mid))))return false;
            }
            return true;
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
            var t=r.gameObject.AddComponent<TextMeshProUGUI>();t.font=source.font;t.fontSharedMaterial=source.fontSharedMaterial;t.fontSize=source.fontSize;
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
            padding=native.GetComponent<HorizontalLayoutGroup>()?.padding??new RectOffset(15,18,5,27);
            singleLineHeight=source.textInfo.lineCount==1?source.rectTransform.rect.height:source.fontSize;
            frame=Rect("Overhead speech",surface);frame.anchorMin=frame.anchorMax=surface.pivot;frame.pivot=native.rectTransform.pivot;
            paper=frame.gameObject.AddComponent<Image>();paper.sprite=native.sprite;paper.type=native.type;paper.material=native.material;
            paper.color=native.color;paper.pixelsPerUnitMultiplier=native.pixelsPerUnitMultiplier;paper.raycastTarget=false;
            body=Rect("Ink",frame);body.offsetMin=new Vector2(padding.left,padding.bottom);body.offsetMax=new Vector2(-padding.right,-padding.top);
            text=Words(Rect("Words",body));text.overflowMode=TextOverflowModes.Page;
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
            var typed=Words(Rect("Draft",viewport));typed.alignment=TextAlignmentOptions.TopLeft;typed.overflowMode=TextOverflowModes.Overflow;
            var hint=Words(Rect("Hint",viewport));hint.text="Speak…";hint.alignment=TextAlignmentOptions.TopLeft;hint.color=new Color(source.color.r,source.color.g,source.color.b,.72f);
            Input=edit.gameObject.AddComponent<DialogueInputField>();Input.targetGraphic=target;Input.textViewport=viewport;Input.textComponent=typed;Input.placeholder=hint;
            Input.richText=true;Input.isRichTextEditingAllowed=true;Input.characterLimit=1200;Input.lineType=TMP_InputField.LineType.MultiLineSubmit;
            Input.onFocusSelectAll=false;Input.customCaretColor=true;Input.caretColor=source.color;Input.caretWidth=2;
            var projection=new DraftProjection();typed.textPreprocessor=projection;typed.OnPreRenderText+=projection.MapCaret;
            Input.onValueChanged.AddListener(value=>ResizeDraft(value));Input.onSubmit.AddListener(value=>submit(value));
            edit.gameObject.SetActive(false);
        }
        public void Edit(string draft)
        {
            draftWidth=0;lastDraft="";
            SetVisible(true);text.gameObject.SetActive(false);Input.gameObject.SetActive(true);Input.SetTextWithoutNotify(draft);
            if(caret==null)caret=Input.GetComponentInChildren<TMP_SelectionCaret>(true)?.rectTransform;
            ResizeDraft(draft);
            EventSystem.current?.SetSelectedGameObject(Input.gameObject);Input.ActivateInputField();Input.stringPosition=draft.Length;
        }
        void ResizeDraft(string value)
        {
            if(!value.StartsWith(lastDraft,StringComparison.Ordinal))draftWidth=0;
            lastDraft=value;
            layout=new BubbleText(value.Length==0?"Speak…":value,true);previousWidth=0;Follow();
        }
        public void Show(string value,bool partial=false)
        {
            if(!Visible||Editing||!value.StartsWith(lastSpeech,StringComparison.Ordinal))page=1;
            lastSpeech=value;
            if(Input!=null&&Input.gameObject.activeSelf){Input.DeactivateInputField();Input.gameObject.SetActive(false);}
            SetVisible(true);text.gameObject.SetActive(true);
            layout=new BubbleText(value,false,partial);previousWidth=0;Follow();
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
            note.text=message;
            noteSize=string.IsNullOrEmpty(message)?Vector2.zero:note.GetPreferredValues(message,note.rectTransform.rect.width,Mathf.Infinity);
        }
        public void TurnPage(int direction){page=Mathf.Clamp(page+direction,1,Pages);text.pageToDisplay=page;}
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
            float width=Mathf.Clamp((surface.rect.width-24)/scale-padding.horizontal,70,660);
            if(Mathf.Abs(previousWidth-width)>.1f){
                var natural=text.GetPreferredValues(layout.Rich,Mathf.Infinity,Mathf.Infinity);
                float w=Mathf.Clamp(Mathf.Ceil(natural.x)+4,Editing?112:56,width);
                if(Editing){w=Mathf.Min(width,Mathf.Max(w,draftWidth));draftWidth=w;}
                var wrapped=text.GetPreferredValues(layout.Rich,w,Mathf.Infinity);
                float maxH=Mathf.Min(text.GetPreferredValues("Ag\nAg\nAg\nAg",w,Mathf.Infinity).y+2,surface.rect.height/scale-padding.vertical-24);
                float h=Mathf.Min(Mathf.Ceil(wrapped.y)+2,maxH);
                frame.sizeDelta=new Vector2(w+padding.horizontal,Mathf.Max(singleLineHeight,h)+padding.vertical);
                draftOverflow=Mathf.Max(0,wrapped.y+2-(frame.sizeDelta.y-padding.vertical));
                if(!Editing){text.text=layout.Rich;text.pageToDisplay=page;text.ForceMeshUpdate(false,true);page=Mathf.Clamp(page,1,Pages);text.pageToDisplay=page;}
                previousWidth=width;
            }
            if(Editing){
                var r=Input.textComponent.rectTransform;var pos=r.anchoredPosition;
                // TMP keeps the caret visible, but a shrinking draft can leave a stale scroll offset.
                var clamped=new Vector2(0,Mathf.Clamp(pos.y,0,draftOverflow));
                if((clamped-pos).sqrMagnitude>.001f)r.anchoredPosition=clamped;
                if(caret!=null&&(caret.anchoredPosition-clamped).sqrMagnitude>.001f)caret.anchoredPosition=clamped;
            }
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
        public void Dispose(){SetVisible(false);if(temporaryAnchor&&anchor!=null)UnityEngine.Object.Destroy(anchor.gameObject);}
    }
}
