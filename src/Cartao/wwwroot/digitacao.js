'use strict';
// Página de digitação (passos 8, 9 e 14). PAN só aqui, em memória; nunca em cookie, storage ou log.
// Chamada só para a própria origem (credentials 'same-origin'), com timeout e falha visível.
(() => {
  const TIMEOUT_MS = 25000; // maior que o timeout do Gateway na API Autorização (12b)
  const take = (n) => { const m = document.querySelector(`meta[name="chk3-${n}"]`); const v = m ? m.content : ''; if (m) m.remove(); return v; };
  const tc = take('tc'), snapshot = take('snapshot'), parentOrigin = take('parent-origin');
  const amount = parseInt(take('amount'), 10) || 0, currency = take('currency') || 'BRL';
  const maxInst = parseInt(take('max-installments'), 10) || 1, fixedInst = parseInt(take('fixed-installments'), 10) || 0;
  take('brands');
  const txId = take('tx'); // transactionId: vai no X-Correlation-Id (rastreio, não é segredo)
  const deviceSessionId = window.chk3DeviceSessionId || null; // opcional (passo 8)
  const $ = (id) => document.getElementById(id);
  const post = (msg) => window.parent.postMessage(msg, parentOrigin);
  const brl = (c) => (c / 100).toLocaleString('pt-BR', { style: 'currency', currency });

  // Parcelamento (exibição). O passo 10 valida contra o cadastro do canal.
  const sel = $('inst');
  for (let n = fixedInst || 1; n <= (fixedInst || maxInst); n++) {
    sel.add(new Option(n === 1 ? `1x de ${brl(amount)}` : `${n}x de ${brl(Math.round(amount / n))}`, String(n)));
  }
  if (fixedInst) sel.disabled = true;
  $('pay').textContent = `Pagar ${brl(amount)}`;

  // Formatação e bandeira (modelo)
  const luhn = (d) => { let s = 0, dbl = false; for (let i = d.length - 1; i >= 0; i--) { let n = +d[i]; if (dbl) { n *= 2; if (n > 9) n -= 9; } s += n; dbl = !dbl; } return d.length >= 12 && s % 10 === 0; };
  const digits = (v) => v.replace(/\D/g, '');
  function brandOf(v) {
    if (/^4/.test(v)) return 'VISA';
    if (/^5[1-5]/.test(v) || /^2[2-7]/.test(v)) return 'MASTERCARD';
    if (/^(4011|4312|5041|5067|6277|6362|6363|650)/.test(v)) return 'ELO';
    if (/^3[47]/.test(v)) return 'AMEX';
    return '';
  }
  function check() {
    const pan = digits($('pan').value), exp = $('exp').value, cvv = $('cvv').value;
    $('pay').disabled = sent || !(pan.length >= 13 && $('name').value.trim().length >= 3 && /^\d{2}\/\d{2}$/.test(exp) && /^\d{3}$/.test(cvv));
  }
  $('pan').addEventListener('input', (e) => {
    const v = digits(e.target.value).slice(0, 16);
    e.target.value = v.replace(/(.{4})/g, '$1 ').trim();
    $('brand').textContent = brandOf(v);
    e.target.classList.remove('invalid'); check();
  });
  $('exp').addEventListener('input', (e) => {
    let v = digits(e.target.value).slice(0, 4);
    if (v.length > 2) v = v.slice(0, 2) + '/' + v.slice(2);
    e.target.value = v; check();
  });
  $('cvv').addEventListener('input', (e) => { e.target.value = digits(e.target.value).slice(0, 3); check(); });
  $('name').addEventListener('input', check);

  function showError(text) { $('msg').textContent = text; $('msg').hidden = false; }

  let sent = false;
  $('f').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    if (sent) return;
    const pan = digits($('pan').value);
    const [mm, yy] = $('exp').value.split('/');
    const cvv = $('cvv').value;
    if (!luhn(pan)) { $('pan').classList.add('invalid'); return showError('Número do cartão inválido.'); }
    if (!(+mm >= 1 && +mm <= 12)) return showError('Validade inválida.');
    sent = true; check(); $('msg').hidden = true;
    post({ type: 'chk3.processing' });
    const ctrl = new AbortController();
    const t = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
    let status = 401, data = {};
    try {
      // Passo 9: nunca envia valor. O nome impresso não é enviado (campo do Gateway a confirmar).
      const r = await fetch('/api/authorize', {
        method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: ctrl.signal,
        headers: { 'Content-Type': 'application/json', 'X-Typing-Credential': tc, ...(txId ? { 'X-Correlation-Id': txId } : {}) },
        body: JSON.stringify({ pan, expMonth: +mm, expYear: 2000 + +yy, cvv, installments: +sel.value, snapshot, deviceSessionId }),
      });
      status = r.status;
      data = await r.json().catch(() => ({}));
    } catch {
      data = { status: 'Indeterminada' }; // rede/timeout: não sabemos se chegou ao Gateway
    } finally { clearTimeout(t); }
    $('cvv').value = '';
    if (data.status === 'ValidacaoFalhou') { // 9a: pede correção, sem tentativa registrada
      sent = false; check();
      showError('Cartão não aceito. Confira os dados ou use outro cartão.');
      return post({ type: 'chk3.validation' });
    }
    $('pan').value = ''; $('name').value = '';
    // Passo 14: resultado para a Tela de opções, origem restrita, sem dados do cartão.
    post({ type: 'chk3.result', status: data.status || (status === 401 ? 'SessaoInvalida' : 'Indeterminada'), transactionId: data.transactionId || null });
  });

  // Altura do conteúdo para a Tela ajustar o iframe; "pronto" confirma que a digitação carregou.
  const height = () => document.documentElement.scrollHeight;
  new ResizeObserver(() => post({ type: 'chk3.resize', height: height() })).observe(document.body);
  post({ type: 'chk3.ready', height: height() });
})();
