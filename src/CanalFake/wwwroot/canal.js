'use strict';
(() => {
  const opcoesOrigin = document.currentScript.dataset.opcoesOrigin;
  const frame = document.getElementById('frame');
  const btn = document.getElementById('recarregar');
  const evento = document.getElementById('evento');
  // Reapresenta a mesma credencial dentro do TTL (passo 6 valida sem consumir).
  if (btn && frame) { const url = frame.src; btn.addEventListener('click', () => { frame.src = 'about:blank'; setTimeout(() => { frame.src = url; }, 50); }); }
  else if (btn) btn.hidden = true;
  window.addEventListener('message', (ev) => {
    if (ev.origin !== opcoesOrigin || !ev.data) return;
    if (ev.data.type === 'chk3.result') {
      evento.textContent = `postMessage do frame: ${ev.data.status} (não é prova de pagamento; o autoritativo é o webhook)`;
    } else if (ev.data.type === 'chk3.back') {
      location.href = '/';
    }
  });
})();
