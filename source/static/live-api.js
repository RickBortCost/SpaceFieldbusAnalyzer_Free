/* Live Acquisition API mode: monitor of the requests that arrive from external systems and built-in end-to-end test. */
(() => {
  'use strict';
  const $=id=>document.getElementById(id),t=s=>window.I18N?I18N.t(s):s;
  const token=()=>document.querySelector('meta[name="opcua-token"]').content;
  const TYPES=['REAL','LREAL','BOOL','BYTE','WORD','DWORD','INT','DINT','UINT','UDINT','SINT','USINT','LINT','ULINT','LWORD','TIME'];
  const state={status:null,settingsLoaded:false,open:new Set(),test:null,clearedAt:null,polling:true,timer:null,testTimer:null,testData:null};

  async function request(url,body){
    const options=body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json','X-OPCUA-Token':token()},body:JSON.stringify(body)};
    const response=await fetch(url,options);const result=await response.json().catch(()=>({}));
    if(!response.ok||result.error)throw Error(result.error||response.statusText);return result;
  }
  let noticeTimer=null;
  function notice(text,error=false){const m=$('msg');m.textContent=text;m.className='message'+(error?' error':'');m.hidden=false;clearTimeout(noticeTimer);noticeTimer=setTimeout(()=>m.hidden=true,error?15000:5000);}
  function cell(text,cls){const td=document.createElement('td');td.textContent=text===null||text===undefined||text===''?'—':String(text);if(cls)td.className=cls;return td;}
  function time(iso){if(!iso)return '—';const d=new Date(iso);return Number.isNaN(d.getTime())?iso:d.toLocaleTimeString(window.I18N?I18N.locale():undefined,{hour12:false})+'.'+String(d.getMilliseconds()).padStart(3,'0');}
  function number(v){return v===null||v===undefined?'—':Number(v).toLocaleString(window.I18N?I18N.locale():undefined);}
  function value(v,unit){if(v===null||v===undefined)return '—';const text=typeof v==='number'?v.toLocaleString(window.I18N?I18N.locale():undefined,{maximumFractionDigits:6}):String(v);return unit?text+' '+unit:text;}
  function badge(text,kind){const s=document.createElement('span');s.className='api-badge '+kind;s.textContent=text;return s;}
  const statusKind=s=>s==='streaming'?'ok':s==='connecting'||s==='starting'?'wait':s==='stopped'||s==='expired'?'off':'bad';

  function renderListener(listener){
    const s=$('api-listener-status');
    if(listener.listening){s.textContent=t('In ascolto')+' · '+listener.address+':'+listener.port;s.className='status ok';}
    else{s.textContent=t('Non in ascolto');s.className='status bad';}
    const urls=$('api-urls');urls.replaceChildren(...(listener.urls||[]).map(u=>{const li=document.createElement('li');li.textContent=u;return li;}));
    $('api-listener-error').hidden=!listener.error;$('api-listener-error').textContent=listener.error||'';
  }

  function renderSettings(s,force){
    if(state.settingsLoaded&&!force)return;state.settingsLoaded=true;
    $('api-key').value=s.api_key||'';
    const map={'set-port':'port','set-bind':'bind','set-max-sessions':'max_sessions','set-max-signals':'max_signals','set-min-sampling':'min_sampling_ms','set-max-ttl':'max_ttl_seconds',
      'set-reconnect-delay':'reconnect_delay_ms','set-reconnect-attempts':'reconnect_max_attempts','set-reconnect-timeout':'reconnect_timeout_seconds','set-batch':'batch_ms'};
    for(const [id,key] of Object.entries(map))$(id).value=s[key];
    $('set-targets').value=(s.allowed_stream_targets||[]).join('\n');
    $('set-aliases').value=Object.entries(s.source_aliases||{}).map(([k,v])=>k+'='+v).join('\n');
    $('set-require-key').checked=!!s.require_key;
  }

  function renderSources(status){
    const configured=status.configured_sources||[],aliases=status.settings?.source_aliases||{};
    $('api-configured').textContent=configured.length?t('Sorgenti della connessione salvata in SFA')+': '+configured.map(p=>p+(p==='s7'?' ('+status.s7_mode+')':'')).join(', ')
      +(Object.keys(aliases).length?' · '+t('alias')+': '+Object.entries(aliases).map(([k,v])=>k+' → '+v).join(', '):'')
      :t('Nessuna sorgente configurata: aprire SFA in modalità completa, configurare la connessione e connettersi una volta per salvarla.');
    const select=$('test-source'),current=select.value,options=[...Object.keys(aliases),...configured];
    if(select.options.length!==options.length||[...select.options].some((o,i)=>o.value!==options[i])){
      select.replaceChildren(...options.map(o=>new Option(aliases[o]?o+' ('+aliases[o]+')':o,o)));if(options.includes(current))select.value=current;
    }
    const open=Object.entries(status.sources||{});
    $('api-sources').textContent=open.length?open.map(([p,s])=>p+' '+t(s.state)).join(' · '):'—';
  }

  function renderSessions(sessions){
    const rows=$('api-session-rows');rows.replaceChildren();$('api-no-sessions').hidden=sessions.length>0;
    for(const s of sessions){
      const tr=document.createElement('tr');
      tr.append(cell(s.sessionId,'mono'),cell(s.requestId,'mono'),cell(s.client),cell(s.sourceId+(s.sourceId!==s.protocol?' ('+s.protocol+')':'')),cell((s.signals||[]).length),
        cell(s.requestedSamplingPeriodMs===s.effectiveSamplingPeriodMs?s.effectiveSamplingPeriodMs+' ms':s.requestedSamplingPeriodMs+' → '+s.effectiveSamplingPeriodMs+' ms'));
      const st=document.createElement('td');st.append(badge(t(s.status),statusKind(s.status)));tr.append(st);
      const ws=document.createElement('td');ws.append(badge(s.streamConnected?t('connesso'):t('non connesso'),s.streamConnected?'ok':'wait'));tr.append(ws);
      tr.append(cell(number(s.sequence)),cell(number(s.samplesSent)),cell(time(s.expiresAt)));
      const actions=document.createElement('td'),details=document.createElement('button'),stop=document.createElement('button');
      details.type=stop.type='button';details.className=stop.className='ghost compact';
      details.textContent=state.open.has(s.sessionId)?t('Nascondi'):t('Dettagli');details.onclick=()=>{state.open.has(s.sessionId)?state.open.delete(s.sessionId):state.open.add(s.sessionId);render();};
      stop.textContent=t('Termina');stop.onclick=()=>stopSession(s.sessionId);
      actions.append(details,stop);tr.append(actions);rows.append(tr);
    }
    const box=$('api-session-details');box.replaceChildren();
    for(const s of sessions.filter(s=>state.open.has(s.sessionId))){
      const card=document.createElement('div');card.className='api-detail';
      const h=document.createElement('h3');h.textContent=s.sessionId+' · '+s.requestId;
      const target=document.createElement('p');target.className='small mono break';target.textContent=t('Destinazione')+': '+s.streamTarget+(s.lastError?' · '+t('Ultimo errore')+': '+s.lastError:'')+(s.dropped?' · '+t('Campioni scartati')+': '+s.dropped:'')+(s.reconnects?' · '+t('Riconnessioni')+': '+s.reconnects:'');
      const table=document.createElement('table');table.className='api-table';
      const head=document.createElement('tr');for(const h2 of ['signalId','address','dataType',t('Ultimo valore'),t('Qualità'),t('Ora'),t('Inviati')]){const th=document.createElement('th');th.textContent=h2;head.append(th);}
      const thead=document.createElement('thead');thead.append(head);const tbody=document.createElement('tbody');
      for(const g of s.signals||[]){const tr=document.createElement('tr');tr.append(cell(g.signalId,'mono'),cell(g.address,'mono'),cell(g.dataType),cell(value(g.lastValue,g.unit)),cell(g.inError?t('errore'):g.lastQuality,g.inError||g.lastQuality==='bad'?'api-bad':''),cell(time(g.lastTimestamp)),cell(number(g.sent)));tbody.append(tr);}
      table.append(thead,tbody);
      const log=document.createElement('pre');log.className='logs-content api-log';log.textContent=(s.log||[]).join('\n');
      card.append(h,target,table,log);box.append(card);
    }
  }

  function renderRequests(list){
    const rows=$('api-request-rows');rows.replaceChildren();$('api-no-requests').hidden=list.length>0;
    for(const r of list.slice(0,100)){
      const tr=document.createElement('tr');
      tr.append(cell(time(r.at)),cell(r.client),cell(r.method),cell(r.path,'mono'),cell(r.status,r.status>=400?'api-bad':''),cell(r.ms),cell(r.code||(r.status<400?'ok':'')),cell(r.requestId,'mono'));
      rows.append(tr);
    }
  }

  function renderEnded(list){
    const rows=$('api-ended-rows');rows.replaceChildren();
    for(const s of list){const tr=document.createElement('tr');tr.append(cell(s.sessionId,'mono'),cell(s.requestId,'mono'),cell(s.client),cell(time(s.endedAt)),cell(t(s.endReason)),cell(number(s.samplesSent)),cell(number(s.reconnects)),cell(s.lastError));rows.append(tr);}
  }

  function render(){
    const s=state.status;if(!s)return;
    renderListener(s.listener);renderSettings(s.settings,false);renderSources(s);renderSessions(s.sessions||[]);renderRequests(s.requests||[]);renderEnded(s.ended||[]);
    $('api-active').textContent=(s.sessions||[]).length;$('api-requests').textContent=number(s.total_requests);$('api-created').textContent=number(s.total_sessions);
    $('api-memory').textContent=s.memory?t({ok:'normale',low:'bassa',critical:'quasi esaurita'}[s.memory.level]||s.memory.level)+' · '+number(s.memory.available_mb)+' MB':'—';
    $('api-updated').textContent=t('Aggiornato alle')+' '+new Date().toLocaleTimeString(window.I18N?I18N.locale():undefined,{hour12:false});
    renderTest();
  }

  async function refresh(){
    try{state.status=await request('/api/live-api/status');render();}
    catch(e){$('api-listener-status').textContent=t('Servizio locale non raggiungibile')+': '+e.message;$('api-listener-status').className='status bad';}
    finally{if(state.polling)state.timer=setTimeout(refresh,1000);}
  }

  async function stopSession(sessionId){
    try{await request('/api/live-api/sessions/stop',{sessionId});notice(t('Sessione terminata')+': '+sessionId);}catch(e){notice(e.message,true);}
    clearTimeout(state.timer);refresh();
  }

  function settingsBody(){
    const n=id=>Number($(id).value);
    const aliases={};for(const line of $('set-aliases').value.split('\n').map(l=>l.trim()).filter(Boolean)){const i=line.indexOf('=');if(i<1)throw Error(t('Alias non valido')+': '+line);aliases[line.slice(0,i).trim()]=line.slice(i+1).trim();}
    return {port:n('set-port'),bind:$('set-bind').value.trim(),max_sessions:n('set-max-sessions'),max_signals:n('set-max-signals'),min_sampling_ms:n('set-min-sampling'),max_ttl_seconds:n('set-max-ttl'),
      reconnect_delay_ms:n('set-reconnect-delay'),reconnect_max_attempts:n('set-reconnect-attempts'),reconnect_timeout_seconds:n('set-reconnect-timeout'),batch_ms:n('set-batch'),
      allowed_stream_targets:$('set-targets').value.split('\n').map(l=>l.trim()).filter(Boolean),source_aliases:aliases,require_key:$('set-require-key').checked};
  }
  async function saveSettings(){
    try{const result=await request('/api/live-api/settings',settingsBody());renderSettings(result.settings,true);renderListener(result.listener);$('api-save-status').textContent=t('Impostazioni salvate');}
    catch(e){$('api-save-status').textContent=e.message;notice(e.message,true);}
  }

  // ---- built-in test: the page plays the external system ----
  async function startTest(){
    const body={sourceId:$('test-source').value,address:$('test-address').value.trim(),dataType:$('test-type').value,unit:$('test-unit').value.trim(),
      samplingPeriodMs:Number($('test-period').value),ttlSeconds:Number($('test-ttl').value),signalId:'prova'};
    try{
      $('test-start').disabled=true;
      const result=await request('/api/live-api/test/start',body);
      state.test={requestId:result.session.requestId,sessionId:result.session.sessionId,unit:body.unit};state.clearedAt=null;state.testData=null;
      notice(t('Sessione di prova avviata')+': '+result.session.sessionId);pollTest();
    }catch(e){notice(e.message,true);$('test-start').disabled=false;}
    renderTest();
  }
  async function pollTest(){
    clearTimeout(state.testTimer);if(!state.test)return;
    try{state.testData=(await request('/api/live-api/test/data?requestId='+encodeURIComponent(state.test.requestId))).stream;renderTest();}catch(e){/* next attempt */}
    const s=testSession();if(s||!state.testData?.stopped)state.testTimer=setTimeout(pollTest,500);
  }
  function testSession(){return state.test?(state.status?.sessions||[]).find(s=>s.requestId===state.test.requestId):null;}
  function renderTest(){
    const s=testSession(),d=state.testData,running=!!s;
    $('test-start').disabled=running;$('test-stop').disabled=!running;
    if(!state.test){$('test-state').textContent=t('Nessuna prova in corso.');drawChart([]);$('test-log').textContent='';return;}
    const ended=!running?(state.status?.ended||[]).find(e=>e.requestId===state.test.requestId):null;
    const points=Object.values(d?.points||{})[0]||[],last=points[points.length-1];
    $('test-state').textContent=[t('Sessione')+' '+state.test.sessionId,t('Stato')+' '+t(s?.status||ended?.status||'—'),'WebSocket '+(d?.connected?t('connesso'):t('non connesso')),
      t('Campioni ricevuti')+' '+number(d?.samples||0),t('Sequenza')+' '+number(d?.sequence||0)+(d?.gaps?' ('+t('salti')+' '+d.gaps+')':''),
      t('Ultimo valore')+' '+(last?value(last[1],state.test.unit)+' · '+(last[2]||'')+' · '+time(last[0]):'—')].join(' · ');
    drawChart(points.filter(p=>!state.clearedAt||Date.parse(p[0])>state.clearedAt));
    $('test-log').textContent=(d?.log||[]).join('\n');
  }
  function drawChart(points){
    const canvas=$('test-chart'),ctx=canvas.getContext&&canvas.getContext('2d');if(!ctx)return;
    const ratio=window.devicePixelRatio||1,w=canvas.clientWidth||600,h=canvas.clientHeight||220;
    if(canvas.width!==Math.round(w*ratio)||canvas.height!==Math.round(h*ratio)){canvas.width=Math.round(w*ratio);canvas.height=Math.round(h*ratio);}
    ctx.setTransform(ratio,0,0,ratio,0,0);ctx.clearRect(0,0,w,h);
    const data=points.map(p=>[Date.parse(p[0]),p[1]]).filter(p=>Number.isFinite(p[0])&&typeof p[1]==='number');
    ctx.strokeStyle='#dce4ec';ctx.strokeRect(50.5,8.5,w-60,h-30);
    if(data.length<2){ctx.fillStyle='#586c80';ctx.font='12px sans-serif';ctx.fillText(t('In attesa dei campioni…'),60,h/2);return;}
    let x0=data[0][0],x1=data[data.length-1][0],y0=Math.min(...data.map(p=>p[1])),y1=Math.max(...data.map(p=>p[1]));
    if(x1===x0)x1=x0+1;if(y1===y0){y0-=1;y1+=1;}
    const X=x=>50+(x-x0)/(x1-x0)*(w-60),Y=y=>8+(1-(y-y0)/(y1-y0))*(h-30);
    ctx.fillStyle='#586c80';ctx.font='11px sans-serif';ctx.fillText(String(+y1.toPrecision(6)),2,16);ctx.fillText(String(+y0.toPrecision(6)),2,h-24);
    ctx.fillText(new Date(x0).toLocaleTimeString(undefined,{hour12:false}),52,h-6);ctx.fillText(new Date(x1).toLocaleTimeString(undefined,{hour12:false}),w-70,h-6);
    ctx.strokeStyle='#1467d5';ctx.lineWidth=1.5;ctx.beginPath();data.forEach((p,i)=>i?ctx.lineTo(X(p[0]),Y(p[1])):ctx.moveTo(X(p[0]),Y(p[1])));ctx.stroke();
  }

  $('test-type').replaceChildren(...TYPES.map(x=>new Option(x,x)));
  $('test-start').addEventListener('click',startTest);
  $('test-stop').addEventListener('click',()=>{const s=testSession();if(s)stopSession(s.sessionId);});
  $('test-clear').addEventListener('click',()=>{state.clearedAt=Date.now();renderTest();});
  $('api-save').addEventListener('click',saveSettings);
  $('api-key-show').addEventListener('click',()=>{const k=$('api-key');k.type=k.type==='password'?'text':'password';$('api-key-show').textContent=k.type==='password'?t('Mostra'):t('Nascondi');});
  $('api-key-copy').addEventListener('click',async()=>{try{await navigator.clipboard.writeText($('api-key').value);notice(t('Chiave copiata'));}catch{$('api-key').type='text';$('api-key').select();notice(t('Copiare la chiave selezionata con Ctrl+C'));}});
  $('api-key-new').addEventListener('click',async()=>{
    if(typeof window.confirm==='function'&&!window.confirm(t('Generare una nuova chiave? I sistemi esterni dovranno usare la nuova chiave.')))return;
    try{$('api-key').value=(await request('/api/live-api/key',{})).api_key;notice(t('Nuova chiave generata'));}catch(e){notice(e.message,true);}
  });
  $('api-close').addEventListener('click',async()=>{
    if((state.status?.sessions||[]).length&&typeof window.confirm==='function'&&!window.confirm(t('Ci sono sessioni attive: chiuderle e chiudere l’applicazione?')))return;
    try{await request('/api/shutdown',{});state.polling=false;document.body.textContent=t('Applicazione chiusa.');}catch(e){notice(e.message,true);}
  });
  document.addEventListener('space-analyzer-languagechange',render);
  window.liveApiTest={get state(){return state;},refresh,render,stop(){state.polling=false;clearTimeout(state.timer);clearTimeout(state.testTimer);}};
  refresh();
})();
