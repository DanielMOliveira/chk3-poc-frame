-- CHK3 POC frame v7. Esquema mínimo dos passos 3, 6, 7, 10 e 13.

-- Passo 3: cadastro do canal (TTL por canal, padrão 30 min, máximo 24 h).
CREATE TABLE channels (
    id                   text PRIMARY KEY,
    merchant_id          text NOT NULL,
    display_name         text NOT NULL,
    session_ttl_seconds  integer NOT NULL DEFAULT 1800 CHECK (session_ttl_seconds BETWEEN 1 AND 86400),
    max_installments     integer NOT NULL DEFAULT 1 CHECK (max_installments BETWEEN 1 AND 24),
    brands               text[] NOT NULL,
    active               boolean NOT NULL DEFAULT true
);

-- Passo 3c: pagamento e sessão numa tabela só (POC). Snapshot do cadastro gravado na criação.
CREATE TABLE payment_sessions (
    transaction_id     uuid PRIMARY KEY,
    channel_id         text NOT NULL REFERENCES channels(id),
    idempotency_key    text NOT NULL,
    fingerprint        bytea NOT NULL,
    order_id           text NOT NULL,
    amount_cents       bigint NOT NULL CHECK (amount_cents > 0),
    currency           char(3) NOT NULL,
    installments       integer NULL,
    campos_adicionais  jsonb NOT NULL DEFAULT '{}'::jsonb,
    brands             text[] NOT NULL,
    max_installments   integer NOT NULL,
    state              text NOT NULL DEFAULT 'Criada'
                       CHECK (state IN ('Criada','AutorizacaoSolicitada','Autorizada','Recusada','Indeterminada')),
    created_at         timestamptz NOT NULL DEFAULT now(),
    expires_at         timestamptz NOT NULL,
    updated_at         timestamptz NOT NULL DEFAULT now(),
    -- Passo 3: admissão atômica por cliente (canal), endpoint e chave.
    CONSTRAINT uq_idempotency UNIQUE (channel_id, idempotency_key)
);

-- Passos 3c e 7: só o hash das credenciais (SHA-256 de 32 bytes aleatórios).
CREATE TABLE credentials (
    hash            bytea PRIMARY KEY,
    transaction_id  uuid NOT NULL REFERENCES payment_sessions(transaction_id),
    kind            text NOT NULL CHECK (kind IN ('pagamento','digitacao')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL
);
CREATE INDEX ix_credentials_tx ON credentials(transaction_id);

-- Passo 10: tentativa única por sessão (PK = transaction_id garante o uso único).
CREATE TABLE attempts (
    transaction_id  uuid PRIMARY KEY REFERENCES payment_sessions(transaction_id),
    local_ref       uuid NOT NULL UNIQUE,
    brand           text NOT NULL,
    installments    integer NOT NULL,
    stage           text NOT NULL,          -- envio_iniciado | resultado_registrado
    result          text NULL,              -- Autorizada | Recusada | Indeterminada
    gateway_code    text NULL,              -- evidência sanitizada, sem PAN
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now()
);

-- Passo 13: outbox atômico com a mudança de estado. Worker (15/16) fora do escopo do POC.
CREATE TABLE outbox (
    id              bigserial PRIMARY KEY,
    transaction_id  uuid NOT NULL REFERENCES payment_sessions(transaction_id),
    event_type      text NOT NULL,
    payload         jsonb NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    delivered_at    timestamptz NULL
);

-- Seeds do POC. canal-fake: TTL padrão 30 min. canal-ttl-curto: 3 s, usado nos testes de expiração.
INSERT INTO channels (id, merchant_id, display_name, session_ttl_seconds, max_installments, brands) VALUES
 ('canal-fake',      'merchant-poc', 'Loja Exemplo POC', 1800, 6, ARRAY['visa','mastercard']),
 ('canal-ttl-curto', 'merchant-poc', 'Canal TTL curto',     3, 3, ARRAY['visa']);
