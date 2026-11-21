'use strict';

const characterViews = [
  ['profile','Profile'], ['interactions','Interactions'], ['sessions','Conversations'],
  ['daily','Daily history'], ['commitments','Commitments'], ['personality','Dynamic personality']
];
const cap = s => s ? s[0].toUpperCase() + s.slice(1) : '';
const pretty = s => cap(String(s).replace(/[_-]/g,' ').replace(/([a-z])([A-Z])/g,'$1 $2'));
const factsList = (pairs) => {
  const dl = el('dl','profile-facts');
  for (const [name,value] of pairs) if(value != null && value !== '') dl.append(attach(el('div'),el('dt','',name),el('dd','',value)));
  return dl;
};
function traitReadings(axes) {
  const grid=el('dl','traits-grid');
  for(const a of axes.filter(x=>x.score!=null)){
    const d=el('div','trait-row');
    d.append(el('dt','',a.label),el('dd','',a.reading),el('small','',cap(a.low)+' / '+cap(a.high)));
    grid.append(d);
  }
  return grid;
}
function itemList(values) {
  const ul=el('ul','profile-list');for(const v of values)ul.append(el('li','',v));return ul;
}
function originalSave(raw) {
  const data=el('div','native'), disclosure=el('details','original-save');
  disclosure.append(el('summary','','Original save details'));
  disclosure.ontoggle=()=>{
    if(!disclosure.open || data.childElementCount)return;
    for(const [name,key] of [['Character controller','controller'],['Last recorded controller','lastKnownController']]){
      const c=raw[key];if(c)data.append(nativeTree(name,c.data),nativeTree('Controller identity',{guid:c.guid,scene:c.scene,type:c.type}));
    }
    for(const record of raw.components||raw.lastKnownComponents||[])data.append(nativeTree(pretty(record.type),record));
    if(raw.registry?.length)data.append(nativeTree('Population register',raw.registry));
  };
  disclosure.append(data);return disclosure;
}
function profileView(profile,story,raw,roster,path,onStory) {
  const box=el('div','profile-pages');
  const facts=factsList([['Hometown',profile.town],['Origin',profile.origin],['Social class',profile.socialClass],['Work',profile.job || (profile.kind==='NPC'?'No recorded occupation':null)],
    ['Family',profile.family],['Spouse',profile.spouse],['Renown',profile.renown],['Morality',profile.morality],
    ['Met the player',profile.metPlayer==null?null:profile.metPlayer?'Yes':'No'],['Hired by the player',profile.isHired==null?null:profile.isHired?'Yes':'No']]);
  if(profile.lastKnown)box.append(el('p','hint','Details from the last living record.'));
  const traits=traitReadings(profile.axes);
  if(traits.childElementCount)box.append(section('Native temperament',traits,
    profile.socialNeed && el('p','field-note','Need for company at the last save: '+profile.socialNeed.toLowerCase()+'.')));
  if(facts.childElementCount)box.append(section('About '+profile.name,facts));
  const s=profile.skills;
  const abilities=factsList([['Positive traits',s.positiveTraits.join(', ')],['Negative traits',s.negativeTraits.join(', ')],
    ['Skill strengths',s.strengths.join(', ')],['Skill weaknesses',s.weaknesses.join(', ')],
    ['Favorite gifts',profile.liked.join(', ')],['Disliked gift',profile.disliked]]);
  if(abilities.childElementCount)box.append(section('Traits, skills & tastes',abilities));
  if(profile.topicFit){
    const names=topics=>topics.map(x=>({Deep:'Deep conversation',Dumb:'Silly conversation',Intellectual:'Intellectual discussion'}[x]||x)).join(', ');
    const readings=factsList([['Closest fit',names(profile.topicFit.closer)],['Weaker fit',names(profile.topicFit.farther)]]);
    if(readings.childElementCount)box.append(section('Conversation interests',readings,el('p','field-note','Based on the saved temperament and the game’s conversation topics. Mood and the other speaker can change the reaction.')));
  }
  const storyBox=section('Backstory');box.append(storyBox);
  function paintStory(value) {
    storyBox.replaceChildren(el('h3','','Backstory'));
    if(value){
      storyBox.append(el('p','journal-prose',value.text),el('small','source',value.source));
      if(value.grounding){
        const known=[value.grounding.town,value.grounding.origin,value.grounding.socialClass,value.grounding.job].filter(Boolean);
        if(known.length)storyBox.append(el('p','hint','Based on '+known.join(' · ')+'.'));
      }
    }else{
      const msg=el('p','hint','No backstory is recorded.');
      const generate=button('Generate backstory',async b=>{
        b.disabled=true;msg.textContent='Writing from the saved character profile…';
        try {const result=await api(path+'/backstory','POST',{});onStory(result);paintStory(result);}
        catch(e){msg.textContent=e.message;if(/provider|model|key|sign.in/i.test(e.message)){const link=el('a','text-action','Open provider settings');link.href='#providers';msg.append(' ',link);}}
        finally {b.disabled=false;}
      });
      storyBox.append(msg,generate);
    }
  }
  paintStory(story);
  if(profile.links.length){
    const rows=profile.links.map(l=>{
      const match=roster.filter(x=>x.nativeId===l.id);
      const who=match.length===1?match[0].name:match.length?'Shared character identity':'Unidentified resident';
      const values=[['Friendship',l.friendshipStatus||l.friendship],['Romance',l.romanceStatus||l.romance]].filter(([,v])=>v!=null).map(([k,v])=>k+' · '+(typeof v==='number'?Math.round(v):v)).join(' / ');
      return who+(values?' — '+values:'');
    });
    box.append(section('Recorded relationships',itemList(rows)));
  }
  if(profile.outfits.length)box.append(section('Appearance',factsList(profile.outfits.map(x=>[x.part,[x.style,x.color].filter(Boolean).join(' · ')]))));
  const inventory=(raw.components||raw.lastKnownComponents||[]).find(x=>x.type==='InventoryCharacter')?.data;
  if(inventory){
    const names=Array.isArray(inventory.items)?inventory.items.filter(x=>x&&typeof x==='object').map(x=>{
      const n=x.itemName||x.name;return typeof n==='string'&&n? n+(typeof x.amount==='number'?' × '+x.amount:''):null;
    }).filter(Boolean):[];
    box.append(section('Inventory',factsList([['Gold',profile.gold],['Saved item entries',Array.isArray(inventory.items)?inventory.items.length:null]]),names.length?itemList(names):null));
  }
  box.append(attach(el('section','section'),originalSave(raw)));
  return box;
}
const activityNames={social:'Social encounter',work:'Hired work',conversation:'Conversation',promise:'Promise made',fulfillment:'Fulfillment reported'};
function appendRoleplay(node,text,truncated=false) {
  let start=0,open=-1;
  text=String(text??'');
  for(let i=0;i<text.length;i++){
    const ch=text[i];
    if(ch==='\n'||ch==='\r'){open=-1;continue;}
    if(ch!=='*'||text[i-1]==='*'||text[i+1]==='*')continue;
    if(open>=0&&i>open+1&&!/\s/u.test(text[i-1])){
      node.append(text.slice(start,open),el('em','',text.slice(open+1,i)));start=i+1;open=-1;
    }else if(i+1<text.length&&!/\s/u.test(text[i+1])&&!(/\p{Nd}/u.test(text[i-1]||'')&&/\p{Nd}/u.test(text[i+1])))open=i;
  }
  if(truncated&&open>=0)node.append(text.slice(start,open),el('em','',text.slice(open+1)));
  else node.append(text.slice(start));
  return node;
}
function roleplayLine(text,cls=''){return appendRoleplay(el('p',cls),text);}
function entryView(view,e,person,openTurningPoint,path) {
  const entry=el('article','journal-entry');const speaker=e.speakerLabel||(e.speaker==='player'?'Player':person);
  if(view==='daily'||view==='sessions'){
    const daily=view==='daily';
    const label=daily?'Daily activity':e.kind==='group'?'Group conversation':e.knownStart===false?'Recorded private exchange':'Private conversation';
    entry.append(attach(el('div','entry-head'),el('strong','',label),el('span','','Day '+e.day)));
    if(e.participants?.length)entry.append(el('p','hint',e.participants.join(', ')));
    entry.append(daily?el('p','journal-prose',e.body):roleplayLine(e.body,'journal-prose'));
    const detail=el('details','memory-originals'),body=el('div');
    detail.append(el('summary','',daily?'Read original observations':'Read conversation'),body);
    let loaded=false,loading=false;
    detail.ontoggle=async()=>{
      if(!detail.open||loaded||loading)return;
      loading=true;body.replaceChildren();
      try{
        const lines=await api(path+'/history/'+view+'/'+encodeURIComponent(e.id));
        for(const line of lines){
          const row=el('div','transcript-line');
          if(line.speaker)row.append(el('strong','',line.speaker));
          row.append(daily?el('p','',line.text):roleplayLine(line.text));
          if(e.kind==='group'){
            row.append(el('small','source','To '+line.to+' · Present: '+line.audience.join(', ')));
          }
          if(line.questOutcome)row.append(el('p','source',line.questOutcome));
          if(line.nativeHandoff)row.append(el('p','source','Native event · '+line.nativeHandoff));
          for(const promise of line.promises||[]){
            const who=promise.speakerLabel||(promise.speaker==='player'?'Player':person);
            row.append(el('p','source','Promise recorded · '+who+' · '+promise.quote+(promise.proposal?' In reply to “'+promise.proposal+'”':'')));
          }
          if(line.commitmentReview==='unavailable')row.append(el('p','source','Echoes could not verify the model’s promise review for this exchange.'));
          if(daily||line.turningPoint)row.append(button('Mark a turning point',()=>openTurningPoint({day:line.day,sourceId:line.source,sourceType:line.sourceType}),'text-action'));
          body.append(row);
        }
        loaded=true;
      }catch(error){body.textContent=error.message;}finally{loading=false;}
    };
    entry.append(detail);return entry;
  }
  const header=attach(el('div','entry-head'),el('strong','',view==='commitments'?(e.status==='fulfilled'?'Fulfilled':'Open promise'):activityNames[e.kind]||pretty(e.kind)),el('span','','Day '+e.day));
  entry.append(header);
  if(view==='commitments'){
    entry.append(el('p','journal-prose',e.statement),el('p','hint','Promised by '+speaker+' · '+(e.due==null?'No recorded due date':'Due on day '+e.due)));
    if(e.addressedTo)entry.append(el('p','hint','Addressed to '+e.addressedTo));
    if(e.proposal)entry.append(el('p','hint','In reply to “'+e.proposal+'”'));
    if(e.model)entry.append(el('small','source','Identified in conversation by '+e.model+'.'));
    if(e.resolvedDay!=null)entry.append(el('small','source','Reported fulfilled by the player on day '+e.resolvedDay+'.'));
  }else{
    entry.append(e.kind==='address'?roleplayLine(e.description,'journal-prose'):el('p','journal-prose',e.description));
    if(e.speaker)entry.append(el('small','source','Promised by '+speaker));
  }
  if(view==='interactions'&&e.sourceId)
    entry.append(button('Mark a turning point',()=>openTurningPoint(e),'text-action'));
  return entry;
}
function turningPointForm(source,path,after,cancel) {
  const f=el('form','turning-form');const choice=select([
    ['0/-1','More anxious'],['0/1','Calmer'],['1/-1','More aggressive'],['1/1','More genial'],['2/-1','More impulsive'],['2/1','More cautious'],
    ['3/-1','More reserved'],['3/1','More sociable'],['4/-1','More active'],['4/1','More passive']]);
  const reason=el('textarea');reason.maxLength=320;reason.required=true;reason.rows=2;
  const ok=el('button','primary','Record turning point'),status=el('p','hint');
  f.append(el('h4','','Turning point · day '+source.day),field('How did this experience change them?',choice),
    field('What happened?',reason),attach(el('div','actions'),ok,button('Cancel',cancel)),status);
  f.onsubmit=async ev=>{ev.preventDefault();const [axis,direction]=choice.value.split('/').map(Number);ok.disabled=true;
    try{await api(path+'/personality','POST',{id:source.sourceId,sourceType:source.sourceType,axis,direction,reason:reason.value.trim()});await after();}
    catch(e){status.textContent=e.message;}finally{ok.disabled=false;}};
  return f;
}
function growthView(d) {
  const box=el('div');
  box.append(el('p','field-note',d.daysTogether===0?'No recorded conversations with the player.': 'Has spoken with the player on '+d.daysTogether+' distinct '+(d.daysTogether===1?'day.':'days.')));
  const rows=el('dl','profile-facts');
  for(const ax of d.axes)rows.append(attach(el('div'),el('dt','',ax.label),el('dd','',ax.reading+(ax.toward?' · moving toward '+ax.toward:''))));
  box.append(rows);
  if(!d.changes.length)box.append(el('p','journal-empty','No turning points yet. Repeated social choices or an interaction marked as a turning point can shape this character.'));
  else for(const x of d.changes)box.append(attach(el('article','journal-entry'),attach(el('div','entry-head'),el('strong','',x.source),el('span','','Day '+x.day)),
    el('p','journal-prose',x.reason),el('small','source',d.axes[x.axis].label)));
  return box;
}
function characterJournal(person,profile,raw,roster,path,alive) {
  const root=el('div'),tabs=el('div','character-tabs'),panel=el('div','journal-panel');
  tabs.setAttribute('role','tablist');tabs.setAttribute('aria-label',person.name+' character record');panel.setAttribute('role','tabpanel');
  let selected='profile',generation=0;const controls=[];
  async function show(view,focus=false){
    selected=view;const seq=++generation, valid=()=>seq===generation&&alive();
    for(const [i,[key]] of characterViews.entries()){
      const active=key===view;controls[i].setAttribute('aria-selected',String(active));controls[i].tabIndex=active?0:-1;
      if(active){panel.setAttribute('aria-labelledby',controls[i].id);if(focus)controls[i].focus();}
    }
    panel.replaceChildren();
    if(view==='profile'){panel.append(profileView(profile.profile,profile.backstory,raw,roster,path,value=>{profile.backstory=value;}));return;}
    const text=characterViews.find(([k])=>k===view)[1],heading=el('h3','journal-heading',text),body=el('div','journal-scroll');
    body.tabIndex=0;body.setAttribute('role','region');body.setAttribute('aria-label',text+' for '+person.name);
    panel.append(heading,body);
    if(view==='personality'){try{const d=await api(path+'/personality');if(valid())body.append(growthView(d));}catch(e){if(valid())body.append(el('p','error',e.message));}return;}
    const messages={interactions:'Recorded encounters and work will appear here.',sessions:'Conversations will appear here after someone speaks.',daily:'Observed daily activities will appear here.',commitments:'No promises recorded for this character.'};
    let cursor=null,loading=false,more=null;
    async function appendPage(){
      if(loading)return;loading=true;if(more)more.disabled=true;
      try{
        const page=await api(path+'/history/'+view+(cursor?'?before='+encodeURIComponent(cursor):''));if(!valid())return;
        more?.remove();
        if(!page.items.length&&!cursor)body.append(el('p','journal-empty',page.day==null?'The campaign calendar is unavailable.':messages[view]));
        for(const e of page.items){
          const card=entryView(view,e,person.name,source=>{
            const old=card.querySelector('form');if(old){old.querySelector('textarea').focus();return;}
            const f=turningPointForm(source,path,()=>show('personality',true),()=>f.remove());card.append(f);f.querySelector('select').focus();
          },path);
          body.append(card);
        }
        cursor=page.next;more=cursor?button('Load earlier entries',appendPage,'load-older'):null;if(more)body.append(more);
      }catch(e){if(valid())body.append(el('p','error',e.message));}finally{loading=false;if(more)more.disabled=false;}
    }
    await appendPage();
  }
  for(const [i,[id,name]] of characterViews.entries()){
    const b=button(name,()=>show(id));b.setAttribute('role','tab');b.id='char-tab-'+id;b.setAttribute('aria-controls','char-journal-panel');
    b.onkeydown=e=>{let j;if(e.key==='ArrowRight')j=(i+1)%characterViews.length;else if(e.key==='ArrowLeft')j=(i+characterViews.length-1)%characterViews.length;else if(e.key==='Home')j=0;else if(e.key==='End')j=characterViews.length-1;else return;
      e.preventDefault();show(characterViews[j][0],true);};
    controls.push(b);tabs.append(b);
  }
  panel.id='char-journal-panel';root.append(tabs,panel);show(selected);return root;
}
