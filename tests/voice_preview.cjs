const assert=require('node:assert/strict'), fs=require('node:fs'), path=require('node:path');

module.exports=async({cmd,evaluate,until,capture,root,base})=>{
  const before=await (await fetch(base+'/api/voices')).json();
  const report={cast:[],pitch:[],automatic:before.automatic};
  for(const voice of before.cast){
    const response=await fetch(base+'/api/voices/'+voice.id+'/sample',{headers:{Range:'bytes=0-43'}});
    const bytes=Buffer.from(await response.arrayBuffer());
    assert.equal(response.status,206);assert.equal(bytes.length,44);
    assert.equal(bytes.toString('ascii',0,4),'RIFF');
    report.cast.push(voice.id);
  }
  assert.equal(new Set(report.cast).size,32);
  report.decoded=await evaluate(`(async()=>{
    const decoder=new OfflineAudioContext(1,1,24000),seconds={};
    for(const id of ${JSON.stringify(report.cast)}){
      const source=await fetch('/api/voices/'+id+'/sample');
      const sound=await decoder.decodeAudioData(await source.arrayBuffer());
      if(sound.numberOfChannels!==1||sound.duration<=1)throw Error('Invalid preview recording for '+id);
      seconds[id]=sound.duration;
    }
    return seconds;
  })()`);
  await evaluate("document.querySelector('[data-tab=voices]').click()");
  await until("document.querySelector('.voice-fields select') && !document.querySelector('.voice-card [role=status]').textContent.startsWith('Checking')");
  await evaluate(`window.vfields=document.querySelectorAll('.voice-fields select');window.scope=vfields[0];window.voice=vfields[1];
    window.slider=document.querySelector('.voice-fields input[type=range]');window.sample=document.querySelector('.voice-card audio');
    window.preview=document.getElementById('voice-preview');window.choose=(e,x)=>{e.value=x;e.dispatchEvent(new Event('change'));};`);
  const saved=await evaluate("scope.value");
  assert.equal(await evaluate("voice.options.length"),33);
  if(saved!=='default' && !await evaluate("voice.value") && before.automatic[saved]){
    assert.equal(await evaluate("sample.src.endsWith('/'+("+JSON.stringify(before.automatic[saved])+")+'/sample')"),true);
  }
  try{
    await evaluate("document.getElementById('voice-enabled').click()");
    await until("document.getElementById('voice-enabled').disabled===false");
    assert.equal((await (await fetch(base+'/api/voices')).json()).settings.enabled,!before.settings.enabled);
    await evaluate("document.getElementById('voice-enabled').click()");
    await until("document.getElementById('voice-enabled').disabled===false");
    report.toggle=true;
  }finally{
    const now=(await (await fetch(base+'/api/voices')).json()).settings.enabled;
    if(now!==before.settings.enabled)assert.ok((await fetch(base+'/api/voices/enabled',{method:'PUT',
      headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled:before.settings.enabled})})).ok);
  }
  await evaluate("choose(voice,'us-m01');slider.focus()");
  for(const [key,code,wanted] of [['Home',36,-2],['End',35,2]]){
    for(const type of ['keyDown','keyUp'])await cmd('Input.dispatchKeyEvent',{type,key,code:key,windowsVirtualKeyCode:code});
    await until("slider.value==='"+wanted+"'");
    const r=await cmd('Runtime.evaluate',{expression:"preview.click()",userGesture:true});
    assert.ok(!r.exceptionDetails);
    await until("!sample.paused && sample.currentTime>0.18");
    const played=await evaluate("({rate:sample.playbackRate,preservesPitch:sample.preservesPitch,position:sample.currentTime,seconds:sample.duration,level:slider.getAttribute('aria-valuetext')})");
    assert.equal(played.preservesPitch,false);assert.ok(Math.abs(played.rate-2**(wanted/12))<.0001);assert.ok(played.seconds>1);
    report.pitch.push(played);
    if(wanted===-2){
      await evaluate("slider.value=2;slider.dispatchEvent(new Event('input'))");
      await until("!sample.paused && sample.playbackRate>1 && sample.currentTime>"+played.position);
      report.pitchChangedWhilePlaying=true;
    }
    await evaluate("preview.click()");await until("sample.paused");
  }
  await capture('voice-tts.png');
  await cmd('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  assert.equal(await evaluate("document.documentElement.scrollWidth<=innerWidth"),true);
  await capture('voice-tts-mobile.png');
  // Exercise the real save controls and restore only this scope, even if an assertion fails.
  const previous=saved==='default'?before.settings.default:before.settings.players[saved];
  try{
    await evaluate("document.getElementById('save-player-voice').click()");
    await until("document.getElementById('notice').textContent==='Player voice saved.'");
    const after=await (await fetch(base+'/api/voices')).json();
    assert.deepEqual(saved==='default'?after.settings.default:after.settings.players[saved],{voice:'us-m01',pitch:2});
    report.saved=true;
  }finally{
    const response=await fetch(base+'/api/voices/player',{method:'PUT',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({scope:saved,voice:previous?.voice||'',pitch:previous?.pitch||0,inherit:!previous&&saved!=='default'})});
    assert.ok(response.ok);
    assert.deepEqual((await (await fetch(base+'/api/voices')).json()).settings,before.settings);
  }
  report.service=await evaluate("document.querySelector('.voice-card [role=status]').textContent");
  if(before.settings.enabled&&(await (await fetch(base+'/api/voices/status')).json()).connected){
    const response=await fetch(base+'/api/voices/speech',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({voice:'us-m01',text:'Hello. *I nod.*'})});
    assert.equal(response.status,200);
    const parts=await response.json();
    assert.deepEqual(parts.map(p=>p.voice),['us-m01','narrator']);
    for(const p of parts){
      assert.equal(Buffer.from(p.audio,'base64').toString('ascii',0,4),'RIFF');assert.ok(p.cues.length>0);
    }
    report.dwemerRoles=parts.map(p=>({voice:p.voice,bytes:Buffer.byteLength(p.audio,'base64'),cues:p.cues.length}));
  }
  fs.writeFileSync(path.join(root,'voice-browser.json'),JSON.stringify(report,null,2));
  console.log('PASS 32 recordings, automatic player preview, pitch during real browser playback, saved selection, restored preferences and narrow layout.');
};
