# Hub de Pagamentos: cartões de teste

Fonte: informado por Daniel em 27/11/2026. Só cartões de teste públicos de homologação/sandbox.

## 1. Gateway CheckoutPay (HUB): homologação

| Bandeira / adquirente | Cartão | Validade | CVV |
|---|---|---|---|
| Visa / Cielo | 4012001037141112 | 05/2028 | 123 |
| Mastercard / Cielo | 5453010000066167 | 05/2028 | 123 |
| Amex / Cielo | 376449047333005 | 05/2028 | 1234 |
| Elo / Cielo | 6362970000457013 | 05/2028 | 123 |
| Diners / Cielo | 36490102462661 | 05/2028 | 123 |
| Discover / Cielo | 6011020000245045 | 05/2028 | 123 |
| JCB / Cielo | 3566007770004971 | 05/2028 | 123 |
| Aura / Cielo | 5078601912345600019 | 05/2028 | 123 |
| Visa / Stone-Global Payments | 4066559930861910 | 05/2028 | 123 |
| Visa / e.Rede (no Gateway) | 4761120000000148 | 12/2028 | 11 |

Regra do CheckoutPay em homologação: **não enviar valores com centavos fracionados** (ex.: 1020 rejeitado; usar
valores redondos como 1000). Só documentado: o CHK3 **não** valida nem arredonda o valor (pedido do Daniel, 27/11,
para testar centavos e o fluxo).

## 2. Sandbox e.Rede direto

Outro número -> erro 58. CVV 123 (Amex 1234), validade futura (ex.: 01/2035).

| Bandeira / modalidade | Cartão |
|---|---|
| Mastercard crédito | 5448280000000007 |
| Mastercard débito | 5277696455399733 |
| Mastercard BIN 2 crédito | 2223020000000005 |
| Mastercard BIN 2 débito | 2223000148400010 |
| Visa crédito | 4235647728025682 |
| Visa débito | 4761120000000148 |
| Elo crédito | 4389351648020055 |
| Elo débito | 4514166653413658 ou 5067230000009011 |
| Amex crédito | 371341553758128 |
| Hipercard crédito | 6062825624254001 |
| Hiper crédito | 6370950847866501 |
| Diners crédito | 36490101441625 |
| JCB crédito | 3569990012290937 |

Tokenizados:

| Bandeira | Modalidade | Cartão | tokenCryptogram |
|---|---|---|---|
| Visa | crédito | 4895370010000005 | AgAAAAAAAIR8CQrXSohbQAAAAAA= |
| Visa | débito | 4824810010000006 | AAABAkkREQAAAAAAAAAAAAAAAAA= |
| Mastercard | crédito | 2223000250000004 | ANbuvvxnDbK2AAEShHMWGgADFA== |
| Mastercard | débito | 5204970000000007 | AOPAIMgflr8UAAIShHMWGgADFA== |
| Elo | crédito | 4389358876174389 | AIeb2oot61nRAAHBnZ8HAAADFA== |
| Elo | débito | 4389356784017450 | ANfuuvxnDbK2AAESiXMWGgAEFw== |

Benefícios (01/2035, CVV 123):

| Bandeira | Cartão |
|---|---|
| Alelo | 4389357063526690 |
| Ticket | 5284776885068867 |
| Sodexo | 6363688827373622 |
| VR | 5490630354637860 |
| GoodCard | 4514166603616681 |

## No CHK3

- `POC_GATEWAY_TEST_PANS` (padrão no código e no compose) = os 10 cartões do HUB (tabela 1): vão para `GATEWAY_URL`.
  Os PANs de simulação do mock continuam indo sempre para o mock.
- Bandeira por prefixo (`Card.Brand`): visa, mastercard (incl. BIN 2), amex, elo (faixas públicas), diners, discover,
  jcb, aura, hipercard e hiper. Pendência: tabela de BINs real.
- CVV: 3 dígitos, Amex 4; para os cartões de teste do Gateway, 2 a 4 (o Visa e.Rede do HUB tem CVV 11).
- Pendências observadas:
  - **4066559930861910** (Visa Stone) **não passa no Luhn**: o CHK3 recusa no passo 9 (`cartao_invalido`).
  - O canal `canal-fake` só aceita `visa` e `mastercard` (seed): as demais bandeiras recebem `bandeira_nao_aceita`
    até o canal ser ampliado.
  - Tabela 2 (e.Rede direto) e tokenizados são referência; o CHK3 fala só com o HUB.
