'use strict';
(() => {
  // Credencial e snapshot só em memória (closure). Remove os <meta> do DOM logo após ler.
  const take = (n) => { const m = document.querySelector(`meta[name="${n}"]`); const v = m ? m.content : ''; if (m) m.remove(); return v; };
  const tc = take('chk3-tc'), snapshot = take('chk3-snapshot'), parentOrigin = take('chk3-parent-origin');
  const installments = parseInt(take('chk3-installments'), 10) || 1;
  const deviceSessionId = window.chk3DeviceSessionId || null; // opcional (passo 8)
  const $ = (id) => document.getElementById(id);
  $('info').textContent = installments > 1 ? `Parcelado em ${installments}x` : 'À vista';

  const luhn = (d) => { let s = 0, dbl = false; for (let i = d.length - 1; i >= 0; i--) { let n = +d[i]; if (dbl) { n *= 2; if (n > 9) n -= 9; } s += n; dbl = !dbl; } return d.length >= 12 && s % 10 === 0; };
  let sent = false;

  $('f').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    if (sent) return;
    const pan = $('pan').value.replace(/\D/g, '');
    const [mm, yy] = $('exp').value.split('/');
    const cvv = $('cvv').value.replace(/\D/g, '');
    // Passo 9 (UX): Luhn, CVV e validade. O servidor revalida tudo.
    if (!luhn(pan) || !/^\d{3}$/.test(cvv) || !/^\d{2}$/.test(mm || '') || !/^\d{2}$/.test(yy || '')) {
      $('msg').textContent = 'Confira os dados do cartão.'; return;
    }
    sent = true; $('pay').disabled = true; $('msg').textContent = 'Processando...';
    let data;
    try {
      const r = await fetch('/api/authorize', {
        method: 'POST', credentials: 'omit', cache: 'no-store',
        headers: { 'Content-Type': 'application/json', 'X-Typing-Credential': tc },
        body: JSON.stringify({ pan, expMonth: +mm, expYear: 2000 + +yy, cvv, installments, snapshot, deviceSessionId }), // nunca valor
      });
      data = await r.json();
    } catch { data = { status: 'Indeterminada' }; }
    $('pan').value = ''; $('cvv').value = '';
    if (data.status === 'ValidacaoFalhou') { // 9a: pede correção, sem tentativa registrada
      sent = false; $('pay').disabled = false; $('msg').textContent = 'Cartão não aceito. Corrija ou use outro cartão.'; return;
    }
    // Passo 14: avisa a Tela de opções, origem restrita, sem dados do cartão.
    window.parent.postMessage({ type: 'chk3.result', status: data.status, transactionId: data.transactionId || null }, parentOrigin);
    $('msg').textContent = 'Concluído.';
  });
})();
