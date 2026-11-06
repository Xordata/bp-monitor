/* i18n: ключ = русский исходный текст; пакеты /lang/<code>.json (плоский JSON ru->перевод)
   правятся на сервере без пересборки. Перевод статического и динамического DOM: обход при
   загрузке пакета + MutationObserver на последующие перерисовки. tr() — для нативных confirm/alert. */
window.I18N={lang:"",map:null};
function tr(s){ if(!window.I18N.map||typeof s!=="string") return s; var t=window.I18N.map[s.trim()]; return (t==null||t==="")?s:t; }
var _i18nSkip={SCRIPT:1,STYLE:1,TEXTAREA:1,CODE:1};
function _i18nText(n){
  var v=n.nodeValue; if(!v) return;
  var t=v.trim(); if(!t) return;
  var m=window.I18N.map;
  var r=m[t];
  if(r!=null&&r!==""&&r!==t){ n.nodeValue=v.replace(t,r); return; }
  // «Метка: значение» — ключ-префикс с «:» на конце переводим частично (низкий риск: только метки)
  if(t.length>4) for(var k in m){
    if(k.length>3&&k.charAt(k.length-1)===":"&&t.indexOf(k)===0&&m[k]){
      n.nodeValue=v.replace(k,m[k]+t.substring(k.length)); return;
    }
  }
}
function _i18nWalk(root){
  if(!window.I18N.map) return;
  if(root.nodeType===3){ if(root.parentNode&&!_i18nSkip[root.parentNode.tagName]) _i18nText(root); return; }
  if(root.nodeType!==1||_i18nSkip[root.tagName]) return;
  if(root.attributes) for(var i=0;i<root.attributes.length;i++){
    var a=root.attributes[i];
    if(a.name==="title"||a.name==="placeholder"){ var r=window.I18N.map[a.value.trim()]; if(r!=null&&r!=="") root.setAttribute(a.name,r); }
  }
  for(var c=root.firstChild;c;c=c.nextSibling) _i18nWalk(c);
}
var _i18nObs=null;
async function loadLang(code){
  window.I18N.lang=code||""; window.I18N.map=null;
  if(_i18nObs){ _i18nObs.disconnect(); _i18nObs=null; }
  if(!code||code==="ru") return;
  try{
    var r=await fetch("/lang/"+code+".json"); if(!r.ok) return;
    window.I18N.map=await r.json();
    _i18nWalk(document.documentElement);
    _i18nObs=new MutationObserver(function(ms){
      if(!window.I18N.map) return;
      ms.forEach(function(m){
        if(m.type==="childList") m.addedNodes.forEach(_i18nWalk);
        else if(m.type==="characterData"){ var n=m.target; if(n.parentNode&&!_i18nSkip[n.parentNode.tagName]) _i18nText(n); }
      });
    });
    _i18nObs.observe(document.body,{childList:true,subtree:true,characterData:true});
  }catch(e){}
}
