'use strict';
// Tela de opções (passos 5, 6, 7 e 14). Visual: modelo "Checkout · Multipagamentos".
// Todas as chamadas vão para a PRÓPRIA origem (credentials 'same-origin'): assim um proxy de
// autenticação na frente (ex.: Cloudflare Access) recebe o cookie dele. A credencial de pagamento
// fica só em memória e vai no header; este app não cria cookie nem usa storage.
(() => {
  const TIMEOUT_MS = 15000;
  const $ = (id) => document.getElementById(id);
  const backBtn = $('backBtn');
  let cfg = null, sess = null, pc = null, cardLoaded = false, readyTimer = null, finished = false;

  // ---------- navegação entre telas (modelo) ----------
  function show(id) {
    document.querySelectorAll('.screen').forEach((s) => s.classList.toggle('active', s.id === id));
    $(id).scrollTop = 0;
    backBtn.hidden = id !== 's-card';
  }
  backBtn.addEventListener('click', () => show('s-options'));

  function fail(title, msg, retry) {
    $('errTitle').textContent = title;
    $('errMsg').textContent = msg;
    $('errRetry').hidden = !retry;
    $('errRetry').onclick = retry || null;
    show('s-error');
  }
  const failNetwork = (retry) => fail('Não conseguimos carregar o pagamento',
    'A conexão falhou ou demorou demais. Verifique sua conexão e tente novamente.', retry);
  const failSession = () => fail('Link de pagamento expirado ou inválido',
    'Este link não é mais válido ou o pagamento já foi enviado. Volte à loja para gerar um novo.');

  // fetch para a própria origem, com timeout. Lança Error('rede') em falha de rede/timeout.
  async function call(path, opts = {}) {
    const ctrl = new AbortController();
    const t = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
    try {
      return await fetch(path, { credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: ctrl.signal, ...opts });
    } catch { throw new Error('rede'); } finally { clearTimeout(t); }
  }
  // X-Correlation-Id = transactionId (conhecido depois do passo 6). Só rastreio; não é segredo nem autoriza nada.
  const api = (path) => call(path, { method: 'POST', headers: { 'X-Payment-Credential': pc, ...(sess && sess.transactionId ? { 'X-Correlation-Id': sess.transactionId } : {}) } });
  const brl = (cents, cur) => (cents / 100).toLocaleString('pt-BR', { style: 'currency', currency: cur || 'BRL' });

  function fillResult(d) {
    document.querySelectorAll('[data-valor]').forEach((e) => { e.textContent = sess ? brl(sess.amountCents, sess.currency) : '—'; });
    document.querySelectorAll('[data-pedido]').forEach((e) => { e.textContent = sess ? `#${sess.orderId}` : '—'; });
    document.querySelectorAll('[data-tx]').forEach((e) => { e.textContent = d.transactionId ? String(d.transactionId).slice(-12) : '—'; });
  }

  // ---------- passo 6: valida a credencial sem consumir ----------
  async function start() {
    show('s-loading');
    try {
      if (!cfg) {
        const r = await call('/config.json');
        if (!r.ok) throw new Error('rede');
        cfg = await r.json();
      }
      const r = await api('/api/session');
      if (r.status === 401) return failSession();          // 6a
      if (!r.ok) throw new Error('rede');
      sess = await r.json();
    } catch { return failNetwork(start); }

    $('hdrVal').textContent = brl(sess.amountCents, sess.currency);
    $('hdrMerchant').textContent = `${sess.merchantName} · Pedido #${sess.orderId}`;
    const max = sess.maxInstallments;
    $('m-card-ds').textContent = max > 1 ? `Crédito à vista ou parcelado em até ${max}x sem juros · ${sess.brands.join(', ')}` : `Crédito à vista · ${sess.brands.join(', ')}`;
    show('s-options');
  }

  // ---------- passo 7: troca pela credencial de digitação e abre o iframe filho por POST ----------
  async function openCard() {
    if (cardLoaded) return show('s-card');               // reaproveita o iframe já carregado
    $('m-card').disabled = true;
    let tc;
    try {
      const t = await api('/api/typing-credential');
      if (t.status === 401) return failSession();
      if (!t.ok) throw new Error('rede');
      tc = (await t.json()).typingCredential;
    } catch { $('m-card').disabled = false; return failNetwork(() => { show('s-options'); }); }
    finally { $('m-card').disabled = false; }

    const form = document.createElement('form');
    form.method = 'POST'; form.action = `${cfg.cartaoOrigin}/digitacao`; form.target = 'cartao'; form.hidden = true;
    const i = document.createElement('input'); i.type = 'hidden'; i.name = 'tc'; i.value = tc; form.appendChild(i);
    const c = document.createElement('input'); c.type = 'hidden'; c.name = 'cid'; c.value = sess.transactionId || ''; form.appendChild(c);
    document.body.appendChild(form);
    $('cardLoading').hidden = false;
    show('s-card');
    form.submit();
    form.remove();
    // Falha visível se o endpoint pay não sinalizar "pronto" a tempo (bloqueio, rede, proxy de autenticação).
    readyTimer = setTimeout(() => fail('Não conseguimos abrir a digitação do cartão',
      'O ambiente seguro do cartão não respondeu. Tente novamente ou volte à loja.', () => { cardLoaded = false; openCard(); }), TIMEOUT_MS);
  }
  $('m-card').addEventListener('click', openCard);

  // ---------- mensagens do iframe do cartão (passos 8, 9 e 14), só da origem do endpoint pay ----------
  window.addEventListener('message', (ev) => {
    if (!cfg || ev.origin !== cfg.cartaoOrigin || ev.source !== $('cartao').contentWindow) return;
    const d = ev.data || {};
    switch (d.type) {
      case 'chk3.ready':
        clearTimeout(readyTimer); cardLoaded = true; $('cardLoading').hidden = true;
        if (typeof d.height === 'number' && d.height > 0 && d.height < 2000) $('cartao').style.height = `${Math.ceil(d.height)}px`;
        break;
      case 'chk3.resize':
        if (typeof d.height === 'number' && d.height > 0 && d.height < 2000) $('cartao').style.height = `${Math.ceil(d.height)}px`;
        break;
      case 'chk3.invalid':                                  // 8 recusado: credencial expirada/inválida
        clearTimeout(readyTimer); failSession(); break;
      case 'chk3.processing':
        backBtn.hidden = true; $('procStep').textContent = 'Enviando ao adquirente…'; show('s-proc'); break;
      case 'chk3.validation':                               // 9a: volta para o formulário
        show('s-card'); break;
      case 'chk3.result':
        if (finished) return;
        finished = true; fillResult(d); showResult(d);
        // O retorno do frame não prova pagamento final: o canal deve confiar no webhook (passo 16).
        window.parent.postMessage({ type: 'chk3.result', status: d.status, transactionId: d.transactionId || null }, cfg.canalOrigin);
        break;
    }
  });

  function showResult(d) {
    switch (d.status) {
      case 'Autorizada': return show('s-success');
      case 'Recusada': return show('s-declined');
      case 'TentativaJaRegistrada':
        $('pendingStatus').textContent = 'Já enviado';
        $('pendingMsg').textContent = 'Este pagamento já foi enviado antes. A loja será avisada do resultado. Não tente pagar de novo.';
        return show('s-pending');
      case 'SessaoInvalida': return failSession();
      case 'LimiteDeTentativas': return fail('Muitas tentativas', 'Por segurança, este pagamento foi bloqueado. Volte à loja para tentar de novo.');
      default: return show('s-pending');                    // Indeterminada ou desconhecido
    }
  }

  document.querySelectorAll('[data-voltar]').forEach((b) => b.addEventListener('click', () => {
    if (cfg) window.parent.postMessage({ type: 'chk3.back' }, cfg.canalOrigin);
  }));

  // ---------- passo 5: credencial no fragment, só em memória; limpa o hash ----------
  pc = new URLSearchParams(location.hash.slice(1)).get('pc');
  history.replaceState(null, '', location.pathname);
  if (!pc) return fail('Link de pagamento inválido', 'Abra o pagamento a partir da loja.');
  start();
})();
