'use strict';
// Credencial de digitação inválida ou expirada (passo 8 recusado): avisa a Tela de opções.
(() => {
  const m = document.querySelector('meta[name="chk3-parent-origin"]');
  if (m && window.parent !== window) window.parent.postMessage({ type: 'chk3.invalid' }, m.content);
})();
