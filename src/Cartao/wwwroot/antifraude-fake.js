// Stand-in do script de antifraude (passo 8). Provedor real a confirmar (v7 §4), entra no inventário
// de scripts PCI DSS 6.4.3/11.6.1 com SRI e CSP. Aqui só gera um deviceSessionId opcional local.
window.chk3DeviceSessionId = 'poc-' + crypto.randomUUID();
