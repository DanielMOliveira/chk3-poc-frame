# Hub de Pagamentos (CheckoutPay): códigos de retorno

Fonte: informado por Daniel em 27/11/2026. Documento de referência do POC; a implementação está em
`src/Autorizacao/HubCodes.cs`.

## 1. Status da transação (`<status>`)

Estado atual do processamento junto à operadora/adquirente.

| Código | Status | Descrição |
|---|---|---|
| 0 | Criada / Em andamento | Ainda não processada pelo adquirente |
| 1 | Autenticada | Validada, não processada financeiramente |
| 2 | Não-Autenticada | Sem validação do cartão |
| 3 | Autorizada pela operadora | Aprovada, pendente de captura |
| 4 | Não-Autorizada pela operadora | Negada |
| 5 | Em Cancelamento | Aguardando confirmação de estorno |
| 6 | Cancelado | Cancelada/estornada |
| 7 | Em Captura | Aguardando liquidação |
| 8 | Capturada / Finalizada | Capturada e liquidada |
| 9 | Não-Capturada | Falha na captura, pode tentar de novo |
| 10 | Pagamento Recorrente - Agendado | Recorrência agendada |
| 16 | Não Cancelada | Falha ao cancelar |
| 19 | Autorizado de fraude | Aprovada para verificação de risco |
| 20 | Cancelado por fraude | Reprovada e cancelada pelo antifraude |
| 21 | Não Autorizada por erro | Erro genérico |
| 22 | Finalizado | Agendamento de recorrência finalizado |
| 23 | Pausado | Recorrência pausada |

## 2. Erros do gateway (HTTP 409, `<errorCode>` / `<code>`)

| Código | Descrição |
|---|---|
| 001 | XML com erros |
| 010 | Autenticação no HUB inválida (MerchantId/MerchantKey) |
| 011 | Autenticação na CIELO inválida |
| 012 | Autenticação na REDE inválida |
| 020 | Inconsistência no envio do cartão, verificar modalidade habilitada (BPL) |
| 021 | Modalidade não habilitada na CIELO |
| 022 | Modalidade não habilitada na REDE |
| 023 | Parcelas inválidas/limite CIELO |
| 024 | Parcelas inválidas/limite REDE |
| 025 | Código de segurança ausente ou inválido |
| 026 | Erro ao processar o cartão |
| 027 | Indicador do código de segurança inválido |
| 028 | Inconsistência no cartão (número ou validade) |
| 030 | Código de transação inválido |
| 031 | TID inválido na CIELO |
| 032 | Código de transação inválido na REDE |
| 033 | Falha ao cancelar |
| 034 | Transação já cancelada |
| 035 | Transação não encontrada para a operação |
| 036 | Transação já capturada |
| 037 | Adquirente não configurado corretamente no painel |
| 100 | Falha ao conectar na operadora (timeout) |

Série 900 (validação de campos):

| Código | Descrição |
|---|---|
| 900 | Moeda inválida (986) |
| 901 | Idioma/origem inválido (BRA) |
| 902 | Data/Hora inválida (YYYY-MM-DD) |
| 903 | Número do pedido inválido (máx 50 alfanuméricos) |
| 904 | Valor inválido |
| 905 | Caracteres inválidos no nome do portador |

## 3. Código da operadora (`<statusCode>` / `<errorAcquirer>`)

`<statusCode>` (nó `<processor>`) ou `<errorAcquirer>` (nó `<error>`) = código bruto do adquirente (Cielo, Rede,
Stone...) com o motivo do emissor (saldo, bloqueado, vencido).

## Mapeamento no CHK3 (proposta, POC)

| Resultado CHK3 | `<status>` / erro |
|---|---|
| Autorizada | 3 (captura automática na V0: também 7 e 8) |
| Recusada | 2, 4, 20, 21 |
| Indeterminada (Worker consulta) | 0, 1, 19, e 409/100 (timeout) |
| Indeterminada + log de aviso | demais (5, 6, 9, 16, 10, 22, 23): não esperados na autorização |

Como está implementado:
- Padrões em `HubCodes.DefaultApproved` (3, 7, 8) e `HubCodes.DefaultDeclined` (2, 4, 20, 21).
  `GATEWAY_APPROVED_CODES` / `GATEWAY_DECLINED_CODES` preenchidos no `.env` **substituem** o padrão; vazios = padrão.
- Resposta de erro (`<status><code>409</code>…` + `result/error`) é sempre **Indeterminada**, com o código do erro
  e a descrição da tabela (1036 e outros fora da tabela aparecem como `fora_da_tabela`).
- `resultado` do passo 11-12 (Grafana/Loki), exemplo:
  `Recusada_codigo_4_nao_autorizada_adq_NA_real_ref_<transactionId do Gateway>`; erro:
  `Indeterminada_codigo_409_erro_gateway_erro_037_adquirente_nao_configurado_real`.
- Linha `chk3gw` de volta: `statusGateway`, `statusDescricao`, `erroGateway`, `erroDescricao`, `codigoAdquirente`
  (`<statusCode>`, ou `<responseCode>` quando é só ele que vem, ou `<errorAcquirer>`) e `resultado`.
  Painel 10 do dashboard `chk3-transacao` mostra esses campos.
- Pedido: a Integration aceita `orderId` com 1 a 50 alfanuméricos (sem hífen/ponto) e gera `CHK3<20 hex>`.
  A Autorização revalida antes de enviar; fora da regra não envia (Recusada, código `CHK3_903`).

## Observações / divergências

- O erro **1036** observado em 27/11 ("Autenticação no gateway inválido" / "Sem Adquirente Cadastrato ou Ativo")
  não está na tabela (que vai até 905); provavelmente equivale a 037 ou é código interno.
- **903** limita o pedido a 50 alfanuméricos: GUID com hífen pode falhar. O orderId gerado pelo CHK3 é alfanumérico.
- **responseCode=NA** visto da E-REDE (27/11, status 4 UNAUTHORIZED) sem explicação na documentação; a doc fala em
  `<statusCode>`, que não veio nessa resposta.
