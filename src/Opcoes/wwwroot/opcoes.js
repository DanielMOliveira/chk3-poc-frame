'use strict';
(async () => {
  const $ = (id) => document.getElementById(id);
  const show = (id) => { for (const s of ['loading', 'erro', 'opcoes', 'digitacao', 'resultado']) $(s).hidden = s !== id; };

  // Passo 5: credencial de pagamento vem no fragment. Lê, guarda só em memória (closure) e limpa o hash.
  const params = new URLSearchParams(location.hash.slice(1));
  const pc = params.get('pc');
  history.replaceState(null, '', location.pathname); // tira do histórico e da barra
  if (!pc) { show('erro'); return; }

  const cfg = await (await fetch('/config.json', { cache: 'no-store' })).json();
  const api = (path) => fetch(path, { method: 'POST', credentials: 'omit', cache: 'no-store', headers: { 'X-Payment-Credential': pc } });

  // Passo 6: valida sem consumir. Recarregar não funciona sem o fragment (credencial não é persistida):
  // a reabertura dentro do TTL é feita pelo canal (3b) ou reaplicando a mesma URL do frame.
  const r = await api('/api/session');
  if (!r.ok) { show('erro'); return; } // 6a
  const s = await r.json();

  $('merchant').textContent = s.merchantName;
  $('resumo').textContent = `Pedido ${s.orderId}: ${(s.amountCents / 100).toLocaleString('pt-BR', { style: 'currency', currency: s.currency })}`;
  $('bandeiras').textContent = `Bandeiras aceitas: ${s.brands.join(', ')}`;
  const max = s.fixedInstallments || s.maxInstallments;
  for (let i = s.fixedInstallments || 1; i <= max; i++) $('parcelas').add(new Option(i === 1 ? 'À vista' : `${i}x`, String(i)));
  if (s.fixedInstallments) $('parcelas').disabled = true;
  show('opcoes');

  // Passo 7: escolhe cartão e parcelas, troca pela credencial de digitação e abre o iframe filho por POST.
  $('continuar').addEventListener('click', async () => {
    $('continuar').disabled = true;
    const t = await api('/api/typing-credential');
    if (!t.ok) { show('erro'); return; }
    const { typingCredential } = await t.json();
    const form = document.createElement('form');
    form.method = 'POST'; form.action = `${cfg.cartaoOrigin}/digitacao`; form.target = 'cartao'; form.hidden = true;
    for (const [k, v] of [['tc', typingCredential], ['installments', $('parcelas').value]]) {
      const i = document.createElement('input'); i.type = 'hidden'; i.name = k; i.value = v; form.appendChild(i);
    }
    document.body.appendChild(form);
    show('digitacao');
    form.submit();
    form.remove();
  });

  // Passo 14: resultado vindo do iframe da digitação, só da origem do endpoint pay.
  window.addEventListener('message', (ev) => {
    if (ev.origin !== cfg.cartaoOrigin || ev.source !== $('cartao').contentWindow) return;
    const d = ev.data || {};
    if (d.type !== 'chk3.result') return;
    const msgs = {
      Autorizada: 'Pagamento autorizado.',
      Recusada: 'Pagamento recusado. Fale com a loja para tentar outro meio.',
      Indeterminada: 'Não conseguimos confirmar o pagamento agora. A loja será avisada do resultado final.',
      TentativaJaRegistrada: 'Este pagamento já foi enviado. A loja será avisada do resultado.',
      SessaoInvalida: 'Sessão expirada ou inválida. Volte à loja.',
    };
    $('resultado-msg').textContent = msgs[d.status] || msgs.Indeterminada;
    show('resultado');
    // O retorno do frame não prova pagamento final: o canal deve confiar no webhook (passo 16).
    window.parent.postMessage({ type: 'chk3.result', status: d.status, transactionId: d.transactionId }, cfg.canalOrigin);
  });
})();
