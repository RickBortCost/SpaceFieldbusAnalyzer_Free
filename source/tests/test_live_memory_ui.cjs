// Memoria del grafico Live: limite di campioni secondo la RAM del PC e il livello riportato dal servizio (/api/values diagnostics.memory),
// array che restituiscono la memoria quando si conserva solo la parte recente, avviso all'operatore. Pagina e script reali.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {JSDOM,VirtualConsole}=require(process.env.JSDOM_PATH||'jsdom');
const [base]=process.argv.slice(2),errors=[];
const wait=async(fn,label)=>{const end=Date.now()+15000;while(Date.now()<end){if(await fn()){console.error('PASS '+label);return;}await new Promise(r=>setTimeout(r,30));}throw Error('Timeout '+label+' '+errors.join(';'));};
(async()=>{
const vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));const html=await(await fetch(base)).text(),dom=new JSDOM(html,{url:base,runScripts:'outside-only',pretendToBeVisual:true,virtualConsole:vc}),w=dom.window,$=id=>w.document.getElementById(id);
w.fetch=(url,options)=>fetch(new URL(url,base),options);w.AbortController=globalThis.AbortController;w.FormData=globalThis.FormData;w.ResizeObserver=class{observe(){}disconnect(){}};
const ctx=new Proxy({measureText:t=>({width:String(t).length*7})},{get:(o,k)=>k in o?o[k]:(()=>{})});w.HTMLCanvasElement.prototype.getContext=()=>ctx;w.HTMLCanvasElement.prototype.getBoundingClientRect=()=>({x:0,y:0,left:0,top:0,width:950,height:560,right:950,bottom:560});
w.addEventListener('error',e=>errors.push(e.message));w.eval([...w.document.querySelectorAll('script[src]')].map(s=>fs.readFileSync(path.join(__dirname,'../static',path.basename(new URL(s.src).pathname)),'utf8')).join('\n')+'\nwindow.memTest={budget:liveSampleBudget,set(m){liveMemory=m;},sync(){return syncLiveSamples(series);},stop(){polling=false;clearTimeout(pollTimer);clearTimeout(workspaceTimer);}};');
const t=w.memTest,checks=[];
try{
 await wait(()=>$('endpoint').value,'config loaded');await w.opcuaConfigReady;
 // Il servizio riporta la memoria del PC in /api/values.
 const values=await w.eval("request(liveValuesURL())");const mem=values.diagnostics.memory;
 assert(['ok','low','critical'].includes(mem.level));assert(mem.total_mb>0&&mem.available_mb>=0&&mem.process_mb>0,JSON.stringify(mem));
 checks.push('Memoria del PC nei dati Live');
 // Limite: circa 1,5 milioni di campioni per GB di RAM, dimezzato con memoria bassa, 2 milioni con memoria quasi esaurita.
 t.set({level:'ok',total_mb:8192});assert.equal(t.budget(),12000000);
 t.set({level:'ok',total_mb:65536});assert.equal(t.budget(),40000000);
 t.set({level:'ok',total_mb:2048});assert.equal(t.budget(),4000000);
 t.set({level:'low',total_mb:8192});assert.equal(t.budget(),6000000);
 t.set({level:'critical',total_mb:8192});assert.equal(t.budget(),2000000);
 checks.push('Limite del Live secondo la RAM e il livello di memoria');
 // keepLast restituisce la memoria: array nuovi e più piccoli, valori e sequenze conservati.
 const s=new w.LiveSeries(false),n=300000,S=new Float64Array(n),T=new Float64Array(n),V=new Float64Array(n),Q=new Uint8Array(n);
 for(let i=0;i<n;i++){S[i]=i+1;T[i]=1e12+i;V[i]=i*.5;}
 s.append(S,T,V,Q);const before=s.cap;assert(before>=n);
 const snap=s.snapshot();s.keepLast(50000);
 assert.equal(s.length,50000);assert(s.cap<=62500&&s.cap<before/2,'cap '+s.cap);assert.equal(s.firstSeq,250001);
 const v=s.view();assert.equal(v.t[0],1e12+250000);assert.equal(v.v[49999],(n-1)*.5);
 assert.equal(snap.length,n,'una vista in pausa conserva i propri dati');
 s.append(new Float64Array([n+1]),new Float64Array([1e12+n]),new Float64Array([7]),new Uint8Array([0]));assert.equal(s.length,50001);assert.equal(s.view().v[50000],7);
 checks.push('Array del Live ridotti davvero quando si tiene solo la parte recente');
 // Avviso all'operatore quando il servizio segnala memoria quasi esaurita.
 t.stop();await new Promise(r=>setTimeout(r,1500));t.set({level:'critical',total_mb:8192});await t.sync().catch(e=>errors.push('sync '+e.message));await wait(()=>/Memoria del PC quasi esaurita|PC memory nearly exhausted/.test($('msg').textContent),'avviso memoria '+$('msg').textContent+' '+errors.join(';'));
 checks.push('Avviso di memoria quasi esaurita');
 assert.deepEqual(errors,[]);console.log(JSON.stringify({result:'PASS',checks,files:[]},null,2));
}finally{t.stop();await new Promise(r=>setTimeout(r,300));w.close();}
})().then(()=>process.exit(0)).catch(e=>{console.error(e.stack);process.exit(1);});
