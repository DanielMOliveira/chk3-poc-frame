// E2E opcional no navegador real (Chrome headless): canal -> iframe Tela de opções -> iframe Cartão.
// Valida CSP/frame-ancestors, fragment limpo, iframe aninhado e postMessage (passo 14).
// Uso: npm i playwright-core && node scripts/browser-e2e.mjs
//   Usa o Google Chrome instalado (channel "chrome"); para outro binário: CHROME=/caminho/do/chrome.
//   Lê portas e origens do .env da raiz, com as mesmas regras do docker-compose.yml.
import { chromium } from 'playwright-core';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const envFile = fileURLToPath(new URL('../.env', import.meta.url));
const dotenv = {};
if (existsSync(envFile)) {
  for (const raw of readFileSync(envFile, 'utf8').split(/\r?\n/)) {
    const m = raw.match(/^([A-Z_][A-Z0-9_]*)=(.*)$/);
    if (m) dotenv[m[1]] = m[2];
  }
}
const env = (k, d) => process.env[k] || dotenv[k] || d;
const CANAL = env('CANAL_PUBLIC_ORIGIN', `http://localhost:${env('CANAL_HOST_PORT', '8080')}`).replace(/\/$/, '');
const OPCOES = env('OPCOES_PUBLIC_ORIGIN', `http://localhost:${env('OPCOES_HOST_PORT', '8081')}`).replace(/\/$/, '');
const CARTAO = env('CARTAO_PUBLIC_ORIGIN', `http://localhost:${env('CARTAO_HOST_PORT', '8082')}`).replace(/\/$/, '');
const browser = await chromium.launch(process.env.CHROME
  ? { executablePath: process.env.CHROME, headless: true }
  : { channel: 'chrome', headless: true });
let fail = 0;
const ok = (cond, msg) => { console.log(`  ${cond ? 'OK  ' : 'FALHA'} ${msg}`); if (!cond) fail = 1; };
// SHOTS_DIR=/caminho salva PNGs das telas principais.
const SHOTS = process.env.SHOTS_DIR;
const shot = async (page, name) => { if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: true }); };
const RESULT_SCREEN = { Autorizada: 's-success', Recusada: 's-declined', Indeterminada: 's-pending' };

// Opcional: simula um proxy de autenticação (ex.: Cloudflare Access) que exige cookie em TODO pedido
// à Tela e ao Cartão e responde 302 para outra origem sem ele. Reproduz o bug do "Carregando...".
async function newContext({ authProxy = false } = {}) {
  const ctx = await browser.newContext({ viewport: { width: 1100, height: 900 } });
  if (!authProxy) return ctx;
  for (const origin of [OPCOES, CARTAO]) {
    const u = new URL(origin);
    await ctx.addCookies([{ name: 'CF_Authorization', value: 'simulado', domain: u.hostname, path: '/', sameSite: 'Lax', httpOnly: true }]);
    await ctx.route(origin + '/**', async (route) => {
      const h = await route.request().allHeaders();
      if ((h.cookie || '').includes('CF_Authorization=')) return route.continue();
      return route.fulfill({ status: 302, headers: { location: 'http://127.0.0.1:9/cdn-cgi/access/login' } });
    });
  }
  return ctx;
}

