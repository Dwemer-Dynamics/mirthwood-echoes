'use strict';
const main = document.querySelector('main');
const notice = document.querySelector('#notice');
let pageGeneration = 0, notifyTimer, currentSave = '', currentActor = '';
function tell(text) { notice.textContent = text; clearTimeout(notifyTimer); notifyTimer = setTimeout(() => notice.textContent = '', 5000); }
function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}
function attach(parent, ...children) { parent.append(...children.filter(Boolean)); return parent; }
function heading(title, sub, tools) {
  return attach(el('div', 'topline'), attach(el('div'), el('h2', '', title), sub && el('p', '', sub)), tools);
}
function button(title, action, cls = '') {
  const b = el('button', cls, title); b.type = 'button';
  b.onclick = async () => { try { await action(b); } catch (e) { tell(e.message); } };
  return b;
}
function field(title, input, hint) { return attach(el('label', 'field'), el('span', '', title), input, hint && el('small', '', hint)); }
function input(type, value = '') { const i = el('input'); i.type = type; i.value = value; return i; }
function pasteField(title, control, id, hint) {
  control.id=id;
  const box=el('div','field'), label=el('label','',title);label.htmlFor=id;
  const native=window.echoesProviderClipboard&&window.chrome?.webview;
  async function paste() {
    control.focus();
    const start=control.selectionStart, end=control.selectionEnd;
    let value;
    if(native){
      const ticket=crypto.randomUUID();
      value=await new Promise((resolve,reject)=>{
        const stop=()=>{clearTimeout(timer);native.removeEventListener('message',receive);};
        const receive=e=>{if(e.data?.ticket!==ticket)return;stop();e.data.error?reject(Error(e.data.error)):resolve(e.data.text);};
        const timer=setTimeout(()=>{stop();reject(Error('Clipboard unavailable. Try Paste again.'));},4000);
        native.addEventListener('message',receive);native.postMessage({action:'pasteProviderField',field:id,ticket});
      });
    }else{
      try{value=await navigator.clipboard.readText();}catch{throw Error('Allow clipboard access, or press Ctrl+V in the field.');}
    }
    if(!control.isConnected||document.activeElement!==control||typeof value!=='string')return;
    const room=control.maxLength-(control.value.length-(end-start));
    control.setRangeText(value.replace(/[\r\n]/g,'').slice(0,Math.max(0,room)),start,end,'end');
    control.dispatchEvent(new Event('input',{bubbles:true}));
  }
  control.addEventListener('keydown',e=>{
    if(native&&((e.ctrlKey&&!e.altKey&&!e.shiftKey&&e.key.toLowerCase()==='v')||(e.shiftKey&&!e.ctrlKey&&e.key==='Insert'))){
      e.preventDefault();if(!e.repeat)paste().catch(err=>tell(err.message));
    }
  });
  const b=button('Paste',paste);b.setAttribute('aria-label','Paste '+title.toLowerCase());
  return attach(box,label,attach(el('div','paste-field'),control,b),hint&&el('small','',hint));
}
function select(values, selected) {
  const node = el('select');
  for (const [value, label] of values) { const op = el('option', '', label); op.value = value; node.append(op); }
  if (selected != null) node.value = selected;
  return node;
}
async function api(path, method = 'GET', data) {
  const r = await fetch(path, {method, headers: data === undefined ? {} : {'Content-Type':'application/json'}, body: data === undefined ? undefined : JSON.stringify(data)});
  if (!r.ok) { const e = await r.json().catch(() => ({})); throw Error(e.error || 'Request failed (' + r.status + ').'); }
  return r.status === 204 ? null : r.json();
}
function section(title, ...children) { return attach(el('section','section'),title && el('h3','',title),...children); }
function row(label, val) { return attach(el('div', 'stat-line'), el('span','',label), el('strong','', String(val ?? '—'))); }
function actions(save) { return attach(el('div','form-actions'), button('Save changes', save, 'primary')); }
function apply(node, generation) { if (generation === pageGeneration) { main.replaceChildren(...node); main.setAttribute('aria-busy','false'); } }

