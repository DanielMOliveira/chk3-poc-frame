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
const browser = await chromium.launch(process.env.CHROME
  ? { executablePath: process.env.CHROME, headless: true }
  : { channel: 'chrome', headless: true });
let fail = 0;
const ok = (cond, msg) => { console.log(`  ${cond ? 'OK  ' : 'FALHA'} ${msg}`); if (!cond) fail = 1; };

async function payCase(pan, expected) {
  const page = await browser.newPage();
  const cspErrors = [];
  page.on('console', (m) => { if (/Content Security Policy|Refused/i.test(m.text())) cspErrors.push(m.text()); });
  console.log(`== Navegador, cartão ${pan.slice(0, 6)}...${pan.slice(-4)}`);
  await page.goto(CANAL + '/');
  await page.click('button');
  const opcoes = page.frameLocator('#frame');
  await opcoes.locator('#continuar').waitFor({ timeout: 10000 });
  const opFrame = page.frames().find((f) => f.url().startsWith(OPCOES));
  ok(!opFrame.url().includes('#'), `fragment limpo na Tela de opções (${opFrame.url()})`);
  ok(await opFrame.evaluate(() => document.cookie === '' && localStorage.length === 0 && sessionStorage.length === 0), 'sem cookie e sem storage na Tela de opções');

  // Recarregar o frame com a mesma credencial (reapresentação no TTL)
  await Promise.all([
    page.waitForEvent('framenavigated', { predicate: (f) => f.url().startsWith(OPCOES), timeout: 10000 }),
    page.click('#recarregar'),
  ]);
  await opcoes.locator('#continuar').waitFor({ state: 'visible', timeout: 10000 });
  ok(true, 'frame recarregado com a mesma credencial (passo 6 de novo)');

  await opcoes.locator('#continuar').click();
  const cartao = opcoes.frameLocator('#cartao');
  await cartao.locator('#pan').fill(pan);
  await cartao.locator('#exp').fill('12/' + String((new Date().getFullYear() + 2) % 100));
  await cartao.locator('#cvv').fill('123');
  await cartao.locator('#pay').click();
  await page.waitForFunction((exp) => document.getElementById('evento').textContent.includes(exp), expected, { timeout: 20000 });
  ok(true, `canal recebeu postMessage: ${await page.textContent('#evento')}`);
  ok((await opcoes.locator('#resultado-msg').textContent()).length > 0, `Tela de opções: "${await opcoes.locator('#resultado-msg').textContent()}"`);
  ok(cspErrors.length === 0, `sem violações de CSP ${cspErrors.join(' | ')}`);
  await page.close();
}

await payCase('4111111111111111', 'Autorizada');
await payCase('4000000000000002', 'Recusada');
await payCase('4000000000000119', 'Indeterminada');

// frame-ancestors: o mesmo canal servido por outra origem (127.0.0.1) não pode embutir o frame.
// Só faz sentido quando o canal está em localhost (local); atrás de domínio HTTPS é pulado.
if (new URL(CANAL).hostname === 'localhost') {
  console.log('== frame-ancestors com origem não autorizada');
  const page = await browser.newPage();
  const blocked = [];
  page.on('console', (m) => blocked.push(m.text()));
  await page.goto(CANAL.replace('localhost', '127.0.0.1') + '/');
  await page.click('button');
  await page.waitForTimeout(1500);
  const opFrame = page.frames().find((f) => f.url().startsWith(OPCOES));
  const rendered = opFrame ? await opFrame.evaluate(() => !!document.getElementById('continuar')).catch(() => false) : false;
  ok(!rendered && blocked.some((t) => t.includes('frame-ancestors')), 'Tela de opções bloqueada por frame-ancestors fora do canal autorizado');
  await page.close();
}

await browser.close();
console.log(fail ? '\nBROWSER E2E FALHOU' : '\nBROWSER E2E OK');
process.exit(fail);
