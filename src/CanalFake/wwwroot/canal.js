'use strict';
(() => {
  const opcoesOrigin = document.currentScript.dataset.opcoesOrigin;
  const frame = document.getElementById('frame');
  const btn = document.getElementById('recarregar');
  // Reapresenta a mesma credencial dentro do TTL (passo 6 valida sem consumir).
  if (btn && frame) { const url = frame.src; btn.addEventListener('click', () => { frame.src = 'about:blank'; setTimeout(() => { frame.src = url; }, 50); }); }
  else if (btn) btn.hidden = true;
  window.addEventListener('message', (ev) => {
    if (ev.origin !== opcoesOrigin || !ev.data || ev.data.type !== 'chk3.result') return;
    document.getElementById('evento').textContent = `postMessage do frame: ${ev.data.status} (não é prova de pagamento, aguardar webhook)`;
  });
})();