async function payCase(pan, expected, { authProxy = false, shots = false } = {}) {
  const ctx = await newContext({ authProxy });
  const page = await ctx.newPage();
  const cspErrors = [];
  page.on('console', (m) => { if (/Content Security Policy|Refused/i.test(m.text())) cspErrors.push(m.text()); });
  console.log(`== Navegador${authProxy ? ' (proxy de autenticação simulado)' : ''}, cartão ${pan.slice(0, 6)}...${pan.slice(-4)}`);
  await page.goto(CANAL + '/');
  if (shots) await shot(page, '01-canal-pedido');
  await page.click('#pagar');
  const opcoes = page.frameLocator('#frame');
  await opcoes.locator('#s-options.active #m-card').waitFor({ timeout: 20000 });
  const opFrame = page.frames().find((f) => f.url().startsWith(OPCOES));
  ok(!opFrame.url().includes('#'), `fragment limpo na Tela de opções (${opFrame.url()})`);
  ok(await opFrame.evaluate(() => document.cookie === '' && localStorage.length === 0 && sessionStorage.length === 0), 'sem cookie legível e sem storage na Tela de opções');
  if (shots) { await page.waitForTimeout(400); await shot(page, '02-canal-com-tela-de-opcoes'); }

  // Recarregar o frame com a mesma credencial (reapresentação no TTL)
  await Promise.all([
    page.waitForEvent('framenavigated', { predicate: (f) => f.url().startsWith(OPCOES), timeout: 10000 }),
    page.click('#recarregar'),
  ]);
  await opcoes.locator('#s-options.active #m-card').waitFor({ timeout: 20000 });
  ok(true, 'frame recarregado com a mesma credencial (passo 6 de novo)');

  await opcoes.locator('#m-card').click();
  const cartao = opcoes.frameLocator('#cartao');
  await cartao.locator('#pan').fill(pan);
  await cartao.locator('#name').fill('TESTE POC');
  await cartao.locator('#exp').fill('12/' + String((new Date().getFullYear() + 2) % 100));
  await cartao.locator('#cvv').fill('123');
  if (shots) { await cartao.locator('#cvv').blur(); await page.waitForTimeout(600); await shot(page, '03-cartao-digitacao'); }
  await cartao.locator('#pay').click();
  await page.waitForFunction((exp) => document.getElementById('evento').textContent.includes(exp), expected, { timeout: 30000 });
  ok(true, `canal recebeu postMessage: ${await page.textContent('#evento')}`);
  await opcoes.locator(`#${RESULT_SCREEN[expected]}.active`).waitFor({ timeout: 5000 });
  ok(true, `Tela de opções mostra a tela de resultado #${RESULT_SCREEN[expected]}`);
  if (shots) { await page.waitForTimeout(500); await shot(page, `04-resultado-${expected.toLowerCase()}`); }
  ok(cspErrors.length === 0, `sem violações de CSP ${cspErrors.join(' | ')}`);
  await ctx.close();
}

await payCase('4111111111111111', 'Autorizada', { shots: true });
await payCase('4000000000000002', 'Recusada', { shots: true });
await payCase('4000000000000119', 'Indeterminada', { shots: true });
// Regressão do "Carregando...": com um proxy que exige cookie em todo pedido, o fluxo precisa funcionar.
await payCase('4111111111111111', 'Autorizada', { authProxy: true });

// Falha visível: se a validação da sessão falhar na rede, a Tela mostra erro (não fica em "Carregando").
{
  console.log('== Falha de rede no passo 6 mostra erro visível');
  const ctx = await newContext();
  await ctx.route(OPCOES + '/api/session', (route) => route.abort('connectionfailed'));
  const page = await ctx.newPage();
  await page.goto(CANAL + '/');
  await page.click('#pagar');
  const opcoes = page.frameLocator('#frame');
  await opcoes.locator('#s-error.active').waitFor({ timeout: 20000 });
  ok(await opcoes.locator('#errRetry').isVisible(), `erro visível com "Tentar novamente": "${await opcoes.locator('#errTitle').textContent()}"`);
  await shot(page, '05-erro-falha-de-rede');
  await ctx.close();
}

// Link expirado/inválido: credencial desconhecida no fragment.
{
  console.log('== Credencial inválida mostra tela de link expirado');
  const ctx = await newContext();
  const page = await ctx.newPage();
  await page.goto(OPCOES + '/#pc=pc_invalida');
  await page.locator('#s-error.active').waitFor({ timeout: 20000 });
  ok((await page.locator('#errTitle').textContent()).includes('expirado'), `"${await page.locator('#errTitle').textContent()}"`);
  await shot(page, '06-erro-link-expirado');
  await ctx.close();
}

// frame-ancestors: o mesmo canal servido por outra origem (127.0.0.1) não pode embutir o frame.
// Só faz sentido quando o canal está em localhost (local); atrás de domínio HTTPS é pulado.
if (new URL(CANAL).hostname === 'localhost') {
  console.log('== frame-ancestors com origem não autorizada');
  const page = await browser.newPage();
  const blocked = [];
  page.on('console', (m) => blocked.push(m.text()));
  await page.goto(CANAL.replace('localhost', '127.0.0.1') + '/');
  await page.click('#pagar');
  await page.waitForTimeout(1500);
  const opFrame = page.frames().find((f) => f.url().startsWith(OPCOES));
  const rendered = opFrame ? await opFrame.evaluate(() => !!document.getElementById('m-card')).catch(() => false) : false;
  ok(!rendered && blocked.some((t) => t.includes('frame-ancestors')), 'Tela de opções bloqueada por frame-ancestors fora do canal autorizado');
  await page.close();
}

await browser.close();
console.log(fail ? '\nBROWSER E2E FALHOU' : '\nBROWSER E2E OK');
process.exit(fail);
