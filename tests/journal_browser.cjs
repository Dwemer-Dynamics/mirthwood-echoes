const fs=require('node:fs'),path=require('node:path'),os=require('node:os'),{spawn}=require('node:child_process');
const delay=n=>new Promise(r=>setTimeout(r,n)), root=path.resolve(process.argv[2]||path.join(__dirname,'../.work/browser'));fs.mkdirSync(root,{recursive:true});
const voiceOnly=process.argv.includes('--voices'), playedTurn=process.argv.includes('--played-turn'), averil=process.argv.includes('--averil');
const group=process.argv.includes('--group'),knowledge=process.argv.includes('--knowledge');
const base=playedTurn||averil||group||knowledge?'http://127.0.0.1:17864':process.env.ECHOES_BROWSER_BASE||'http://127.0.0.1:17841';
const profile=fs.mkdtempSync(path.join(os.tmpdir(),'mirthwood-echoes-edge-'));
const browser=spawn('C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',['--headless=new','--disable-gpu','--disable-extensions','--disable-background-mode','--disable-background-networking','--no-first-run','--no-default-browser-check','--disable-component-update','--disk-cache-size=2097152','--renderer-process-limit=1','--remote-debugging-port=0','--user-data-dir='+profile,'about:blank'],{windowsHide:true,stdio:'ignore'});
let socket,seq=0,startedBackend=false;const pending=new Map(),errors=[];
async function cmd(method,params={}){
  const id=++seq,reply=new Promise((resolve,reject)=>pending.set(id,{resolve,reject}));
  socket.send(JSON.stringify({id,method,params}));return reply;
}
async function evaluate(expression){
  const r=await cmd('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});
  if(r.exceptionDetails)throw Error(r.exceptionDetails.exception?.description||r.exceptionDetails.text);
  return r.result.value;
}
async function until(expression){for(let i=0;i<210;i++){if(await evaluate(expression))return;await delay(100);}throw Error('Timeout: '+expression);}
async function capture(filename){const {data}=await cmd('Page.captureScreenshot',{format:'png'});fs.writeFileSync(path.join(root,filename),Buffer.from(data,'base64'));}
(async()=>{
  let endpoint;
  for(let i=0;i<120;i++){
    try{const p=fs.readFileSync(path.join(profile,'DevToolsActivePort'),'utf8').split('\n')[0];endpoint=(await (await fetch('http://127.0.0.1:'+p+'/json/list')).json()).find(t=>t.type==='page')?.webSocketDebuggerUrl;if(endpoint)break;}catch{}
    await delay(100);
  }
  if(!endpoint)throw Error('No headless browser endpoint.');
  socket=new WebSocket(endpoint);await new Promise((r,j)=>{socket.onopen=r;socket.onerror=j;});
  socket.onclose=()=>{for(const item of pending.values())item.reject(Error('Test browser closed.'));pending.clear();};
  socket.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);if(p){pending.delete(m.id);if(m.error)p.reject(Error(m.error.message));else p.resolve(m.result);}}else if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails.text);};
  await cmd('Runtime.enable');await cmd('Page.enable');
  await cmd('Emulation.setDeviceMetricsOverride',{width:1280,height:800,deviceScaleFactor:1,mobile:false});
  await cmd('Page.navigate',{url:base+(voiceOnly?'/#voices':'/')});
  if(knowledge){
    const turn=JSON.parse(fs.readFileSync(path.join(root,'probe.json'),'utf8')).result.turns[0];
    const roster=await (await fetch(base+'/api/saves/'+turn.campaign+'/characters')).json();
    const index=roster.findIndex(c=>c.key===turn.actor);
    await until("document.querySelectorAll('.roster button').length==="+roster.length);
    await evaluate("document.querySelectorAll('.roster button')["+index+"].click()");
    await until("document.querySelector('.inspect-header h3')?.textContent==='Averil'");
    await evaluate("document.querySelector('#char-tab-knowledge').click()");
    await until("document.querySelector('.journal-scroll')?.textContent.includes('Plum tart is my favorite food.')");
    await capture('averil-character-knowledge.png');
    await cmd('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
    if(!await evaluate("document.documentElement.scrollWidth<=innerWidth"))throw Error('Character knowledge overflows the journal');
    await capture('averil-character-knowledge-narrow.png');
    if(errors.length)throw Error(errors.join(', '));
    console.log('PASS Averil’s durable source wording in the desktop and narrow journal.');
    await cmd('Browser.close');return;
  }
  if(group){
    const report=JSON.parse(fs.readFileSync(path.join(root,'probe.json'),'utf8')).result;
    const roster=await (await fetch(base+'/api/saves/'+report.campaign+'/characters')).json();
    for(const guest of report.guests){
      const index=roster.findIndex(c=>c.key===guest.actor);
      if(index<0)throw Error('Missing group member '+guest.name);
      await until("document.querySelectorAll('.roster button').length==="+roster.length);
      await evaluate("document.querySelectorAll('.roster button')["+index+"].click()");
      await until("document.querySelector('.inspect-header h3')?.textContent==="+JSON.stringify(guest.name));
      await evaluate("document.querySelector('#char-tab-sessions').click()");
      await until("[...document.querySelectorAll('.journal-entry')].some(e=>e.querySelector('strong').textContent==='Group conversation')");
      await evaluate("[...document.querySelectorAll('.journal-entry')].find(e=>e.querySelector('strong').textContent==='Group conversation').querySelector('summary').click()");
      const lines=report.groupLines.filter(e=>e.line.audience.includes(guest.actor)).map(e=>e.line.words.replace(/\*([^*]+)\*/g,'$1'));
      await until(JSON.stringify(lines)+".every(s=>[...document.querySelectorAll('.transcript-line p')].some(p=>p.textContent===s))");
      if(guest.name==='Averil')await capture('group-journal.png');
    }
    if(errors.length)throw Error(errors.join(', '));
    console.log('PASS the group speakers, delivered words and audience in the character journal.');
    await cmd('Browser.close');return;
  }
  if(playedTurn||averil){
    const game=averil?JSON.parse(fs.readFileSync(path.join(root,'probe.json'),'utf8')).result:
      JSON.parse(fs.readFileSync(path.join(root,'game.json'),'utf8').replace(/^\uFEFF/,''))[0].result;
    const turn=averil?{...game.turns[0],resident:game.resident}:game.trials.find(t=>t.text.startsWith('I will bring flour'));
    const roster=await (await fetch(base+'/api/saves/'+turn.campaign+'/characters')).json();
    const index=roster.findIndex(c=>c.key===turn.actor);
    if(index<0)throw Error('The resident played in Mirthwood is missing from the journal.');
    await until("document.querySelectorAll('.roster button').length==="+roster.length);
    await evaluate("document.querySelectorAll('.roster button')["+index+"].click()");
    await until("document.querySelector('.inspect-header h3')?.textContent==="+JSON.stringify(turn.resident));
    await evaluate("document.querySelector('#char-tab-sessions').click()");
    await until("document.querySelector('.memory-originals summary')");
    await evaluate("document.querySelectorAll('.memory-originals summary').forEach(s=>s.click())");
    if(averil){
      const heard=[...game.turns.map(t=>t.reply),...game.openers.map(t=>t.line)];
      const clean=heard.map(line=>line.replace(/\*([^*]+)\*/g,'$1'));
      await until(JSON.stringify(clean)+".every(line=>[...document.querySelectorAll('.transcript-line p')].some(p=>p.textContent===line))");
      if(await evaluate("[...document.querySelectorAll('.journal-entry')].some(entry=>"+JSON.stringify(clean)+".some(line=>entry.textContent.includes(line))&&entry.textContent.includes('Native event ·'))"))throw Error('A spoken reply also displayed an internal return card');
      const italics=await evaluate("[...document.querySelectorAll('.transcript-line em')].map(e=>e.textContent)");
      for(const line of heard)for(const act of line.matchAll(/\*([^*]+)\*/g))if(!italics.includes(act[1]))throw Error('Missing physical gesture: '+act[1]);
      await capture('averil-journal.png');
      if(errors.length)throw Error(errors.join(', '));
      console.log('PASS Averil’s heard lines and physical gestures in the journal.');
      await cmd('Browser.close');return;
    }
    const spoken=turn.spoken.replaceAll('*','');
    const transcript=JSON.stringify([turn.text,spoken]);
    await until("[...document.querySelectorAll('.journal-entry')].some(entry=>"+transcript+".every(line=>entry.textContent.includes(line)))");
    await capture('played-conversation.png');
    await evaluate("document.querySelector('#char-tab-commitments').click()");
    await until("[...document.querySelectorAll('.journal-entry')].some(entry=>['I will bring flour to the mill tomorrow.','Promised by Player','Open promise'].every(line=>entry.textContent.includes(line)))");
    await capture('played-commitment.png');
    if(errors.length)throw Error(errors.join(', '));
    console.log('PASS the copied-save spoken farewell and attributed promise appear in the selected resident’s journal after a backend restart.');
    await cmd('Browser.close');
    return;
  }
  await until("document.querySelector('#server-controls')?.hidden===false");
  if(!await evaluate("document.querySelector('#server-switch').getAttribute('aria-checked')==='true'")){
    await until("document.querySelector('h2')?.textContent==='Server is off' && document.activeElement?.id==='start-game'");
    await capture('launcher-desktop.png');
    await cmd('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
    if(!await evaluate("document.documentElement.scrollWidth<=innerWidth && ['start-game','exit-echoes'].every(id=>document.getElementById(id).getBoundingClientRect().bottom<innerHeight)"))throw Error('Mobile launcher actions');
    await capture('launcher-mobile.png');
    await cmd('Emulation.setDeviceMetricsOverride',{width:1280,height:800,deviceScaleFactor:1,mobile:false});
    startedBackend=true;await evaluate("document.querySelector('#server-switch').click()");
    console.log('PASS visible primary Start Game selection and Exit on desktop and narrow layouts.');
  }
  if(voiceOnly){
    await until("document.querySelector('.voice-fields')");
    try{await require('./voice_preview.cjs')({cmd,evaluate,until,capture,root,base});}
    finally{await cmd('Browser.close').catch(()=>{});}
    return;
  }
  await until("document.querySelectorAll('.roster button').length>0 && document.querySelector('.inspect-header h3')");
  await evaluate("document.querySelector('input[type=search]').value='Cedric';document.querySelector('input[type=search]').dispatchEvent(new Event('input'))");
  await until("document.querySelector('.roster button strong')?.textContent==='Cedric'");
  await evaluate("document.querySelector('.roster button').click()");
  await until("document.querySelector('.inspect-header h3').textContent==='Cedric' && document.querySelector('.profile-pages')?.textContent.includes('Strongly genial')");
  if(!await evaluate("['Light Ale','Custard','Need for company','Conversation interests','Small Talk'].every(t=>document.querySelector('.profile-pages').textContent.includes(t))"))throw Error('Readable character profile is incomplete');
  if(await evaluate("Array.from(document.querySelectorAll('button')).some(b=>/^Talk to /.test(b.textContent)) || !!document.querySelector('form.talk')"))throw Error('Character roster offers dialogue');
  await capture('cedric-desktop.png');
  const tabs=await evaluate("Array.from(document.querySelectorAll('[role=tab]'),b=>b.textContent)");
  if(tabs.join('|')!=='Profile|Interactions|Conversations|Daily history|Character Knowledge|Promises & Commitments|Dynamic personality') throw Error('Character tabs');
  for(const tab of ['interactions','sessions','daily','knowledge','commitments','personality']){
    await evaluate("document.getElementById('char-tab-"+tab+"').click()");
    await until("document.querySelector('.journal-scroll')?.children.length>0");
    if(await evaluate("getComputedStyle(document.querySelector('.journal-scroll')).overflowY!=='auto'"))throw Error('Journal scroll');
  }
  // Render a populated transcript and compacted memory in a separate, browser-only fixture.
  await evaluate(`(async()=>{
    const originApi=api;
    const turns=Array.from({length:30},(_,i)=>({id:'session'+i,kind:i===1?'group':'private',day:44-i,body:'Resident reply '+i,participants:['Player','Test resident']}));
    const original=[
      {speaker:'Player',text:'*I doff my hat.* Good day. <b>plain</b>',source:'original',sourceType:'turn',day:44},
      {speaker:'Test resident',text:'Resident reply 0. *Nods.* Then *smiles.*',source:'original',sourceType:'turn',day:44,turningPoint:true,
       nativeHandoff:'The resident is done talking.',promises:[{speaker:'npc',quote:'Yes.',proposal:'Will you bring flour tomorrow?'}]}];
    const mount=el('div'),fixture='/__journal_fixture';
    api=(url,...args)=>{
      if(!url.startsWith(fixture))return originApi(url,...args);
      if(url.endsWith('/sessions/session0'))return Promise.resolve(original);
      if(url.endsWith('/sessions/session1'))return Promise.resolve([{speaker:'Averil',text:'I worked at the mill.',to:'Drystan',audience:['Averil','Drystan','Player']}]);
      if(url.endsWith('/knowledge'))return Promise.resolve({items:[{kind:'knowledge',subject:'Player',speakerLabel:'Player',facet:'favorite food',value:'plum tart',quote:'My favorite food is plum tart.',firsthand:true,mode:'private',day:10}]});
      if(url.endsWith('/daily/10'))return Promise.resolve([{day:10,text:'Collected flour at the mill.',sourceType:'event',source:'observation'}]);
      if(url.endsWith('/daily'))return Promise.resolve({items:[{kind:'daily',id:'10',day:10,entries:1,body:'Collected flour.'}]});
      if(url.includes('/commitments'))return Promise.resolve({items:[{kind:'promise',speaker:'npc',statement:'Yes.',proposal:'Will you bring flour tomorrow?',due:11,day:10,model:'Test model'}]});
      return Promise.resolve(url.includes('?before=')?{items:[{id:'old',kind:'private',day:2,body:'Oldest recorded line'}]}:{items:turns,next:'earlier'});
    };
    const resident={key:'demo',name:'Test resident',campaign:'example'};
    const p={name:resident.name,kind:'NPC',axes:[],skills:{positiveTraits:[],negativeTraits:[],strengths:[],weaknesses:[]},liked:[],links:[],outfits:[]};
    mount.append(characterJournal(resident,{profile:p,backstory:null},{},[resident],fixture,()=>true));
    document.body.append(mount);
    const pick=name=>Array.from(mount.querySelectorAll('[role=tab]')).find(x=>x.textContent===name).click();
    const waitFor=async check=>{for(let i=0;i<30;i++){if(check())return;await new Promise(r=>setTimeout(r,15));}throw Error('Fixture journal did not update');};
    try{
      pick('Conversations');await waitFor(()=>mount.querySelector('.load-older'));
      const scroll=mount.querySelector('.journal-scroll');
      const first=scroll.querySelector('.journal-entry');
      first.querySelector('summary').click();await waitFor(()=>first.querySelector('.transcript-line'));
      if([...first.querySelectorAll('.transcript-line em')].map(e=>e.textContent).join('|')!=='I doff my hat.|Nods.|smiles.'||first.querySelector('b')||!first.textContent.includes('<b>plain</b>'))throw Error('Action italics or literal dialogue markup failed');
      if(![...first.querySelectorAll('.source')].some(source=>source.textContent.endsWith(original[1].nativeHandoff)&&!source.querySelector('em')))throw Error('Missing plain historical native event');
      if(scroll.scrollHeight<=scroll.clientHeight)throw Error('Populated transcript cannot scroll');
      mount.querySelector('.load-older').click();await waitFor(()=>scroll.textContent.includes('Oldest recorded line'));
      if(!scroll.textContent.includes('Resident reply 0'))throw Error('Earlier page replaced the current transcript');
      if(!scroll.textContent.includes('Promise recorded · Test resident · Yes.'))throw Error('Missing promise in its transcript');
      pick('Character Knowledge');await waitFor(()=>mount.textContent.includes('My favorite food is plum tart.'));
      if(!mount.textContent.includes('Said by Player')||!mount.textContent.includes('Private conversation'))throw Error('Knowledge evidence lost its speaker or audience');
      pick('Promises & Commitments');await waitFor(()=>mount.textContent.includes('Identified in conversation by Test model.'));
      if(!mount.textContent.includes('Will you bring flour tomorrow?')||mount.querySelector('.journal-entry button'))throw Error('Promise evidence or read-only view failed');
      pick('Daily history');await waitFor(()=>mount.querySelector('.memory-originals summary'));
      mount.querySelector('.memory-originals summary').click();await waitFor(()=>mount.textContent.includes('Collected flour at the mill.'));
      if(!mount.querySelector('.transcript-line button'))throw Error('Daily observation lost its turning point action');
      pick('Conversations');await waitFor(()=>mount.querySelectorAll('.memory-originals summary').length===30);
      mount.querySelectorAll('.memory-originals summary')[1].click();await waitFor(()=>mount.textContent.includes('I worked at the mill.'));
      if(!mount.textContent.includes('To Drystan')||!mount.textContent.includes('Averil, Drystan, Player'))throw Error('Group transcript lost addressee or audience');
    }finally{api=originApi;mount.remove();}
  })()`);
  await evaluate("document.getElementById('char-tab-profile').click();document.querySelector('.original-save summary').click()");
  await until("document.querySelector('.original-save .native details')");
  if(process.argv.includes('--switch-test')){
    await until("document.querySelector('#server-switch').getAttribute('aria-checked')==='true'");
    await evaluate("document.querySelector('#server-switch').click()");
    await until("document.querySelector('h2')?.textContent==='Server is off'");
    await cmd('Page.reload',{ignoreCache:true});
    await until("document.querySelector('#server-switch')?.getAttribute('aria-checked')==='false' && document.querySelector('h2')?.textContent==='Server is off'");
    await evaluate("document.querySelector('#module-install').click()");
    await until("!document.querySelector('#module-install').disabled && document.querySelector('#control-notice').textContent.toLowerCase().includes('server')");
    if(!await evaluate("document.querySelector('#server-switch').getAttribute('aria-checked')==='false'"))throw Error('Module check switched on the backend');
    await evaluate("document.querySelector('#server-switch').click()");
    await until("document.querySelector('#server-switch').getAttribute('aria-checked')==='true' && document.querySelector('.roster button')");
    const externalSwitch=async running=>{
      const response=await fetch(base+'/api/control/server',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({running})});
      if(!response.ok)throw Error('Could not switch the server from the second client.');
    };
    try{
      await externalSwitch(false);
      await until("document.querySelector('#server-switch').getAttribute('aria-checked')==='false' && document.querySelector('h2')?.textContent==='Server is off'");
    }finally{await externalSwitch(true);}
    await until("document.querySelector('#server-switch').getAttribute('aria-checked')==='true' && document.querySelector('.roster button')");
    console.log('PASS an open journal follows another client stopping and restarting Echoes without a page reload.');
  }
  await cmd('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  await until("document.querySelector('.profile-pages')");
  if(!await evaluate("document.documentElement.scrollWidth<=innerWidth"))throw Error('Mobile overflow');
  await capture('character-mobile.png');
  await evaluate("document.querySelector('.inspect-header').scrollIntoView({block:'start'})");
  await capture('character-mobile-profile.png');
  console.log('PASS seven character tabs, Cedric native labels, full native details, scrollable histories and narrow layout.');
  console.log('PASS populated transcript scrolls, a detected promise appears beside its exchange and in read-only Commitments, and compacted memories expand.');
  await evaluate("document.querySelector('[data-tab=options]').click()");
  await until("document.querySelector('#positive-desire-cost')?.labels?.length===1");
  if(!await evaluate("document.documentElement.scrollWidth<=innerWidth && document.querySelector('output[for=positive-desire-cost]').value===document.querySelector('#positive-desire-cost').value+'%'"))throw Error('Narrow social desire slider or displayed percent');
  await cmd('Emulation.setDeviceMetricsOverride',{width:1280,height:800,deviceScaleFactor:1,mobile:false});
  await capture('positive-desire-options.png');
  await evaluate("const slider=document.querySelector('#positive-desire-cost');slider.focus()");
  await cmd('Input.dispatchKeyEvent',{type:'keyDown',key:'End',code:'End',windowsVirtualKeyCode:35});
  await cmd('Input.dispatchKeyEvent',{type:'keyUp',key:'End',code:'End',windowsVirtualKeyCode:35});
  if(!await evaluate("slider.value==='100' && document.querySelector('output[for=positive-desire-cost]').value==='100%'"))throw Error('Slider keyboard or live percent');
  await evaluate(`(async()=>{
    const original=api;let stored;
    try{
      api=(path,method,data)=>path==='/api/options'&&method==='PUT'?(stored=data,Promise.resolve()):original(path,method,data);
      await document.querySelector('main .form-actions button').onclick();
      if(stored?.positiveDesireCostPercent!==100)throw Error('Options form lost the selected desire cost');
    }finally{api=original;}
  })()`);
  console.log('PASS social desire slider fits the narrow layout, responds to the keyboard, displays the cost, and submits the chosen percent.');
  if(process.argv.includes('--switch-test')) console.log('PASS browser stops and starts the real server; reload and module verification work while off.');
  if(process.argv.includes('--windows-codex')){
    await evaluate("document.querySelector('[data-tab=providers]').click()");
    await until("document.querySelector('select option:checked')?.value==='codex_subscription' && [...document.querySelectorAll('select')].some(s=>s.value==='windows')");
    if(!await evaluate("[...document.querySelectorAll('input')].some(i=>i.value==='gpt-6.2-sol')"))throw Error('Missing configured Codex model.');
    console.log('PASS provider page shows the saved Windows Codex sign-in and default model.');
  }
  const diagnostics=await (await fetch(base+'/api/diagnostics')).json();
  await evaluate("document.querySelector('[data-tab=diagnostics]').click()");
  await until("document.querySelector('main h2')?.textContent==='Diagnostics' && document.querySelector('.diagnostics')");
  const readDiagnostics=async()=>{
    const cards=await evaluate("[...document.querySelectorAll('.diagnostics .card')].map(c=>c.innerText)");
    if(!cards[0]?.includes(diagnostics.installation.version)||!cards[2]?.includes(diagnostics.database)
       ||!cards[2]?.includes(String(diagnostics.counts.characters))||!cards[3]?.includes('First readable text')
       ||!cards[3]?.includes('Finished')||!cards[4]?.includes('Run in game'))throw Error('Diagnostics differs from the installed backend');
  };
  await readDiagnostics();
  await evaluate("[...document.querySelectorAll('main button')].find(b=>b.textContent==='Refresh').click()");
  await until("document.querySelector('.diagnostics') && document.querySelector('main').getAttribute('aria-busy')!=='true'");
  await readDiagnostics();
  await capture('diagnostics.png');
  console.log('PASS Diagnostics and Refresh show the installed release, campaign roster, provider timings and game check controls.');
  if(errors.length)throw Error(errors.join(', '));

  try{await cmd('Browser.close');}catch{}
})().catch(e=>{console.error(e.message);process.exitCode=1;}).finally(async()=>{
  if(startedBackend){
    try{const r=await fetch('http://127.0.0.1:17841/api/control/server',{method:'POST',headers:{'Content-Type':'application/json'},body:'{"running":false}'});if(!r.ok)throw Error('Could not restore the backend switch');}catch(e){console.error(e.message);process.exitCode=1;}
  }
  socket?.close();if(browser.exitCode===null){await Promise.race([new Promise(r=>browser.once('exit',r)),delay(2500)]);if(browser.exitCode===null)browser.kill();}
  if(path.dirname(path.resolve(profile))===path.resolve(os.tmpdir())&&path.basename(profile).startsWith('mirthwood-echoes-edge-')){
    try{await fs.promises.rm(profile,{recursive:true,force:true,maxRetries:12,retryDelay:200});}
    catch(e){console.error('Could not clear temporary test browser profile: '+e.message);process.exitCode=1;}
  }
});
