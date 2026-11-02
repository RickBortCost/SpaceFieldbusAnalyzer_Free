// Live Acquisition API mode against the real C# service and the S7 simulator: start page, REST control plane, outbound WebSocket
// data plane (stream_started, samples, sequence, quality, reconnect, stream_stopped), TTL, errors, monitor page and built-in test.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),http=require('node:http'),crypto=require('node:crypto');
const {JSDOM,VirtualConsole}=require(process.env.JSDOM_PATH||'jsdom');
const [base,s7Port]=process.argv.slice(2),errors=[],checks=[];
const wait=async(fn,label,timeout=30000)=>{const end=Date.now()+timeout;while(Date.now()<end){if(await fn()){console.error('PASS '+label);return;}await new Promise(r=>setTimeout(r,40));}throw Error('Timeout '+label+' '+errors.join(';'));};
const sleep=ms=>new Promise(r=>setTimeout(r,ms));

/** Minimal WebSocket server (RFC 6455 text frames) playing the external Test Backend. */
function wsServer(){
  const connections=[];
  const server=http.createServer((_,res)=>{res.writeHead(426);res.end();});
  server.on('upgrade',(req,socket)=>{
    const accept=crypto.createHash('sha1').update(req.headers['sec-websocket-key']+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: '+accept+'\r\n\r\n');
    const conn={path:req.url,messages:[],closed:false,socket};connections.push(conn);let buffer=Buffer.alloc(0),fragments=[];
    socket.on('data',chunk=>{
      buffer=Buffer.concat([buffer,chunk]);
      while(buffer.length>=2){
        const fin=buffer[0]&0x80,op=buffer[0]&0x0f,masked=buffer[1]&0x80;let len=buffer[1]&0x7f,offset=2;
        if(len===126){if(buffer.length<4)return;len=buffer.readUInt16BE(2);offset=4;}else if(len===127){if(buffer.length<10)return;len=Number(buffer.readBigUInt64BE(2));offset=10;}
        const total=offset+(masked?4:0)+len;if(buffer.length<total)return;
        let payload=buffer.subarray(offset+(masked?4:0),total);
        if(masked){const mask=buffer.subarray(offset,offset+4);payload=Buffer.from(payload.map((b,i)=>b^mask[i%4]));}
        buffer=buffer.subarray(total);
        if(op===8){conn.closed=true;try{socket.end(Buffer.from([0x88,0]));}catch{}return;}
        if(op===9){socket.write(Buffer.concat([Buffer.from([0x8a,payload.length]),payload]));continue;}
        if(op===1||op===0){fragments.push(payload);if(fin){conn.messages.push(JSON.parse(Buffer.concat(fragments).toString('utf8')));fragments=[];}}
      }
    });
    socket.on('close',()=>{conn.closed=true;});socket.on('error',()=>{conn.closed=true;});
  });
  return new Promise(resolve=>server.listen(0,'127.0.0.1',()=>resolve({server,connections,port:server.address().port,
    drop(conn){conn.socket.destroy();conn.closed=true;},close(){for(const c of connections)c.socket.destroy();server.close();}})));
}

async function page(){
  const vc=new VirtualConsole();vc.on('jsdomError',e=>{if(!/navigation|Not implemented/.test(e.message))errors.push(e.message);});
  const html=await(await fetch(base)).text(),dom=new JSDOM(html,{url:base,runScripts:'outside-only',pretendToBeVisual:true,virtualConsole:vc}),w=dom.window,$=id=>w.document.getElementById(id);
  w.fetch=(url,options)=>fetch(new URL(url,base),options);
  const ctx=new Proxy({},{get:()=>()=>{}});w.HTMLCanvasElement.prototype.getContext=()=>ctx;
  w.addEventListener('error',e=>errors.push(e.message));
  const scripts=[...w.document.querySelectorAll('script[src]')].map(s=>fs.readFileSync(path.join(__dirname,'../static',path.basename(new URL(s.src).pathname)),'utf8'));
  w.eval(scripts.join('\n'));w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
  return {w,$,html};
}

(async()=>{
  const backend=await wsServer();let monitor=null;
  try{
    const token=async()=>/content="([^"]+)" name="opcua-token"/.exec(await(await fetch(base)).text())[1];
    const local=async(url,body)=>{const r=await fetch(base+url,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json','X-OPCUA-Token':await token()},body:JSON.stringify(body)});return {status:r.status,body:await r.json()};};

    // ---- start page: the operator chooses the mode; nothing of the SFA interface is loaded ----
    let {w,$,html}=await page();
    assert($('mode-sfa')&&$('mode-api'),'two modes offered');assert(!html.includes('app.js'),'start page does not load the SFA interface');
    assert($('mode-api').textContent.includes('Live Acquisition API'));
    // The saved SFA connection provides the sources (here the S7 simulator).
    const cfg=(await local('/api/config')).body;
    for(const k of ['client_cert_abs','client_key_abs','server_cert_abs','server_cert_present','server_cert_sha256','version','max_selected','saved_settings_present','saved_at','selected_nodes','saved_server_data','data_dir','recordings_dir','workspace','calculations','recording_busy'])delete cfg[k];
    Object.assign(cfg,{source:'s7',enabled_sources:['s7'],s7_host:'127.0.0.1',s7_tcp_port:Number(s7Port),s7_mode:'classic'});
    assert.equal((await local('/api/config',cfg)).status,200);
    const savedBefore=JSON.stringify((await local('/api/config')).body);
    await w.modeTest.choose('api');
    await wait(async()=>(await local('/api/mode')).body.mode==='api','API mode chosen');
    w.close();
    assert.match((await local('/api/mode',{mode:'sfa'})).body.error,/riavviare/);checks.push('start page with two modes, mode fixed until restart');

    // ---- monitor page ----
    ({w,$,html}=await page());monitor=w;
    assert(html.includes('live-api.js')&&!html.includes('app.js'),'monitor page only');
    await wait(()=>w.liveApiTest.state.status,'monitor status loaded');
    const freePort=await new Promise(r=>{const s=require('node:net').createServer().listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>r(p));});});
    let saved=await local('/api/live-api/settings',{port:freePort,bind:'127.0.0.1',source_aliases:{PLC01:'s7'},reconnect_delay_ms:200,reconnect_max_attempts:3,reconnect_timeout_seconds:20,batch_ms:50});
    assert.equal(saved.status,200,JSON.stringify(saved.body));assert(saved.body.listener.listening,JSON.stringify(saved.body.listener));
    const key=saved.body.settings.api_key;assert(key.length>=16);
    const api='http://127.0.0.1:'+freePort;
    const call=async(method,url,body,headers={'X-Api-Key':key})=>{const r=await fetch(api+url,{method,headers:{'Content-Type':'application/json',...headers},body:body===undefined?undefined:JSON.stringify(body)});const text=await r.text();return {status:r.status,body:text?JSON.parse(text):null};};
    await wait(()=>$('api-urls').textContent.includes(':'+freePort+'/api/v1/live-sessions'),'monitor shows the API address');
    assert.equal((await call('GET','/api/v1/health',undefined,{})).body.status,'ok');checks.push('listener on its own port, settings saved');

    // ---- control plane: errors ----
    const target=p=>({protocol:'websocket',url:'ws://127.0.0.1:'+backend.port+'/api/live-streams/'+p});
    const request=(id,extra={})=>({requestId:id,sourceId:'PLC01',signals:[{signalId:'pyrometer-entry-stand-1',address:'DB1.DBD0',dataType:'REAL',unit:'°C'}],samplingPeriodMs:250,ttlSeconds:60,streamTarget:target(id),...extra});
    let r=await call('POST','/api/v1/live-sessions',request('nokey'),{});assert.equal(r.status,401);assert.equal(r.body.error.code,'unauthorized');
    r=await call('POST','/api/v1/live-sessions',request('x',{signals:[{signalId:'a',address:'DB1.DBX0',dataType:'REAL'}]}));
    assert.equal(r.status,400);assert.equal(r.body.status,'error');assert.equal(r.body.error.code,'signal_address_invalid');assert.equal(r.body.error.retryable,false);
    r=await call('POST','/api/v1/live-sessions',request('x',{signals:[{signalId:'a',address:'DB1.DBD0',dataType:'FLOAT9'}]}));assert.equal(r.body.error.code,'signal_type_unsupported');
    r=await call('POST','/api/v1/live-sessions',request('x',{sourceId:'PLC99'}));assert.equal(r.status,404);assert.equal(r.body.error.code,'source_not_found');
    r=await call('POST','/api/v1/live-sessions',request('x',{streamTarget:{protocol:'websocket',url:'ws://example.invalid:8080/x'}}));assert.equal(r.body.error.code,'invalid_stream_target');
    r=await call('POST','/api/v1/live-sessions',request('x',{ttlSeconds:undefined}));assert.equal(r.body.error.code,'invalid_request');
    r=await call('POST','/api/v1/live-sessions',request('x',{samplingPeriodMs:0}));assert.equal(r.body.error.code,'sampling_period_unsupported');
    r=await call('GET','/api/v1/live-sessions/sfa-missing');assert.equal(r.status,404);assert.equal(r.body.error.code,'session_not_found');
    checks.push('structured REST errors (401, invalid address/type/source/target/ttl/period, unknown session)');

    // ---- a session: 201, outbound WebSocket, stream_started, samples ----
    r=await call('POST','/api/v1/live-sessions',request('test-8f21'));
    assert.equal(r.status,201,JSON.stringify(r.body));const s1=r.body;
    assert.equal(s1.requestId,'test-8f21');assert.match(s1.sessionId,/^sfa-[0-9a-f]{8}$/);assert.equal(s1.requestedSamplingPeriodMs,250);assert.equal(s1.effectiveSamplingPeriodMs,250);
    assert(['starting','connecting','streaming'].includes(s1.status));assert.match(s1.createdAt,/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/);
    assert.equal(Date.parse(s1.expiresAt)-Date.parse(s1.createdAt),60000);
    const conn1=()=>backend.connections.find(c=>c.path==='/api/live-streams/test-8f21');
    try{await wait(()=>conn1()&&conn1().messages.filter(m=>m.type==='samples').length>=6,'samples received on the external WebSocket');}catch(e){console.error(JSON.stringify((await local('/api/live-api/status')).body.ended.map(x=>x.log)),backend.connections.map(c=>c.path+' '+c.messages.length));throw e;}
    const m1=conn1().messages;
    assert.deepEqual({...m1[0]},{type:'stream_started',requestId:'test-8f21',sessionId:s1.sessionId,schemaVersion:1,samplingPeriodMs:250,requestedSamplingPeriodMs:250,
      signals:[{signalId:'pyrometer-entry-stand-1',dataType:'REAL',unit:'°C'}]});
    const batches=m1.filter(m=>m.type==='samples');
    batches.forEach((b,i)=>{assert.equal(b.sequence,i+1,'sequence without gaps');assert.equal(b.requestId,'test-8f21');assert.equal(b.sessionId,s1.sessionId);});
    const samples=batches.flatMap(b=>b.samples);
    for(const s of samples){assert.equal(s.signalId,'pyrometer-entry-stand-1');assert.match(s.timestamp,/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/);assert.equal(s.quality,'good');assert.equal(typeof s.value,'number');}
    const times=samples.map(s=>Date.parse(s.timestamp));for(let i=1;i<times.length;i++)assert(times[i]-times[i-1]>=150,'thinned to about 250 ms: '+(times[i]-times[i-1]));
    assert(Math.abs(times[times.length-1]-Date.now())<5000,'timestamps assigned by the acquisition PC (UTC)');
    assert(new Set(samples.map(s=>s.value)).size>1,'values follow the PLC');
    r=await call('GET','/api/v1/live-sessions/'+s1.sessionId);assert.equal(r.body.status,'streaming');assert.equal(r.body.streamConnected,true);assert.equal(r.body.effectiveSamplingPeriodMs,250);
    checks.push('201, outbound WebSocket, stream_started, batched samples with sequence, UTC timestamps, quality, thinning');

    // ---- second session: other type and faster period; duplicate requestId refused ----
    r=await call('POST','/api/v1/live-sessions',request('test-bool',{signals:[{signalId:'flag',address:'DB1.DBX6.7',dataType:'BOOL'},{signalId:'count',address:'DB1.DBW4',dataType:'INT'}],samplingPeriodMs:100}));
    assert.equal(r.status,201,JSON.stringify(r.body));const s2=r.body;assert.equal(s2.effectiveSamplingPeriodMs,100);
    assert.equal((await call('POST','/api/v1/live-sessions',request('test-bool'))).body.error.code,'invalid_request');
    const conn2=()=>backend.connections.find(c=>c.path==='/api/live-streams/test-bool');
    await wait(()=>conn2()&&conn2().messages.some(m=>m.type==='samples'&&m.samples.some(s=>s.signalId==='flag')&&m.samples.some(s=>s.signalId==='count')),'second session samples');
    const s2samples=conn2().messages.filter(m=>m.type==='samples').flatMap(m=>m.samples);
    assert.equal(s2samples.find(s=>s.signalId==='flag').value,true);assert.equal(s2samples.find(s=>s.signalId==='count').value,-1234);
    r=await call('GET','/api/v1/live-sessions');assert.equal(r.body.sessions.length,2);checks.push('two concurrent sessions, BOOL/INT, list, duplicate requestId refused');

    // ---- a read error is reported as signal_status and "bad" samples without a value, never as the last valid value ----
    r=await call('POST','/api/v1/live-sessions',request('test-bad',{signals:[{signalId:'missing',address:'DB1.DBD4000',dataType:'REAL'}],samplingPeriodMs:100}));
    assert.equal(r.status,201,JSON.stringify(r.body));const sBad=r.body;
    const connBad=()=>backend.connections.find(c=>c.path==='/api/live-streams/test-bad');
    await wait(()=>connBad()&&connBad().messages.some(m=>m.type==='signal_status')&&connBad().messages.some(m=>m.type==='samples'),'read error reported');
    const status=connBad().messages.find(m=>m.type==='signal_status');
    assert.equal(status.signalId,'missing');assert.equal(status.status,'error');assert.equal(status.error.code,'acquisition_failed');assert(status.error.message.length>0);assert.match(status.timestamp,/Z$/);
    for(const s of connBad().messages.filter(m=>m.type==='samples').flatMap(m=>m.samples)){assert.equal(s.quality,'bad');assert.equal(s.value,null);}
    // The other session on the same PLC keeps valid samples while this one fails.
    const before2=conn2().messages.length;await sleep(600);
    const during=conn2().messages.slice(before2).filter(m=>m.type==='samples').flatMap(m=>m.samples);
    assert(during.length>0&&during.every(s=>s.quality==='good'),'other session unaffected: '+JSON.stringify(during.slice(0,3)));
    assert.equal((await call('DELETE','/api/v1/live-sessions/'+sBad.sessionId)).status,204);
    checks.push('read error: signal_status error and bad samples without value');

    // ---- monitor page shows sessions and requests ----
    await wait(()=>$('api-session-rows').querySelectorAll('tr').length===2&&$('api-session-rows').textContent.includes('test-8f21'),'monitor lists the sessions');
    await wait(()=>$('api-request-rows').textContent.includes('401')&&$('api-request-rows').textContent.includes('unauthorized'),'monitor logs the requests');
    [...$('api-session-rows').querySelectorAll('button')].find(b=>b.closest('tr').textContent.includes('test-8f21')&&/Dettagli/.test(b.textContent)).click();
    await wait(()=>$('api-session-details').textContent.includes('pyrometer-entry-stand-1')&&$('api-session-details').textContent.includes('stream_started'),'session details with signal and log');
    checks.push('monitor page: sessions, request log, details');

    // ---- WebSocket lost: SFA reconnects and starts the stream again ----
    backend.drop(conn1());
    await wait(()=>backend.connections.filter(c=>c.path==='/api/live-streams/test-8f21').length>=2&&backend.connections.filter(c=>c.path==='/api/live-streams/test-8f21').at(-1).messages.some(m=>m.type==='samples'),'reconnected after the loss');
    const again=backend.connections.filter(c=>c.path==='/api/live-streams/test-8f21').at(-1).messages;assert.equal(again[0].type,'stream_started');
    checks.push('automatic reconnect with a new stream_started');

    // ---- DELETE: stream_stopped, socket closed, session gone ----
    r=await call('DELETE','/api/v1/live-sessions/'+s1.sessionId);assert.equal(r.status,204);
    const last=backend.connections.filter(c=>c.path==='/api/live-streams/test-8f21').at(-1);
    await wait(()=>last.closed,'socket closed by SFA');
    const stopped=last.messages.at(-1);assert.equal(stopped.type,'stream_stopped');assert.equal(stopped.reason,'requested');assert.equal(stopped.sessionId,s1.sessionId);assert.match(stopped.timestamp,/Z$/);
    assert.equal((await call('GET','/api/v1/live-sessions/'+s1.sessionId)).status,404);
    checks.push('DELETE: 204, stream_stopped requested, socket closed, session removed');

    // ---- TTL ----
    r=await call('POST','/api/v1/live-sessions',request('test-ttl',{ttlSeconds:2}));assert.equal(r.status,201);
    const conn3=()=>backend.connections.find(c=>c.path==='/api/live-streams/test-ttl');
    await wait(()=>conn3()&&conn3().closed&&conn3().messages.at(-1).type==='stream_stopped',' TTL expiry',10000);
    assert.equal(conn3().messages.at(-1).reason,'expired');assert.equal((await call('GET','/api/v1/live-sessions/'+r.body.sessionId)).status,404);
    checks.push('TTL: stream_stopped expired');

    // ---- stream target unreachable: retries, then the session ends with an error ----
    const closedPort=await new Promise(res=>{const s=require('node:net').createServer().listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>res(p));});});
    r=await call('POST','/api/v1/live-sessions',request('test-down',{streamTarget:{protocol:'websocket',url:'ws://127.0.0.1:'+closedPort+'/x'}}));assert.equal(r.status,201);
    await wait(async()=>(await local('/api/live-api/status')).body.ended.some(e=>e.requestId==='test-down'&&e.endReason==='stream_target_unavailable'&&e.status==='error'),'unreachable target ends the session',20000);
    checks.push('unreachable stream target: retries, then error');

    // ---- session limit ----
    await local('/api/live-api/settings',{max_sessions:1});
    r=await call('POST','/api/v1/live-sessions',request('test-limit'));assert.equal(r.status,429);assert.equal(r.body.error.code,'session_limit_reached');assert.equal(r.body.error.retryable,true);
    await local('/api/live-api/settings',{max_sessions:8});
    r=await call('DELETE','/api/v1/live-sessions/'+s2.sessionId);assert.equal(r.status,204);
    checks.push('session limit');

    // ---- built-in test on the monitor page ----
    await wait(()=>[...$('test-source').options].some(o=>o.value==='s7'),'test sources listed');
    $('test-source').value='s7';$('test-address').value='DB1.DBD0';$('test-type').value='REAL';$('test-unit').value='°C';$('test-period').value='100';$('test-ttl').value='60';
    $('test-start').click();
    await wait(()=>w.liveApiTest.state.testData?.samples>5&&/Campioni ricevuti [1-9]/.test($('test-state').textContent),'built-in test receives samples');
    assert($('test-log').textContent.includes('stream_started ricevuto'));
    await wait(()=>!$('test-stop').disabled,'test running');$('test-stop').click();
    await wait(()=>w.liveApiTest.state.testData?.stopped==='requested','built-in test stopped');
    checks.push('built-in end-to-end test from the monitor page');

    // ---- nothing left: sources closed, saved configuration unchanged ----
    await wait(async()=>{const s=(await local('/api/live-api/status')).body;return s.sessions.length===0&&Object.keys(s.sources).length===0;},'sources closed when no session is left');
    assert.equal(JSON.stringify((await local('/api/config')).body),savedBefore,'SFA configuration unchanged');
    checks.push('resources freed and SFA configuration unchanged');
    assert.deepEqual(errors,[]);
    console.log(JSON.stringify({result:'PASS',checks}));
  }finally{monitor?.liveApiTest?.stop();backend.close();}
  process.exit(0);
})().catch(e=>{console.error(e);process.exit(1);});