async function optionsPage(g) {
  const o = await api('/api/options');
  const tokens = input('number',o.replyTokens); tokens.min = 32; tokens.max = 256;
  const days = input('number',o.dailyDays); days.min = 1; days.max = 7;
  const cost=input('range');cost.id='positive-desire-cost';cost.min=10;cost.max=100;cost.step=1;cost.value=o.positiveDesireCostPercent??25;
  const value=el('output');value.setAttribute('for',cost.id);
  function showCost(){value.textContent=cost.value+'%';cost.setAttribute('aria-valuetext',cost.value+' percent of normal desire loss');}
  cost.addEventListener('input',showCost);showCost();
  const costField=field('Minimum desire loss in a positive conversation',attach(el('div','desire-range'),cost,value),
    'Positive reactions gradually reduce desire loss toward this share of normal. Choose 100% for Mirthwood’s usual cost.');
  costField.htmlFor=cost.id;
  const panel = attach(el('div','card'), section('Everyday conversation',attach(el('div','pair'),
    field('Spoken reply target',tokens,'32–256 tokens. The model has a separate allowance to identify promises.'),
    field('Daily activity window',days,'1–7 in-game days, including today.')),costField),
    actions(async () => { const latest = await api('/api/options'); await api('/api/options','PUT',{...latest,replyTokens: Number(tokens.value),dailyDays:Number(days.value),positiveDesireCostPercent:Number(cost.value)}); tell('Conversation options saved.'); }));
  const note = attach(el('aside','card'),el('h3','','Commitments'),el('p','','An open promise stays with its participants after ordinary daily activity fades from recall.'));
  apply([heading('Options'),attach(el('div','form-layout'),panel,note)],g);
}
async function providersPage(g) {
  const [o,all] = await Promise.all([api('/api/options'),api('/api/providers')]);
  const picker = select(all.map(p => [p.id,p.name]),o.provider);
  const form = el('div','card'), aside=el('aside','card'); let selection=0;
  async function show() {
    const id=picker.value, seq=++selection;
    const p=await api('/api/providers/'+id);
    if(seq!==selection)return;
    const d=input('text',p.defaultModel),chat=input('text',p.conversation),memory=input('text',p.memory);
    for (const i of [d,chat,memory]) { i.maxLength=200; i.autocomplete='off'; }
    const endpoint=input('url',p.endpoint), key=input('password'), clear=input('checkbox');
    key.autocomplete='new-password'; key.maxLength=8192; key.placeholder=p.hasKey?'Saved credential':'Enter API key';
    const identity=attach(el('div','stack'),field('Provider',picker));
    const codexSignIn=select([['service','Echoes service account'],['windows','Windows Codex']],p.codexHost||'service');
    const effort=select([['','Model default'],...['low','medium','high','xhigh','max','ultra'].map(x=>[x,x==='xhigh'?'Extra high':x[0].toUpperCase()+x.slice(1)])],p.conversationEffort||'');
    if(id==='openai_compatible')identity.append(field('Chat completions endpoint',endpoint,'Use a full HTTPS URL or a localhost HTTP endpoint.'));
    if(id!=='codex_subscription'){
      identity.append(pasteField('API key',key,'provider-key'));
      if(p.hasKey)identity.append(attach(el('label','hint'),clear,document.createTextNode(' Remove saved key')));
    }else identity.append(field('Codex sign-in',codexSignIn,'Choose the account that will generate replies.'));
    function showEffective(){
      chat.placeholder=memory.placeholder=d.value||'Uses default model';
      aside.replaceChildren(el('h3','','Effective models'),row('Conversation',chat.value.trim()||d.value.trim()||'Choose a model'),
        row('Memory',memory.value.trim()||d.value.trim()||'Choose a model'),el('p','hint','Each provider keeps its own model settings.'));
    }
    for(const control of [d,chat,memory])control.oninput=showEffective;
    showEffective();
    form.replaceChildren(section('Provider connection',identity),section('Models',attach(el('div','stack'),
      pasteField('Default model',d,'default-model','Used for every purpose unless you enter an override.'),
      attach(el('div','pair'),field('Conversation override',chat),field('Memory override',memory)),
      id==='codex_subscription'&&field('Conversation reasoning',effort))),
      actions(async b=>{
        b.disabled=true;
        try{
          await api('/api/providers/'+id,'PUT',{defaultModel:d.value,conversation:chat.value,memory:memory.value,endpoint:endpoint.value,apiKey:key.value,clearKey:clear.checked,...(id==='codex_subscription'?{codexHost:codexSignIn.value,conversationEffort:effort.value}:{})});
          const latest=await api('/api/options');await api('/api/options','PUT',{...latest,provider:id});
          tell('Provider and models saved.');await show();
        }finally{b.disabled=false;}
      }));
  }
  picker.onchange=()=>show().catch(e=>tell(e.message));
  await show();
  apply([heading('LLM Provider / Models'),attach(el('div','form-layout'),form,aside)],g);
}
function nativeTree(label,v) {
  if(v===null || typeof v!=='object')return attach(el('div','prop-value'),el('span','',label),el('span','',v===null?'null':v===''?'(empty string)':String(v)));
  const entries=Object.entries(v), node=el('details');
  node.append(el('summary','',label+' · '+entries.length));
  node.addEventListener('toggle',()=>{if(node.open && node.childElementCount===1){
    if(!entries.length)node.append(el('div','subtle',Array.isArray(v)?'Empty list':'Empty object'));
    else for(const [key,value] of entries)node.append(nativeTree(key,value));
  }});
  return node;
}
async function charactersPage(g,target) {
  const saves=await api('/api/saves');
  const refresh=button('Refresh',async b=>{b.disabled=true;await api('/api/saves/refresh','POST',{});await route();});
  const tools=attach(el('div','tools'),refresh);
  if(!saves.length){apply([heading('Characters','',tools),el('div','card empty','No saved characters found. Save in Mirthwood, then refresh.')],g);return;}
  let resolved=null;
  if(target){try{resolved=await api('/api/module/resolve','POST',JSON.parse(target));currentSave=resolved.campaign;currentActor=resolved.actor;}catch(e){tell(e.message);}}
  if(!saves.some(s=>s.id===currentSave))currentSave=saves[0].id;
  const save=currentSave,picker=select(saves.map(s=>[s.id,'Slot '+s.slot+' · '+s.player]),save);picker.setAttribute('aria-label','Save slot');
  picker.onchange=()=>{currentSave=picker.value;currentActor='';location.hash='characters';route();};tools.prepend(picker);
  const roster=await api('/api/saves/'+save+'/characters');
  const search=input('search');search.placeholder='Find a character…';search.setAttribute('aria-label','Find a character');
  const list=el('div','roster'), detail=el('article','card'), count=el('span','count',roster.length+' entries');
  const left=attach(el('aside','card'),attach(el('div','roster-head'),attach(el('h3','','Residents'),count),search),list);
  let selected=roster.find(c=>c.key===currentActor)||roster.find(c=>c.kind==='NPC')||roster[0],selection=0;
  function paintList(){
    list.replaceChildren();const q=search.value.trim().toLowerCase();
    const matches=roster.filter(c=>[c.name,c.town,c.job,c.kind].join(' ').toLowerCase().includes(q));
    count.textContent=matches.length+(matches.length===1?' entry':' entries');
    for(const c of matches){
      const b=button('',()=>inspect(c));b.classList.toggle('selected',selected?.key===c.key);
      b.setAttribute('aria-pressed',String(selected?.key===c.key));
      const sub=[c.kind==='NPC'?null:c.kind,c.job==='None'?null:c.job,c.town].filter(Boolean).join(' · ');
      b.append(el('span','initial',c.name?.[0]||'·'),attach(el('span'),el('strong','',c.name||'Unnamed'),el('small','',sub||'NPC')));list.append(b);
    }
    if(!matches.length)list.append(el('div','empty','No matching characters.'));
  }
  async function inspect(c){
    if(!c){detail.replaceChildren(el('div','empty','Select a character.'));return;}
    selected=c;currentActor=c.key;paintList();const seq=++selection;
    const alive=()=>seq===selection&&g===pageGeneration;
    const path='/api/saves/'+save+'/characters/'+c.key;
    detail.setAttribute('aria-busy','true');
    try{
      const [raw,profile]=await Promise.all([api(path),api(path+'/profile')]);if(!alive())return;
      const identity=profile.profile;
      const header=attach(el('div','inspect-header'),attach(el('div'),el('h3','',c.name),
        el('p','',[c.kind==='NPC'?'Resident':c.kind,identity.job,identity.town].filter(Boolean).join(' · '))));
      detail.replaceChildren(header,characterJournal({...c,campaign:save},profile,raw,roster,path,alive));
    }catch(e){if(alive())detail.replaceChildren(el('p','empty error',e.message));}
    finally{if(alive())detail.setAttribute('aria-busy','false');}
  }
  search.oninput=paintList;
  apply([heading('Characters','',tools),attach(el('div','split'),left,detail)],g);await inspect(selected);
}
async function diagnosticsPage(g) {
  const d=await api('/api/diagnostics'), m=d.installation, b=d.game;
  const label=s=>s.replaceAll('_',' ').replace(/^./,ch=>ch.toUpperCase());
  const group=(title,...items)=>attach(el('section','card'),el('h3','',title),...items);
  const active=b && Date.now()-Date.parse(b.seen)<35000;
  const probes=el('section','card'),mode=select([['runtime','Loaded plugin and hooks'],['interface','In-game interface and reply stream'],['world','Loaded character and save']],'runtime');
  const progress=el('p','hint'),report=el('div');
  probes.append(el('h3','','In-game checks'),field('Check',mode),button('Run in game',async trigger=>{
    trigger.disabled=true;report.replaceChildren();progress.textContent='Waiting for Mirthwood…';
    try{
      let task=await api('/api/diagnostics/probes','POST',{mode:mode.value});
      while(g===pageGeneration&&['waiting','running'].includes(task.state)){
        await new Promise(resolve=>setTimeout(resolve,1200));if(g!==pageGeneration)return;
        task=await api('/api/diagnostics/probes/'+task.id);
      }
      progress.textContent=({passed:'Passed',failed:'Failed',expired:'Expired before the game completed the check.'})[task.state]||task.state;
      for(const c of task.result?.checks||[])report.append(row(c.name,(c.passed?'Passed':'Failed')+' · '+c.detail));
    }catch(e){progress.textContent=e.message;}finally{trigger.disabled=false;}
  }),progress,report);
  apply([heading('Diagnostics','',button('Refresh',route)),attach(el('div','diagnostics'),
    group('Game module',row('Installation',label(m.status)),row('Bundled release',m.version),row('Last check',new Date(m.checkedUtc).toLocaleTimeString()),el('p','hint',m.detail)),
    group('Live game',row('Connection',active?'In contact':b?'Last session ended':'Awaiting game'),row('Loaded release',b?.version),row('Observation hooks',b?.hooks),row('Dropped events',b?.dropped),row('Last heartbeat',b?new Date(b.seen).toLocaleTimeString():null)),
    group('Campaign records',row('Database',d.database),row('Characters',d.counts.characters),row('Daily observations',d.counts.observations),row('Open commitments',d.counts.commitments),el('p','hint',d.saveScan)),
    group('Conversation',row('Completed turns',d.counts.replies),row('Last turn',d.lastReply?.state),row('First readable text',d.lastReply?.firstMs==null?null:d.lastReply.firstMs+' ms'),row('Finished',d.lastReply?.totalMs==null?null:d.lastReply.totalMs+' ms')),probes)],g);
}
const switches=document.querySelector('#server-controls');
const playButton=document.querySelector('#start-game'),exitButton=document.querySelector('#exit-echoes'),launchNotice=document.querySelector('#launch-notice');
let starting=false,exiting=false,gameState='idle',serverOn=false,gamePoll;
let release=null,lastSessionPoll=0,polling=false;
function showGame(state){
  if(state)gameState=state.state;
  playButton.disabled=exiting||starting||gameState==='starting'||(gameState==='running'&&serverOn);
  playButton.textContent=starting||gameState==='starting'?'Starting game…':gameState==='running'?(serverOn?'Game running':'Connect to game'):'Start Game';
  if(state?.error)launchNotice.textContent=state.error;
}
async function pollGame(onFocus=false){
  if(exiting||starting||switching||polling)return;
  polling=true;
  try{
    const session=onFocus||Date.now()-lastSessionPoll>=10000;
    const state=await api('/api/control/game'+(session?'?session':''));
    if(exiting||starting||switching)return;
    if(session){
      lastSessionPoll=Date.now();
      if(typeof state.running==='boolean'&&(state.running!==serverOn||state.release!==release)){
        await route();return;
      }
    }
    showGame(state);
  }catch{}
  finally{polling=false;}
}
playButton.onclick=async()=>{
  if(playButton.disabled)return;
  starting=true;launchNotice.textContent='Checking the game module…';showGame();
  try{
    const state=await api('/api/control/game','POST',{});
    launchNotice.textContent='';showGame(state);await route();
  }catch(e){launchNotice.textContent=e.message;await updateControls();}
  finally{starting=false;showGame();}
};
exitButton.onclick=async()=>{
  if(exiting)return;
  exiting=true;showGame();exitButton.disabled=true;launchNotice.textContent='Closing Echoes…';
  try{
    await api('/api/control/exit','POST',{});
    clearInterval(gamePoll);switches.hidden=true;main.replaceChildren(heading('Echoes is closed'));
    launchNotice.textContent='Open the desktop shortcut to return.';
  }catch(e){exiting=false;exitButton.disabled=false;showGame();launchNotice.textContent=e.message;}
};
gamePoll=setInterval(pollGame,2500);
window.addEventListener('focus',()=>pollGame(true));
let switching=false;
async function updateControls(){
  let state;
  try{state=await api('/api/control');}catch{switches.hidden=true;return null;}
  switches.hidden=false;
  serverOn=state.running;release=state.release;showGame(state.game);
  const toggle=document.querySelector('#server-switch'), check=document.querySelector('#module-install');
  toggle.setAttribute('aria-checked',String(state.running));
  document.querySelector('#switch-state').textContent=state.running?'On':'Off';
  const labels={current:'Check module',update:'Update module',missing:'Install module',game_missing:'Check installation'};
  check.textContent=labels[state.module.state]||'Check module';
  document.querySelector('#module-state').textContent=({current:'Module '+state.module.version+' installed',update:'Module update available',missing:'Module not installed',game_missing:'Game unavailable'})[state.module.state]||'Module status unknown';
  return state;
}
async function controlAction(action){
  if(switching)return;switching=true;
  const buttons=[...switches.querySelectorAll('button')],status=document.querySelector('#control-notice');
  buttons.forEach(b=>b.disabled=true);
  try{
    if(action==='switch'){
      const desired=document.querySelector('#server-switch').getAttribute('aria-checked')!=='true';
      status.textContent=desired?'Starting Echoes…':'Stopping Echoes…';
      await api('/api/control/server','POST',{running:desired});status.textContent='';await route();
    }else{
      status.textContent='Checking the Steam installation…';
      const result=await api('/api/control/module','POST',{});status.textContent=result.detail;
      await updateControls();
    }
  }catch(e){status.textContent=e.message;await updateControls();}
  finally{buttons.forEach(b=>b.disabled=false);switching=false;}
}
document.querySelector('#server-switch').onclick=()=>controlAction('switch');
document.querySelector('#module-install').onclick=()=>controlAction('module');
let voicePreview;
async function voicePage(g){
  const [data,saves]=await Promise.all([api('/api/voices'),api('/api/saves')]);
  if(g!==pageGeneration)return;
  const {cast,settings}=data;
  const panel=el('section','panel voice-card');
  const service=el('p','hint','Checking Dwemer Pocket TTS…');
  service.setAttribute('role','status');
  const enabled=el('input');enabled.type='checkbox';enabled.id='voice-enabled';enabled.checked=settings.enabled;
  const onLabel=el('label','voice-enable');onLabel.htmlFor=enabled.id;onLabel.append(enabled,document.createTextNode('Enable voice playback'));
  enabled.onchange=async()=>{
    const checked=enabled.checked;enabled.disabled=true;
    try{await api('/api/voices/enabled','PUT',{enabled:checked});settings.enabled=checked;tell(checked?'Voice playback is on.':'Voice playback is off.');}
    catch(e){enabled.checked=!checked;tell(e.message);}finally{enabled.disabled=false;}
  };
  const scope=select([['default','Default for player characters'],...saves.map(s=>[s.id,s.player+' · Save '+s.slot])],saves.find(s=>s.id===currentSave)?.id||saves[0]?.id||'default');
  const choices=select([['','Automatic from the matching cast'],...cast.map(v=>[v.id,v.name+' · '+v.accent+' · '+v.sex])],'');
  const pitch=el('input');pitch.id='voice-pitch';pitch.type='range';pitch.min='-2';pitch.max='2';pitch.step='1';pitch.setAttribute('aria-label','Voice pitch');
  const level=el('output','pitch-value'),direction=el('p','hint'),sample=el('audio');
  level.setAttribute('for',pitch.id);
  sample.preload='none';voicePreview=sample;
  const pitchLabels=['Lowest · −2 semitones','Lower · −1 semitone','Natural pitch','Higher · +1 semitone','Highest · +2 semitones'];
  function setPitch(){const semitones=Number(pitch.value);level.textContent=pitchLabels[semitones+2];pitch.setAttribute('aria-valuetext',level.textContent);
    sample.preservesPitch=false;sample.playbackRate=Math.pow(2,semitones/12);}
  function buttonState(){preview.textContent=sample.paused?'Play sample':'Stop sample';}
  const preview=button('Play sample',async()=>{
    if(!sample.paused){sample.pause();sample.currentTime=0;return;}
    sample.currentTime=0;setPitch();
    try{await sample.play();}catch(e){if(e.name!=='AbortError')tell('This recording could not be played.');}
  });
  preview.id='voice-preview';
  sample.onplay=sample.onpause=sample.onended=buttonState;
  function showVoice(){
    sample.pause();const member=cast.find(v=>v.id===(choices.value||data.automatic[scope.value]));
    if(member)sample.src='/api/voices/'+encodeURIComponent(member.id)+'/sample';else sample.removeAttribute('src');
    sample.load();preview.disabled=!member;
    direction.textContent=member?(choices.value?'':member.name+' · ')+member.direction:'Choose a cast voice to hear a sample, or select a saved character to hear their automatic voice.';
    setPitch();
  }
  function fromScope(){const value=settings.players[scope.value]||settings.default;choices.value=value.voice||'';pitch.value=String(value.pitch);showVoice();inherit.hidden=scope.value==='default'||!settings.players[scope.value];}
  let saving=false;
  async function save(inherited){
    if(saving)return;
    saving=true;saveButton.disabled=inherit.disabled=true;
    const key=scope.value,player={voice:choices.value||null,pitch:Number(pitch.value)};
    try{
      await api('/api/voices/player','PUT',{scope:key,voice:player.voice||'',pitch:player.pitch,inherit:inherited});
      if(key==='default')settings.default=player;else if(inherited)delete settings.players[key];else settings.players[key]=player;
      if(scope.value===key&&(choices.value||null)===player.voice&&Number(pitch.value)===player.pitch)fromScope();
      else inherit.hidden=scope.value==='default'||!settings.players[scope.value];
      tell('Player voice saved.');
    }finally{saving=false;saveButton.disabled=inherit.disabled=false;}
  }
  const inherit=button('Use default',()=>save(true),'subtle');
  scope.onchange=fromScope;choices.onchange=showVoice;pitch.oninput=setPitch;
  const saveButton=button('Save player voice',()=>save(false));saveButton.id='save-player-voice';
  const pickers=attach(el('div','voice-pickers'),field('Player character',scope),field('Voice',choices));
  const tuning=attach(el('div','voice-tuning'),field('Pitch',pitch),preview,level);
  const controls=attach(el('div','voice-fields'),pickers,direction,tuning,attach(el('div','voice-actions'),saveButton,inherit));
  panel.append(onLabel,service,controls,sample);
  fromScope();apply([heading('Voice TTS'),panel],g);
  let polling=false;
  async function status(){
    if(polling||g!==pageGeneration)return;
    polling=true;
    try{
      const state=await api('/api/voices/status');
      service.textContent=state.connected?'Dwemer Pocket TTS is ready on port 8086.':'Dwemer Pocket TTS is offline. You can still play the voice samples.';
    }catch{service.textContent='Could not check Dwemer Pocket TTS. You can still play the voice samples.';}
    finally{polling=false;if(g===pageGeneration)setTimeout(status,12000);}
  }
  status();
}
async function route(){
  if(voicePreview){voicePreview.pause();voicePreview.removeAttribute('src');voicePreview.load();voicePreview=null;}
  const g=++pageGeneration,[hash,query]=location.hash.slice(1).split('?');
  const name=['options','providers','voices','diagnostics','characters'].includes(hash)?hash:'characters';
  for(const a of document.querySelectorAll('nav a')){if(a.dataset.tab===name)a.setAttribute('aria-current','page');else a.removeAttribute('aria-current');}
  main.setAttribute('aria-busy','true');
  try{
    const control=await updateControls();if(g!==pageGeneration)return;
    if(control && !control.running){apply([heading('Server is off'),el('p','hint','Start Game turns on Echoes and opens Mirthwood. To browse your journal before playing, switch the server on above.')],g);return;}
    if(name==='characters')await charactersPage(g,new URLSearchParams(query).get('target'));
    else await ({options:optionsPage,providers:providersPage,voices:voicePage,diagnostics:diagnosticsPage})[name](g);
  }catch(e){
    apply([heading('Mirthwood: Echoes is unavailable'),el('p','error',e.message),button('Retry',route)],g);
  }
}
window.addEventListener('hashchange',route);route();
