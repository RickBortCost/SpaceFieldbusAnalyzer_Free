// Grafici Live disattivabili dal popup Connessione: connessione e registrazione proseguono senza buffer dei grafici (servizio e pagina),
// i valori correnti restano aggiornati, la scelta è salvata e si può riattivare. Pagina e script reali contro il servizio C# e il server OPC UA.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {JSDOM,VirtualConsole}=require(process.env.JSDOM_PATH||'jsdom');
const [base,endpoint,rootNode,...ids]=process.argv.slice(2),errors=[];
const wait=async(fn,label,timeout=20000)=>{const end=Date.now()+timeout;while(Date.now()<end){if(await fn()){console.error('PASS '+label);return;}await new Promise(r=>setTimeout(r,35));}throw Error('Timeout '+label+' '+errors.join(';'));};
const api=async(route,body)=>(await fetch(base+route,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json','X-OPCUA-Token':/content="([^"]+)" name="opcua-token"/.exec(await(await fetch(base)).text())[1]},body:JSON.stringify(body)})).json();

async function page(){
  const vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));
  const html=await(await fetch(base)).text(),dom=new JSDOM(html,{url:base,runScripts:'outside-only',pretendToBeVisual:true,virtualConsole:vc}),w=dom.window,$=id=>w.document.getElementById(id);
  w.fetch=(url,options)=>fetch(new URL(url,base),options);w.AbortController=globalThis.AbortController;w.FormData=globalThis.FormData;w.ResizeObserver=class{observe(){}disconnect(){}};
  const ctx=new Proxy({measureText:t=>({width:String(t).length*7})},{get:(o,k)=>k in o?o[k]:(()=>{})});w.HTMLCanvasElement.prototype.getContext=()=>ctx;
  w.HTMLCanvasElement.prototype.getBoundingClientRect=()=>({x:0,y:0,left:0,top:0,width:950,height:560,right:950,bottom:560});
  w.addEventListener('error',e=>errors.push(e.message));
  w.eval([...w.document.querySelectorAll('script[src]')].map(s=>fs.readFileSync(path.join(__dirname,'../static',path.basename(new URL(s.src).pathname)),'utf8')).join('\n')
    +'\nwindow.chartsTest={get series(){return series},get on(){return liveChartsOn},get connected(){return connected},get selected(){return selected},get record(){return recordState},get trend(){return liveCharts.charts[0].trend},stop(){polling=false;clearTimeout(pollTimer);clearTimeout(workspaceTimer);}};');
  await wait(()=>$('endpoint').value,'configuration loaded');await w.opcuaConfigReady;
  return {dom,w,$,t:w.chartsTest};
}
const samplesIn=t=>Object.values(t.series).reduce((n,s)=>n+(s?.length||0),0);

(async()=>{
  let {dom,w,$,t}=await page();const checks=[];
  try{
    assert.equal(t.on,true);assert.equal($('live-charts-toggle').getAttribute('aria-pressed'),'true');assert.match($('live-charts-toggle').textContent,/Disattiva grafici/);
    $('nav-connection').click();$('endpoint').value=endpoint;$('security').value='None';$('sample_ms').value='20';$('root_nodeid').value=rootNode;
    $('connect').click();await wait(()=>t.connected,'connected');
    for(const id of ids.slice(0,2)){$('manual-node').value=id;$('add-node').click();await wait(()=>t.selected.has(id)&&!$('add-node').disabled,'add '+id);}
    await wait(()=>samplesIn(t)>20,'charts receive samples');
    checks.push('charts on by default');

    // ---- the button in Connessione switches the charts off while connected ----
    $('nav-connection').click();$('live-charts-toggle').click();
    await wait(async()=>(await api('/api/values')).diagnostics.live_charts===false&&!t.on,'charts off in service and page');
    assert.equal($('live-charts-toggle').getAttribute('aria-pressed'),'false');assert.match($('live-charts-toggle').textContent,/Attiva grafici/);
    assert.match($('live-charts-state').textContent,/registrazione proseguono/);
    assert.equal(samplesIn(t),0,'page series freed');
    assert.equal((await api('/api/config')).live_charts,false,'choice saved with the connection');
    // The service keeps no chart buffers, but the current values go on.
    const before=(await api('/api/values')).values.map(v=>v.source_ts||v.server_ts||v.value);
    await new Promise(r=>setTimeout(r,800));
    const now=await api('/api/values?view=all');
    for(const id of ids.slice(0,2))assert.equal(now.diagnostics.session_samples[id],0,'no buffer for '+id);
    assert.equal(now.values.length,2);assert(now.values.every(v=>v.good),JSON.stringify(now.values));
    assert.notDeepEqual(now.values.map(v=>v.source_ts||v.server_ts||v.value),before,'current values keep updating');
    await wait(()=>samplesIn(t)===0&&/Grafici disattivati/.test(w.document.body.textContent),'page shows that charts are off');
    checks.push('charts off: no buffers in service and page, current values updated');

    // ---- Record works with the charts off ----
    $('connection-close')?.click();
    $('record').click();await wait(()=>!$('record-modal').hidden,'record popup');
    w.document.querySelector('input[name="record-format"][value="csv"]').checked=true;
    $('record-form').dispatchEvent(new w.Event('submit',{bubbles:true,cancelable:true}));
    await wait(()=>t.record.active,'recording with charts off');
    await wait(()=>t.record.cycles_received>=60,'recording receives cycles');
    assert.equal(samplesIn(t),0,'still no chart samples while recording');
    $('record-stop').click();await wait(()=>!t.record.active&&!t.record.busy,'recording closed',30000);
    const rec=(await api('/api/record/status')).recording;assert.equal(rec.state,'stopped');assert(rec.rows_written>=100,JSON.stringify(rec.rows_written));
    const files=(await api('/api/recordings')).files;assert(files.some(f=>f.name===rec.files[0].name&&Number(f.rows_written)===rec.rows_written),'recorded file listed with every row');
    checks.push('Record with charts off: every row written');

    // ---- the choice survives a reload of the page ----
    t.stop();dom.window.close();({dom,w,$,t}=await page());
    await wait(()=>!t.on&&$('live-charts-toggle').getAttribute('aria-pressed')==='false','choice restored after reload');
    await new Promise(r=>setTimeout(r,600));assert.equal(samplesIn(t),0);
    checks.push('choice kept after reload');

    // ---- switched on again: the charts start from the new samples ----
    $('nav-connection').click();$('live-charts-toggle').click();
    await wait(async()=>(await api('/api/values')).diagnostics.live_charts===true&&t.on,'charts on again');
    await wait(()=>samplesIn(t)>20,'charts receive samples again');
    assert.equal((await api('/api/config')).live_charts,true);
    checks.push('charts on again');
    assert.deepEqual(errors,[]);
    console.log(JSON.stringify({result:'PASS',checks}));
  }finally{t.stop();dom.window.close();}
  process.exit(0);
})().catch(e=>{console.error(e);process.exit(1);});
