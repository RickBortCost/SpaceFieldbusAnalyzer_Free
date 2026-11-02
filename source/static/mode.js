/* Start page: the operator chooses the complete interface or the Live Acquisition API service. */
(() => {
  'use strict';
  const $=id=>document.getElementById(id),token=()=>document.querySelector('meta[name="opcua-token"]').content;
  async function post(url,body){
    const response=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json','X-OPCUA-Token':token()},body:JSON.stringify(body)});
    const result=await response.json().catch(()=>({}));if(!response.ok||result.error)throw Error(result.error||response.statusText);return result;
  }
  async function choose(mode){
    for(const id of ['mode-sfa','mode-api'])$(id).disabled=true;$('mode-error').hidden=true;
    try{await post('/api/mode',{mode});window.location.reload();}
    catch(e){$('mode-error').textContent=e.message;$('mode-error').hidden=false;for(const id of ['mode-sfa','mode-api'])$(id).disabled=false;}
  }
  $('mode-sfa').addEventListener('click',()=>choose('sfa'));
  $('mode-api').addEventListener('click',()=>choose('api'));
  $('mode-close').addEventListener('click',async()=>{try{await post('/api/shutdown',{});document.body.textContent=I18N.t('Applicazione chiusa.');}catch(e){$('mode-error').textContent=e.message;$('mode-error').hidden=false;}});
  window.modeTest={choose};
})();
